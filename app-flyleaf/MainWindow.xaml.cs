using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using FlyleafLib.MediaFramework.MediaStream;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer
{
    public enum SidebarTab { Playlist, History, Plex, Podcasts, YtMusic }

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
        private DispatcherTimer _cursorHideTimer;   // hides the pointer after idle in cinema fullscreen
        private bool            _cursorHidden;

        // Right-edge sidebar overlay (cinema fullscreen only)
        private bool            _sideOverlayActive;    // panel is popped out over the video
        private DispatcherTimer _sideOverlayHideTimer; // collapses ~0.3s after the pointer leaves
        private Grid            _sidePanelHome;         // SidePanel's normal grid parent (for restore)
        private double          _sidePanelHomeWidth;    // SidePanel.Width to restore after the overlay

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
        private ContextMenu         _historyContextMenu;
        private HistoryEntry        _historyContextMenuTarget;

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
        private int         _avSyncDiagTick;      // throttles A/V-sync decode diagnostics
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
        private ICollectionView _plexCategoryView;         // grouped + filtered view over _plexCategories
        private string _plexCategoryFilter = "";           // current text in the category filter box
        private int _plexRenderToken;                      // stale-guard for the batched list fill
        private ScrollViewer _plexScroller;                // cached results ScrollViewer (scroll-to-top)

        // Auto-play-next (TV): the ordered episode list we're walking and our position in it,
        // plus the end-of-episode countdown state.
        private List<PlexItem> _plexPlayQueue = new();
        private int _plexPlayIndex = -1;
        private System.Windows.Threading.DispatcherTimer _nextEpTimer;
        private int _nextEpCountdown;
        private PlexItem _nextEpTarget;

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
        public ICommand SearchCommand   { get; }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // Group the Plex category selector into "Views" and "Genres", with a live filter.
            var catView = new CollectionViewSource { Source = _plexCategories };
            catView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PlexCategory.Group)));
            _plexCategoryView = catView.View;
            _plexCategoryView.Filter = PlexCategoryFilterPredicate;
            PlexCategoryList.ItemsSource = _plexCategoryView;

            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var versionText = $"v{v.Major}.{v.Minor}.{v.Build}";
            Title = $"Nullcast {versionText}";
            VersionLabel.Text = versionText;

            OpenUrlCommand   = new RelayCommand(_ => OpenUrl_Click(null, null));
            PlayPauseCommand = new RelayCommand(_ => PlayPause_Click(null, null));
            SearchCommand    = new RelayCommand(_ => ToggleSearchPalette());

            InitializeFlyleaf();
            SetupTimer();
            InitializeYtDlp();

            // Build playlist context menu entirely in code. It shares the themed
            // ContextMenu/MenuItem look defined in XAML (VideoContextMenuStyle) so the
            // icons render cleanly instead of the default empty checkmark boxes.
            var contextMenuStyle = (Style)FindResource("VideoContextMenuStyle");

            _toggleMenuItem = new MenuItem
            {
                Header = "Mark as Completed",
                Icon   = MakeMenuIcon(IconCheckCircle),
            };
            var deleteMenuItem = new MenuItem
            {
                Header = "Delete",
                Icon   = MakeMenuIcon(IconTrash),
            };
            _toggleMenuItem.Click += PlaylistItem_ToggleCompleted_Click;
            deleteMenuItem.Click  += PlaylistItem_Delete_Click;

            _playlistContextMenu = new ContextMenu { Style = contextMenuStyle };
            _playlistContextMenu.Items.Add(_toggleMenuItem);
            _playlistContextMenu.Items.Add(deleteMenuItem);
            _playlistContextMenu.Opened += PlaylistContextMenu_Opened;

            PlaylistBox.ContextMenu = _playlistContextMenu;
            PlaylistBox.PreviewMouseRightButtonDown += PlaylistBox_PreviewMouseRightButtonDown;

            // History right-click menu (Delete only), same themed look.
            var historyDeleteItem = new MenuItem
            {
                Header = "Delete",
                Icon   = MakeMenuIcon(IconTrash),
            };
            historyDeleteItem.Click += HistoryItem_Delete_Click;
            _historyContextMenu = new ContextMenu { Style = contextMenuStyle };
            _historyContextMenu.Items.Add(historyDeleteItem);

            HistoryBox.ContextMenu = _historyContextMenu;
            HistoryBox.PreviewMouseRightButtonDown += HistoryBox_PreviewMouseRightButtonDown;

            // Video right-click menu (styled in XAML resources).
            _videoContextMenu = (ContextMenu)FindResource("VideoContextMenu");

            // Keep the overlay controls alive while the pointer is over them, and
            // keep them positioned when the video area resizes.
            ControlsBar.MouseMove += (s, e) => { if (_controlsOverlayMode) ShowOverlayControls(); };
            VideoContainer.SizeChanged += (s, e) =>
            {
                if (_controlsOverlayMode && OverlayControlsPopup.IsOpen)
                    PositionOverlayControls();
                if (_sideOverlayActive)
                    PositionSideOverlay();
            };

            // Right-edge sidebar overlay: remember the panel's home so it can be
            // re-parented into a floating popup during fullscreen and back again.
            _sidePanelHome      = SidePanel.Parent as Grid;
            _sidePanelHomeWidth = SidePanel.Width;
            SidePanel.MouseEnter += SidePanel_MouseEnter;
            SidePanel.MouseLeave += SidePanel_MouseLeave;
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

            // A/V-sync diagnostics: FramesDisplayed/FramesDropped/FPSCurrent only
            // populate when Stats is on (Engine.UIRefresh is already enabled). This
            // lets us see whether the video decoder is falling behind the audio
            // clock (the likely cause of audio-runs-ahead desync).
            config.Player.Stats = true;

            App.Log($"[AVSync] Decode config: VideoAcceleration={config.Video.VideoAcceleration} " +
                    $"AllowDropFrames={config.Decoder.AllowDropFrames} VideoThreads={config.Decoder.VideoThreads} " +
                    $"DemuxerBufferDuration={config.Demuxer.BufferDuration / 10000}ms");

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
                // FlyleafLib raises this when the playback loop halts — which
                // includes pausing. Only zero the bar on a genuine stop/end;
                // when merely paused, keep the current position visible so the
                // user can still see (and hover) where they are.
                if (_player == null || _player.Status != Status.Paused)
                    ProgressSlider.Value = 0;
            });

            // For split-stream sources, attach the parked audio URL as an external stream
            // once the main (video) open completes. Best-effort: if the attach fails the
            // video still plays (no worse than before), so we only log.
            _player.OpenCompleted += (s, e) => Dispatcher.InvokeAsync(() =>
            {
                // Log the open outcome — without this, a failed open (e.g. a Plex Direct Play the
                // demuxer/decoder rejects, or a 404 Part URL) produces NO log line at all, so the
                // failure is invisible. The URL carries the X-Plex-Token, so never log it raw.
                if (!e.IsSubtitles)
                {
                    if (e.Success)
                        App.Log("[Flyleaf] OpenCompleted OK");
                    else
                    {
                        // Scrub the URL to its path — it carries the X-Plex-Token in the query.
                        var path = e.Url;
                        var q = path?.IndexOf('?') ?? -1;
                        if (q >= 0) path = path!.Substring(0, q);
                        App.Log($"[Flyleaf] OpenCompleted FAILED err=\"{e.Error}\" path=\"{path}\"");
                        ShowPlaybackError(e.Error);
                        Telemetry.Track("media_open_failed", new()
                        {
                            ["error"] = e.Error ?? "unknown",
                        });
                    }
                }

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
                                HidePlaybackError();
                                // Any fresh playback supersedes a pending up-next countdown.
                                CancelNextEpisodeCountdown();

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

                                // Repeat-One loops the current media in place — seek back and
                                // replay rather than re-opening (which would resume from the
                                // just-saved end position and loop instantly). Covers every source.
                                if (_repeatMode == RepeatMode.One && _player != null)
                                {
                                    _player.SeekAccurate(0);
                                    _player.Play();
                                    break;
                                }

                                Telemetry.Track("media_completed", new()
                                {
                                    ["source"]      = _activePlex != null ? "plex"
                                                    : _activeMuid != null ? "bookmark" : "url",
                                    ["title"]       = _activePlex?.Title ?? "",
                                    ["media_id"]    = _activePlex?.RatingKey ?? _activeMuid ?? "",
                                    ["duration_ms"] = _player != null ? _player.Duration / 10000L : 0L,
                                });

                                if (_activePlex != null && _plex != null)
                                {
                                    // Report final position so Plex marks it watched/resumable.
                                    _ = _plex.ReportTimelineAsync(_activePlex, "stopped", _player.Duration / 10000L);

                                    // Offer to auto-advance to the next episode in this show.
                                    if (_settings.AutoPlayNextEpisode)
                                    {
                                        var next = GetNextEpisode();
                                        if (next != null) StartNextEpisodeCountdown(next);
                                    }
                                }
                                if (_activeMuid != null && _api != null)
                                {
                                    var muid = _activeMuid;

                                    // The video reached its end: mark it completed and reset the
                                    // saved position back to the start. Persisting the end position
                                    // would make a later replay resume at the very end and instantly
                                    // re-fire Ended — which in a playlist reads as "starts the video,
                                    // then immediately skips to the next one". Resetting the pointer
                                    // as we leave means clicking it again begins from the beginning.
                                    _ = _api.SavePositionAsync(muid, 0);

                                    var bm2 = PlaylistItems.FirstOrDefault(b => b.Muid == muid);
                                    if (bm2 != null)
                                    {
                                        bm2.Position    = 0;
                                        bm2.IsCompleted = true;
                                    }
                                    if (_settings.CompletedMuids.Add(muid))
                                        SaveSettings();

                                    if (_selectedWorkspace != null)
                                        _ = RefreshBookmarksAsync(_selectedWorkspace.Id);
                                }

                                // Repeat-one / shuffle / auto-advance for the flat queue
                                // (playlist bookmarks, YouTube Music, podcasts). Plex handles
                                // its own advance above.
                                await HandleFlatTrackEndedAsync();
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
            // App-wide live-cookie broker client (shared by every player type, not just YT Music).
            _tether = new TetherClient(_services);
            UpdatePlexTabState();

            await LoadSettingsAsync();
            CookiesMenuItem.IsChecked = _settings.UseBrowserCookies;
            AutoPlayNextMenuItem.IsChecked = _settings.AutoPlayNextEpisode;
            _shuffle    = _settings.Shuffle;
            _repeatMode = (RepeatMode)Math.Clamp(_settings.RepeatMode, 0, 2);
            RefreshTransportToggles();
            UpdateCookieFileMenuState();
            ApplyPlexViewMode();   // restore the remembered Plex list/tile view

            await _history.LoadAsync();
            RefreshHistoryView();

            var tokens = await _auth.LoadTokensAsync();
            if (tokens != null && !string.IsNullOrEmpty(tokens.AccessToken))
            {
                UpdateLoginUI(tokens.DisplayName);
                Telemetry.SetUser(tokens.Email, tokens.DisplayName);
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
        private void ShowYtMusicTab_Click(object sender, RoutedEventArgs e)  => SelectTab(SidebarTab.YtMusic);

        private void SelectTab(SidebarTab tab)
        {
            _activeTab = tab;
            Telemetry.Track("tab_switched", new() { ["tab"] = tab.ToString() });

            // Leaving Plex? Drop the full-screen browse takeover (and resume playback).
            if (_plexFullscreen && tab != SidebarTab.Plex)
                ExitPlexFullscreen(resumeVideo: true, reapply: true);

            PlaylistContent.Visibility = tab == SidebarTab.Playlist ? Visibility.Visible : Visibility.Collapsed;
            HistoryContent.Visibility  = tab == SidebarTab.History  ? Visibility.Visible : Visibility.Collapsed;
            PlexContent.Visibility     = tab == SidebarTab.Plex     ? Visibility.Visible : Visibility.Collapsed;
            PodcastContent.Visibility  = tab == SidebarTab.Podcasts ? Visibility.Visible : Visibility.Collapsed;
            YtMusicContent.Visibility  = tab == SidebarTab.YtMusic  ? Visibility.Visible : Visibility.Collapsed;

            // Underline tabs: active reads bright with an accent underline, inactive dims.
            var accent  = (Brush)FindResource("AccentBrush");
            var primary = (Brush)FindResource("TextPrimaryBrush");
            var muted   = (Brush)FindResource("TextMutedBrush");

            StyleTab(PlaylistTabButton, tab == SidebarTab.Playlist, accent, primary, muted);
            StyleTab(HistoryTabButton,  tab == SidebarTab.History,  accent, primary, muted);
            StyleTab(PlexTabButton,     tab == SidebarTab.Plex,     accent, primary, muted);
            StyleTab(PodcastsTabButton, tab == SidebarTab.Podcasts, accent, primary, muted);
            StyleTab(YtMusicTabButton,  tab == SidebarTab.YtMusic,  accent, primary, muted);

            if (tab == SidebarTab.History) RefreshHistoryView();
            if (tab == SidebarTab.Plex)    EnterPlexTab();
            if (tab == SidebarTab.YtMusic) EnterYtMusicTab();
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

                Telemetry.Track("media_play", new()
                {
                    ["source"] = "history",
                    ["title"]  = entry.Title ?? "",
                    ["url"]    = entry.Url,
                });
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

        // Feather-style icon path data (24×24 viewbox), reused by the context menus.
        private const string IconCheckCircle =
            "M22 11.08 V12 a10 10 0 1 1 -5.93 -9.14 M22 4 L12 14.01 L9 11.01";
        private const string IconTrash =
            "M3 6 h18 M19 6 v14 a2 2 0 0 1 -2 2 H7 a2 2 0 0 1 -2 -2 V6 m3 0 V4 a2 2 0 0 1 2 -2 h4 a2 2 0 0 1 2 2 v2 M10 11 v6 M14 11 v6";

        /// <summary>Builds a stroked 18×18 menu icon from 24×24 SVG path data.</summary>
        private static Viewbox MakeMenuIcon(string pathData)
        {
            var stroke = (Brush)new BrushConverter().ConvertFromString("#E6E6E6");
            var path = new System.Windows.Shapes.Path
            {
                Stroke             = stroke,
                StrokeThickness    = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
                StrokeLineJoin     = PenLineJoin.Round,
                Data               = Geometry.Parse(pathData),
            };
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(path);
            return new Viewbox { Width = 18, Height = 18, Child = canvas };
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

        private void HistoryBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as DependencyObject;
            while (element != null && element is not ListBoxItem)
                element = VisualTreeHelper.GetParent(element);
            _historyContextMenuTarget = (element as ListBoxItem)?.DataContext as HistoryEntry;

            e.Handled = true;

            if (_historyContextMenuTarget != null)
                _historyContextMenu.IsOpen = true;
        }

        private void HistoryItem_Delete_Click(object sender, RoutedEventArgs e)
        {
            var entry = _historyContextMenuTarget;
            if (entry == null) return;

            _history.Delete(entry);
            HistoryItems.Remove(entry);
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
                PlexCategoryHost.Visibility  = Visibility.Collapsed;
                PlexBreadcrumbBar.Visibility = Visibility.Collapsed;
                if (PlexSpinner != null) PlexSpinner.Visibility = Visibility.Collapsed;
                if (PlexViewToolbar != null) PlexViewToolbar.Visibility = Visibility.Collapsed;
                PlexStatusText.Text = "No Plex server configured. Open Services (⚙) to add one.";
                PlexStatusText.Visibility = Visibility.Visible;
                return;
            }

            PlexLibraryBar.Visibility = Visibility.Visible;
            // Any load that ends here is finished — stop the spinner.
            if (PlexSpinner != null) PlexSpinner.Visibility = Visibility.Collapsed;
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

        /// <summary>Show the animated spinner + a status line while an async load is in flight.</summary>
        private void ShowPlexLoading(string text)
        {
            if (PlexSpinner != null) PlexSpinner.Visibility = Visibility.Visible;
            PlexStatusText.Text = text;
            PlexStatusText.Visibility = Visibility.Visible;
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
                Telemetry.Track("plex_view_mode", new() { ["mode"] = tiles ? "tiles" : "list" });
            }
            ApplyPlexViewMode();
        }

        /// <summary>True when the current drill level is a season's episodes.</summary>
        private bool PlexViewingEpisodes =>
            !_plexSearchMode && _plexCurrentItems.Count > 0 && _plexCurrentItems.All(i => i.IsEpisode);

        /// <summary>Swaps the results ListBox between the tile grid and the compact list, and
        /// reflects the active mode on the toolbar buttons. Idempotent and safe to call before
        /// data loads (assignments are skipped when unchanged, so it can run on every render).
        /// Episodes always use the lean image-free list; full-screen browse otherwise tiles.</summary>
        private void ApplyPlexViewMode()
        {
            if (PlexResultsBox == null) return;

            bool episodes = PlexViewingEpisodes;
            bool tiles    = !episodes && (_settings.PlexTileView || _plexFullscreen);

            var tpl = (DataTemplate)FindResource(
                episodes ? "PlexEpisodeItemTemplate"
                         : tiles ? "PlexTileItemTemplate" : "PlexListItemTemplate");
            var panel = (ItemsPanelTemplate)FindResource(tiles ? "PlexTilePanel" : "PlexListPanel");

            if (!ReferenceEquals(PlexResultsBox.ItemTemplate, tpl)) PlexResultsBox.ItemTemplate = tpl;
            if (!ReferenceEquals(PlexResultsBox.ItemsPanel, panel)) PlexResultsBox.ItemsPanel   = panel;

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

            PlexCategoryHost.Visibility  = Visibility.Visible;
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

            PlexCategoryHost.Visibility  = Visibility.Collapsed;
            PlexBreadcrumbBar.Visibility = Visibility.Collapsed;
            RebuildSeasonBar();
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

            // Default to the first "Views" entry ("All") and browse it.
            await ApplyPlexCategory(_plexCategories.FirstOrDefault());
        }

        // ──────────────────────────────────────────────────────
        // Category selector: a filterable, scrollable popup that
        // replaces the old (truncating, unfilterable) ComboBox.
        // ──────────────────────────────────────────────────────

        private void PlexCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlexCategoryButton.IsChecked == true)
            {
                PlexCategoryFilter.Text = "";           // start each open with a clean filter
                PlexCategoryPopup.IsOpen = true;
                // Focus the filter box once the popup has rendered.
                Dispatcher.BeginInvoke(new Action(() => PlexCategoryFilter.Focus()),
                                       DispatcherPriority.Input);
            }
            else
            {
                PlexCategoryPopup.IsOpen = false;
            }
        }

        private void PlexCategoryPopup_Closed(object sender, EventArgs e)
            => PlexCategoryButton.IsChecked = false;

        private void PlexCategoryFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            _plexCategoryFilter = PlexCategoryFilter.Text ?? "";
            _plexCategoryView?.Refresh();
        }

        private void PlexCategoryFilter_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                PlexCategoryPopup.IsOpen = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                // Commit the first match, if any.
                var first = _plexCategoryView?.Cast<PlexCategory>().FirstOrDefault();
                if (first != null) { _ = ApplyPlexCategory(first); PlexCategoryPopup.IsOpen = false; }
                e.Handled = true;
            }
        }

        private async void PlexCategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlexCategoryList.SelectedItem is not PlexCategory cat) return;
            if (ReferenceEquals(cat, _plexCategory)) return;   // ignore programmatic re-selection
            PlexCategoryPopup.IsOpen = false;
            Telemetry.Track("plex_browse", new() { ["category"] = cat.Label ?? "", ["view"] = cat.View.ToString() });
            await ApplyPlexCategory(cat);
        }

        /// <summary>Filter predicate: match category labels against the popup's filter text.</summary>
        private bool PlexCategoryFilterPredicate(object o)
            => string.IsNullOrEmpty(_plexCategoryFilter)
               || (o is PlexCategory c &&
                   c.Label.Contains(_plexCategoryFilter, StringComparison.OrdinalIgnoreCase));

        /// <summary>Select a category (updates the button face + list) and browse it.</summary>
        private async Task ApplyPlexCategory(PlexCategory cat)
        {
            if (cat == null) return;
            _plexCategory = cat;
            PlexCategoryButton.Content = cat.Label;
            PlexCategoryList.SelectedItem = cat;
            await LoadPlexBrowse();
        }

        private async Task LoadPlexBrowse()
        {
            if (_plexSection == null || _plexCategory == null) return;

            _plexDrill.Clear();
            RebuildBreadcrumb();

            int token = ++_plexLoadToken;
            ShowPlexLoading("Loading…");

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
            // Pick the right row template for this drill level (episodes → lean list) before render.
            ApplyPlexViewMode();

            var q = _plexSearchMode ? "" : (PlexSearchBox?.Text ?? "").Trim();
            var filtered = new List<PlexItem>(_plexCurrentItems.Count);
            foreach (var it in _plexCurrentItems)
            {
                if (q.Length == 0
                    || it.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || it.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(it);
                }
            }
            RenderPlexItems(filtered);
        }

        /// <summary>
        /// Populates the bound <see cref="PlexItems"/> in small batches, yielding to the
        /// dispatcher between them. The tile grid isn't UI-virtualized, so adding a few hundred
        /// posters in one synchronous burst would realize every tile (and start every image
        /// download) at once and freeze the UI — the spinner would stall and the mouse would
        /// lock. Batching keeps input and animation flowing while the grid fills, and a render
        /// token cancels an in-flight fill the moment a newer one starts (e.g. a fast category
        /// switch or each keystroke in the narrow filter). Also resets the scroll to the top.
        /// </summary>
        private async void RenderPlexItems(IReadOnlyList<PlexItem> items)
        {
            int myToken = ++_plexRenderToken;

            PlexItems.Clear();
            ScrollPlexToTop();

            const int batch = 24;
            for (int i = 0; i < items.Count; i++)
            {
                if (myToken != _plexRenderToken) return;   // a newer render superseded us
                PlexItems.Add(items[i]);
                if ((i + 1) % batch == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            if (myToken != _plexRenderToken) return;
            UpdatePlexTabState();
        }

        /// <summary>Reset the Plex results list back to the top (e.g. after a category change).</summary>
        private void ScrollPlexToTop()
        {
            _plexScroller ??= FindDescendant<ScrollViewer>(PlexResultsBox);
            _plexScroller?.ScrollToTop();
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T hit) return hit;
                var deeper = FindDescendant<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>Drill from a show into its seasons, or a season into its episodes.</summary>
        private async Task PlexDrillInto(PlexItem container)
        {
            int token = ++_plexLoadToken;
            ShowPlexLoading("Loading…");

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
            }
            else
            {
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

            RebuildSeasonBar();
        }

        /// <summary>
        /// Rebuilds the season quick-nav chips (S01/S02…) shown while viewing a season's episodes.
        /// The sibling seasons are the items of the frame above the current one (the show); the
        /// current season is the top frame. Clicking a chip jumps straight to that season's
        /// episodes. Hidden unless we're two levels deep with more than one season to hop between.
        /// </summary>
        private void RebuildSeasonBar()
        {
            if (PlexSeasonBar == null) return;
            PlexSeasonBar.Children.Clear();

            List<PlexItem> seasons = null;
            string currentKey = null;
            if (!_plexSearchMode && _plexDrill.Count >= 2)
            {
                var parent = _plexDrill[_plexDrill.Count - 2];
                seasons    = parent.Items?.Where(s => s.Kind == "season").ToList();
                currentKey = _plexDrill[^1].RatingKey;
            }

            if (seasons == null || seasons.Count < 2)
            {
                PlexSeasonBar.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var s in seasons)
            {
                bool isCurrent = s.RatingKey == currentKey;
                var chip = new Button
                {
                    Content = s.SeasonShortLabel,
                    Style   = (Style)FindResource("PlexSeasonChip"),
                    ToolTip = s.Title,
                    Tag     = s,
                    Background = isCurrent ? SegActiveBg : SegInactiveBg,
                    Foreground = isCurrent ? SegActiveFg : SegInactiveFg,
                };
                if (!isCurrent) chip.Click += SeasonChip_Click;
                PlexSeasonBar.Children.Add(chip);
            }

            PlexSeasonBar.Visibility = Visibility.Visible;
        }

        /// <summary>Jump to another season's episodes: swap the current season frame for the picked one.</summary>
        private async void SeasonChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not PlexItem season) return;
            if (_plexDrill.Count < 2) return;

            // Drop the current season frame (keeping the show frame), then drill into the pick.
            _plexDrill.RemoveAt(_plexDrill.Count - 1);
            _plexCurrentItems = _plexDrill[^1].Items;
            await PlexDrillInto(season);
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
                RenderPlexItems(System.Array.Empty<PlexItem>());
                return;
            }

            ShowPlexLoading("Searching…");

            try
            {
                var results = await _plex.SearchAsync(query);

                // Ignore stale responses if the box has moved on since we fired.
                if (PlexSearchBox.Text.Trim() != query) return;

                RenderPlexItems(results);
            }
            catch (Exception ex)
            {
                App.Log($"[Plex] Search failed: {ex.Message}");
                RenderPlexItems(System.Array.Empty<PlexItem>());
            }
        }

        private async void PlexResultsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            if (PlexResultsBox.SelectedItem is not PlexItem item) return;

            // Shows/seasons drill in (staying in full-screen browse); leaves play.
            if (item.IsContainer)
            {
                await PlexDrillInto(item);
            }
            else
            {
                // Picking something to play collapses the full-screen takeover back to the
                // split view. Don't resume the old paused item — we're starting a new one.
                if (_plexFullscreen) ExitPlexFullscreen(resumeVideo: false, reapply: true);
                PlayPlexItem(item);
            }
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

            // Transport Next/Prev bridge to Plex's own episode queue; the flat queue is inactive.
            _queueKind    = QueueKind.Plex;
            _queueListBox = null;

            // A new item is starting — drop any pending end-of-episode countdown, then work out
            // the queue we're walking so we can auto-advance when this one ends. Prefer an
            // already-established queue (so auto-advance keeps going even if the user has browsed
            // elsewhere); otherwise seed a fresh one from the current drill list.
            CancelNextEpisodeCountdown();
            if (item.IsEpisode)
            {
                int qIdx = _plexPlayQueue.FindIndex(p => p.RatingKey == item.RatingKey);
                if (qIdx >= 0)
                {
                    _plexPlayIndex = qIdx;
                }
                else
                {
                    int idx = _plexCurrentItems.FindIndex(p => p.RatingKey == item.RatingKey);
                    if (idx >= 0) { _plexPlayQueue = new List<PlexItem>(_plexCurrentItems); _plexPlayIndex = idx; }
                    else          { _plexPlayQueue = new(); _plexPlayIndex = -1; }
                }
            }
            else
            {
                _plexPlayQueue = new(); _plexPlayIndex = -1;
            }

            // Resume where Plex left off, using the shared seek-on-play mechanism.
            _seekOnPlay = item.HasResume ? item.ViewOffsetMs : null;

            try
            {
                HidePlaybackError();
                StatusText.Text = "Loading video...";
                StatusText.Visibility = Visibility.Visible;

                _player.Stop();

                _currentUrl = streamUrl;
                SetWindowTitle(item.Title);

                // Local history (Plex items keyed by a stable plex:// id, not the tokenized URL).
                _history.Record($"plex://{item.RatingKey}", item.Title);
                if (HistoryContent.Visibility == Visibility.Visible)
                    RefreshHistoryView();

                App.Log($"[Plex] Opening ratingKey={item.RatingKey} resume={item.ViewOffsetMs}ms part={item.PartKey}");
                Telemetry.Track("media_play", new()
                {
                    ["source"]      = "plex",
                    ["title"]       = item.Title ?? "",
                    ["media_id"]    = item.RatingKey ?? "",
                    ["media_type"]  = item.Kind ?? "",
                    ["duration_ms"] = item.DurationMs,
                    ["resume_ms"]   = item.ViewOffsetMs,
                    ["show_title"]  = item.ShowTitle ?? "",
                    ["season"]      = item.SeasonIndex,
                    ["episode"]     = item.EpisodeIndex,
                });
                _player.OpenAsync(streamUrl);
                _ = _plex.ReportTimelineAsync(item, "playing", item.ViewOffsetMs);
            }
            catch (Exception ex)
            {
                ShowPlaybackError(ex.Message);
            }
        }

        // ──────────────────────────────────────────────────────
        // Playback-error overlay
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Show a prominent, human-readable reason over the video area when a media open fails, so
        /// a failure is never a silent black screen. <paramref name="rawError"/> is the raw
        /// FlyleafLib/FFmpeg (or exception) message; <see cref="DescribeOpenError"/> maps it to
        /// plain language, with the raw text kept as a small technical detail underneath.
        /// </summary>
        private void ShowPlaybackError(string rawError)
        {
            StatusText.Visibility = Visibility.Collapsed;

            var (reason, detail) = DescribeOpenError(rawError);
            ErrorOverlayReason.Text = reason;
            if (string.IsNullOrWhiteSpace(detail))
                ErrorOverlayDetail.Visibility = Visibility.Collapsed;
            else
            {
                ErrorOverlayDetail.Text = detail;
                ErrorOverlayDetail.Visibility = Visibility.Visible;
            }
            ErrorOverlay.Visibility = Visibility.Visible;
        }

        /// <summary>Hide the playback-error overlay (a new load is starting, or playback began).</summary>
        private void HidePlaybackError()
        {
            if (ErrorOverlay != null) ErrorOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Translate a raw FlyleafLib/FFmpeg open error into a plain-language reason (shown large)
        /// plus the raw text as a small technical detail. Covers the common failure modes — server
        /// unreachable/offline, access denied, file missing, server error, unsupported/corrupt
        /// media — and falls back to a generic message for anything unrecognised, so we always say
        /// SOMETHING rather than showing a black screen.
        /// </summary>
        private static (string reason, string detail) DescribeOpenError(string rawError)
        {
            var e     = rawError ?? "";
            var lower = e.ToLowerInvariant();

            // Server answered with an HTTP status.
            if (lower.Contains("404") || lower.Contains("not found"))
                return ("The server couldn't find this file. It may have been moved, renamed, or "
                      + "deleted — or the library needs a refresh.", e);
            if (lower.Contains("401") || lower.Contains("403") || lower.Contains("unauthorized")
                || lower.Contains("forbidden"))
                return ("The server refused access. Your sign-in may have expired — try "
                      + "reconnecting the service in Settings.", e);
            if (lower.Contains("500") || lower.Contains("502") || lower.Contains("503")
                || lower.Contains("server error") || lower.Contains("bad gateway")
                || lower.Contains("unavailable"))
                return ("The server reported an error. It may be busy, restarting, or "
                      + "misconfigured.", e);

            // Never reached the server.
            if (lower.Contains("connection refused") || lower.Contains("failed to connect")
                || lower.Contains("could not connect") || lower.Contains("connection reset")
                || lower.Contains("timed out") || lower.Contains("timeout")
                || lower.Contains("network is unreachable") || lower.Contains("no route")
                || lower.Contains("name or service not known") || lower.Contains("temporary failure"))
                return ("Couldn't reach the server. It may be offline, or unreachable from this "
                      + "network.", e);

            // Reached something, but it isn't playable media.
            if (lower.Contains("invalid data") || lower.Contains("protocol not found")
                || lower.Contains("decoder") || lower.Contains("codec")
                || lower.Contains("moov atom") || lower.Contains("end of file"))
                return ("This file couldn't be played. Its format may be unsupported, or the file "
                      + "is incomplete or damaged.", e);

            // Unknown — still say something useful, and surface the raw text for support.
            return (string.IsNullOrWhiteSpace(e)
                        ? "Playback failed for an unknown reason."
                        : "Playback failed.", e);
        }

        // ──────────────────────────────────────────────────────
        // Auto-play next episode (TV)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// The episode that follows the one currently playing, or null when there isn't a sensible
        /// one. Auto-advance stays within a single show and only to playable episodes.
        /// </summary>
        private PlexItem GetNextEpisode()
        {
            if (_plexPlayIndex < 0 || _plexPlayIndex + 1 >= _plexPlayQueue.Count) return null;

            var cur  = _activePlex;
            var next = _plexPlayQueue[_plexPlayIndex + 1];
            if (cur == null || next == null || !next.IsEpisode || !next.IsPlayable) return null;

            // Don't jump across shows (guards flat mixed lists like "Recently Added").
            if (!string.IsNullOrEmpty(cur.ShowTitle) && !string.IsNullOrEmpty(next.ShowTitle)
                && !string.Equals(cur.ShowTitle, next.ShowTitle, StringComparison.Ordinal))
                return null;

            return next;
        }

        /// <summary>Shows the "Up next" card and starts a 3-second countdown to the given episode.</summary>
        private void StartNextEpisodeCountdown(PlexItem next)
        {
            _nextEpTarget    = next;
            _nextEpCountdown = 3;

            NextEpisodeTitle.Text = next.Title;
            NextEpisodeSub.Text   = next.HasEpisodeBadge
                ? $"{next.ShowTitle} · {next.EpisodeBadge}".Trim(' ', '·')
                : next.ShowTitle;
            NextEpisodeCount.Text        = _nextEpCountdown.ToString();
            NextEpisodeOverlay.Visibility = Visibility.Visible;

            _nextEpTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _nextEpTimer.Tick -= NextEpisodeTick;
            _nextEpTimer.Tick += NextEpisodeTick;
            _nextEpTimer.Start();
        }

        private void NextEpisodeTick(object sender, EventArgs e)
        {
            _nextEpCountdown--;
            if (_nextEpCountdown <= 0)
            {
                PlayNextEpisodeNow();
                return;
            }
            NextEpisodeCount.Text = _nextEpCountdown.ToString();
        }

        /// <summary>Skip the wait and play the queued next episode immediately.</summary>
        private void PlayNextEpisodeNow()
        {
            var next = _nextEpTarget;
            CancelNextEpisodeCountdown();
            if (next != null) PlayPlexItem(next);
        }

        /// <summary>Hide the card and stop the countdown (also called whenever new playback starts).</summary>
        private void CancelNextEpisodeCountdown()
        {
            _nextEpTimer?.Stop();
            _nextEpTarget = null;
            if (NextEpisodeOverlay != null)
                NextEpisodeOverlay.Visibility = Visibility.Collapsed;
        }

        private void NextEpisodeNow_Click(object sender, RoutedEventArgs e)    => PlayNextEpisodeNow();
        private void NextEpisodeCancel_Click(object sender, RoutedEventArgs e) => CancelNextEpisodeCountdown();

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
            if (_navigatingQueue) return;   // Next/Prev drives selection itself; don't re-capture
            if (e.AddedItems.Count == 0) return;
            var selected = PodcastResultsBox.SelectedItem;

            if (selected is PodcastShow show)
                await LoadPodcastEpisodes(show);
            else if (selected is PodcastEpisode episode)
            {
                var eps = PodcastItems.OfType<PodcastEpisode>().Cast<object>().ToList();
                SetFlatQueue(eps, eps.IndexOf(episode), PodcastResultsBox);
                await PlayPodcastEpisodeAsync(episode);
            }
        }

        private async Task PlayPodcastEpisodeAsync(PodcastEpisode episode)
        {
            _activeMuid = null;      // podcasts aren't bookmarks — don't save position server-side
            _activePlex = null;
            _seekOnPlay = null;
            Telemetry.Track("media_play", new()
            {
                ["source"]     = "podcast",
                ["title"]      = episode.Title ?? "",
                ["show_title"] = episode.ShowTitle ?? "",
                ["url"]        = episode.AudioUrl ?? "",
            });
            await PlayUrl(episode.AudioUrl, episode.Title, forceDirect: true);
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
                Telemetry.SetUser(tokens.Email, tokens.DisplayName);
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
            Telemetry.ClearUser();
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
            if (_navigatingQueue) return;   // Next/Prev drives selection itself; don't re-capture
            if (e.AddedItems.Count == 0 || _api == null) return;
            if (PlaylistBox.SelectedItem is Bookmark bookmark)
            {
                SetFlatQueue(PlaylistItems.Cast<object>().ToList(),
                             PlaylistItems.IndexOf(bookmark), PlaylistBox);
                await PlayBookmark(bookmark);
            }
        }

        private async Task PlayBookmark(Bookmark bookmark)
        {
            _activePlex        = null;   // a bookmark is playing — detach from the Plex path
            _activeMuid        = bookmark.Muid;
            _positionSaveTick  = 0;
            _seekOnPlay        = null;

            var fresh = await _api.GetBookmarkAsync(bookmark.Muid);
            if (fresh?.Position is int pos && pos > 0)
                _seekOnPlay = pos * 1000L;

            Telemetry.Track("media_play", new()
            {
                ["source"]     = "bookmark",
                ["title"]      = bookmark.Title ?? "",
                ["media_id"]   = bookmark.Muid ?? "",
                ["media_type"] = bookmark.Type ?? "",
                ["url"]        = bookmark.Url ?? "",
                ["resume_ms"]  = _seekOnPlay ?? 0L,
            });
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

            // A/V-sync diagnostics: every ~5s log whether the video decoder is
            // keeping up. If FramesDropped climbs and FPSCurrent sits below FPS,
            // video is falling behind the audio clock (audio-runs-ahead desync).
            // BufferedDuration near-zero points at a starved demuxer/network instead.
            _avSyncDiagTick++;
            if (_avSyncDiagTick >= 10)
            {
                _avSyncDiagTick = 0;
                var v = _player.Video;
                App.Log($"[AVSync] t={FormatTime((long)time)} " +
                        $"fps={v.FPS:F1}/cur={v.FPSCurrent:F1} " +
                        $"displayed={v.FramesDisplayed} dropped={v.FramesDropped} " +
                        $"buffered={_player.BufferedDuration / 10000}ms " +
                        $"bitrate={v.BitRate / 1000.0:F0}kbps" +
                        (_activePlex != null ? " src=plex" : ""));
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
                _queueKind  = QueueKind.None;   // an ad-hoc URL isn't part of any queue
                Telemetry.Track("media_play", new() { ["source"] = "url", ["entry"] = "open_dialog", ["url"] = dialog.Url ?? "" });
                await PlayUrl(dialog.Url);
            }
        }

        private void ToggleBrowserCookies_Click(object sender, RoutedEventArgs e)
        {
            _settings.UseBrowserCookies = CookiesMenuItem.IsChecked;
            SaveSettings();
        }

        private void LoadCookiesFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title           = "Select exported cookies.txt",
                Filter          = "Cookie files (*.txt)|*.txt|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) == true)
            {
                _settings.CookieFilePath = dlg.FileName;
                SaveSettings();
                UpdateCookieFileMenuState();
            }
        }

        private void ClearCookiesFile_Click(object sender, RoutedEventArgs e)
        {
            _settings.CookieFilePath = "";
            SaveSettings();
            UpdateCookieFileMenuState();
        }

        /// <summary>Reflects the loaded cookies.txt (if any) in the File-menu items.</summary>
        private void UpdateCookieFileMenuState()
        {
            var path = _settings.CookieFilePath;
            bool set  = !string.IsNullOrWhiteSpace(path);
            CookieFileMenuItem.Header    = set && File.Exists(path)
                ? $"Cookies file: {System.IO.Path.GetFileName(path)}"
                : "Load _cookies.txt file…";
            ClearCookieFileMenuItem.IsEnabled = set;
        }

        private void ToggleAutoPlayNext_Click(object sender, RoutedEventArgs e)
        {
            _settings.AutoPlayNextEpisode = AutoPlayNextMenuItem.IsChecked;
            if (!_settings.AutoPlayNextEpisode) CancelNextEpisodeCountdown();
            SaveSettings();
            RefreshTransportToggles();
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
        private async Task PlayUrl(string url, string displayTitle = null, bool forceDirect = false,
                                  bool audioOnly = false, bool? cookieOverride = null)
        {
            try
            {
                // Leaving the Plex ecosystem — report stop and detach so Timer_Tick
                // stops sending Plex timeline updates for this playback.
                if (_activePlex != null && _player?.Status == Status.Playing)
                    _ = _plex.ReportTimelineAsync(_activePlex, "stopped", _player.CurTime / 10000L);
                _activePlex = null;

                HidePlaybackError();
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

                var (title, videoUrl, audioUrl) = await ResolveWithYtDlpAsync(url, audioOnly, cookieOverride);

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
                ShowPlaybackError(ex.Message);
            }
        }

        /// <summary>
        /// Resolves a page URL to a stream via yt-dlp. Returns the title, the video URL, and
        /// (only when the source has no muxed rendition) a separate audio URL. The format
        /// selector prefers a single muxed stream so common sources keep their existing
        /// single-URL behaviour and only Reddit-style split sources use the external-audio path.
        /// </summary>
        private async Task<(string title, string videoUrl, string audioUrl)> ResolveWithYtDlpAsync(
            string url, bool audioOnly = false, bool? cookieOverride = null)
        {
            // Audio-first for music (YT Music "art tracks" are audio-only); video-first otherwise.
            var fmt = audioOnly
                ? "bestaudio/best"
                : $"best[height<={_selectedHeight}]/bv*[height<={_selectedHeight}]+ba/best";

            // Resolve the cookie file once for this URL (Tether live cookies for the site,
            // manual export as fallback, none for YouTube). Shared by both yt-dlp calls.
            var cookieFile = await ResolveCookieFileForUrlAsync(url, cookieOverride);

            var titleTask  = RunYtDlp("--no-warnings --print \"%(title)s\"", url, cookieFile: cookieFile);
            var streamTask = RunYtDlp($"--no-warnings -f \"{fmt}\" -g", url, cookieFile: cookieFile);
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

        /// <summary>
        /// Picks the cookie file yt-dlp should use for playing <paramref name="url"/>, available to
        /// EVERY player type (Facebook, TikTok, …): live cookies pulled from the Tether broker
        /// for the site's registrable domain, falling back to the manually exported cookies.txt.
        /// Returns "" for no cookies. YouTube is deliberately excluded — valid YouTube cookies trigger
        /// YouTube's SABR-only experiment which strips the direct stream URLs yt-dlp needs.
        /// </summary>
        private async Task<string> ResolveCookieFileForUrlAsync(string url, bool? cookieOverride)
        {
            if (cookieOverride == false) return "";

            var host = TryGetHost(url);
            if (IsYouTubeHost(host)) return "";

            if (_tether?.IsInstalled == true && !string.IsNullOrEmpty(host))
            {
                var live = await _tether.FetchCookiesFileAsync(RegistrableDomain(host));
                if (!string.IsNullOrEmpty(live)) return live;
            }

            var file = _settings.CookieFilePath;
            return !string.IsNullOrWhiteSpace(file) && File.Exists(file) ? file : "";
        }

        private static string TryGetHost(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;

        private static bool IsYouTubeHost(string host) =>
            !string.IsNullOrEmpty(host) &&
            (host.EndsWith("youtube.com", StringComparison.Ordinal) ||
             host == "youtu.be" || host.EndsWith(".youtu.be", StringComparison.Ordinal) ||
             host.EndsWith("googlevideo.com", StringComparison.Ordinal));

        /// <summary>Registrable domain (eTLD+1) approximation — matches Tether's share key.</summary>
        private static string RegistrableDomain(string host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            var parts = host.Split('.');
            if (parts.Length <= 2) return host;
            string[] twoLabel = { "co.uk", "com.au", "co.nz", "co.jp", "com.br", "co.za" };
            var lastTwo = $"{parts[^2]}.{parts[^1]}";
            return Array.IndexOf(twoLabel, lastTwo) >= 0 && parts.Length >= 3
                ? $"{parts[^3]}.{parts[^2]}.{parts[^1]}"
                : lastTwo;
        }

        /// <summary>
        /// Builds the yt-dlp cookie flag (with a trailing space), honoring precedence:
        /// an exported cookies.txt wins (no browser lock / app-bound-encryption issues), then
        /// live browser cookies. <paramref name="cookieOverride"/>: null follows the global
        /// setting, false forces no cookies, true forces cookies (file or browser) on.
        /// </summary>
        private string ResolveCookieArgs(bool? cookieOverride)
        {
            if (cookieOverride == false) return "";

            // A manually exported cookies.txt (avoids the browser cookie-DB lock / app-bound
            // encryption that make --cookies-from-browser fail). NOTE: we deliberately do NOT feed
            // the live Tether YouTube cookies here — valid YouTube auth cookies trigger
            // YouTube's SABR-only streaming experiment, which strips the direct stream URLs yt-dlp
            // needs. Live cookies are for the InnerTube library only; YT Music tracks play cookie-free.
            var file = _settings.CookieFilePath;
            if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                return $"--cookies \"{file}\" ";

            // Fall back to a live browser read (borrow the user's logged-in session).
            var useBrowser = cookieOverride ?? _settings.UseBrowserCookies;
            if (useBrowser && !string.IsNullOrWhiteSpace(_settings.CookieBrowser))
                return $"--cookies-from-browser {_settings.CookieBrowser} ";

            return "";
        }

        /// <param name="cookieOverride">
        /// null → follow the global "Use browser cookies" setting (default).
        /// false → never send cookies (e.g. public YouTube-Music search, which doesn't need them
        ///         and would otherwise fail when the browser holds a lock on its cookie DB).
        /// true → send cookies if a browser is configured, regardless of the global toggle.
        /// </param>
        /// <param name="cookieFile">
        /// When non-null it is the authoritative cookie decision for this call: "" → send no
        /// cookies; a path → <c>--cookies "&lt;path&gt;"</c>. When null, fall back to the setting-based
        /// <see cref="ResolveCookieArgs"/>. (Playback resolves a per-URL file first, via Tether.)
        /// </param>
        private async Task<string> RunYtDlp(string arguments, string url, bool? cookieOverride = null,
                                            string cookieFile = null)
        {
            var cookies = cookieFile != null
                ? (cookieFile.Length == 0 ? "" : $"--cookies \"{cookieFile}\" ")
                : ResolveCookieArgs(cookieOverride);

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
                ? $"Nullcast v{v}"
                : $"Nullcast v{v} - {mediaTitle}";

            // Mirror the loaded item's name above the timeline.
            NowPlayingTitle.Text = mediaTitle ?? "";
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
                Telemetry.Track("media_pause", new() { ["position_ms"] = _player.CurTime / 10000L });
            }
            else
            {
                if (_activePlex != null && _plex != null)
                    _ = _plex.ReportTimelineAsync(_activePlex, "playing", _player.CurTime / 10000L);
                _player.Play();
                Telemetry.Track("media_resume", new() { ["position_ms"] = _player.CurTime / 10000L });
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
            Telemetry.Track("speed_changed", new() { ["speed"] = _playbackSpeed });
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
                Telemetry.Track("seek", new() { ["method"] = "slider", ["target_ms"] = newTimeMs });
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_player != null)
            {
                _player.Audio.Volume = (int)VolumeSlider.Value;
                Telemetry.Track("volume_changed", new() { ["volume"] = (int)VolumeSlider.Value });
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
                    Telemetry.Track("media_play", new() { ["source"] = "url", ["entry"] = "drop", ["url"] = url ?? "" });
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
                // A double-click enters/exits the borderless "cinema" fullscreen
                // (video fills the whole screen, no chrome) and must NOT toggle
                // play/pause — cancel the pending single-click action.
                _clickTimer?.Stop();
                ToggleFullscreen();
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

        private void VideoArea_MouseMove(object sender, MouseEventArgs e)
        {
            NudgeCursorIdle();
            MaybeTriggerSideOverlay(e);
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
            NudgeCursorIdle();
            MaybeTriggerSideOverlay(e);
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
            Telemetry.Track("fullscreen_toggled", new() { ["enabled"] = !_isFullscreen });
            if (_isFullscreen)
            {
                _isFullscreen = false;
                StopCursorHide();
                HideSideOverlay();
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

                // Arm the idle-cursor timer; the pointer hides after 3s of stillness.
                NudgeCursorIdle();
            }
        }

        // ──────────────────────────────────────────────────────
        // Auto-hiding cursor (cinema fullscreen only)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// In borderless fullscreen, hide the mouse pointer after 3 seconds of no
        /// motion. Any movement restores it immediately and restarts the timer.
        /// No-op outside fullscreen.
        /// </summary>
        private void NudgeCursorIdle()
        {
            if (!_isFullscreen)
            {
                StopCursorHide();
                return;
            }

            if (_cursorHidden)
            {
                Mouse.OverrideCursor = null;
                _cursorHidden = false;
            }

            if (_cursorHideTimer == null)
            {
                _cursorHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _cursorHideTimer.Tick += (s, _) =>
                {
                    _cursorHideTimer.Stop();
                    if (!_isFullscreen || _sideOverlayActive) return;
                    Mouse.OverrideCursor = Cursors.None;
                    _cursorHidden = true;
                };
            }
            _cursorHideTimer.Stop();
            _cursorHideTimer.Start();
        }

        /// <summary>Stops the idle timer and restores the pointer if it was hidden.</summary>
        private void StopCursorHide()
        {
            _cursorHideTimer?.Stop();
            if (_cursorHidden)
            {
                Mouse.OverrideCursor = null;
                _cursorHidden = false;
            }
        }

        // ──────────────────────────────────────────────────────
        // Right-edge sidebar overlay (cinema fullscreen only)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// In cinema fullscreen, a pointer at the extreme right edge of the screen
        /// pops the sidebar out over the video (no resize). No-op otherwise.
        /// </summary>
        private void MaybeTriggerSideOverlay(MouseEventArgs e)
        {
            if (!_isFullscreen || _sideOverlayActive) return;

            var pos = e.GetPosition(this);
            if (pos.X >= ActualWidth - 3 && pos.Y >= 0 && pos.Y <= ActualHeight)
                ShowSideOverlay();
        }

        private void ShowSideOverlay()
        {
            if (_sideOverlayActive || _sidePanelHome == null) return;
            _sideOverlayActive = true;

            // Re-parent the docked SidePanel into the floating popup so it paints
            // above the Flyleaf D3D surface (same trick as the controls overlay).
            _sidePanelHomeWidth = SidePanel.Width;
            if (_sidePanelHome.Children.Contains(SidePanel))
                _sidePanelHome.Children.Remove(SidePanel);
            OverlaySidePanelPopup.Child = SidePanel;

            SidePanel.Visibility = Visibility.Visible;
            PositionSideOverlay();
            OverlaySidePanelPopup.IsOpen = true;

            // Slide in from the right edge.
            var slide = new TranslateTransform(SidePanel.Width, 0);
            SidePanel.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(SidePanel.Width, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        /// <summary>Anchors the overlay to the right edge, full video height.</summary>
        private void PositionSideOverlay()
        {
            double w = VideoContainer.ActualWidth;
            double h = VideoContainer.ActualHeight;
            if (w <= 0 || h <= 0) return;

            const double panelW = 366;
            SidePanel.Width  = panelW;
            SidePanel.Height = h;

            OverlaySidePanelPopup.Width           = panelW;
            OverlaySidePanelPopup.Height          = h;
            OverlaySidePanelPopup.HorizontalOffset = Math.Max(0, w - panelW);
            OverlaySidePanelPopup.VerticalOffset   = 0;
        }

        private void SidePanel_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_sideOverlayActive) return;
            _sideOverlayHideTimer?.Stop();
        }

        private void SidePanel_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_sideOverlayActive) return;

            if (_sideOverlayHideTimer == null)
            {
                _sideOverlayHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _sideOverlayHideTimer.Tick += (s, _) =>
                {
                    _sideOverlayHideTimer.Stop();
                    // Guard: pointer may have slipped back over the panel.
                    if (SidePanel.IsMouseOver) return;
                    HideSideOverlay();
                };
            }
            _sideOverlayHideTimer.Stop();
            _sideOverlayHideTimer.Start();
        }

        /// <summary>Collapses the overlay and returns SidePanel to its grid column.</summary>
        private void HideSideOverlay()
        {
            if (!_sideOverlayActive) return;
            _sideOverlayActive = false;
            _sideOverlayHideTimer?.Stop();

            OverlaySidePanelPopup.IsOpen = false;
            OverlaySidePanelPopup.Child  = null;

            SidePanel.RenderTransform = null;
            SidePanel.Height          = double.NaN;
            SidePanel.Width           = _sidePanelHomeWidth;

            if (_sidePanelHome != null && !_sidePanelHome.Children.Contains(SidePanel))
            {
                _sidePanelHome.Children.Add(SidePanel);
                Grid.SetColumn(SidePanel, 0);
            }

            // Restore the docked-mode visibility (collapsed while in fullscreen).
            UpdatePlaylistVisibility();
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
                    Telemetry.Track("seek", new()
                    {
                        ["method"]    = "keyboard",
                        ["target_ms"] = newMs,
                        ["delta_ms"]  = e.Key == Key.Right ? deltaMs : -deltaMs,
                    });
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
