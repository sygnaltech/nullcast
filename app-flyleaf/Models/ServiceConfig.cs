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
    /// Credentials linking this app to the local <c>Tether</c> broker (M3 per-app auth).
    /// The <see cref="TokenEncrypted"/> key is DPAPI-protected at rest (see <c>SecretProtector</c>);
    /// <see cref="AppId"/> is not a secret. When unset, the client falls back to the M1 dev token.
    /// </summary>
    public class TetherConfig
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
    /// Credentials for the Nullcast.TV catalog (<c>https://nullcast.tv/api/v1</c>).
    /// <para>
    /// OAuth is the normal door and its tokens live in their own file, not here — this row
    /// holds the <see cref="ClientId"/> handed back by dynamic client registration (RFC 7591),
    /// which is not a secret but must survive a restart or the app re-registers on every
    /// launch and litters the server with dead client rows.
    /// </para>
    /// <para>
    /// <see cref="TokenEncrypted"/> is the other door: a personal access token pasted in by
    /// hand, for an install that cannot run a browser redirect. DPAPI-protected at rest.
    /// </para>
    /// </summary>
    public class NullcastTvConfig
    {
        /// <summary>Client id from <c>POST /oauth/register</c>. Public by design, not a secret.</summary>
        [JsonPropertyName("client_id")]
        public string ClientId { get; set; } = "";

        /// <summary>DPAPI-encrypted personal access token (<c>aitv_pat_…</c>), or "" for none.</summary>
        [JsonPropertyName("token_encrypted")]
        public string TokenEncrypted { get; set; } = "";
    }

    /// <summary>
    /// Which content providers this install actually uses.
    /// <para>
    /// Every provider is independently switchable: a sidebar tab, its search source and its
    /// settings page all appear only when its switch is on. Nobody should have to look at a
    /// Plex tab on a machine with no Plex server, and a provider that requires an account
    /// should not be able to sit half-on.
    /// </para>
    /// <para>
    /// Everything that shipped before this switch existed defaults to <c>true</c>, so an
    /// upgrade is a no-op. Nullcast.TV defaults to <c>false</c> — it is new, and turning it
    /// on is a decision to sign in.
    /// </para>
    /// </summary>
    public class ProvidersConfig
    {
        [JsonPropertyName("playlists")]
        public bool Playlists { get; set; } = true;

        [JsonPropertyName("nullcast_tv")]
        public bool NullcastTv { get; set; }

        [JsonPropertyName("plex")]
        public bool Plex { get; set; } = true;

        [JsonPropertyName("podcasts")]
        public bool Podcasts { get; set; } = true;

        [JsonPropertyName("ytmusic")]
        public bool YtMusic { get; set; } = true;
    }

    /// <summary>
    /// Root of <c>services.json</c> — the registry of external services the player can
    /// connect to, plus the per-install switch that decides which of them are in use.
    /// </summary>
    public class ServicesConfig
    {
        /// <summary>Per-provider on/off. Never null — an absent block means "the old defaults".</summary>
        [JsonPropertyName("providers")]
        public ProvidersConfig Providers { get; set; } = new();

        [JsonPropertyName("plex")]
        public PlexServerConfig? Plex { get; set; }

        [JsonPropertyName("nullcast_tv")]
        public NullcastTvConfig? NullcastTv { get; set; }

        [JsonPropertyName("tether")]
        public TetherConfig? Tether { get; set; }

        /// <summary>
        /// Back-compat: pre-rebrand builds stored these credentials under <c>browser_helper</c>.
        /// Deserialize that legacy key into <see cref="Tether"/> when the new key is absent, so
        /// existing saved credentials survive the rename. The getter is always null so we never
        /// write the legacy key back out (config migrates forward on the next save).
        /// </summary>
        [JsonPropertyName("browser_helper")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TetherConfig? TetherLegacy
        {
            get => null;
            set { if (value != null && Tether == null) Tether = value; }
        }
    }
}
