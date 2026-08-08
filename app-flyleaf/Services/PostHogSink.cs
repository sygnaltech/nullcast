using System;
using System.Collections.Generic;
using PostHog;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Thin, defensive wrapper around the PostHog .NET SDK. Isolating every reference to the
    /// SDK here means the rest of the app talks only to <see cref="Telemetry"/> and never
    /// touches PostHog types — if the SDK's surface changes, this is the only file to update.
    ///
    /// The client is fire-and-forget: <see cref="Capture"/> never throws (failures are swallowed),
    /// so a telemetry hiccup can never disrupt playback. Construction is also guarded — a bad key
    /// or host leaves the sink disabled rather than crashing startup.
    /// </summary>
    public sealed class PostHogSink : IDisposable
    {
        private readonly PostHogClient? _client;

        public PostHogSink(string apiKey, string? host)
        {
            try
            {
                var opts = new PostHogOptions { ProjectApiKey = apiKey };
                if (!string.IsNullOrWhiteSpace(host))
                    opts.HostUrl = new Uri(host);
                _client = new PostHogClient(opts);
            }
            catch
            {
                _client = null; // stay dormant on any construction failure
            }
        }

        /// <summary>Enqueue an event. Never throws; no-op if the client failed to construct.</summary>
        public void Capture(string distinctId, string eventName, Dictionary<string, object>? properties)
        {
            if (_client == null) return;
            try
            {
                if (properties == null)
                    _client.Capture(distinctId, eventName);
                else
                    _client.Capture(distinctId, eventName, properties);
            }
            catch
            {
                // Telemetry is best-effort — never let it surface to the user.
            }
        }

        public void Dispose()
        {
            // Disposing flushes any buffered events. Guarded: shutdown must never throw.
            try { (_client as IDisposable)?.Dispose(); } catch { }
        }
    }
}
