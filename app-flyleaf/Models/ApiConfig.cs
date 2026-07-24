using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoPlayer.Services;

namespace VideoPlayer.Models
{
    /// <summary>
    /// Configuration for the local Remote Control API (F-654). Persisted to
    /// <c>%AppData%\VideoPlayer\api.json</c>, separate from UI-state settings.
    ///
    /// Safe by default: the listener binds to loopback only, and no token is required
    /// for same-machine callers. LAN exposure (<see cref="Bind"/> = "lan") is an explicit
    /// opt-in that always requires a bearer token — one is auto-generated on first LAN
    /// enable if none is set. The token is encrypted at rest via <see cref="SecretProtector"/>
    /// (DPAPI), exactly like the Plex token.
    /// </summary>
    public class ApiConfig
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayer", "api.json");

        /// <summary>Master on/off for the API server.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>TCP port. Default 47893 — distinct from the transient OAuth listener (47891).</summary>
        [JsonPropertyName("port")]
        public int Port { get; set; } = 47893;

        /// <summary>"loopback" (127.0.0.1 only, default) or "lan" (all interfaces, token-guarded).</summary>
        [JsonPropertyName("bind")]
        public string Bind { get; set; } = "loopback";

        /// <summary>
        /// Require a bearer token even for loopback callers. Off by default — on a personal
        /// desktop, local apps are trusted. Turn on for shared machines.
        /// </summary>
        [JsonPropertyName("require_token_on_loopback")]
        public bool RequireTokenOnLoopback { get; set; }

        /// <summary>DPAPI-encrypted bearer token. Never stored in plaintext.</summary>
        [JsonPropertyName("token_encrypted")]
        public string TokenEncrypted { get; set; } = "";

        /// <summary>
        /// Origins allowed for browser (CORS) callers, e.g. "http://localhost:3000".
        /// Empty (default) emits no CORS headers — server-side agents don't need them.
        /// A single "*" allows any origin.
        /// </summary>
        [JsonPropertyName("allowed_origins")]
        public List<string> AllowedOrigins { get; set; } = new();

        [JsonIgnore]
        public bool IsLan => string.Equals(Bind, "lan", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when a caller must present a valid bearer token.</summary>
        [JsonIgnore]
        public bool RequiresToken => IsLan || RequireTokenOnLoopback;

        /// <summary>The decrypted bearer token, or "" if none/undecryptable.</summary>
        [JsonIgnore]
        public string Token => SecretProtector.Unprotect(TokenEncrypted) ?? "";

        /// <summary>Set (and encrypt) the bearer token. Pass "" to clear.</summary>
        public void SetToken(string raw) => TokenEncrypted = SecretProtector.Protect(raw?.Trim());

        public static ApiConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return new ApiConfig();
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<ApiConfig>(json) ?? new ApiConfig();
            }
            catch
            {
                return new ApiConfig();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { /* config persistence is best-effort */ }
        }
    }
}
