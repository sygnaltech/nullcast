using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
            // Disposing flushes any buffered events — a synchronous, blocking network flush.
            //
            // This runs from App.OnExit on the WPF Dispatcher (UI) thread, where a
            // DispatcherSynchronizationContext is installed. The SDK's Dispose blocks that thread
            // on an async flush whose continuation is posted back to the very same thread, so it
            // DEADLOCKS: the window closes but the process's foreground UI thread hangs forever and
            // the app never exits (a launching terminal never gets its prompt back).
            //
            // Fix: run the blocking Dispose on a thread-pool thread, which carries no
            // SynchronizationContext, so the flush completes normally (~sub-second). Cap the wait
            // so a dead network can't stall exit either — telemetry is best-effort, and losing the
            // last few buffered events on a hung flush is preferable to hanging shutdown.
            if (_client is not IDisposable d) return;
            try
            {
                Task.Run(() => { try { d.Dispose(); } catch { } })
                    .Wait(TimeSpan.FromSeconds(3));
            }
            catch { }
        }
    }
}
