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
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer
{
    public partial class MainWindow : Window
    {
        private Player _player;
        private bool _isDraggingSlider;
        private DispatcherTimer _timer;
        private string _ytdlpPath;
        private bool _ytdlpReady;

        // Quality selection
        private string _currentUrl;
        private int _selectedHeight = 1080;
        private static readonly (int Height, string Label)[] QualityLevels =
        {
            (2160, "4K (2160p)"),
            (1440, "1440p"),
            (1080, "1080p"),
            (720,  "720p"),
            (480,  "480p"),
            (360,  "360p"),
        };

        // Local playback history
        private readonly HistoryService _history = new();

        // Click disambiguation + auto-hide overlay controls
        private DispatcherTimer _clickTimer;
        private DispatcherTimer _controlsHideTimer;
        private bool            _controlsOverlayMode;

        // Playlist service
        private PlaylistAuthService _auth;
        private PlaylistApiService  _api;
        private string              _activeMuid;
        private int                 _positionSaveTick;
        private long?               _seekOnPlay;         // ms to seek when Playing fires
        private bool                _loadingWorkspaces;
        private List<Workspace>     _workspaces = new();
        private Workspace           _selectedWorkspace;
        private Bookmark            _contextMenuTarget;
        private ContextMenu         _playlistContextMenu;
        private MenuItem            _toggleMenuItem;

        // App settings
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayer", "settings.json");
        private AppSettings _settings = new();

        public ObservableCollection<Bookmark> PlaylistItems { get; } = new();
        public ObservableCollection<HistoryEntry> HistoryItems { get; } = new();

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

            InitializeFlyleaf();
            SetupTimer();
            InitializeYtDlp();

            // Build playlist context menu entirely in code
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

            // Keep the overlay controls alive while the pointer is over them, and
            // keep them positioned when the video area resizes.
            ControlsBar.MouseMove += (s, e) => { if (_controlsOverlayMode) ShowOverlayControls(); };
            VideoContainer.SizeChanged += (s, e) =>
            {
                if (_controlsOverlayMode && OverlayControlsPopup.IsOpen)
                    PositionOverlayControls();
            };
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

        private void InitializeFlyleaf()
        {
            var config = new Config();

            _player = new Player(config);

            Loaded += async (s, e) =>
            {
                // Attach the player to the FlyleafHost control
                FlyleafPlayer.Player = _player;

                // Hook mouse events on overlay and surface windows
                FlyleafPlayer.OverlayCreated += (ps, pe) =>
                {
                    App.Log("[Flyleaf] Overlay created, hooking mouse + drop events");
                    FlyleafPlayer.Overlay.MouseLeftButtonDown += VideoArea_MouseLeftButtonDown;
                    FlyleafPlayer.Overlay.MouseMove          += VideoArea_MouseMove;
                    FlyleafPlayer.Overlay.AllowDrop = true;
                    FlyleafPlayer.Overlay.DragOver += VideoArea_DragOver;
                    FlyleafPlayer.Overlay.Drop     += VideoArea_Drop;
                };
                FlyleafPlayer.SurfaceCreated += (ps, pe) =>
                {
                    App.Log("[Flyleaf] Surface created, hooking mouse + drop events");
                    FlyleafPlayer.Surface.MouseLeftButtonDown += VideoArea_MouseLeftButtonDown;
                    FlyleafPlayer.Surface.MouseMove          += VideoArea_MouseMove;
                    // The surface HWND is the real OLE drop target over the video, so
                    // register our handler here (Flyleaf's own OpenOnDrop is disabled).
                    FlyleafPlayer.Surface.AllowDrop = true;
                    FlyleafPlayer.Surface.DragOver += VideoArea_DragOver;
                    FlyleafPlayer.Surface.Drop     += VideoArea_Drop;
                };

                // Pin to all virtual desktops
                var hwnd = new WindowInteropHelper(this).Handle;
                var pinned = VirtualDesktopPinner.PinWindow(hwnd);
                App.Log($"[VideoPlayer] Virtual desktop pin: {(pinned ? "success" : "failed")}, HWND: {hwnd}");

                await InitializePlaylistAsync();
            };

            _player.PlaybackStopped += (s, e) => Dispatcher.InvokeAsync(() =>
            {
                PlayPauseButton.Content = "▶";
                ProgressSlider.Value = 0;
            });

            _player.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_player.Status))
                {
                    Dispatcher.InvokeAsync(async () =>
                    {
                        switch (_player.Status)
                        {
                            case Status.Playing:
                                PlayPauseButton.Content = "⏸";
                                StatusText.Visibility = Visibility.Collapsed;

                                if (_seekOnPlay.HasValue)
                                {
                                    var seekTo = _seekOnPlay.Value;
                                    _seekOnPlay = null;
                                    await Task.Delay(300);
                                    _player.SeekAccurate((int)seekTo);
                                }

                                // Stamp duration on the active bookmark
                                var dur = (int)((_player.Duration / 10000000.0));
                                if (dur > 0 && _activeMuid != null)
                                {
                                    var muid = _activeMuid;
                                    var bm = PlaylistItems.FirstOrDefault(b => b.Muid == muid);
                                    if (bm != null) bm.DurationSeconds = dur;
                                    if (!_settings.KnownDurations.TryGetValue(muid, out var existing) || existing != dur)
                                    {
                                        _settings.KnownDurations[muid] = dur;
                                        SaveSettings();
                                    }
                                }
                                break;

                            case Status.Paused:
                                PlayPauseButton.Content = "▶";
                                break;

                            case Status.Stopped:
                                PlayPauseButton.Content = "▶";
                                ProgressSlider.Value = 0;
                                break;

                            case Status.Ended:
                                PlayPauseButton.Content = "▶";
                                if (_activeMuid != null && _api != null)
                                {
                                    var muid    = _activeMuid;
                                    var seconds = (int)(_player.Duration / 10000000.0);
                                    _ = _api.SavePositionAsync(muid, seconds);

                                    var bm2 = PlaylistItems.FirstOrDefault(b => b.Muid == muid);
                                    if (bm2 != null) bm2.IsCompleted = true;
                                    if (_settings.CompletedMuids.Add(muid))
                                        SaveSettings();

                                    if (_selectedWorkspace != null)
                                        _ = RefreshBookmarksAsync(_selectedWorkspace.Id);
                                }
                                break;
                        }
                    });
                }
            };
        }

        // ──────────────────────────────────────────────────────
        // Playlist initialisation
        // ──────────────────────────────────────────────────────

        private async Task InitializePlaylistAsync()
        {
            _auth = new PlaylistAuthService();
            _api  = new PlaylistApiService(_auth);

            await LoadSettingsAsync();

            await _history.LoadAsync();
            RefreshHistoryView();

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
            // The sidebar hosts both the online Playlist (needs sign-in) and the
            // local History (always available), so it is hidden only in the
            // immersive maximized/fullscreen modes.
            bool hideAll = WindowState == WindowState.Maximized || _isFullscreen;

            if (hideAll)
            {
                SidePanel.Visibility   = Visibility.Collapsed;
                PlaylistTab.Visibility = Visibility.Collapsed;
                return;
            }

            PlaylistTab.Visibility = Visibility.Visible;
            CollapseToggleButton.Content = _settings.PlaylistCollapsed ? "❯" : "❮";
            SidePanel.Visibility = _settings.PlaylistCollapsed
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        // ──────────────────────────────────────────────────────
        // Sidebar tabs (Playlist / History)
        // ──────────────────────────────────────────────────────

        private void ShowPlaylistTab_Click(object sender, RoutedEventArgs e) => SelectTab(history: false);
        private void ShowHistoryTab_Click(object sender, RoutedEventArgs e)  => SelectTab(history: true);

        private void SelectTab(bool history)
        {
            PlaylistContent.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
            HistoryContent.Visibility  = history ? Visibility.Visible   : Visibility.Collapsed;

            // Active tab reads brighter; inactive dims.
            PlaylistTabButton.Background = new SolidColorBrush(history ? Color.FromRgb(0x11, 0x11, 0x11) : Color.FromRgb(0x1a, 0x1a, 0x1a));
            PlaylistTabButton.Foreground = history ? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) : Brushes.White;
            HistoryTabButton.Background  = new SolidColorBrush(history ? Color.FromRgb(0x1a, 0x1a, 0x1a) : Color.FromRgb(0x11, 0x11, 0x11));
            HistoryTabButton.Foreground  = history ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

            if (history) RefreshHistoryView();
        }

        // ──────────────────────────────────────────────────────
        // History
        // ──────────────────────────────────────────────────────

        private void RefreshHistoryView()
        {
            var query = HistorySearchBox?.Text ?? "";
            HistoryItems.Clear();
            foreach (var entry in _history.Search(query))
                HistoryItems.Add(entry);
        }

        private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshHistoryView();
        }

        private async void HistoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            if (HistoryBox.SelectedItem is HistoryEntry entry && !string.IsNullOrEmpty(entry.Url))
            {
                _activeMuid = null;
                _seekOnPlay = null;
                await PlayYouTubeUrl(entry.Url);
            }
        }

        private void CollapseToggle_Click(object sender, RoutedEventArgs e)
        {
            _settings.PlaylistCollapsed = !_settings.PlaylistCollapsed;
            SaveSettings();
            UpdatePlaylistVisibility();
        }

        private void PlaylistBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as DependencyObject;
            while (element != null && element is not ListBoxItem)
                element = VisualTreeHelper.GetParent(element);
            _contextMenuTarget = (element as ListBoxItem)?.DataContext as Bookmark;

            e.Handled = true;

            if (_contextMenuTarget != null)
                _playlistContextMenu.IsOpen = true;
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

            var fresh = await _api.GetBookmarkAsync(bookmark.Muid);
            if (fresh?.Position is int pos && pos > 0)
                _seekOnPlay = pos * 1000L;

            await PlayYouTubeUrl(bookmark.Url);
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            UpdatePlaylistVisibility();
            UpdateControlsMode();
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
            if (_player == null || _player.Status != Status.Playing || _isDraggingSlider)
                return;

            var length  = (_player.Duration / 10000.0);
            var time    = (_player.CurTime / 10000.0);
            var seconds = (int)(time / 1000);

            if (length > 0)
            {
                ProgressSlider.Value = (time * 100.0) / length;
                TimeDisplay.Text     = $"{FormatTime((long)time)} / {FormatTime((long)length)}";
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

                App.Log($"[Flyleaf] Stop requested. Status={_player.Status}");
                _player.Stop();
                App.Log("[Flyleaf] Stop() returned");

                if (!_ytdlpReady)
                {
                    MessageBox.Show("yt-dlp is not ready yet. Please wait.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _currentUrl = url;

                // Fetch title and stream URL in parallel
                // Use best muxed format (video+audio in one stream) capped at selected quality
                var titleTask = RunYtDlp("--get-title", url);
                var streamTask = RunYtDlp($"-f best[height<={_selectedHeight}]/best -g", url);
                await Task.WhenAll(titleTask, streamTask);

                var title     = titleTask.Result?.Trim();
                var streamUrl = streamTask.Result?.Trim();

                if (!string.IsNullOrEmpty(title))
                    Title = $"Video Player v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3)} - {title}";

                if (string.IsNullOrEmpty(streamUrl))
                    throw new Exception("Could not get stream URL");

                // Record to local-only history (de-duped by URL, most-recent first).
                _history.Record(url, title);
                if (HistoryContent.Visibility == Visibility.Visible)
                    RefreshHistoryView();

                App.Log($"[Flyleaf] Opening stream. Status={_player.Status}");
                _player.OpenAsync(streamUrl);
                App.Log("[Flyleaf] OpenAsync() called");
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
            if (_player == null) return;

            if (_player.Status == Status.Playing)
            {
                // Save position before pausing
                if (_activeMuid != null && _api != null)
                {
                    var seconds = (int)(_player.CurTime / 10000000.0);
                    _ = _api.SavePositionAsync(_activeMuid, seconds);
                }
                _player.Pause();
            }
            else
            {
                _player.Play();
            }
        }

        private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = false;
            if (_player != null && (_player.Duration / 10000.0) > 0)
            {
                var newTimeMs = (int)(ProgressSlider.Value * (_player.Duration / 10000.0) / 100.0);
                _player.Seek(newTimeMs);
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_player != null)
            {
                _player.Audio.Volume = (int)VolumeSlider.Value;
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
            if (_activeMuid != null && _api != null && _player?.Status == Status.Playing)
            {
                var seconds = (int)(_player.CurTime / 10000000.0);
                _ = _api.SavePositionAsync(_activeMuid, seconds);
            }

            _player?.Dispose();
        }

        // ──────────────────────────────────────────────────────
        // Drag & drop
        // ──────────────────────────────────────────────────────

        // Window-level drag probes: fire for any drag over the WPF client area,
        // even if element hit-testing/AllowDrop routing fails. If these DON'T fire,
        // the OS never delivered the drop (surface HWND swallowed it / source issue).
        private void Window_PreviewDragOver(object sender, DragEventArgs e) => LogDrag("Window", e);

        private void Window_PreviewDrop(object sender, DragEventArgs e)
            => App.Log("[Drop] Window_PreviewDrop fired");

        private string _dragLogSig;

        /// <summary>Logs the drag payload once per distinct drag (DragOver fires
        /// continuously) so we can see whether the event fires and what formats
        /// the source is offering.</summary>
        private void LogDrag(string where, DragEventArgs e)
        {
            try
            {
                var formats = string.Join(",", e.Data.GetFormats());
                string text = null;
                if (e.Data.GetDataPresent(DataFormats.UnicodeText))
                    text = e.Data.GetData(DataFormats.UnicodeText) as string;
                else if (e.Data.GetDataPresent(DataFormats.Text))
                    text = e.Data.GetData(DataFormats.Text) as string;

                var sig = $"{where}|{formats}|{text}";
                if (sig == _dragLogSig) return;
                _dragLogSig = sig;
                App.Log($"[DragOver] {where} formats=[{formats}] text=\"{text}\" isYT={IsYouTubeUrl(text)}");
            }
            catch (Exception ex)
            {
                App.Log($"[DragOver] {where} log error: {ex.Message}");
            }
        }

        private void VideoArea_DragOver(object sender, DragEventArgs e)
        {
            LogDrag("VideoArea", e);
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
            App.Log("[Drop] VideoArea_Drop fired");
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
                    _activeMuid = null;
                    _seekOnPlay = null;
                    await PlayYouTubeUrl(url);
                }
            }
        }

        private void PlaylistArea_DragOver(object sender, DragEventArgs e)
        {
            LogDrag("PlaylistArea", e);
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
            App.Log($"[Drop] PlaylistArea_Drop fired. SignedIn={_auth?.IsSignedIn} Workspace={_selectedWorkspace?.Name ?? "null"}");

            if (_auth?.IsSignedIn != true || _selectedWorkspace == null)
            {
                App.Log("[Drop] Aborted — not signed in or no workspace");
                return;
            }

            e.Handled = true;

            string url = null;
            if (e.Data.GetDataPresent(DataFormats.Text))
                url = e.Data.GetData(DataFormats.Text) as string;
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
                url = e.Data.GetData(DataFormats.UnicodeText) as string;

            App.Log($"[Drop] Raw URL: {url ?? "null"}");

            if (string.IsNullOrEmpty(url)) return;
            url = ExtractYouTubeUrl(url);
            App.Log($"[Drop] Extracted URL: {url}");
            if (string.IsNullOrEmpty(url)) return;

            string title = ExtractTitleFromDragData(e.Data);
            App.Log($"[Drop] Title: {title ?? "null"}");

            try
            {
                App.Log($"[Drop] Calling CreateBookmarkAsync...");
                var bookmark = await _api.CreateBookmarkAsync(url, _selectedWorkspace.Id, title);
                App.Log($"[Drop] CreateBookmarkAsync returned: {(bookmark == null ? "null" : bookmark.Muid)}");
                if (bookmark != null)
                {
                    // Re-pull the list from the server so the dropped item shows up
                    // (whether it was a new insert or an existing duplicate)
                    await RefreshBookmarksAsync(_selectedWorkspace.Id);

                    if (_player.Status != Status.Playing)
                    {
                        await PlayBookmark(bookmark);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"[Drop] Exception: {ex}");
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
            if (data.GetDataPresent("FileGroupDescriptorW"))
            {
                try
                {
                    using var ms = data.GetData("FileGroupDescriptorW") as System.IO.MemoryStream;
                    if (ms != null)
                    {
                        var bytes = ms.ToArray();
                        const int nameOffset = 4 + 72;
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
                catch { }
            }

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
                catch { }
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
        // Mouse interaction on video area
        // ──────────────────────────────────────────────────────

        private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // A double-click maximizes/normalizes the window and must NOT toggle
                // play/pause — cancel the pending single-click action.
                _clickTimer?.Stop();
                ToggleMaximizeNormal();
                e.Handled = true;
            }
            else if (e.ClickCount == 1)
            {
                // Defer the play/pause toggle by the system double-click interval so
                // it only fires if no second click follows.
                _clickTimer?.Stop();
                if (_clickTimer == null)
                {
                    _clickTimer = new DispatcherTimer();
                    _clickTimer.Tick += (s, _) =>
                    {
                        _clickTimer.Stop();
                        PlayPause_Click(null, null);
                    };
                }
                _clickTimer.Interval = TimeSpan.FromMilliseconds(
                    System.Windows.Forms.SystemInformation.DoubleClickTime);
                _clickTimer.Start();
                e.Handled = true;
            }
        }

        /// <summary>Double-click behaviour: toggle Maximized ↔ Normal (windowed
        /// fullscreen stays on F11/Esc). Never affects the play/pause state.</summary>
        private void ToggleMaximizeNormal()
        {
            if (_isFullscreen) return; // borderless fullscreen is managed via F11/Esc
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void VideoArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (_controlsOverlayMode)
                ShowOverlayControls();
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (_controlsOverlayMode)
                ShowOverlayControls();
        }

        // ──────────────────────────────────────────────────────
        // Right-click quality menu (WPF ContextMenu — no Win32 needed)
        // ──────────────────────────────────────────────────────

        private void ShowQualityMenu()
        {
            var menu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                BorderThickness = new Thickness(1),
            };

            var headerItem = new MenuItem
            {
                Header = "Quality",
                IsEnabled = false,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            };
            menu.Items.Add(headerItem);
            menu.Items.Add(new Separator());

            foreach (var (height, label) in QualityLevels)
            {
                var item = new MenuItem
                {
                    Header = label,
                    IsChecked = _selectedHeight == height,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                };
                var h = height;
                item.Click += (s, ev) =>
                {
                    _selectedHeight = h;
                    _seekOnPlay = null;
                    if (!string.IsNullOrEmpty(_currentUrl))
                        _ = PlayYouTubeUrl(_currentUrl);
                };
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
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
                _isFullscreen = false;
                TopBar.Visibility = Visibility.Visible;
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
                UpdateControlsMode();
            }
            else
            {
                _isFullscreen        = true;
                _previousWindowState = WindowState;
                _previousWidth       = Width;
                _previousHeight      = Height;
                _previousLeft        = Left;
                _previousTop         = Top;

                TopBar.Visibility = Visibility.Collapsed;
                UpdatePlaylistVisibility();
                UpdateControlsMode();

                WindowStyle = WindowStyle.None;
                ResizeMode  = ResizeMode.NoResize;
                Topmost     = true;

                if (WindowState == WindowState.Maximized)
                    WindowState = WindowState.Normal;

                var hwnd   = new WindowInteropHelper(this).Handle;
                var screen = System.Windows.Forms.Screen.FromHandle(hwnd);

                var source = PresentationSource.FromVisual(this);
                var dpiX   = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                var dpiY   = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

                Left   = screen.Bounds.Left  * dpiX;
                Top    = screen.Bounds.Top   * dpiY;
                Width  = screen.Bounds.Width * dpiX;
                Height = screen.Bounds.Height * dpiY;
            }
        }

        // ──────────────────────────────────────────────────────
        // Auto-hiding controls overlay (maximized / fullscreen only)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Docked bottom bar in normal windowed mode; a floating auto-hiding
        /// overlay when maximized or fullscreen.
        /// </summary>
        private void UpdateControlsMode()
        {
            bool overlay = _isFullscreen || WindowState == WindowState.Maximized;
            if (overlay == _controlsOverlayMode) return;
            _controlsOverlayMode = overlay;

            if (overlay)
            {
                // Re-parent the docked ControlsBar into the top-level Popup so it
                // paints above the Flyleaf video surface.
                if (RootGrid.Children.Contains(ControlsBar))
                    RootGrid.Children.Remove(ControlsBar);
                OverlayControlsPopup.Child = ControlsBar;

                // Reveal briefly on entry, then auto-hide.
                ShowOverlayControls();
            }
            else
            {
                _controlsHideTimer?.Stop();
                OverlayControlsPopup.IsOpen = false;
                OverlayControlsPopup.Child  = null;

                ControlsBar.Width      = double.NaN;   // stretch inside the grid row
                ControlsBar.Visibility = Visibility.Visible;
                if (!RootGrid.Children.Contains(ControlsBar))
                {
                    RootGrid.Children.Add(ControlsBar);
                    Grid.SetRow(ControlsBar, 2);
                }
            }
        }

        private void PositionOverlayControls()
        {
            double w = VideoContainer.ActualWidth;
            double h = VideoContainer.ActualHeight;
            if (w <= 0 || h <= 0) return;

            ControlsBar.Width = w;
            OverlayControlsPopup.Width           = w;
            OverlayControlsPopup.HorizontalOffset = 0;
            OverlayControlsPopup.VerticalOffset   = Math.Max(0, h - ControlsBar.Height);
        }

        private void ShowOverlayControls()
        {
            if (!_controlsOverlayMode) return;

            PositionOverlayControls();
            OverlayControlsPopup.IsOpen = true;

            if (_controlsHideTimer == null)
            {
                _controlsHideTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                _controlsHideTimer.Tick += (s, _) =>
                {
                    // Stay visible while the pointer is over the controls themselves.
                    if (ControlsBar.IsMouseOver)
                    {
                        _controlsHideTimer.Stop();
                        _controlsHideTimer.Start();
                        return;
                    }
                    _controlsHideTimer.Stop();
                    OverlayControlsPopup.IsOpen = false;
                };
            }
            _controlsHideTimer.Stop();
            _controlsHideTimer.Start();
        }

        // ──────────────────────────────────────────────────────
        // Dark title bar (black caption, white text)
        // ──────────────────────────────────────────────────────

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // dark caption buttons
        private const int DWMWA_CAPTION_COLOR           = 35; // Win11 22000+: caption bg
        private const int DWMWA_TEXT_COLOR              = 36; // Win11 22000+: caption text

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyDarkTitleBar(new WindowInteropHelper(this).Handle);
        }

        private static void ApplyDarkTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

                // COLORREF is 0x00BBGGRR.
                int black = 0x00000000; // caption background
                int white = 0x00FFFFFF; // caption text
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref black, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR,    ref white, sizeof(int));
            }
            catch { /* pre-Win11 builds ignore the caption color attrs */ }
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
                if (_player != null && (_player.Duration / 10000.0) > 0)
                {
                    var deltaMs = (e.KeyboardDevice.Modifiers == ModifierKeys.Control ? 60 : 10) * 1000;
                    var curMs   = (int)(_player.CurTime / 10000.0);
                    var maxMs   = (int)(_player.Duration / 10000.0);
                    var newMs   = Math.Clamp(curMs + (e.Key == Key.Right ? deltaMs : -deltaMs), 0, maxMs);
                    _player.Seek(newMs);
                }
                e.Handled = true;
            }
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
