using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

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

        public static void Log(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            Trace.WriteLine(line);
        }
    }
}
