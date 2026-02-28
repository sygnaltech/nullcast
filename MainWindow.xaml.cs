using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer
{
    public partial class MainWindow : Window
    {
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private Media _currentMedia;
        private bool _isDraggingSlider;
        private DispatcherTimer _timer;
        private string _ytdlpPath;
        private bool _ytdlpReady;

        // Win32 click detection on the native LibVLC HWND
        private const int WM_PARENTNOTIFY  = 0x0210;
        private const int WM_CREATE        = 0x0001;
        private const int WM_LBUTTONDOWN   = 0x0201;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONDOWN   = 0x0204;
        private DispatcherTimer _clickTimer;
        private bool _pendingClick;

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        // Quality selection
        private string _currentUrl;
        private int _selectedHeight = 1080; // default: good quality, streams well on fiber
        private static readonly (int Height, string Label)[] QualityLevels =
        {
            (2160, "4K (2160p)"),
            (1440, "1440p"),
            (1080, "1080p"),
            (720,  "720p"),
            (480,  "480p"),
            (360,  "360p"),
        };

        // Playlist service
        private PlaylistAuthService _auth;
        private PlaylistApiService  _api;
        private string              _activeMuid;
        private int                 _positionSaveTick;
        private long?               _seekOnPlay;         // ms to seek when Playing fires
        private bool                _loadingWorkspaces;  // suppress SelectionChanged during load
        private List<Workspace>     _workspaces = new();
        private Workspace           _selectedWorkspace;
        private Bookmark            _contextMenuTarget;  // item under mouse when context menu opened
        private ContextMenu         _playlistContextMenu;
        private MenuItem            _toggleMenuItem;

        // App settings
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayer", "settings.json");
        private AppSettings _settings = new();

        public ObservableCollection<Bookmark> PlaylistItems { get; } = new();

        public ICommand OpenUrlCommand  { get; }
        public ICommand PlayPauseCommand { get; }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Title = $"Video Player v{v.Major}.{v.Minor}.{v.Build}";

            OpenUrlCommand   = new RelayCommand(_ => OpenUrl_Click(null, null));
            PlayPauseCommand = new RelayCommand(_ => PlayPause_Click(null, null));

            InitializeVLC();
            SetupTimer();
            InitializeYtDlp();

            // Build playlist context menu entirely in code to avoid XAML NameScope/Connect() issues
            _toggleMenuItem = new MenuItem { Header = "Mark as Completed" };
            var deleteMenuItem = new MenuItem { Header = "Delete" };
            _toggleMenuItem.Click += PlaylistItem_ToggleCompleted_Click;
            deleteMenuItem.Click  += PlaylistItem_Delete_Click;

            var menuItemStyle = new Style(typeof(MenuItem));
            menuItemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, Brushes.White));
            menuItemStyle.Setters.Add(new Setter(MenuItem.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11))));

            _playlistContextMenu = new ContextMenu
            {
                Background   = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
                BorderBrush  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(1),
                ItemContainerStyle = menuItemStyle,
            };
            _playlistContextMenu.Items.Add(_toggleMenuItem);
            _playlistContextMenu.Items.Add(deleteMenuItem);
            _playlistContextMenu.Opened += PlaylistContextMenu_Opened;

            PlaylistBox.ContextMenu = _playlistContextMenu;
            PlaylistBox.PreviewMouseRightButtonDown += PlaylistBox_PreviewMouseRightButtonDown;
        }

        private async void InitializeYtDlp()
        {
            try
            {
                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                _ytdlpPath = Path.Combine(appDir, "yt-dlp.exe");

                if (!File.Exists(_ytdlpPath))
                {
                    StatusText.Text = "Downloading yt-dlp (first run only)...";
                    await DownloadYtDlp(_ytdlpPath);
                }

                _ytdlpReady = true;
                StatusText.Text = "Press Ctrl+O or File > Open URL to load a video";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to initialize yt-dlp: {ex.Message}";
            }
        }

        private async Task DownloadYtDlp(string targetPath)
        {
            const string url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
            using var client = new HttpClient();
            var bytes = await client.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(targetPath, bytes);
        }

        private void InitializeVLC()
        {
            Core.Initialize();
            _libVLC = new LibVLC("--no-xlib");
            _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

            // Must set after the VideoView is loaded
            Loaded += async (s, e) =>
            {
                VideoView.MediaPlayer = _mediaPlayer;

                // SetHwndBackground is still called as a belt-and-suspenders repaint once
                // the layout pass completes and the HWND tree is stable.
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(SetHwndBackground));

                // Pin to all virtual desktops
                var hwnd = new WindowInteropHelper(this).Handle;
                var pinned = VirtualDesktopPinner.PinWindow(hwnd);
                App.Log($"[VideoPlayer] Virtual desktop pin: {(pinned ? "success" : "failed")}, HWND: {hwnd}");

                // Hook Win32 messages to detect clicks on the native LibVLC child HWND.
                // WPF mouse events don't fire over HwndHost — WM_PARENTNOTIFY is the reliable path.
                HwndSource.FromHwnd(hwnd)?.AddHook(VideoHwndHook);

                // Timer to distinguish a single click from the first beat of a double-click.
                _clickTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime())
                };
                _clickTimer.Tick += (ts, te) =>
                {
                    _clickTimer.Stop();
                    _pendingClick = false;
                    PlayPause_Click(null, null);
                };

                await InitializePlaylistAsync();
            };

            _mediaPlayer.Playing += async (s, e) =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    PlayPauseButton.Content = "⏸";
                    StatusText.Visibility = Visibility.Collapsed;
                });

                if (_seekOnPlay.HasValue)
                {
                    var seekTo = _seekOnPlay.Value;
                    _seekOnPlay = null;
                    // Short delay to let VLC buffer before seeking
                    await Task.Delay(500);
                    _mediaPlayer.Time = seekTo;
                }

                // Stamp duration on the active bookmark so progress fill can render
                var dur = (int)(_mediaPlayer.Length / 1000);
                if (dur > 0 && _activeMuid != null)
                {
                    var muid = _activeMuid;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var bm = PlaylistItems.FirstOrDefault(b => b.Muid == muid);
                        if (bm != null) bm.DurationSeconds = dur;
                        // Persist so progress bars render correctly on next load
                        if (_settings.KnownDurations.TryGetValue(muid, out var existing) && existing == dur)
                            return;
                        _settings.KnownDurations[muid] = dur;
                        SaveSettings();
                    });
                }
            };

            _mediaPlayer.Paused += (s, e) => Dispatcher.InvokeAsync(() =>
            {
                PlayPauseButton.Content = "▶";
            });

            _mediaPlayer.Stopped += (s, e) => Dispatcher.InvokeAsync(() =>
            {
                PlayPauseButton.Content = "▶";
                ProgressSlider.Value = 0;
            });

            _mediaPlayer.EndReached += (s, e) => Dispatcher.InvokeAsync(() =>
            {
                PlayPauseButton.Content = "▶";

                if (_activeMuid != null && _api != null)
                {
                    var muid    = _activeMuid;
                    var seconds = (int)(_mediaPlayer.Length / 1000);
                    _ = _api.SavePositionAsync(muid, seconds);

                    // Mark completed locally and persist
                    var bm = PlaylistItems.FirstOrDefault(b => b.Muid == muid);
                    if (bm != null) bm.IsCompleted = true;
                    if (_settings.CompletedMuids.Add(muid))
                        SaveSettings();

                    if (_selectedWorkspace != null)
                        _ = RefreshBookmarksAsync(_selectedWorkspace.Id);
                }
            });
        }

        // ──────────────────────────────────────────────────────
        // Playlist initialisation
        // ──────────────────────────────────────────────────────

        private async Task InitializePlaylistAsync()
        {
            _auth = new PlaylistAuthService();
            _api  = new PlaylistApiService(_auth);

            await LoadSettingsAsync();

            var tokens = await _auth.LoadTokensAsync();
            if (tokens != null && !string.IsNullOrEmpty(tokens.AccessToken))
            {
                UpdateLoginUI(tokens.DisplayName);
                try
                {
                    await LoadPlaylistAsync();
                }
                catch (Exception ex)
                {
                    App.Log($"[Playlist] Startup load failed: {ex.Message}");
                }
            }
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var json = await File.ReadAllTextAsync(SettingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }

        private async Task LoadPlaylistAsync()
        {
            _loadingWorkspaces = true;
            try
            {
                _workspaces = await _api.GetWorkspacesAsync();
                WorkspaceCombo.ItemsSource = _workspaces;
                _selectedWorkspace = _workspaces.FirstOrDefault(w => w.IsDefault == 1)
                                  ?? _workspaces.FirstOrDefault();
                WorkspaceCombo.SelectedItem = _selectedWorkspace;
            }
            finally
            {
                _loadingWorkspaces = false;
            }

            if (_selectedWorkspace != null)
                await RefreshBookmarksAsync(_selectedWorkspace.Id);

            UpdatePlaylistVisibility();
        }

        private async Task RefreshBookmarksAsync(int workspaceId)
        {
            try
            {
                PlaylistErrorText.Visibility = Visibility.Collapsed;
                var items = await _api.GetYouTubeBookmarksAsync(workspaceId);
                PlaylistItems.Clear();
                foreach (var bm in items)
                {
                    if (_settings.CompletedMuids.Contains(bm.Muid))
                        bm.IsCompleted = true;
                    if (_settings.KnownDurations.TryGetValue(bm.Muid, out var dur))
                        bm.DurationSeconds = dur;
                    PlaylistItems.Add(bm);
                }
            }
            catch (Exception ex)
            {
                PlaylistErrorText.Text = $"Could not load playlist: {ex.Message}";
                PlaylistErrorText.Visibility = Visibility.Visible;
            }
        }

        private void UpdateLoginUI(string displayName)
        {
            if (!string.IsNullOrEmpty(displayName))
            {
                UserDisplayName.Text       = displayName;
                UserDisplayName.Visibility = Visibility.Visible;
                ConnectButton.Visibility   = Visibility.Collapsed;
                SignOutButton.Visibility   = Visibility.Visible;
            }
            else
            {
                UserDisplayName.Visibility = Visibility.Collapsed;
                ConnectButton.Visibility   = Visibility.Visible;
                SignOutButton.Visibility   = Visibility.Collapsed;
            }
            UpdatePlaylistVisibility();
        }

        private void UpdatePlaylistVisibility()
        {
            bool signedIn = _auth?.IsSignedIn == true;
            bool hideAll  = !signedIn || WindowState == WindowState.Maximized || _isFullscreen;

            if (hideAll)
            {
                PlaylistPanel.Visibility = Visibility.Collapsed;
                PlaylistTab.Visibility   = Visibility.Collapsed;
                return;
            }

            PlaylistTab.Visibility = Visibility.Visible;
            CollapseToggleButton.Content = _settings.PlaylistCollapsed ? "❯" : "❮";
            PlaylistPanel.Visibility = _settings.PlaylistCollapsed
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void CollapseToggle_Click(object sender, RoutedEventArgs e)
        {
            _settings.PlaylistCollapsed = !_settings.PlaylistCollapsed;
            SaveSettings();
            UpdatePlaylistVisibility();
        }

        private void PlaylistBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Walk up from the clicked element to find the ListBoxItem and store its Bookmark
            var element = e.OriginalSource as DependencyObject;
            while (element != null && element is not ListBoxItem)
                element = VisualTreeHelper.GetParent(element);
            _contextMenuTarget = (element as ListBoxItem)?.DataContext as Bookmark;
            // Suppress the menu if not over an item
            _playlistContextMenu.Visibility = _contextMenuTarget != null
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PlaylistContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (_contextMenuTarget == null) return;
            _toggleMenuItem.Header = _contextMenuTarget.IsCompleted
                ? "Mark as Not Completed" : "Mark as Completed";
        }

        private void PlaylistItem_ToggleCompleted_Click(object sender, RoutedEventArgs e)
        {
            var bm = _contextMenuTarget;
            if (bm == null) return;

            bm.IsCompleted = !bm.IsCompleted;
            if (bm.IsCompleted)
                _settings.CompletedMuids.Add(bm.Muid);
            else
                _settings.CompletedMuids.Remove(bm.Muid);
            SaveSettings();
        }

        private async void PlaylistItem_Delete_Click(object sender, RoutedEventArgs e)
        {
            var bm = _contextMenuTarget;
            if (bm == null) return;

            PlaylistItems.Remove(bm);
            _settings.CompletedMuids.Remove(bm.Muid);
            _settings.KnownDurations.Remove(bm.Muid);
            SaveSettings();

            if (_api != null)
                await _api.DeleteBookmarkAsync(bm.Muid);
        }

        // ──────────────────────────────────────────────────────
        // Login / sign-out
        // ──────────────────────────────────────────────────────

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsEnabled = false;
            try
            {
                var tokens = await _auth.LoginAsync();
                UpdateLoginUI(tokens.DisplayName);
                await LoadPlaylistAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Sign-in failed: {ex.Message}", "Playlist", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }

        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            await _auth.SignOutAsync();
            PlaylistItems.Clear();
            UpdateLoginUI(null);
        }

        // ──────────────────────────────────────────────────────
        // Workspace + playlist item interactions
        // ──────────────────────────────────────────────────────

        private async void WorkspaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingWorkspaces) return;
            if (WorkspaceCombo.SelectedItem is Workspace ws)
            {
                _selectedWorkspace = ws;
                await RefreshBookmarksAsync(ws.Id);
            }
        }

        private async void RefreshPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedWorkspace != null)
                await RefreshBookmarksAsync(_selectedWorkspace.Id);
        }

        private async void PlaylistBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || _api == null) return;
            if (PlaylistBox.SelectedItem is Bookmark bookmark)
                await PlayBookmark(bookmark);
        }

        private async Task PlayBookmark(Bookmark bookmark)
        {
            _activeMuid        = bookmark.Muid;
            _positionSaveTick  = 0;
            _seekOnPlay        = null;

            // Fetch the freshest position before starting
            var fresh = await _api.GetBookmarkAsync(bookmark.Muid);
            if (fresh?.Position is int pos && pos > 0)
                _seekOnPlay = pos * 1000L;

            await PlayYouTubeUrl(bookmark.Url);
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            UpdatePlaylistVisibility();
        }

        // ──────────────────────────────────────────────────────
        // Timer
        // ──────────────────────────────────────────────────────

        private void SetupTimer()
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_mediaPlayer == null || !_mediaPlayer.IsPlaying || _isDraggingSlider)
                return;

            var length  = _mediaPlayer.Length;
            var time    = _mediaPlayer.Time;
            var seconds = (int)(time / 1000);

            if (length > 0)
            {
                ProgressSlider.Value = (time * 100.0) / length;
                TimeDisplay.Text     = $"{FormatTime(time)} / {FormatTime(length)}";
            }

            // Update sidebar progress bar live
            if (_activeMuid != null)
            {
                var bm = PlaylistItems.FirstOrDefault(b => b.Muid == _activeMuid);
                if (bm != null)
                    bm.Position = seconds;
            }

            // Save position to server every 10 seconds (20 × 500ms ticks)
            if (_activeMuid != null && _api != null)
            {
                _positionSaveTick++;
                if (_positionSaveTick >= 20)
                {
                    _positionSaveTick = 0;
                    _ = _api.SavePositionAsync(_activeMuid, seconds);
                }
            }
        }

        private string FormatTime(long milliseconds)
        {
            var ts = TimeSpan.FromMilliseconds(milliseconds);
            return ts.Hours > 0
                ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        // ──────────────────────────────────────────────────────
        // Playback
        // ──────────────────────────────────────────────────────

        private async void OpenUrl_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenUrlDialog { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _activeMuid = null;
                _seekOnPlay = null;
                await PlayYouTubeUrl(dialog.Url);
            }
        }

        private async Task PlayYouTubeUrl(string url)
        {
            try
            {
                StatusText.Text = "Loading video...";
                StatusText.Visibility = Visibility.Visible;

                // Stop the player and wait for it to reach a clean stopped state
                App.Log($"[VLC] Stop requested. State={_mediaPlayer.State} IsPlaying={_mediaPlayer.IsPlaying}");
                // Stop() must run off the UI thread — calling it from the UI thread blocks the Win32
                // message pump, which LibVLC needs to finish its cleanup, causing a deadlock.
                App.Log("[VLC] Stopping off UI thread...");
                await Task.Run(() => _mediaPlayer.Stop());
                App.Log("[VLC] Stop() returned");

                App.Log("[VLC] Disposing old media");
                _currentMedia?.Dispose();
                _currentMedia = null;
                App.Log("[VLC] Old media disposed");

                if (!_ytdlpReady)
                {
                    MessageBox.Show("yt-dlp is not ready yet. Please wait.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _currentUrl = url;

                // Fetch title, best video URL, and best audio URL in parallel.
                // bestvideo+bestaudio gives full resolution (1080p/4K); "best" alone caps at 720p
                // because YouTube no longer provides high-res combined streams.
                var titleTask = RunYtDlp("--get-title", url);
                var videoTask = RunYtDlp($"-f bestvideo[height<={_selectedHeight}]/bestvideo -g", url);
                var audioTask = RunYtDlp("-f bestaudio -g", url);
                await Task.WhenAll(titleTask, videoTask, audioTask);

                var title    = titleTask.Result?.Trim();
                var videoUrl = videoTask.Result?.Trim();
                var audioUrl = audioTask.Result?.Trim();

                if (!string.IsNullOrEmpty(title))
                    Title = $"Video Player v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3)} - {title}";

                if (string.IsNullOrEmpty(videoUrl))
                    throw new Exception("Could not get stream URL");

                App.Log($"[VLC] Creating new Media. Player state={_mediaPlayer.State}");
                _currentMedia = new Media(_libVLC, new Uri(videoUrl));
                if (!string.IsNullOrEmpty(audioUrl))
                {
                    App.Log("[VLC] Attaching audio slave");
                    _currentMedia.AddOption($":input-slave={audioUrl}");
                }
                App.Log("[VLC] Calling Play()");
                _mediaPlayer.Play(_currentMedia);
                App.Log("[VLC] Play() returned");
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error loading video";
                MessageBox.Show($"Error loading video: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<string> RunYtDlp(string arguments, string url)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ytdlpPath,
                    Arguments = $"{arguments} \"{url}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error  = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
            {
                throw new Exception(error);
            }

            return output.Split('\n')[0];
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;

            if (_mediaPlayer.IsPlaying)
            {
                // Save position before pausing
                if (_activeMuid != null && _api != null)
                {
                    var seconds = (int)(_mediaPlayer.Time / 1000);
                    _ = _api.SavePositionAsync(_activeMuid, seconds);
                }
                _mediaPlayer.Pause();
            }
            else
            {
                _mediaPlayer.Play();
            }
        }

        private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = false;
            if (_mediaPlayer != null && _mediaPlayer.Length > 0)
            {
                var newTime = (long)(ProgressSlider.Value * _mediaPlayer.Length / 100.0);
                _mediaPlayer.Time = newTime;
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Volume = (int)VolumeSlider.Value;
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _timer?.Stop();

            // Best-effort position save on close
            if (_activeMuid != null && _api != null && _mediaPlayer?.IsPlaying == true)
            {
                var seconds = (int)(_mediaPlayer.Time / 1000);
                _ = _api.SavePositionAsync(_activeMuid, seconds);
            }

            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _currentMedia?.Dispose();
            _libVLC?.Dispose();
        }

        // ──────────────────────────────────────────────────────
        // Drag & drop
        // ──────────────────────────────────────────────────────

        private void VideoArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;

            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                var text = e.Data.GetData(DataFormats.Text) as string;
                if (IsYouTubeUrl(text))
                    e.Effects = DragDropEffects.Copy;
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = e.Data.GetData(DataFormats.UnicodeText) as string;
                if (IsYouTubeUrl(text))
                    e.Effects = DragDropEffects.Copy;
            }

            e.Handled = true;
        }

        private async void VideoArea_Drop(object sender, DragEventArgs e)
        {
            string url = null;

            if (e.Data.GetDataPresent(DataFormats.Text))
                url = e.Data.GetData(DataFormats.Text) as string;
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
                url = e.Data.GetData(DataFormats.UnicodeText) as string;

            if (!string.IsNullOrEmpty(url))
            {
                url = ExtractYouTubeUrl(url);
                if (!string.IsNullOrEmpty(url))
                {
                    // Play only — do not add to playlist regardless of sign-in state
                    _activeMuid = null;
                    _seekOnPlay = null;
                    await PlayYouTubeUrl(url);
                }
            }
        }

        private void PlaylistArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;

            if (_auth?.IsSignedIn != true)
            {
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                var text = e.Data.GetData(DataFormats.Text) as string;
                if (IsYouTubeUrl(text))
                    e.Effects = DragDropEffects.Copy;
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = e.Data.GetData(DataFormats.UnicodeText) as string;
                if (IsYouTubeUrl(text))
                    e.Effects = DragDropEffects.Copy;
            }

            e.Handled = true;
        }

        private async void PlaylistArea_Drop(object sender, DragEventArgs e)
        {
            if (_auth?.IsSignedIn != true || _selectedWorkspace == null) return;

            string url = null;
            if (e.Data.GetDataPresent(DataFormats.Text))
                url = e.Data.GetData(DataFormats.Text) as string;
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
                url = e.Data.GetData(DataFormats.UnicodeText) as string;

            if (string.IsNullOrEmpty(url)) return;
            url = ExtractYouTubeUrl(url);
            if (string.IsNullOrEmpty(url)) return;

            // Extract the page title from whichever format the browser populated.
            // URL bar drag (Chrome/Edge): "FileGroupDescriptorW" contains a virtual .url file
            //   whose filename is the page title.
            // Link/anchor drag: DataFormats.Html contains <a href="...">Title</a>.
            string title = ExtractTitleFromDragData(e.Data);

            try
            {
                var bookmark = await _api.CreateBookmarkAsync(url, _selectedWorkspace.Id, title);
                if (bookmark != null)
                {
                    PlaylistItems.Add(bookmark);

                    if (!_mediaPlayer.IsPlaying)
                    {
                        // Nothing playing — add and immediately play
                        await PlayBookmark(bookmark);
                    }
                    // Else: added to list only, current video continues
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not add to playlist: {ex.Message}",
                    "Playlist", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private bool IsYouTubeUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.Contains("youtube.com") || text.Contains("youtu.be");
        }

        private string ExtractTitleFromDragData(IDataObject data)
        {
            // Strategy 1: URL bar drag (Chrome/Edge/Firefox) puts a virtual Internet Shortcut
            // file into "FileGroupDescriptorW". The filename is "<Page Title>.url".
            // FILEGROUPDESCRIPTORW layout: DWORD cItems (4) + FILEDESCRIPTORW[]
            // FILEDESCRIPTORW layout: 72 bytes of flags/timestamps + WCHAR cFileName[MAX_PATH]
            if (data.GetDataPresent("FileGroupDescriptorW"))
            {
                try
                {
                    using var ms = data.GetData("FileGroupDescriptorW") as System.IO.MemoryStream;
                    if (ms != null)
                    {
                        var bytes = ms.ToArray();
                        const int nameOffset = 4 + 72; // cItems DWORD + header fields
                        if (bytes.Length > nameOffset + 2)
                        {
                            var nameLen = Math.Min(520, bytes.Length - nameOffset);
                            var name = System.Text.Encoding.Unicode.GetString(bytes, nameOffset, nameLen);
                            var nullIdx = name.IndexOf('\0');
                            if (nullIdx >= 0) name = name[..nullIdx];
                            name = name.Trim();
                            if (name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                                name = name[..^4].Trim();
                            if (!string.IsNullOrEmpty(name))
                                return StripYouTubeSuffix(name);
                        }
                    }
                }
                catch { /* fall through */ }
            }

            // Strategy 2: link/anchor drag puts <a href="...">Title</a> in the Html format.
            if (data.GetDataPresent(DataFormats.Html))
            {
                try
                {
                    var html = data.GetData(DataFormats.Html) as string;
                    if (!string.IsNullOrEmpty(html))
                    {
                        var match = Regex.Match(html, @">([^<]+)</a>", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            var title = match.Groups[1].Value.Trim();
                            if (!string.IsNullOrEmpty(title))
                                return StripYouTubeSuffix(title);
                        }
                    }
                }
                catch { /* fall through */ }
            }

            return null;
        }

        private static string StripYouTubeSuffix(string title)
        {
            if (title.EndsWith(" - YouTube", StringComparison.OrdinalIgnoreCase))
                title = title[..^" - YouTube".Length].Trim();
            return string.IsNullOrEmpty(title) ? null : title;
        }

        private string ExtractYouTubeUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var pattern = @"(https?://)?(www\.)?(youtube\.com/watch\?v=|youtu\.be/|youtube\.com/shorts/)[\w-]+";
            var match   = Regex.Match(text, pattern);

            if (match.Success)
            {
                var url = match.Value;
                if (!url.StartsWith("http"))
                    url = "https://" + url;
                return url;
            }

            return text.Trim();
        }

        // ──────────────────────────────────────────────────────
        // Win32 HWND hook
        // ──────────────────────────────────────────────────────

        private IntPtr VideoHwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_PARENTNOTIFY)
            {
                var eventCode = wParam.ToInt32() & 0xFFFF;

                if (eventCode == WM_CREATE)
                {
                    // A Win32 child window is being created (VLC's rendering HWND).
                    // WM_PARENTNOTIFY(WM_CREATE) fires BEFORE the window receives its first
                    // WM_ERASEBKGND, so setting the class brush here paints the initial
                    // erase black instead of the default white — no flash at all.
                    var childHwnd = lParam;
                    if (childHwnd != IntPtr.Zero)
                    {
                        var blackBrush = CreateSolidBrush(0x00000000);
                        SetClassLongPtr(childHwnd, GCL_HBRBACKGROUND, blackBrush);
                    }
                }
                else if (eventCode == WM_LBUTTONDBLCLK)
                {
                    // CS_DBLCLKS window: Windows detected the double-click itself
                    _clickTimer?.Stop();
                    _pendingClick = false;
                    ToggleFullscreen();
                }
                else if (eventCode == WM_LBUTTONDOWN)
                {
                    if (_pendingClick)
                    {
                        // Non-CS_DBLCLKS window: two WM_LBUTTONDOWN within double-click time
                        _clickTimer.Stop();
                        _pendingClick = false;
                        ToggleFullscreen();
                    }
                    else
                    {
                        _pendingClick = true;
                        _clickTimer.Start();
                    }
                }
                else if (eventCode == WM_RBUTTONDOWN)
                {
                    // Defer past current message so WM_RBUTTONUP is processed and mouse capture released
                    Dispatcher.InvokeAsync(ShowQualityMenu, System.Windows.Threading.DispatcherPriority.Input);
                }
            }
            return IntPtr.Zero;
        }

        private void ShowQualityMenu()
        {
            GetCursorPos(out var pt);
            var hwnd  = new WindowInteropHelper(this).Handle;
            var hMenu = CreatePopupMenu();
            try
            {
                AppendMenu(hMenu, MF_STRING | MF_DISABLED, 0, "Quality");
                AppendMenu(hMenu, MF_SEPARATOR, 0, null);

                for (uint i = 0; i < QualityLevels.Length; i++)
                {
                    var (height, label) = QualityLevels[i];
                    var flags = MF_STRING | (_selectedHeight == height ? MF_CHECKED : 0);
                    AppendMenu(hMenu, flags, i + 1, label);
                }

                // TrackPopupMenu with TPM_RETURNCMD is blocking but runs its own message loop,
                // so the UI stays responsive and Win32 focus/capture is handled correctly.
                var cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);

                if (cmd >= 1 && cmd <= (uint)QualityLevels.Length)
                {
                    _selectedHeight = QualityLevels[cmd - 1].Height;
                    _seekOnPlay     = null; // Don't seek to saved position on quality change
                    if (!string.IsNullOrEmpty(_currentUrl))
                        _ = PlayYouTubeUrl(_currentUrl);
                }
            }
            finally
            {
                DestroyMenu(hMenu);
            }
        }

        private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Fallback for WPF-visible clicks (e.g. no video loaded, HwndHost not active).
            // Clicks over the native LibVLC HWND are handled by VideoHwndHook instead.
            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.ClickCount == 1)
            {
                PlayPause_Click(null, null);
                e.Handled = true;
            }
        }

        private void VideoView_Loaded(object sender, RoutedEventArgs e)
        {
            VideoView.Background = System.Windows.Media.Brushes.Black;
            // Note: SetHwndBackground() is NOT called here — the HwndHost doesn't exist yet
            // because MediaPlayer hasn't been assigned.  It's called from the Loaded handler.
        }

        // ──────────────────────────────────────────────────────
        // Fullscreen
        // ──────────────────────────────────────────────────────

        private bool _isFullscreen;
        private WindowState _previousWindowState;
        private double _previousWidth;
        private double _previousHeight;
        private double _previousLeft;
        private double _previousTop;

        private void ToggleFullscreen()
        {
            if (_isFullscreen)
            {
                // Exit fullscreen
                _isFullscreen = false;
                TopBar.Visibility      = Visibility.Visible;
                ControlsBar.Visibility = Visibility.Visible;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode  = ResizeMode.CanResize;
                Topmost     = false;

                WindowState = _previousWindowState;
                if (_previousWindowState == WindowState.Normal)
                {
                    Width  = _previousWidth;
                    Height = _previousHeight;
                    Left   = _previousLeft;
                    Top    = _previousTop;
                }

                UpdatePlaylistVisibility();
            }
            else
            {
                // Enter fullscreen
                _isFullscreen        = true;
                _previousWindowState = WindowState;
                _previousWidth       = Width;
                _previousHeight      = Height;
                _previousLeft        = Left;
                _previousTop         = Top;

                TopBar.Visibility      = Visibility.Collapsed;
                ControlsBar.Visibility = Visibility.Collapsed;
                UpdatePlaylistVisibility(); // hides playlist panel

                WindowStyle = WindowStyle.None;
                ResizeMode  = ResizeMode.NoResize;
                Topmost     = true;

                if (WindowState == WindowState.Maximized)
                    WindowState = WindowState.Normal;

                // Get the current monitor's working area using WPF
                var hwnd   = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var screen = System.Windows.Forms.Screen.FromHandle(hwnd);

                // Convert from device pixels to WPF units (DPI-aware)
                var source = PresentationSource.FromVisual(this);
                var dpiX   = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                var dpiY   = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

                Left   = screen.Bounds.Left  * dpiX;
                Top    = screen.Bounds.Top   * dpiY;
                Width  = screen.Bounds.Width * dpiX;
                Height = screen.Bounds.Height * dpiY;
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _isFullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Key.Right || e.Key == Key.Left)
            {
                if (_mediaPlayer != null && _mediaPlayer.Length > 0)
                {
                    var delta   = (e.KeyboardDevice.Modifiers == ModifierKeys.Control ? 60 : 10) * 1000L;
                    var newTime = _mediaPlayer.Time + (e.Key == Key.Right ? delta : -delta);
                    _mediaPlayer.Time = Math.Clamp(newTime, 0, _mediaPlayer.Length);
                }
                e.Handled = true;
            }
        }

        // ──────────────────────────────────────────────────────
        // Win32 P/Invokes
        // ──────────────────────────────────────────────────────

        [DllImport("user32.dll")]
        private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int crColor);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const int  GCL_HBRBACKGROUND = -10;
        private const uint RDW_INVALIDATE    = 0x0001;
        private const uint RDW_ERASE         = 0x0004;
        private const uint RDW_ALLCHILDREN   = 0x0080;
        private const uint RDW_UPDATENOW     = 0x0100;
        private const uint SWP_NOMOVE        = 0x0002;
        private const uint SWP_NOZORDER      = 0x0004;
        private const uint SWP_NOACTIVATE    = 0x0010;

        // Win32 native popup menu (avoids WPF ContextMenu focus issues over native HWNDs)
        private const uint MF_STRING    = 0x0000;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint MF_CHECKED   = 0x0008;
        private const uint MF_DISABLED  = 0x0002;
        private const uint TPM_RETURNCMD   = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;

        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);
        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private void SetHwndBackground()
        {
            try
            {
                // VLC only re-renders its idle frame in response to WM_SIZE, not WM_PAINT.
                // Resize by 1 device pixel and immediately restore — same trigger as
                // moving the window between monitors, which is known to fix the white flash.
                var hwnd = new WindowInteropHelper(this).Handle;
                GetWindowRect(hwnd, out RECT r);
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h + 1, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h,     SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch
            {
                // Ignore errors — video will still work
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;

                var childResult = FindVisualChild<T>(child);
                if (childResult != null)
                    return childResult;
            }
            return null;
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute    = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add    => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object parameter)    => _execute(parameter);
    }
}
