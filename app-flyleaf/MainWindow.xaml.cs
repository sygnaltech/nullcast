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
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using FlyleafLib.MediaFramework.MediaStream;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer
{
    public enum SidebarTab { Playlist, History, Plex, Podcasts }

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

        // When yt-dlp resolves a source to separate video + audio streams (e.g. Reddit's
        // split DASH), the audio URL is parked here and attached as an external stream once
        // the video finishes opening. Null for muxed sources that already carry audio.
        private string _pendingExternalAudioUrl;
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
        private ContextMenu         _videoContextMenu;
        private MenuItem            _toggleMenuItem;

        // App settings
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayer", "settings.json");
        private AppSettings _settings = new();

        // Plex (separate ecosystem — not routed through the bookmarks service)
        private readonly ServicesStore _services = new();
        private PlexService _plex;
        private PlexItem    _activePlex;      // set while a Plex item is playing; drives timeline reports
        private int         _plexTimelineTick;
        private DispatcherTimer _plexSearchDebounce;
        private SidebarTab _activeTab = SidebarTab.Playlist;

        // Plex browse state (library selector → category → drill-down)
        private List<PlexSection> _plexSections = new();
        private PlexSection _plexSection;                 // active library (null while in search mode)
        private bool _plexSearchMode;                     // true when the "Search" segment is active
        private bool _plexSectionsLoaded;
        private readonly ObservableCollection<PlexCategory> _plexCategories = new();
        private PlexCategory _plexCategory;
        private readonly Dictionary<string, List<PlexGenre>> _plexGenreCache = new();
        private List<PlexItem> _plexBrowseItems = new();  // level-0 list for the current category
        private List<PlexItem> _plexCurrentItems = new(); // list at the current drill depth
        private readonly List<PlexDrillFrame> _plexDrill = new();
        private int _plexLoadToken;                        // stale-guard for async browse loads
        private bool _plexFullscreen;                      // full-screen browse takeover active
        private bool _plexResumeAfterFullscreen;           // was the player playing when we took over?

        // Segment pill colors (active vs inactive) — mirror the PlexSegment XAML style.
        private static readonly Brush SegActiveBg   = Frozen(Color.FromArgb(0x21, 0x7D, 0x97, 0xFF));
        private static readonly Brush SegInactiveBg = Frozen(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
        private static readonly Brush SegActiveFg   = Frozen(Color.FromRgb(0xE7, 0xE9, 0xF1));
        private static readonly Brush SegInactiveFg = Frozen(Color.FromRgb(0x84, 0x8B, 0x9F));
        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>One level of TV drill-down; its children are cached so breadcrumb hops don't refetch.</summary>
        private class PlexDrillFrame
        {
            public string RatingKey = "";
            public string Label = "";
            public List<PlexItem> Items = new();
        }

        public ObservableCollection<Bookmark> PlaylistItems { get; } = new();
        public ObservableCollection<HistoryEntry> HistoryItems { get; } = new();
        public ObservableCollection<PlexItem> PlexItems { get; } = new();

        // Podcasts — the results box shows either shows (search results) or the episodes of
        // a selected show; both models expose DisplayTitle/DisplaySubtitle so one template fits.
        public ObservableCollection<object> PodcastItems { get; } = new();
        private readonly PodcastService _podcasts = new();
        private bool _podcastViewingEpisodes;   // false = show list, true = episode list
        private double _playbackSpeed = 1.0;

        public ICommand OpenUrlCommand  { get; }
        public ICommand PlayPauseCommand { get; }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // Group the Plex category dropdown into "Views" and "Genres" sections.
            var catView = new CollectionViewSource { Source = _plexCategories };
            catView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PlexCategory.Group)));
            PlexCategoryCombo.ItemsSource = catView.View;

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
            menuItemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(0xE7, 0xE9, 0xF1))));
            menuItemStyle.Setters.Add(new Setter(MenuItem.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x26))));

            _playlistContextMenu = new ContextMenu
            {
                Background   = new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x26)),
                BorderBrush  = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x42)),
                BorderThickness = new Thickness(1),
                ItemContainerStyle = menuItemStyle,
            };
            _playlistContextMenu.Items.Add(_toggleMenuItem);
            _playlistContextMenu.Items.Add(deleteMenuItem);
            _playlistContextMenu.Opened += PlaylistContextMenu_Opened;

            PlaylistBox.ContextMenu = _playlistContextMenu;
            PlaylistBox.PreviewMouseRightButtonDown += PlaylistBox_PreviewMouseRightButtonDown;

            // Video right-click menu (styled in XAML resources).
            _videoContextMenu = (ContextMenu)FindResource("VideoContextMenu");

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

                // Self-heal a stale binary in the background (non-blocking, best-effort) so
                // the "your yt-dlp is older than 90 days" warning never resurfaces.
                _ = MaybeUpdateYtDlpAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to initialize yt-dlp: {ex.Message}";
            }
        }

        // yt-dlp is refreshed in the background when the local copy is older than this.
        private static readonly TimeSpan YtDlpMaxAge = TimeSpan.FromDays(30);

        /// <summary>
        /// If the local yt-dlp is older than <see cref="YtDlpMaxAge"/>, run <c>yt-dlp -U</c>
        /// in the background. Never blocks startup or playback; failures are logged and
        /// ignored. After a check we stamp the file's timestamp so we don't re-check every
        /// launch — only once per <see cref="YtDlpMaxAge"/> window until the next release.
        /// </summary>
        private async Task MaybeUpdateYtDlpAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_ytdlpPath) || !File.Exists(_ytdlpPath)) return;

                var age = DateTime.Now - File.GetLastWriteTime(_ytdlpPath);
                if (age < YtDlpMaxAge) return;

                App.Log($"[yt-dlp] Local copy is {age.Days}d old — updating in background.");

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName               = _ytdlpPath,
                        Arguments              = "-U",
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                    }
                };
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                App.Log($"[yt-dlp] Update finished: {output.Trim().Replace("\r\n", " | ")}");

                // yt-dlp -U may leave the old mtime (or report "up to date" without a
                // rewrite); stamp it so the 30-day check resets either way.
                try { File.SetLastWriteTime(_ytdlpPath, DateTime.Now); } catch { }
            }
            catch (Exception ex)
            {
                App.Log($"[yt-dlp] Background update failed: {ex.Message}");
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
                    FlyleafPlayer.Overlay.MouseLeftButtonDown  += VideoArea_MouseLeftButtonDown;
                    FlyleafPlayer.Overlay.MouseMove            += VideoArea_MouseMove;
                    FlyleafPlayer.Overlay.MouseRightButtonUp   += VideoArea_MouseRightButtonUp;
                    FlyleafPlayer.Overlay.MouseWheel           += VideoArea_MouseWheel;
                    FlyleafPlayer.Overlay.AllowDrop = true;
                    FlyleafPlayer.Overlay.DragOver += VideoArea_DragOver;
                    FlyleafPlayer.Overlay.Drop     += VideoArea_Drop;
                };
                FlyleafPlayer.SurfaceCreated += (ps, pe) =>
                {
                    App.Log("[Flyleaf] Surface created, hooking mouse + drop events");
                    FlyleafPlayer.Surface.MouseLeftButtonDown  += VideoArea_MouseLeftButtonDown;
                    FlyleafPlayer.Surface.MouseMove            += VideoArea_MouseMove;
                    FlyleafPlayer.Surface.MouseRightButtonUp   += VideoArea_MouseRightButtonUp;
                    FlyleafPlayer.Surface.MouseWheel           += VideoArea_MouseWheel;
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

            // For split-stream sources, attach the parked audio URL as an external stream
            // once the main (video) open completes. Best-effort: if the attach fails the
            // video still plays (no worse than before), so we only log.
            _player.OpenCompleted += (s, e) => Dispatcher.InvokeAsync(() =>
            {
                var audio = _pendingExternalAudioUrl;
                _pendingExternalAudioUrl = null;
                if (string.IsNullOrEmpty(audio)) return;
                try
                {
                    // Non-blocking; the audio decoder syncs to the already-open video.
                    _player.OpenAsync(new ExternalAudioStream { Url = audio });
                    App.Log("[Flyleaf] External audio stream attaching.");
                }
                catch (Exception ex)
                {
                    App.Log($"[Flyleaf] External audio attach failed: {ex.Message}");
                }
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
                                if (_activePlex != null && _plex != null)
                                {
                                    // Report final position so Plex marks it watched/resumable.
                                    _ = _plex.ReportTimelineAsync(_activePlex, "stopped", _player.Duration / 10000L);
                                }
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

            _services.Load();
            _plex = new PlexService(_services);
            UpdatePlexTabState();

            await LoadSettingsAsync();
            CookiesMenuItem.IsChecked = _settings.UseBrowserCookies;
            ApplyPlexViewMode();   // restore the remembered Plex list/tile view

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

            // Remote Control API (F-654) — additive local control surface. See MainWindow.Api.cs.
            InitRemoteControl();
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
                var items = await _api.GetBookmarksAsync(workspaceId);
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

        private void ShowPlaylistTab_Click(object sender, RoutedEventArgs e) => SelectTab(SidebarTab.Playlist);
        private void ShowHistoryTab_Click(object sender, RoutedEventArgs e)  => SelectTab(SidebarTab.History);
        private void ShowPlexTab_Click(object sender, RoutedEventArgs e)     => SelectTab(SidebarTab.Plex);
        private void ShowPodcastsTab_Click(object sender, RoutedEventArgs e) => SelectTab(SidebarTab.Podcasts);

        private void SelectTab(SidebarTab tab)
        {
            _activeTab = tab;

            // Leaving Plex? Drop the full-screen browse takeover (and resume playback).
            if (_plexFullscreen && tab != SidebarTab.Plex)
                ExitPlexFullscreen(resumeVideo: true, reapply: true);

            PlaylistContent.Visibility = tab == SidebarTab.Playlist ? Visibility.Visible : Visibility.Collapsed;
            HistoryContent.Visibility  = tab == SidebarTab.History  ? Visibility.Visible : Visibility.Collapsed;
            PlexContent.Visibility     = tab == SidebarTab.Plex     ? Visibility.Visible : Visibility.Collapsed;
            PodcastContent.Visibility  = tab == SidebarTab.Podcasts ? Visibility.Visible : Visibility.Collapsed;

            // Underline tabs: active reads bright with an accent underline, inactive dims.
            var accent  = (Brush)FindResource("AccentBrush");
            var primary = (Brush)FindResource("TextPrimaryBrush");
            var muted   = (Brush)FindResource("TextMutedBrush");

            StyleTab(PlaylistTabButton, tab == SidebarTab.Playlist, accent, primary, muted);
            StyleTab(HistoryTabButton,  tab == SidebarTab.History,  accent, primary, muted);
            StyleTab(PlexTabButton,     tab == SidebarTab.Plex,     accent, primary, muted);
            StyleTab(PodcastsTabButton, tab == SidebarTab.Podcasts, accent, primary, muted);

            if (tab == SidebarTab.History) RefreshHistoryView();
            if (tab == SidebarTab.Plex)    EnterPlexTab();
        }

        private static void StyleTab(Button btn, bool active, Brush accent, Brush primary, Brush muted)
        {
            btn.Foreground  = active ? primary : muted;
            btn.BorderBrush = active ? accent  : Brushes.Transparent;
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

                // Plex history entries are stored as plex://<ratingKey> — they can't go
                // through yt-dlp; re-resolve them against the Plex server instead.
                if (entry.Url.StartsWith("plex://", StringComparison.OrdinalIgnoreCase))
                {
                    await PlayPlexFromHistory(entry.Url);
                    return;
                }

                await PlayUrl(entry.Url);
            }
        }

        /// <summary>Replay a Plex item recorded in local History (keyed by ratingKey).</summary>
        private async Task PlayPlexFromHistory(string plexUrl)
        {
            if (_plex?.IsConfigured != true)
            {
                MessageBox.Show("Connect a Plex server (⚙ Services) to play this item.",
                    "Plex", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var ratingKey = plexUrl.Substring("plex://".Length);
            var item = await _plex.GetItemAsync(ratingKey);
            if (item == null)
            {
                MessageBox.Show("This Plex item is no longer available on the server.",
                    "Plex", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PlayPlexItem(item);
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
        // Services (gear) + Plex
        // ──────────────────────────────────────────────────────

        private void OpenServices_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ServicesSettingsDialog(_services) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                // Config changed — rebuild the client and reload the Plex tab from scratch.
                _plex = new PlexService(_services);
                _plexSectionsLoaded = false;
                _plexGenreCache.Clear();
                if (_activeTab == SidebarTab.Plex) EnterPlexTab();
                else UpdatePlexTabState();
            }
        }

        /// <summary>Shows the configure/empty/results state for the Plex tab.</summary>
        private void UpdatePlexTabState()
        {
            if (PlexStatusText == null) return; // called before the UI is ready

            if (_services?.IsPlexConfigured != true)
            {
                PlexItems.Clear();
                PlexLibraryBar.Visibility   = Visibility.Collapsed;
                PlexCategoryCombo.Visibility = Visibility.Collapsed;
                PlexBreadcrumbBar.Visibility = Visibility.Collapsed;
                if (PlexViewToolbar != null) PlexViewToolbar.Visibility = Visibility.Collapsed;
                PlexStatusText.Text = "No Plex server configured. Open Services (⚙) to add one.";
                PlexStatusText.Visibility = Visibility.Visible;
                return;
            }

            PlexLibraryBar.Visibility = Visibility.Visible;
            // The list/tile toggle stays available whenever there's something to look at.
            if (PlexViewToolbar != null)
                PlexViewToolbar.Visibility = PlexItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (PlexItems.Count == 0)
            {
                PlexStatusText.Text = _plexSearchMode
                    ? (string.IsNullOrWhiteSpace(PlexSearchBox?.Text)
                        ? "Search your Plex libraries above."
                        : "No results.")
                    : "Nothing to show here.";
                PlexStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                PlexStatusText.Visibility = Visibility.Collapsed;
            }
        }

        // ──────────────────────────────────────────────────────
        // Plex view mode: compact list ⇄ poster tiles (remembered)
        // ──────────────────────────────────────────────────────

        private void PlexListView_Click(object sender, RoutedEventArgs e) => SetPlexViewMode(tiles: false);
        private void PlexTileView_Click(object sender, RoutedEventArgs e) => SetPlexViewMode(tiles: true);
        private void PlexFullscreenView_Click(object sender, RoutedEventArgs e) => TogglePlexFullscreen();

        /// <summary>Pick the compact list or poster tiles (and leave full-screen browse if active).</summary>
        private void SetPlexViewMode(bool tiles)
        {
            if (_plexFullscreen) ExitPlexFullscreen(resumeVideo: true, reapply: false);
            if (_settings.PlexTileView != tiles)
            {
                _settings.PlexTileView = tiles;
                SaveSettings();
            }
            ApplyPlexViewMode();
        }

        /// <summary>Swaps the results ListBox between the tile grid and the compact list, and
        /// reflects the active mode on the toolbar buttons. Safe to call before data loads.
        /// Full-screen browse always renders as tiles.</summary>
        private void ApplyPlexViewMode()
        {
            if (PlexResultsBox == null) return;

            bool tiles = _settings.PlexTileView || _plexFullscreen;
            PlexResultsBox.ItemTemplate = (DataTemplate)FindResource(
                tiles ? "PlexTileItemTemplate" : "PlexListItemTemplate");
            PlexResultsBox.ItemsPanel = (ItemsPanelTemplate)FindResource(
                tiles ? "PlexTilePanel" : "PlexListPanel");

            // Highlight exactly one of the three toolbar buttons.
            StylePlexViewButton(PlexListViewBtn,       !_settings.PlexTileView && !_plexFullscreen);
            StylePlexViewButton(PlexTileViewBtn,        _settings.PlexTileView && !_plexFullscreen);
            StylePlexViewButton(PlexFullscreenViewBtn,  _plexFullscreen);
        }

        private static void StylePlexViewButton(Button b, bool active)
        {
            if (b == null) return;
            b.Foreground = active ? SegActiveFg : SegInactiveFg;
            b.Background = active ? SegActiveBg : SegInactiveBg;
        }

        // ──────────────────────────────────────────────────────
        // Full-screen browse: the Plex panel takes over the video
        // area so posters fill the window. Playback pauses while it's
        // up and resumes when we collapse back down.
        // ──────────────────────────────────────────────────────

        private void TogglePlexFullscreen()
        {
            if (_plexFullscreen) ExitPlexFullscreen(resumeVideo: true, reapply: true);
            else                 EnterPlexFullscreen();
        }

        private void EnterPlexFullscreen()
        {
            if (_plexFullscreen) return;
            _plexFullscreen = true;

            // Pause the video (remember whether it was actually playing).
            _plexResumeAfterFullscreen = _player?.Status == Status.Playing;
            if (_plexResumeAfterFullscreen)
            {
                if (_activePlex != null && _plex != null)
                    _ = _plex.ReportTimelineAsync(_activePlex, "paused", _player.CurTime / 10000L);
                _player.Pause();
            }

            // Expand the side panel across the (now-hidden) video column.
            VideoColumn.Width     = new GridLength(0);
            PanelColumn.Width     = new GridLength(1, GridUnitType.Star);
            SidePanelColumn.Width = new GridLength(1, GridUnitType.Star);
            SidePanel.Width       = double.NaN;   // stretch to fill the star column
            PlaylistTab.Visibility = Visibility.Collapsed;

            ApplyPlexViewMode();
        }

        private void ExitPlexFullscreen(bool resumeVideo, bool reapply)
        {
            if (!_plexFullscreen) return;
            _plexFullscreen = false;

            // Restore the split layout.
            VideoColumn.Width     = new GridLength(1, GridUnitType.Star);
            PanelColumn.Width     = GridLength.Auto;
            SidePanelColumn.Width = GridLength.Auto;
            SidePanel.Width       = 325;
            PlaylistTab.Visibility = Visibility.Visible;

            if (resumeVideo && _plexResumeAfterFullscreen && _player != null)
            {
                if (_activePlex != null && _plex != null)
                    _ = _plex.ReportTimelineAsync(_activePlex, "playing", _player.CurTime / 10000L);
                _player.Play();
            }
            _plexResumeAfterFullscreen = false;

            if (reapply) ApplyPlexViewMode();
        }

        // ──────────────────────────────────────────────────────
        // Plex browse: libraries → category → drill-down
        // ──────────────────────────────────────────────────────

        /// <summary>Entered whenever the Plex tab becomes active; loads libraries once.</summary>
        private async void EnterPlexTab()
        {
            if (_services?.IsPlexConfigured != true) { UpdatePlexTabState(); return; }
            if (_plexSectionsLoaded) { UpdatePlexTabState(); return; }
            await LoadPlexSections();
        }

        private async Task LoadPlexSections()
        {
            _plexSectionsLoaded = true;
            try   { _plexSections = await _plex.GetVideoSectionsAsync(); }
            catch { _plexSections = new(); }

            BuildPlexLibraryBar();

            // Default to the first movie library, else the first video library, else search-only.
            var first = _plexSections.FirstOrDefault(s => s.Type == "movie") ?? _plexSections.FirstOrDefault();
            if (first != null) await SelectPlexLibrary(first);
            else               SelectPlexSearchMode();
        }

        private void BuildPlexLibraryBar()
        {
            PlexLibraryBar.Children.Clear();
            foreach (var s in _plexSections)
                PlexLibraryBar.Children.Add(MakeSegment(s.Title, s));
            // Trailing "Search" segment preserves the global cross-library hub search.
            PlexLibraryBar.Children.Add(MakeSegment("Search", null));
            PlexLibraryBar.Visibility = Visibility.Visible;
        }

        private Button MakeSegment(string label, PlexSection tag)
        {
            var b = new Button
            {
                Content = label,
                Tag     = (object)tag ?? "__search__",
                Style   = (Style)FindResource("PlexSegment"),
            };
            b.Click += PlexSegment_Click;
            return b;
        }

        private async void PlexSegment_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b) return;
            if (b.Tag is PlexSection s) await SelectPlexLibrary(s);
            else                        SelectPlexSearchMode();
        }

        private void StylePlexSegments()
        {
            foreach (var b in PlexLibraryBar.Children.OfType<Button>())
            {
                bool active = b.Tag is PlexSection s
                    ? (!_plexSearchMode && _plexSection != null && s.Key == _plexSection.Key)
                    : _plexSearchMode;
                b.Background = active ? SegActiveBg   : SegInactiveBg;
                b.Foreground = active ? SegActiveFg   : SegInactiveFg;
            }
        }

        /// <summary>Switch to a video library: show its categories and browse the default view.</summary>
        private async Task SelectPlexLibrary(PlexSection section)
        {
            _plexSearchMode = false;
            _plexSection    = section;
            _plexDrill.Clear();

            PlexCategoryCombo.Visibility = Visibility.Visible;
            PlexSearchPlaceholder.Text   = "Filter titles…";
            PlexSearchBox.Text           = "";   // reset the narrow filter
            StylePlexSegments();
            RebuildBreadcrumb();

            await PopulatePlexCategories(section);   // selecting a category triggers the browse
        }

        /// <summary>Switch to the global search segment: text box hits /hubs/search across all libraries.</summary>
        private void SelectPlexSearchMode()
        {
            _plexSearchMode = true;
            _plexSection    = null;
            _plexCategory   = null;
            _plexDrill.Clear();

            PlexCategoryCombo.Visibility = Visibility.Collapsed;
            PlexBreadcrumbBar.Visibility = Visibility.Collapsed;
            PlexSearchPlaceholder.Text   = "Search Plex…";
            PlexSearchBox.Text           = "";
            _plexBrowseItems  = new();
            _plexCurrentItems = new();
            PlexItems.Clear();
            StylePlexSegments();
            UpdatePlexTabState();
            PlexSearchBox.Focus();
        }

        private async Task PopulatePlexCategories(PlexSection section)
        {
            _plexCategories.Clear();
            _plexCategories.Add(new PlexCategory("All",              PlexBrowseView.All,            "Views"));
            _plexCategories.Add(new PlexCategory("Recently Added",   PlexBrowseView.RecentlyAdded,  "Views"));
            _plexCategories.Add(new PlexCategory("Recently Watched", PlexBrowseView.RecentlyWatched,"Views"));
            _plexCategories.Add(new PlexCategory("Never Watched",    PlexBrowseView.NeverWatched,   "Views"));

            if (!_plexGenreCache.TryGetValue(section.Key, out var genres))
            {
                genres = await _plex.GetGenresAsync(section.Key);
                _plexGenreCache[section.Key] = genres;
                // A different library may have been picked while we awaited — bail if so.
                if (_plexSearchMode || _plexSection?.Key != section.Key) return;
            }
            foreach (var g in genres)
                _plexCategories.Add(new PlexCategory(g.Title, PlexBrowseView.Genre, "Genres") { GenreId = g.Id });

            PlexCategoryCombo.SelectedIndex = 0;   // "All" → fires PlexCategory_SelectionChanged
        }

        private async void PlexCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlexCategoryCombo.SelectedItem is not PlexCategory cat) return;
            _plexCategory = cat;
            await LoadPlexBrowse();
        }

        private async Task LoadPlexBrowse()
        {
            if (_plexSection == null || _plexCategory == null) return;

            _plexDrill.Clear();
            RebuildBreadcrumb();

            int token = ++_plexLoadToken;
            PlexStatusText.Text = "Loading…";
            PlexStatusText.Visibility = Visibility.Visible;

            List<PlexItem> items;
            try
            {
                items = await _plex.BrowseAsync(
                    _plexSection.Key, _plexSection.Type, _plexCategory.View, _plexCategory.GenreId);
            }
            catch (Exception ex)
            {
                App.Log($"[Plex] Browse failed: {ex.Message}");
                items = new();
            }

            if (token != _plexLoadToken) return;   // a newer load superseded this one

            _plexBrowseItems  = items;
            _plexCurrentItems = items;
            ApplyPlexNarrow();
        }

        /// <summary>Client-side narrowing of the current list by the filter box (title/subtitle contains).</summary>
        private void ApplyPlexNarrow()
        {
            var q = _plexSearchMode ? "" : (PlexSearchBox?.Text ?? "").Trim();
            PlexItems.Clear();
            foreach (var it in _plexCurrentItems)
            {
                if (q.Length == 0
                    || it.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || it.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    PlexItems.Add(it);
                }
            }
            UpdatePlexTabState();
        }

        /// <summary>Drill from a show into its seasons, or a season into its episodes.</summary>
        private async Task PlexDrillInto(PlexItem container)
        {
            int token = ++_plexLoadToken;
            PlexStatusText.Text = "Loading…";
            PlexStatusText.Visibility = Visibility.Visible;

            List<PlexItem> kids;
            try   { kids = await _plex.GetChildrenAsync(container.RatingKey); }
            catch { kids = new(); }

            if (token != _plexLoadToken) return;

            _plexDrill.Add(new PlexDrillFrame
            {
                RatingKey = container.RatingKey,
                Label     = container.Title,
                Items     = kids,
            });
            _plexCurrentItems = kids;
            RebuildBreadcrumb();
            ApplyPlexNarrow();
        }

        /// <summary>Rebuilds the breadcrumb row (Section › Show › Season) with clickable hops.</summary>
        private void RebuildBreadcrumb()
        {
            PlexBreadcrumbBar.Children.Clear();

            if (_plexSearchMode || _plexDrill.Count == 0)
            {
                PlexBreadcrumbBar.Visibility = Visibility.Collapsed;
                return;
            }

            // Root crumb → back to the browse (level-0) list.
            AddCrumb(_plexSection?.Title ?? "Library", isCurrent: false, () =>
            {
                _plexDrill.Clear();
                _plexCurrentItems = _plexBrowseItems;
                RebuildBreadcrumb();
                ApplyPlexNarrow();
            });

            for (int i = 0; i < _plexDrill.Count; i++)
            {
                AddSeparator();
                int index = i;
                bool isCurrent = i == _plexDrill.Count - 1;
                AddCrumb(_plexDrill[i].Label, isCurrent, isCurrent ? null : () => PlexPopTo(index));
            }

            PlexBreadcrumbBar.Visibility = Visibility.Visible;
        }

        private void PlexPopTo(int frameIndex)
        {
            var frame = _plexDrill[frameIndex];
            _plexDrill.RemoveRange(frameIndex + 1, _plexDrill.Count - frameIndex - 1);
            _plexCurrentItems = frame.Items;
            RebuildBreadcrumb();
            ApplyPlexNarrow();
        }

        private void AddCrumb(string text, bool isCurrent, Action onClick)
        {
            if (isCurrent || onClick == null)
            {
                PlexBreadcrumbBar.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                return;
            }

            var b = new Button
            {
                Content = text,
                Style   = (Style)FindResource("TextButton"),
                FontSize = 12,
                Padding = new Thickness(0),
            };
            b.Click += (_, __) => onClick();
            PlexBreadcrumbBar.Children.Add(b);
        }

        private void AddSeparator()
        {
            PlexBreadcrumbBar.Children.Add(new TextBlock
            {
                Text = " › ",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x5C, 0x6D)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        private void PlexSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_plexSearchMode)
            {
                // Global search: debounce keystrokes so we don't hammer the server.
                _plexSearchDebounce ??= CreatePlexDebounce();
                _plexSearchDebounce.Stop();
                _plexSearchDebounce.Start();
            }
            else
            {
                // Browse: narrow the already-loaded list client-side (instant, no round trip).
                ApplyPlexNarrow();
            }
        }

        private DispatcherTimer CreatePlexDebounce()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            t.Tick += async (s, _) =>
            {
                t.Stop();
                await RunPlexSearch(PlexSearchBox.Text);
            };
            return t;
        }

        private async Task RunPlexSearch(string query)
        {
            if (_services?.IsPlexConfigured != true)
            {
                UpdatePlexTabState();
                return;
            }

            query = query?.Trim() ?? "";
            if (string.IsNullOrEmpty(query))
            {
                PlexItems.Clear();
                UpdatePlexTabState();
                return;
            }

            PlexStatusText.Text = "Searching…";
            PlexStatusText.Visibility = Visibility.Visible;

            try
            {
                var results = await _plex.SearchAsync(query);

                // Ignore stale responses if the box has moved on since we fired.
                if (PlexSearchBox.Text.Trim() != query) return;

                PlexItems.Clear();
                foreach (var item in results)
                    PlexItems.Add(item);
            }
            catch (Exception ex)
            {
                App.Log($"[Plex] Search failed: {ex.Message}");
                PlexItems.Clear();
            }

            UpdatePlexTabState();
        }

        private async void PlexResultsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            if (PlexResultsBox.SelectedItem is not PlexItem item) return;

            // Shows/seasons drill in; leaves play.
            if (item.IsContainer) await PlexDrillInto(item);
            else                  PlayPlexItem(item);
        }

        private void PlayPlexItem(PlexItem item)
        {
            var streamUrl = _plex?.ResolveStreamUrl(item);
            if (string.IsNullOrEmpty(streamUrl))
            {
                MessageBox.Show("This Plex item has no playable file.", "Plex",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Report any in-progress Plex playback as stopped before switching items.
            if (_activePlex != null && _player?.Status == Status.Playing)
                _ = _plex.ReportTimelineAsync(_activePlex, "stopped", _player.CurTime / 10000L);

            // Plex is a separate ecosystem — detach from the bookmarks position-save path.
            _activeMuid = null;
            _activePlex = item;
            _plexTimelineTick = 0;

            // Resume where Plex left off, using the shared seek-on-play mechanism.
            _seekOnPlay = item.HasResume ? item.ViewOffsetMs : null;

            try
            {
                StatusText.Text = "Loading video...";
                StatusText.Visibility = Visibility.Visible;

                _player.Stop();

                _currentUrl = streamUrl;
                Title = $"Video Player v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3)} - {item.Title}";

                // Local history (Plex items keyed by a stable plex:// id, not the tokenized URL).
                _history.Record($"plex://{item.RatingKey}", item.Title);
                if (HistoryContent.Visibility == Visibility.Visible)
                    RefreshHistoryView();

                App.Log($"[Plex] Opening ratingKey={item.RatingKey} resume={item.ViewOffsetMs}ms");
                _player.OpenAsync(streamUrl);
                _ = _plex.ReportTimelineAsync(item, "playing", item.ViewOffsetMs);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error loading video";
                MessageBox.Show($"Error loading Plex video: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ──────────────────────────────────────────────────────
        // Podcasts (Apple Podcasts search → episode list → audio playback)
        // ──────────────────────────────────────────────────────

        private List<PodcastShow> _podcastShowResults = new();

        private async void PodcastSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RunPodcastSearch(PodcastSearchBox.Text);
        }

        private async Task RunPodcastSearch(string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return;

            _podcastViewingEpisodes = false;
            PodcastBackButton.Visibility = Visibility.Collapsed;
            PodcastStatusText.Visibility = Visibility.Visible;
            PodcastStatusText.Text = "Searching…";
            PodcastItems.Clear();

            try
            {
                _podcastShowResults = await _podcasts.SearchAsync(term);
                ShowPodcastShowResults();
            }
            catch (Exception ex)
            {
                PodcastStatusText.Text = $"Search failed: {ex.Message}";
            }
        }

        private void ShowPodcastShowResults()
        {
            _podcastViewingEpisodes = false;
            PodcastBackButton.Visibility = Visibility.Collapsed;
            PodcastItems.Clear();
            foreach (var show in _podcastShowResults)
                PodcastItems.Add(show);

            if (PodcastItems.Count == 0)
            {
                PodcastStatusText.Visibility = Visibility.Visible;
                PodcastStatusText.Text = "No podcasts found.";
            }
            else
            {
                PodcastStatusText.Visibility = Visibility.Collapsed;
            }
        }

        private async void PodcastResultsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            var selected = PodcastResultsBox.SelectedItem;

            if (selected is PodcastShow show)
                await LoadPodcastEpisodes(show);
            else if (selected is PodcastEpisode episode)
            {
                _activeMuid = null;      // podcasts aren't bookmarks — don't save position server-side
                _seekOnPlay = null;
                await PlayUrl(episode.AudioUrl, episode.Title, forceDirect: true);
            }
        }

        private async Task LoadPodcastEpisodes(PodcastShow show)
        {
            PodcastStatusText.Visibility = Visibility.Visible;
            PodcastStatusText.Text = $"Loading “{show.Title}”…";
            PodcastItems.Clear();

            try
            {
                var episodes = await _podcasts.GetEpisodesAsync(show);
                _podcastViewingEpisodes = true;
                PodcastBackButton.Visibility = Visibility.Visible;
                foreach (var ep in episodes)
                    PodcastItems.Add(ep);

                PodcastStatusText.Visibility = episodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (episodes.Count == 0)
                    PodcastStatusText.Text = "No playable episodes in this feed.";
            }
            catch (Exception ex)
            {
                PodcastStatusText.Visibility = Visibility.Visible;
                PodcastStatusText.Text = $"Could not load episodes: {ex.Message}";
            }
        }

        private void PodcastBack_Click(object sender, RoutedEventArgs e) => ShowPodcastShowResults();

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

            await PlayUrl(bookmark.Url);
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

            // Report Plex playback progress every 10 seconds (separate ecosystem).
            if (_activePlex != null && _plex != null)
            {
                _plexTimelineTick++;
                if (_plexTimelineTick >= 20)
                {
                    _plexTimelineTick = 0;
                    _ = _plex.ReportTimelineAsync(_activePlex, "playing", (long)time);
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
                await PlayUrl(dialog.Url);
            }
        }

        private void ToggleBrowserCookies_Click(object sender, RoutedEventArgs e)
        {
            _settings.UseBrowserCookies = CookiesMenuItem.IsChecked;
            SaveSettings();
        }

        // Direct-media / local file extensions that FFmpeg can open without yt-dlp.
        private static readonly string[] DirectMediaExtensions =
        {
            ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".avi", ".flv", ".wmv", ".ts",
            ".m3u8", ".mpd", ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".oga",
            ".opus", ".wma"
        };

        /// <summary>
        /// Plays any media URL. Local files and direct media URLs open straight through
        /// FFmpeg; every other http(s) page URL is resolved with yt-dlp, which supports
        /// ~1,800 sites (YouTube, Reddit, Facebook, TikTok, Vimeo, ...). Split-stream
        /// sources (separate video+audio) are recombined via <see cref="_pendingExternalAudioUrl"/>.
        /// </summary>
        private async Task PlayUrl(string url, string displayTitle = null, bool forceDirect = false)
        {
            try
            {
                // Leaving the Plex ecosystem — report stop and detach so Timer_Tick
                // stops sending Plex timeline updates for this playback.
                if (_activePlex != null && _player?.Status == Status.Playing)
                    _ = _plex.ReportTimelineAsync(_activePlex, "stopped", _player.CurTime / 10000L);
                _activePlex = null;

                StatusText.Text = "Loading video...";
                StatusText.Visibility = Visibility.Visible;

                App.Log($"[Flyleaf] Stop requested. Status={_player.Status}");
                _player.Stop();
                _pendingExternalAudioUrl = null;
                App.Log("[Flyleaf] Stop() returned");

                _currentUrl = url;

                // Fast path: local files, direct media URLs, and caller-forced direct playback
                // (e.g. podcast audio enclosures, whose URLs aren't always extension-suffixed).
                if (forceDirect || IsDirectlyPlayable(url))
                {
                    var directTitle = displayTitle ?? DeriveTitleFromUrl(url);
                    SetWindowTitle(directTitle);
                    _history.Record(url, directTitle);
                    if (HistoryContent.Visibility == Visibility.Visible)
                        RefreshHistoryView();

                    App.Log("[Flyleaf] Opening direct media (no yt-dlp).");
                    _player.OpenAsync(url);
                    return;
                }

                if (!_ytdlpReady)
                {
                    MessageBox.Show("yt-dlp is not ready yet. Please wait.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var (title, videoUrl, audioUrl) = await ResolveWithYtDlpAsync(url);

                if (string.IsNullOrEmpty(videoUrl))
                    throw new Exception("Could not resolve a playable stream for this URL.");

                SetWindowTitle(title);

                // Record to local-only history (de-duped by URL, most-recent first).
                _history.Record(url, title);
                if (HistoryContent.Visibility == Visibility.Visible)
                    RefreshHistoryView();

                // A second URL means yt-dlp split video from audio (e.g. Reddit) — park the
                // audio so OpenCompleted attaches it once the video is open.
                _pendingExternalAudioUrl = string.IsNullOrEmpty(audioUrl) ? null : audioUrl;

                App.Log($"[Flyleaf] Opening stream. audio={(audioUrl != null ? "external" : "muxed")} Status={_player.Status}");
                _player.OpenAsync(videoUrl);
                App.Log("[Flyleaf] OpenAsync() called");
            }
            catch (Exception ex)
            {
                _pendingExternalAudioUrl = null;
                StatusText.Text = "Error loading video";
                StatusText.Visibility = Visibility.Visible;
                MessageBox.Show($"Error loading video: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Resolves a page URL to a stream via yt-dlp. Returns the title, the video URL, and
        /// (only when the source has no muxed rendition) a separate audio URL. The format
        /// selector prefers a single muxed stream so common sources keep their existing
        /// single-URL behaviour and only Reddit-style split sources use the external-audio path.
        /// </summary>
        private async Task<(string title, string videoUrl, string audioUrl)> ResolveWithYtDlpAsync(string url)
        {
            var fmt = $"best[height<={_selectedHeight}]/bv*[height<={_selectedHeight}]+ba/best";

            var titleTask  = RunYtDlp("--no-warnings --print \"%(title)s\"", url);
            var streamTask = RunYtDlp($"--no-warnings -f \"{fmt}\" -g", url);
            await Task.WhenAll(titleTask, streamTask);

            var title = FirstLine(titleTask.Result);

            var urls = (streamTask.Result ?? "")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var videoUrl = urls.Count > 0 ? urls[0] : null;
            var audioUrl = urls.Count > 1 ? urls[1] : null;
            return (title, videoUrl, audioUrl);
        }

        private async Task<string> RunYtDlp(string arguments, string url)
        {
            // When enabled, borrow the user's browser session so private / logged-in content
            // (e.g. a friends-only Facebook video) can be resolved.
            var cookies = _settings.UseBrowserCookies && !string.IsNullOrWhiteSpace(_settings.CookieBrowser)
                ? $"--cookies-from-browser {_settings.CookieBrowser} "
                : "";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ytdlpPath,
                    Arguments = $"{cookies}{arguments} \"{url}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error  = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
            {
                throw new Exception(CleanYtDlpError(error));
            }

            return output;   // full stdout; callers parse the lines they need
        }

        private static string FirstLine(string text) =>
            string.IsNullOrEmpty(text) ? null
                : text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

        /// <summary>Turns yt-dlp's multi-line stderr into a single readable sentence.</summary>
        private static string CleanYtDlpError(string error)
        {
            var line = error.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                ?? FirstLine(error) ?? "yt-dlp could not extract a video from this URL.";
            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                line = line.Substring("ERROR:".Length).Trim();
            return line;
        }

        private static bool IsDirectlyPlayable(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            url = url.Trim();

            try { if (File.Exists(url)) return true; } catch { }

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile) return true;
                if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                {
                    var path = uri.AbsolutePath;
                    foreach (var ext in DirectMediaExtensions)
                        if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                            return true;
                }
            }
            return false;
        }

        private static string DeriveTitleFromUrl(string url)
        {
            try
            {
                if (File.Exists(url)) return Path.GetFileNameWithoutExtension(url);
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var name = Path.GetFileName(uri.IsFile ? uri.LocalPath : uri.AbsolutePath);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }
            return null;
        }

        private void SetWindowTitle(string mediaTitle)
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Title = string.IsNullOrEmpty(mediaTitle)
                ? $"Video Player v{v}"
                : $"Video Player v{v} - {mediaTitle}";
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
                if (_activePlex != null && _plex != null)
                    _ = _plex.ReportTimelineAsync(_activePlex, "paused", _player.CurTime / 10000L);
                _player.Pause();
            }
            else
            {
                if (_activePlex != null && _plex != null)
                    _ = _plex.ReportTimelineAsync(_activePlex, "playing", _player.CurTime / 10000L);
                _player.Play();
            }
        }

        private static readonly double[] SpeedSteps = { 1.0, 1.25, 1.5, 1.75, 2.0 };

        private void SpeedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_player == null) return;

            var idx = Array.IndexOf(SpeedSteps, _playbackSpeed);
            _playbackSpeed = SpeedSteps[(idx + 1) % SpeedSteps.Length];
            _player.Speed = _playbackSpeed;

            // "1×", "1.25×", …
            SpeedButton.Content = (_playbackSpeed % 1 == 0 ? _playbackSpeed.ToString("0") : _playbackSpeed.ToString("0.##")) + "×";
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
            ShutdownRemoteControl();

            // Best-effort position save on close
            if (_activeMuid != null && _api != null && _player?.Status == Status.Playing)
            {
                var seconds = (int)(_player.CurTime / 10000000.0);
                _ = _api.SavePositionAsync(_activeMuid, seconds);
            }

            // Best-effort Plex progress report on close
            if (_activePlex != null && _plex != null && _player?.Status == Status.Playing)
                _ = _plex.ReportTimelineAsync(_activePlex, "stopped", _player.CurTime / 10000L);

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
                App.Log($"[DragOver] {where} formats=[{formats}] text=\"{text}\" isYT={IsVideoUrl(text)}");
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
                if (IsVideoUrl(text))
                    e.Effects = DragDropEffects.Copy;
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = e.Data.GetData(DataFormats.UnicodeText) as string;
                if (IsVideoUrl(text))
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
                url = ExtractFirstUrl(url);
                if (!string.IsNullOrEmpty(url))
                {
                    _activeMuid = null;
                    _seekOnPlay = null;
                    await PlayUrl(url);
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
                if (IsVideoUrl(text))
                    e.Effects = DragDropEffects.Copy;
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = e.Data.GetData(DataFormats.UnicodeText) as string;
                if (IsVideoUrl(text))
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
            url = ExtractFirstUrl(url);
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

        /// <summary>
        /// True for anything we can attempt to play: any http(s) URL (yt-dlp decides whether
        /// it can extract a video) or a local/direct media file. No longer YouTube-specific.
        /// </summary>
        private bool IsVideoUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || IsDirectlyPlayable(text);
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

        /// <summary>
        /// Pulls the first http(s) URL out of dragged/dropped text (which often carries
        /// extra label text). Falls back to the trimmed text so a bare local path still works.
        /// </summary>
        private string ExtractFirstUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var match = Regex.Match(text, @"https?://[^\s""'<>]+");
            if (match.Success)
                return match.Value;

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

        // ──────────────────────────────────────────────────────
        // Right-click menu: copy YouTube link (optionally at current time)
        // ──────────────────────────────────────────────────────

        private void VideoArea_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentUrl) || _videoContextMenu == null) return;

            _videoContextMenu.PlacementTarget = VideoContainer;
            _videoContextMenu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _videoContextMenu.IsOpen          = true;
            e.Handled = true;
        }

        private void CopyVideoUrl_Click(object sender, RoutedEventArgs e)
        {
            TrySetClipboard(_currentUrl);
        }

        private void CopyVideoUrlAtTime_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentUrl)) return;

            var seconds = _player != null ? (int)(_player.CurTime / 10000000L) : 0;
            var sep     = _currentUrl.Contains('?') ? "&" : "?";
            TrySetClipboard($"{_currentUrl}{sep}t={seconds}s");
        }

        private void TrySetClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                App.Log($"[Clipboard] Copied: {text}");
            }
            catch (Exception ex)
            {
                App.Log($"[Clipboard] Failed: {ex.Message}");
            }
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (_controlsOverlayMode)
                ShowOverlayControls();
        }

        /// <summary>Mouse wheel over the video adjusts volume (~5% per notch).</summary>
        private void VideoArea_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_player == null || e.Delta == 0) return;

            var step = 5.0 * (e.Delta / 120.0); // 120 = one wheel notch
            VolumeSlider.Value = Math.Clamp(
                VolumeSlider.Value + step, VolumeSlider.Minimum, VolumeSlider.Maximum);

            // Reveal the (auto-hiding) controls so the change is visible in
            // maximized/fullscreen; in normal windowed mode the bar is always shown.
            if (_controlsOverlayMode)
                ShowOverlayControls();

            e.Handled = true;
        }

        // ──────────────────────────────────────────────────────
        // Right-click quality menu (WPF ContextMenu — no Win32 needed)
        // ──────────────────────────────────────────────────────

        private void ShowQualityMenu()
        {
            var menu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x42)),
                BorderThickness = new Thickness(1),
            };

            var headerItem = new MenuItem
            {
                Header = "Quality",
                IsEnabled = false,
                Foreground = new SolidColorBrush(Color.FromRgb(0x84, 0x8B, 0x9F)),
            };
            menu.Items.Add(headerItem);
            menu.Items.Add(new Separator());

            foreach (var (height, label) in QualityLevels)
            {
                var item = new MenuItem
                {
                    Header = label,
                    IsChecked = _selectedHeight == height,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0xE9, 0xF1)),
                    Background = new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x26)),
                };
                var h = height;
                item.Click += (s, ev) =>
                {
                    _selectedHeight = h;
                    _seekOnPlay = null;
                    if (!string.IsNullOrEmpty(_currentUrl))
                        _ = PlayUrl(_currentUrl);
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

                // COLORREF is 0x00BBGGRR. Caption bg = #0F1118 to match the app.
                int captionBg = 0x0018110F; // #0F1118
                int white     = 0x00FFFFFF; // caption text
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionBg, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR,    ref white,     sizeof(int));
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
