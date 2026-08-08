using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoPlayer.Services
{
    /// <summary>
    /// PostHog telemetry settings, loaded at runtime from <c>telemetry.json</c> shipped next to the
    /// executable (<see cref="AppContext.BaseDirectory"/>). Editing that file and relaunching applies
    /// a new key with no code rebuild.
    ///
    /// <see cref="ProjectKey"/> is a PostHog <b>project</b> key — a public, write-only ingest key meant
    /// to ship inside the client — so committing it is safe; it is not a secret. Telemetry stays
    /// dormant unless <see cref="Enabled"/> is true and a non-empty key is present.
    /// </summary>
    public sealed class TelemetrySettings
    {
        /// <summary>PostHog project (ingest) key, e.g. <c>phc_xxxx</c>. Empty ⇒ telemetry disabled.</summary>
        [JsonPropertyName("project_key")]
        public string ProjectKey { get; set; } = "";

        /// <summary>PostHog host. Empty ⇒ SDK default (US cloud). EU: https://eu.i.posthog.com</summary>
        [JsonPropertyName("host")]
        public string Host { get; set; } = "";

        /// <summary>Master switch. False keeps telemetry off even with a key present.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>True when telemetry should actually run (enabled and a key is set).</summary>
        [JsonIgnore]
        public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(ProjectKey);

        /// <summary>
        /// Load <c>telemetry.json</c> from the app directory. Tolerant of <c>//</c> comments,
        /// trailing commas, and unknown fields (e.g. a <c>_comment</c>). Returns a disabled default
        /// if the file is missing or unreadable — telemetry never blocks startup.
        /// </summary>
        public static TelemetrySettings Load()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "telemetry.json");
                if (!File.Exists(path)) return new TelemetrySettings { Enabled = false };

                var json = File.ReadAllText(path);
                var opts = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                };
                return JsonSerializer.Deserialize<TelemetrySettings>(json, opts)
                       ?? new TelemetrySettings { Enabled = false };
            }
            catch
            {
                return new TelemetrySettings { Enabled = false };
            }
        }
    }
}
