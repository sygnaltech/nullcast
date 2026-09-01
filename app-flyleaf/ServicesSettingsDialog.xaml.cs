using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer
{
    /// <summary>
    /// "Gear" settings dialog, tabbed into General / Plex Media Server / Tether.
    /// <para>
    /// The two service tabs are written through <see cref="ServicesStore"/>, which encrypts the
    /// tokens at rest via DPAPI. The General tab edits the caller's <see cref="AppSettings"/>
    /// in place — that object is owned by <c>MainWindow</c>, which persists it and re-applies
    /// anything with a live effect once the dialog returns true.
    /// </para>
    /// </summary>
    public partial class ServicesSettingsDialog : Window
    {
        private const string TokenSentinel = "••••••••••••";

        /// <summary>Tab order is General, Plex, Tether — see the TabControl in the XAML.</summary>
        private const int PlexTabIndex = 1;

        private readonly ServicesStore _store;
        private readonly AppSettings _settings;
        private bool _loading;        // guards programmatic edits during load
        private bool _tokenTouched;       // true once the user edits the Plex token field
        private bool _tetherTokenTouched; // true once the user edits the Tether key field
        private readonly bool _hadExistingToken;

        public ServicesSettingsDialog(ServicesStore store, AppSettings settings)
        {
            InitializeComponent();
            _store    = store;
            _settings = settings;

            _loading = true;

            // General.
            PinAllDesktopsCheck.IsChecked = _settings.PinToAllDesktops;

            ServerBox.Text = _store.PlexBaseUrl;
            ServerBox.TextChanged += (s, e) => UpdateServerPlaceholder();

            _hadExistingToken = _store.IsPlexConfigured;
            if (_hadExistingToken)
                TokenBox.Password = TokenSentinel; // show masked, don't reveal the real token

            // Tether credentials.
            TetherAppIdBox.Text = _store.TetherAppId;
            if (_store.HasTetherCreds)
                TetherTokenBox.Password = TokenSentinel;
            TetherDevTokenBox.Text = _store.TetherDevToken;
            TetherDevFallbackCheck.IsChecked = _store.TetherDevTokenFallback;
            _loading = false;

            UpdateServerPlaceholder();
        }

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

        private void TetherTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _tetherTokenTouched = true;
        }

        /// <summary>The Tether key: freshly-typed one, or the stored one if untouched.</summary>
        private string ResolveTetherToken() =>
            _tetherTokenTouched ? TetherTokenBox.Password : _store.GetTetherToken();

        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            TestButton.IsEnabled = false;
            SetStatus("Testing…", muted: true);

            var (ok, message) = await PlexService.TestConnectionAsync(ServerBox.Text, ResolveToken());
            SetStatus(message, ok);

            TestButton.IsEnabled = true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var server = ServerBox.Text.Trim();
            var token  = ResolveToken();

            // Plex is optional — only validate/save when a server address was entered.
            if (!string.IsNullOrEmpty(server))
            {
                if (string.IsNullOrEmpty(token))
                {
                    // The status line lives on the Plex tab, so surface it — Save is reachable
                    // from any tab and a silent refusal would look like a broken button.
                    SettingsTabs.SelectedIndex = PlexTabIndex;
                    SetStatus("Enter a Plex token.", ok: false);
                    return;
                }
                _store.SetPlex(server, token); // encrypts the token before persisting
            }

            // General settings are written straight onto the caller's AppSettings; MainWindow
            // persists and applies them when ShowDialog() returns true.
            _settings.PinToAllDesktops = PinAllDesktopsCheck.IsChecked == true;

            // Tether settings: provisioned app id + token (optional) plus the dev-token
            // fallback value + toggle. Persisted together.
            var tetherAppId    = TetherAppIdBox.Text.Trim();
            var tetherToken    = ResolveTetherToken();
            var tetherDevToken = TetherDevTokenBox.Text.Trim();
            var tetherFallback = TetherDevFallbackCheck.IsChecked == true;
            _store.SetTether(tetherAppId, tetherToken, tetherDevToken, tetherFallback);

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SetStatus(string message, bool ok = false, bool muted = false)
        {
            StatusText.Text = message;
            StatusText.Foreground = muted
                ? new SolidColorBrush(Color.FromRgb(0x84, 0x8B, 0x9F))
                : ok
                    ? new SolidColorBrush(Color.FromRgb(0x44, 0xCC, 0x44))
                    : new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66));
        }
    }
}
