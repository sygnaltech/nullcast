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
    }
}
