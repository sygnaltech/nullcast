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
    /// Root of <c>services.json</c> — the registry of external services the player can
    /// connect to. Plex is the first; the shape leaves room for more.
    /// </summary>
    public class ServicesConfig
    {
        [JsonPropertyName("plex")]
        public PlexServerConfig? Plex { get; set; }
    }
}
