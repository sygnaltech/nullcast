using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VideoPlayer.Services;

namespace VideoPlayer
{
    /// <summary>
    /// "Gear" settings dialog for external services. v1 configures a single Plex
    /// Media Server (address + token). The token is written through
    /// <see cref="ServicesStore"/>, which encrypts it at rest via DPAPI.
    /// </summary>
    public partial class ServicesSettingsDialog : Window
    {
        private const string TokenSentinel = "••••••••••••";

        private readonly ServicesStore _store;
        private bool _loading;        // guards programmatic edits during load
        private bool _tokenTouched;   // true once the user edits the Plex token field
        private bool _bhTokenTouched; // true once the user edits the browser-helper key field
        private readonly bool _hadExistingToken;

        public ServicesSettingsDialog(ServicesStore store)
        {
            InitializeComponent();
            _store = store;

            _loading = true;
            ServerBox.Text = _store.PlexBaseUrl;
            ServerBox.TextChanged += (s, e) => UpdateServerPlaceholder();

            _hadExistingToken = _store.IsPlexConfigured;
            if (_hadExistingToken)
                TokenBox.Password = TokenSentinel; // show masked, don't reveal the real token

            // browser-helper credentials.
            BhAppIdBox.Text = _store.BrowserHelperAppId;
            if (_store.HasBrowserHelperCreds)
                BhTokenBox.Password = TokenSentinel;
            BhDevTokenBox.Text = _store.BrowserHelperDevToken;
            BhDevFallbackCheck.IsChecked = _store.BrowserHelperDevTokenFallback;
            _loading = false;

            UpdateServerPlaceholder();
            ServerBox.Focus();
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

        private void BhTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _bhTokenTouched = true;
        }

        /// <summary>The browser-helper key: freshly-typed one, or the stored one if untouched.</summary>
        private string ResolveBhToken() =>
            _bhTokenTouched ? BhTokenBox.Password : _store.GetBrowserHelperToken();

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
                    SetStatus("Enter a Plex token.", ok: false);
                    return;
                }
                _store.SetPlex(server, token); // encrypts the token before persisting
            }

            // browser-helper settings: provisioned app id + token (optional) plus the dev-token
            // fallback value + toggle. Persisted together.
            var bhAppId    = BhAppIdBox.Text.Trim();
            var bhToken    = ResolveBhToken();
            var bhDevToken = BhDevTokenBox.Text.Trim();
            var bhFallback = BhDevFallbackCheck.IsChecked == true;
            _store.SetBrowserHelper(bhAppId, bhToken, bhDevToken, bhFallback);

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
