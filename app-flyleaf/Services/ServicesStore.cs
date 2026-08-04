using System;
using System.IO;
using System.Text.Json;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Loads/saves the external-services registry (<c>%AppData%\VideoPlayer\services.json</c>).
    /// Secrets (the Plex token) are encrypted via <see cref="SecretProtector"/> before they
    /// touch disk and decrypted on demand — the raw token is never persisted.
    /// </summary>
    public class ServicesStore
    {
        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayer", "services.json");

        private ServicesConfig _config = new();

        public void Load()
        {
            try
            {
                if (!File.Exists(StorePath)) { _config = new(); return; }
                var json = File.ReadAllText(StorePath);
                _config = JsonSerializer.Deserialize<ServicesConfig>(json) ?? new();
            }
            catch
            {
                _config = new();
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                var json = JsonSerializer.Serialize(_config,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StorePath, json);
            }
            catch { }
        }

        // ── Plex ──────────────────────────────────────────────

        /// <summary>The saved Plex base URL (no trailing slash), or "" if none.</summary>
        public string PlexBaseUrl => (_config.Plex?.BaseUrl ?? "").TrimEnd('/');

        /// <summary>True when there is an enabled Plex config with a URL and a decryptable token.</summary>
        public bool IsPlexConfigured =>
            _config.Plex is { Enabled: true } p
            && !string.IsNullOrWhiteSpace(p.BaseUrl)
            && !string.IsNullOrEmpty(GetPlexToken());

        /// <summary>Decrypts and returns the stored Plex token, or "" if unset/undecryptable.</summary>
        public string GetPlexToken() => SecretProtector.Unprotect(_config.Plex?.TokenEncrypted) ?? "";

        /// <summary>Store the Plex server URL and token (token is encrypted before saving).</summary>
        public void SetPlex(string baseUrl, string rawToken)
        {
            _config.Plex ??= new PlexServerConfig();
            _config.Plex.BaseUrl        = (baseUrl ?? "").Trim().TrimEnd('/');
            _config.Plex.TokenEncrypted = SecretProtector.Protect(rawToken?.Trim());
            _config.Plex.Enabled        = true;
            Save();
        }

        public void ClearPlex()
        {
            _config.Plex = null;
            Save();
        }

        // ── Tether (live-cookie broker) ──────────────────────

        /// <summary>The configured Tether app id, or "" if none.</summary>
        public string TetherAppId => _config.Tether?.AppId ?? "";

        /// <summary>Decrypts and returns the stored Tether token, or "" if unset/undecryptable.</summary>
        public string GetTetherToken() =>
            SecretProtector.Unprotect(_config.Tether?.TokenEncrypted) ?? "";

        /// <summary>True when a provisioned app id + token are both present (else use the dev token).</summary>
        public bool HasTetherCreds =>
            !string.IsNullOrWhiteSpace(TetherAppId) && !string.IsNullOrEmpty(GetTetherToken());

        /// <summary>The dev token used as a fallback — the configured one, or the built-in default.</summary>
        public string TetherDevToken =>
            string.IsNullOrWhiteSpace(_config.Tether?.DevToken)
                ? DefaultDevToken
                : _config.Tether.DevToken;

        /// <summary>Whether the dev-token fallback is enabled (default on).</summary>
        public bool TetherDevTokenFallback => _config.Tether?.DevTokenFallback ?? true;

        /// <summary>The built-in M1 dev token (used when none is configured).</summary>
        public const string DefaultDevToken = "dev-token-tether";

        /// <summary>
        /// Store the Tether settings: provisioned app id + token (encrypted), the dev-token
        /// fallback value ("" → built-in default), and whether the dev-token fallback is enabled.
        /// </summary>
        public void SetTether(string appId, string rawToken, string devToken, bool devTokenFallback)
        {
            _config.Tether ??= new TetherConfig();
            _config.Tether.AppId            = (appId ?? "").Trim();
            _config.Tether.TokenEncrypted   = SecretProtector.Protect(rawToken?.Trim());
            // Store "" when it matches the built-in default so we don't pin a stale copy.
            var dt = (devToken ?? "").Trim();
            _config.Tether.DevToken         = dt == DefaultDevToken ? "" : dt;
            _config.Tether.DevTokenFallback = devTokenFallback;
            Save();
        }

        public void ClearTether()
        {
            _config.Tether = null;
            Save();
        }
    }
}
