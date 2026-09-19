using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer
{
    /// <summary>
    /// The gear dialog: a left nav rail over a stack of pages.
    ///
    /// <para>
    /// <b>Why a rail and not tabs.</b> A row of tabs held four before it wrapped. There are now
    /// three groups of pages — General, one per content provider, and Advanced — and a vertical
    /// rail carries that grouping and grows without a redesign.
    /// </para>
    ///
    /// <para>
    /// <b>What commits when.</b> Two different things happen in here and they behave differently
    /// on purpose:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Settings</b> (provider switches, server addresses, tokens, the General page)
    ///   are gathered and written on Save. Cancel discards them.</item>
    ///   <item><b>Signing in and out</b> are actions against a remote service that have already
    ///   happened by the time the button returns — there is nothing to "save" and nothing Cancel
    ///   could undo. The dialog reports them through <see cref="PlaylistAuthChanged"/> and
    ///   <see cref="NullcastTvAuthChanged"/>, which the caller applies either way.</item>
    /// </list>
    /// </summary>
    public partial class ServicesSettingsDialog : Window
    {
        private const string TokenSentinel = "••••••••••••";

        private readonly ServicesStore        _store;
        private readonly AppSettings          _settings;
        private readonly PlaylistAuthService  _playlistAuth;
        private readonly NullcastTvAuthService _tvAuth;

        private bool _loading;              // guards programmatic edits during load
        private bool _tokenTouched;         // true once the user edits the Plex token field
        private bool _tetherTokenTouched;   // true once the user edits the Tether key field
        private bool _tvTokenTouched;       // true once the user edits the Nullcast.TV token field

        /// <summary>True when the Playlist session changed while the dialog was open.</summary>
        public bool PlaylistAuthChanged { get; private set; }

        /// <summary>True when the Nullcast.TV session changed while the dialog was open.</summary>
        public bool NullcastTvAuthChanged { get; private set; }

        // ── Nav model ─────────────────────────────────────────

        /// <summary>One row in the nav rail: a label, the group it files under, and its page.</summary>
        private class NavEntry
        {
            public NavEntry(string label, string group, FrameworkElement page)
            {
                Label = label; Group = group; Page = page;
            }
            public string            Label { get; }
            public string            Group { get; }
            public FrameworkElement  Page  { get; }
        }

        private readonly ObservableCollection<NavEntry> _nav = new();

        public ServicesSettingsDialog(ServicesStore store, AppSettings settings,
                                      PlaylistAuthService playlistAuth,
                                      NullcastTvAuthService tvAuth)
        {
            InitializeComponent();
            _store        = store;
            _settings     = settings;
            _playlistAuth = playlistAuth;
            _tvAuth       = tvAuth;

            BuildNav();

            _loading = true;

            // General.
            PinAllDesktopsCheck.IsChecked = _settings.PinToAllDesktops;

            // Provider switches.
            var providers = _store.Providers;
            TvEnabledCheck.IsChecked        = providers.NullcastTv;
            PlaylistsEnabledCheck.IsChecked = providers.Playlists;
            PlexEnabledCheck.IsChecked      = providers.Plex;
            PodcastsEnabledCheck.IsChecked  = providers.Podcasts;
            YtMusicEnabledCheck.IsChecked   = providers.YtMusic;

            // Plex.
            ServerBox.Text = _store.PlexBaseUrl;
            ServerBox.TextChanged += (s, e) => UpdateServerPlaceholder();
            if (_store.IsPlexConfigured)
                TokenBox.Password = TokenSentinel;   // show masked, don't reveal the real token

            // Nullcast.TV.
            if (_store.HasNullcastTvToken)
                TvTokenBox.Password = TokenSentinel;

            // Tether.
            TetherAppIdBox.Text = _store.TetherAppId;
            if (_store.HasTetherCreds)
                TetherTokenBox.Password = TokenSentinel;
            TetherDevTokenBox.Text = _store.TetherDevToken;
            TetherDevFallbackCheck.IsChecked = _store.TetherDevTokenFallback;

            _loading = false;

            UpdateServerPlaceholder();
            RefreshPlaylistAccount();
            RefreshTvAccount();

            NavList.SelectedIndex = 0;
        }

        // ──────────────────────────────────────────────────────
        // Nav
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Build the rail. Every provider gets a page whether or not it is switched on — the
        /// page is where you switch it on, so hiding it would make the setting unreachable.
        /// </summary>
        private void BuildNav()
        {
            _nav.Add(new NavEntry("General", "", PageGeneral));

            _nav.Add(new NavEntry("Nullcast.TV",    "Providers", PageNullcastTv));
            _nav.Add(new NavEntry("Playlists",      "Providers", PagePlaylists));
            _nav.Add(new NavEntry("Plex",           "Providers", PagePlex));
            _nav.Add(new NavEntry("Apple Podcasts", "Providers", PagePodcasts));
            _nav.Add(new NavEntry("YouTube Music",  "Providers", PageYtMusic));

            _nav.Add(new NavEntry("Tether", "Advanced", PageTether));

            var view = new CollectionViewSource { Source = _nav };
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavEntry.Group)));
            NavList.ItemsSource = view.View;
        }

        private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavList.SelectedItem is not NavEntry entry) return;
            foreach (var row in _nav)
                row.Page.Visibility = ReferenceEquals(row, entry) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ──────────────────────────────────────────────────────
        // Plex
        // ──────────────────────────────────────────────────────

        private void UpdateServerPlaceholder()
        {
            ServerPlaceholder.Visibility = string.IsNullOrEmpty(ServerBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _tokenTouched = true;
        }

        /// <summary>The token to use: the freshly-typed one, or the stored one if untouched.</summary>
        private string ResolveToken() =>
            _tokenTouched ? TokenBox.Password : _store.GetPlexToken();

        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            TestButton.IsEnabled = false;
            SetStatus(StatusText, "Testing…", muted: true);

            var (ok, message) = await PlexService.TestConnectionAsync(ServerBox.Text, ResolveToken());
            SetStatus(StatusText, message, ok);

            TestButton.IsEnabled = true;
        }

        // ──────────────────────────────────────────────────────
        // Tether
        // ──────────────────────────────────────────────────────

        private void TetherTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _tetherTokenTouched = true;
        }

        /// <summary>The Tether key: freshly-typed one, or the stored one if untouched.</summary>
        private string ResolveTetherToken() =>
            _tetherTokenTouched ? TetherTokenBox.Password : _store.GetTetherToken();

        // ──────────────────────────────────────────────────────
        // Playlists account
        // ──────────────────────────────────────────────────────

        private void RefreshPlaylistAccount()
        {
            bool signedIn = _playlistAuth?.IsSignedIn == true;

            PlaylistAccountName.Text = signedIn
                ? (string.IsNullOrWhiteSpace(_playlistAuth.DisplayName) ? "Connected" : _playlistAuth.DisplayName)
                : "Not connected";

            var email = signedIn ? _playlistAuth.Email : "";
            PlaylistAccountEmail.Text = email;
            PlaylistAccountEmail.Visibility = string.IsNullOrWhiteSpace(email)
                ? Visibility.Collapsed : Visibility.Visible;

            PlaylistConnectBtn.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            // The service is created on the startup path, so it can still be null if the gear
            // is clicked in the first moments of a cold launch.
            PlaylistConnectBtn.IsEnabled  = _playlistAuth != null;
            PlaylistSignOutBtn.IsEnabled  = signedIn;
        }

        private async void PlaylistConnect_Click(object sender, RoutedEventArgs e)
        {
            PlaylistConnectBtn.IsEnabled = false;
            SetStatus(PlaylistStatus, "Waiting for the browser…", muted: true);
            try
            {
                await _playlistAuth.LoginAsync();
                PlaylistAuthChanged = true;
                SetStatus(PlaylistStatus, "Connected.", ok: true);
            }
            catch (Exception ex)
            {
                SetStatus(PlaylistStatus, $"Sign-in failed: {ex.Message}", ok: false);
            }
            finally
            {
                PlaylistConnectBtn.IsEnabled = true;
                RefreshPlaylistAccount();
            }
        }

        private async void PlaylistSignOut_Click(object sender, RoutedEventArgs e)
        {
            await _playlistAuth.SignOutAsync();
            PlaylistAuthChanged = true;
            SetStatus(PlaylistStatus, "Signed out.", muted: true);
            RefreshPlaylistAccount();
        }

        // ──────────────────────────────────────────────────────
        // Nullcast.TV account
        // ──────────────────────────────────────────────────────

        private void RefreshTvAccount()
        {
            bool signedIn = _tvAuth?.IsSignedIn == true;

            TvAccountName.Text = signedIn
                ? (string.IsNullOrWhiteSpace(_tvAuth.DisplayName)
                    ? (_tvAuth.UsesPersonalToken ? "Connected with a personal access token" : "Connected")
                    : _tvAuth.DisplayName)
                : "Not connected";

            var email = signedIn ? _tvAuth.Email : "";
            TvAccountEmail.Text = email;
            TvAccountEmail.Visibility = string.IsNullOrWhiteSpace(email)
                ? Visibility.Collapsed : Visibility.Visible;

            TvConnectBtn.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            TvConnectBtn.IsEnabled  = _tvAuth != null;   // null only in the first moments of a cold launch
            TvSignOutBtn.IsEnabled  = signedIn;
            // Testable once there is something to test: a live session, or a token in the box
            // that has not been saved yet.
            TvTestBtn.IsEnabled     = signedIn || (_tvTokenTouched && TvTokenBox.Password.Length > 0);
        }

        private async void TvConnect_Click(object sender, RoutedEventArgs e)
        {
            TvConnectBtn.IsEnabled = false;
            SetStatus(TvStatus, "Waiting for the browser…", muted: true);
            try
            {
                await _tvAuth.LoginAsync();
                NullcastTvAuthChanged = true;
                // Connecting is also the moment somebody means to use it. Switching the
                // provider on for them is the obvious next step, not a decision to re-ask.
                TvEnabledCheck.IsChecked = true;
                SetStatus(TvStatus, "Connected.", ok: true);
            }
            catch (Exception ex)
            {
                SetStatus(TvStatus, $"Sign-in failed: {ex.Message}", ok: false);
            }
            finally
            {
                TvConnectBtn.IsEnabled = true;
                RefreshTvAccount();
            }
        }

        private async void TvSignOut_Click(object sender, RoutedEventArgs e)
        {
            await _tvAuth.SignOutAsync();
            NullcastTvAuthChanged = true;
            // SignOutAsync already cleared any stored personal token; clear the field to match,
            // then drop the "touched" flag that this very edit just set so Save has nothing
            // left to write back.
            TvTokenBox.Password = "";
            _tvTokenTouched     = false;
            SetStatus(TvStatus, "Signed out.", muted: true);
            RefreshTvAccount();
        }

        private async void TvTest_Click(object sender, RoutedEventArgs e)
        {
            TvTestBtn.IsEnabled = false;
            SetStatus(TvStatus, "Testing…", muted: true);

            // A token typed but not yet saved is tested as-is, so Test never commits something
            // Cancel is supposed to discard — the same contract the Plex page has.
            var (ok, message) = _tvTokenTouched
                ? await NullcastTvService.TestCredentialAsync(TvTokenBox.Password)
                : await new NullcastTvService(_tvAuth).TestConnectionAsync();
            SetStatus(TvStatus, message, ok);

            TvTestBtn.IsEnabled = true;
        }

        private void TvTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _tvTokenTouched = true;
            RefreshTvAccount();   // a typed token makes "Test connection" meaningful
        }

        /// <summary>Write a freshly-typed personal access token through to the store (encrypted).</summary>
        private void CommitNullcastTvToken()
        {
            if (!_tvTokenTouched) return;
            _store.SetNullcastTvToken(TvTokenBox.Password);
            _tvTokenTouched = false;
            // A pasted token is itself a credential change — the caller has a tab to reload.
            NullcastTvAuthChanged = true;
        }

        /// <summary>
        /// Turning Nullcast.TV on is a decision to sign in, so say so at the moment it is made
        /// rather than leaving an empty tab to explain itself later.
        /// </summary>
        private void TvEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (TvEnabledCheck.IsChecked == true && _tvAuth?.IsSignedIn != true)
                SetStatus(TvStatus, "Connect an account below — Nullcast.TV needs one to show its full catalog.",
                          muted: true);
        }

        // ──────────────────────────────────────────────────────
        // Commit
        // ──────────────────────────────────────────────────────

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var server = ServerBox.Text.Trim();
            var token  = ResolveToken();

            // Plex is optional — only validate/save when a server address was entered.
            if (!string.IsNullOrEmpty(server))
            {
                if (string.IsNullOrEmpty(token))
                {
                    // The status line lives on the Plex page, so go there — Save is reachable
                    // from any page and a silent refusal would look like a broken button.
                    SelectPage(PagePlex);
                    SetStatus(StatusText, "Enter a Plex token.", ok: false);
                    return;
                }
                _store.SetPlex(server, token);   // encrypts the token before persisting
            }

            // General settings are written straight onto the caller's AppSettings; MainWindow
            // persists and applies them when ShowDialog() returns true.
            _settings.PinToAllDesktops = PinAllDesktopsCheck.IsChecked == true;

            // Provider switches, committed as one block.
            var providers = _store.Providers;
            providers.NullcastTv = TvEnabledCheck.IsChecked        == true;
            providers.Playlists  = PlaylistsEnabledCheck.IsChecked == true;
            providers.Plex       = PlexEnabledCheck.IsChecked      == true;
            providers.Podcasts   = PodcastsEnabledCheck.IsChecked  == true;
            providers.YtMusic    = YtMusicEnabledCheck.IsChecked   == true;
            _store.SaveProviders();

            CommitNullcastTvToken();

            // Tether settings: provisioned app id + token (optional) plus the dev-token
            // fallback value + toggle. Persisted together.
            _store.SetTether(TetherAppIdBox.Text.Trim(),
                             ResolveTetherToken(),
                             TetherDevTokenBox.Text.Trim(),
                             TetherDevFallbackCheck.IsChecked == true);

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ──────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────

        /// <summary>Jump the rail (and therefore the right pane) to a given page.</summary>
        private void SelectPage(FrameworkElement page)
        {
            foreach (var entry in _nav)
            {
                if (!ReferenceEquals(entry.Page, page)) continue;
                NavList.SelectedItem = entry;
                NavList.ScrollIntoView(entry);
                return;
            }
        }

        private static readonly Brush StatusMuted = new SolidColorBrush(Color.FromRgb(0x84, 0x8B, 0x9F));
        private static readonly Brush StatusOk    = new SolidColorBrush(Color.FromRgb(0x44, 0xCC, 0x44));
        private static readonly Brush StatusBad   = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66));

        private static void SetStatus(TextBlock target, string message, bool ok = false, bool muted = false)
        {
            if (target == null) return;
            target.Text       = message;
            target.Foreground = muted ? StatusMuted : ok ? StatusOk : StatusBad;
            target.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
