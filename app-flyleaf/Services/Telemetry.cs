using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VideoPlayer.Services
{
    /// <summary>
    /// App-wide telemetry seam. Two sinks, deliberately tiered:
    ///
    ///  • <b>Local</b> — every call writes a structured line to the debug log
    ///    (<c>%TEMP%\videoplayer-debug.log</c> via <see cref="App.Log"/>). This is the verbose,
    ///    per-request trail for diagnosing a specific machine.
    ///  • <b>PostHog</b> — only failures and meaningful interactions are emitted as events, so we
    ///    get cross-install/cross-session aggregation without turning PostHog into a log drain
    ///    (it is billed per event). Successful, high-frequency requests stay local-only.
    ///
    /// The PostHog sink is configured by <c>telemetry.json</c> shipped next to the exe (see
    /// <see cref="TelemetrySettings"/>) — one file for the whole build, editable without a rebuild.
    /// When it is absent, disabled, or has no key, telemetry is dormant and only the local sink
    /// runs. Local logging always works.
    ///
    /// <b>Privacy:</b> Plex URLs carry the <c>X-Plex-Token</c> as a query param and often a private
    /// server host. <see cref="Endpoint"/> reduces any URL to its path only (no host, no query),
    /// so neither the token nor the server address can ever reach a sink. The PostHog
    /// <c>distinct_id</c> is an anonymous per-install GUID, not tied to any user identity.
    /// </summary>
    public static class Telemetry
    {
        private static PostHogSink? _posthog;
        private static string _distinctId = "anonymous";
        private static bool _enabled;

        // Ambient context merged into every event so we always know "who / which build".
        private static string _appVersion = "unknown";
        private static string _userEmail  = "";
        private static string _userName   = "";

        /// <summary>
        /// Wire up telemetry from <c>telemetry.json</c> (loaded next to the exe — see
        /// <see cref="TelemetrySettings"/>). Safe to call more than once; a prior PostHog sink is
        /// flushed and replaced. No-op (local sink only) when the file is absent, disabled, or has
        /// no key. Never throws.
        /// </summary>
        public static void Init()
        {
            try
            {
                _posthog?.Dispose();
                _posthog = null;
                _enabled = false;

                _appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

                var settings = TelemetrySettings.Load();
                if (!settings.IsActive) return;

                _distinctId = GetOrCreateInstallId();
                _posthog    = new PostHogSink(settings.ProjectKey, settings.Host);
                _enabled    = true;
                App.Log("[Telemetry] PostHog sink enabled.");
            }
            catch (Exception ex)
            {
                App.Log($"[Telemetry] init failed: {ex.Message}");
            }
        }

        /// <summary>Flush + release the PostHog sink. Call on app exit. Never throws.</summary>
        public static void Shutdown()
        {
            try { _posthog?.Dispose(); } catch { }
            _posthog = null;
            _enabled = false;
        }

        /// <summary>
        /// Record the outcome of a single Plex HTTP request. Always logs locally; on failure
        /// (<paramref name="error"/> non-null) also emits a <c>plex_request_failed</c> PostHog event.
        /// Successful requests are local-only to keep event volume down.
        /// </summary>
        /// <param name="op">Low-cardinality operation name, e.g. "search", "browse", "timeline".</param>
        /// <param name="url">The full request URL (scrubbed before use — never logged raw).</param>
        /// <param name="status">HTTP status code, or null if the request never got a response.</param>
        /// <param name="ms">Elapsed wall-clock time in milliseconds.</param>
        /// <param name="error">Failure detail (exception or HTTP status), or null on success.</param>
        public static void PlexRequest(string op, string url, int? status, long ms, string? error)
        {
            var endpoint = Endpoint(url);
            var code     = status?.ToString() ?? "none";

            if (error == null)
            {
                App.Log($"[Plex] {op} ok status={code} {ms}ms {endpoint}");
                return;
            }

            App.Log($"[Plex] {op} FAILED status={code} {ms}ms {endpoint} err=\"{error}\"");
            Track("plex_request_failed", new Dictionary<string, object>
            {
                ["op"]       = op,
                ["status"]   = status ?? (object)"none",
                ["ms"]       = ms,
                ["endpoint"] = endpoint,
                ["error"]    = error,
            });
        }

        /// <summary>
        /// Attach the signed-in Nullcast user to this session. Sets ambient context (added to every
        /// subsequent event) and updates the PostHog <em>person</em> via <c>$set</c>, then records a
        /// <c>user_identified</c> event. Call on login. Never throws.
        /// </summary>
        public static void SetUser(string? email, string? displayName)
        {
            _userEmail = email?.Trim() ?? "";
            _userName  = displayName?.Trim() ?? "";

            var set = new Dictionary<string, object>();
            if (_userEmail.Length > 0) set["email"] = _userEmail;
            if (_userName.Length  > 0) set["name"]  = _userName;
            Track("user_identified", new Dictionary<string, object> { ["$set"] = set });
        }

        /// <summary>Record a sign-out and clear the ambient user context. Never throws.</summary>
        public static void ClearUser()
        {
            Track("user_signed_out");
            _userEmail = "";
            _userName  = "";
        }

        /// <summary>
        /// Emit a product-analytics event to PostHog (feature use, surface interactions, …).
        /// Ambient context (install id, app version, signed-in user) is merged in automatically;
        /// explicit <paramref name="properties"/> win on key collisions. No-op when telemetry is
        /// dormant. Never throws. Per-request Plex outcomes go through <see cref="PlexRequest"/>.
        /// </summary>
        public static void Track(string eventName, Dictionary<string, object>? properties = null)
        {
            if (!_enabled || _posthog == null) return;
            _posthog.Capture(_distinctId, eventName, WithContext(properties));
        }

        /// <summary>Merge ambient context with an event's own properties (explicit props win).</summary>
        private static Dictionary<string, object> WithContext(Dictionary<string, object>? props)
        {
            var merged = new Dictionary<string, object>
            {
                ["install_id"]  = _distinctId,
                ["app_version"] = _appVersion,
            };
            if (_userEmail.Length > 0) merged["user_email"] = _userEmail;
            if (_userName.Length  > 0) merged["user_name"]  = _userName;
            if (props != null)
                foreach (var kv in props) merged[kv.Key] = kv.Value;
            return merged;
        }

        /// <summary>
        /// Reduce a URL to just its path — no scheme, host, or query. This strips both the
        /// <c>X-Plex-Token</c> (a query param) and the server address, so nothing sensitive reaches
        /// any sink. Falls back to "?" if the URL can't be parsed.
        /// </summary>
        private static string Endpoint(string url)
        {
            try { return new Uri(url).AbsolutePath; }
            catch { return "?"; }
        }

        /// <summary>
        /// Stable, anonymous per-install id used as the PostHog <c>distinct_id</c>. Persisted in a
        /// small file next to the app's other data (<c>%AppData%\VideoPlayer\install-id</c>) so it
        /// survives restarts and does not depend on any service being configured. Generated once on
        /// first run. Falls back to "anonymous" if the file can't be read or written.
        /// </summary>
        private static string GetOrCreateInstallId()
        {
            try
            {
                var dir  = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoPlayer");
                var file = Path.Combine(dir, "install-id");

                if (File.Exists(file))
                {
                    var existing = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrWhiteSpace(existing)) return existing;
                }

                Directory.CreateDirectory(dir);
                var id = Guid.NewGuid().ToString("N");
                File.WriteAllText(file, id);
                return id;
            }
            catch
            {
                return "anonymous";
            }
        }
    }
}
