using System.Text.Json.Serialization;

namespace VideoPlayer.Models
{
    /// <summary>
    /// Configuration for a connected Plex Media Server. The token is never stored in
    /// the clear: <see cref="TokenEncrypted"/> holds a DPAPI-protected base64 blob
    /// (see <c>SecretProtector</c>). <see cref="BaseUrl"/> is not a secret.
    /// </summary>
    public class PlexServerConfig
    {
        [JsonPropertyName("base_url")]
        public string BaseUrl { get; set; } = "";

        [JsonPropertyName("token_encrypted")]
        public string TokenEncrypted { get; set; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Credentials linking this app to the local <c>browser-helper</c> broker (M3 per-app auth).
    /// The <see cref="TokenEncrypted"/> key is DPAPI-protected at rest (see <c>SecretProtector</c>);
    /// <see cref="AppId"/> is not a secret. When unset, the client falls back to the M1 dev token.
    /// </summary>
    public class BrowserHelperConfig
    {
        [JsonPropertyName("app_id")]
        public string AppId { get; set; } = "";

        [JsonPropertyName("token_encrypted")]
        public string TokenEncrypted { get; set; } = "";

        /// <summary>The M1 dev token used as a fallback. Empty → the built-in default. Not a secret.</summary>
        [JsonPropertyName("dev_token")]
        public string DevToken { get; set; } = "";

        /// <summary>Whether to fall back to the dev token when the provisioned credential fails.</summary>
        [JsonPropertyName("dev_token_fallback")]
        public bool DevTokenFallback { get; set; } = true;
    }

    /// <summary>
    /// Root of <c>services.json</c> — the registry of external services the player can
    /// connect to. Plex is the first; the shape leaves room for more.
    /// </summary>
    public class ServicesConfig
    {
        [JsonPropertyName("plex")]
        public PlexServerConfig? Plex { get; set; }

        [JsonPropertyName("browser_helper")]
        public BrowserHelperConfig? BrowserHelper { get; set; }
    }
}
