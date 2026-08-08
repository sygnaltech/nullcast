using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using FlyleafLib;

namespace VideoPlayer
{
    public partial class App : Application
    {
        private static string _logPath;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _logPath = Path.Combine(Path.GetTempPath(), "videoplayer-debug.log");
            Trace.Listeners.Add(new TextWriterTraceListener(_logPath));
            Trace.AutoFlush = true;
            Log($"=== App started ===");

            // Telemetry config lives in telemetry.json next to the exe — dormant unless a key is set.
            Services.Telemetry.Init();
            Services.Telemetry.Track("app_started");

            // Initialize Flyleaf engine (must happen before any Player is created)
            Engine.Start(new EngineConfig
            {
                FFmpegPath     = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg"),
                FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel.Quiet,
                UIRefresh      = true,
            });

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var msg = ex.ExceptionObject?.ToString() ?? "Unknown error";
                Log($"[CRASH][UnhandledException] {msg}");
                MessageBox.Show($"Crash logged to:\n{_logPath}\n\n{msg}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            DispatcherUnhandledException += (s, ex) =>
            {
                Log($"[CRASH][DispatcherUnhandledException] {ex.Exception}");
                MessageBox.Show($"Crash logged to:\n{_logPath}\n\n{ex.Exception}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("=== App exiting: shutting telemetry down ===");
            // Flush + release the PostHog telemetry sink (no-op when analytics is off). Bounded so a
            // telemetry flush can never hang shutdown — see PostHogSink.Dispose for the deadlock this
            // guards against.
            Services.Telemetry.Shutdown();
            Log("=== App exit complete ===");
            base.OnExit(e);
        }

        public static void Log(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            Trace.WriteLine(line);
        }
    }
}
