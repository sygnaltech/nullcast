using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace VideoPlayer
{
    public partial class MainWindow : Window
    {
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private bool _isDraggingSlider;
        private DispatcherTimer _timer;
        private string _ytdlpPath;
        private bool _ytdlpReady;

        public ICommand OpenUrlCommand { get; }
        public ICommand PlayPauseCommand { get; }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            OpenUrlCommand = new RelayCommand(_ => OpenUrl_Click(null, null));
            PlayPauseCommand = new RelayCommand(_ => PlayPause_Click(null, null));

            InitializeVLC();
            SetupTimer();
            InitializeYtDlp();
        }

        private async void InitializeYtDlp()
        {
            try
            {
                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                _ytdlpPath = Path.Combine(appDir, "yt-dlp.exe");

                if (!File.Exists(_ytdlpPath))
                {
                    StatusText.Text = "Downloading yt-dlp (first run only)...";
                    await DownloadYtDlp(_ytdlpPath);
                }

                _ytdlpReady = true;
                StatusText.Text = "Press Ctrl+O or File > Open URL to load a video";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to initialize yt-dlp: {ex.Message}";
            }
        }

        private async Task DownloadYtDlp(string targetPath)
        {
            const string url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
            using var client = new HttpClient();
            var bytes = await client.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(targetPath, bytes);
        }

        private void InitializeVLC()
        {
            Core.Initialize();
            _libVLC = new LibVLC("--no-xlib");
            _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

            // Must set after the VideoView is loaded
            Loaded += (s, e) =>
            {
                VideoView.MediaPlayer = _mediaPlayer;

                // Pin to all virtual desktops
                var hwnd = new WindowInteropHelper(this).Handle;
                var pinned = VirtualDesktopPinner.PinWindow(hwnd);
                Debug.WriteLine($"[VideoPlayer] Virtual desktop pin: {(pinned ? "success" : "failed")}, HWND: {hwnd}");
            };

            _mediaPlayer.Playing += (s, e) => Dispatcher.Invoke(() =>
            {
                PlayPauseButton.Content = "⏸";
                StatusText.Visibility = Visibility.Collapsed;
            });

            _mediaPlayer.Paused += (s, e) => Dispatcher.Invoke(() =>
            {
                PlayPauseButton.Content = "▶";
            });

            _mediaPlayer.Stopped += (s, e) => Dispatcher.Invoke(() =>
            {
                PlayPauseButton.Content = "▶";
                ProgressSlider.Value = 0;
            });

            _mediaPlayer.EndReached += (s, e) => Dispatcher.Invoke(() =>
            {
                PlayPauseButton.Content = "▶";
            });
        }

        private void SetupTimer()
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_mediaPlayer == null || !_mediaPlayer.IsPlaying || _isDraggingSlider)
                return;

            var length = _mediaPlayer.Length;
            var time = _mediaPlayer.Time;

            if (length > 0)
            {
                ProgressSlider.Value = (time * 100.0) / length;
                TimeDisplay.Text = $"{FormatTime(time)} / {FormatTime(length)}";
            }
        }

        private string FormatTime(long milliseconds)
        {
            var ts = TimeSpan.FromMilliseconds(milliseconds);
            return ts.Hours > 0
                ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        private async void OpenUrl_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenUrlDialog { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                await PlayYouTubeUrl(dialog.Url);
            }
        }

        private async Task PlayYouTubeUrl(string url)
        {
            try
            {
                StatusText.Text = "Loading video...";
                StatusText.Visibility = Visibility.Visible;

                _mediaPlayer.Stop();

                if (!_ytdlpReady)
                {
                    MessageBox.Show("yt-dlp is not ready yet. Please wait.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Get video title
                var title = await RunYtDlp("--get-title", url);
                if (!string.IsNullOrEmpty(title))
                {
                    Title = $"Video Player - {title.Trim()}";
                }

                // Get the direct stream URL
                var streamUrl = await RunYtDlp("-f best -g", url);

                if (string.IsNullOrEmpty(streamUrl))
                {
                    throw new Exception("Could not get stream URL");
                }

                var media = new Media(_libVLC, new Uri(streamUrl.Trim()));
                _mediaPlayer.Play(media);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error loading video";
                MessageBox.Show($"Error loading video: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<string> RunYtDlp(string arguments, string url)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ytdlpPath,
                    Arguments = $"{arguments} \"{url}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
            {
                throw new Exception(error);
            }

            return output.Split('\n')[0];
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;

            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
            }
            else
            {
                _mediaPlayer.Play();
            }
        }

        private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = false;
            if (_mediaPlayer != null && _mediaPlayer.Length > 0)
            {
                var newTime = (long)(ProgressSlider.Value * _mediaPlayer.Length / 100.0);
                _mediaPlayer.Time = newTime;
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Volume = (int)VolumeSlider.Value;
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _timer?.Stop();
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }

        private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        private void VideoView_Loaded(object sender, RoutedEventArgs e)
        {
            VideoView.Background = System.Windows.Media.Brushes.Black;

            // Find the HwndHost inside VideoView and set its background
            SetHwndBackground();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int crColor);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        private const int GCL_HBRBACKGROUND = -10;

        private void SetHwndBackground()
        {
            try
            {
                // Find the HwndHost in the visual tree
                var hwndHost = FindVisualChild<HwndHost>(VideoView);
                if (hwndHost != null)
                {
                    var hwnd = hwndHost.Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        // Create a black brush and set it as the window class background
                        var blackBrush = CreateSolidBrush(0x00000000); // RGB(0,0,0)
                        SetClassLongPtr(hwnd, GCL_HBRBACKGROUND, blackBrush);
                        InvalidateRect(hwnd, IntPtr.Zero, true);
                    }
                }
            }
            catch
            {
                // Ignore errors - video will still work
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;

                var childResult = FindVisualChild<T>(child);
                if (childResult != null)
                    return childResult;
            }
            return null;
        }

        private bool _isFullscreen;
        private WindowState _previousWindowState;
        private double _previousWidth;
        private double _previousHeight;
        private double _previousLeft;
        private double _previousTop;

        private void ToggleFullscreen()
        {
            if (_isFullscreen)
            {
                // Exit fullscreen
                _isFullscreen = false;
                MenuBar.Visibility = Visibility.Visible;
                ControlsBar.Visibility = Visibility.Visible;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                Topmost = false;

                WindowState = _previousWindowState;
                if (_previousWindowState == WindowState.Normal)
                {
                    Width = _previousWidth;
                    Height = _previousHeight;
                    Left = _previousLeft;
                    Top = _previousTop;
                }
            }
            else
            {
                // Enter fullscreen
                _isFullscreen = true;
                _previousWindowState = WindowState;
                _previousWidth = Width;
                _previousHeight = Height;
                _previousLeft = Left;
                _previousTop = Top;

                MenuBar.Visibility = Visibility.Collapsed;
                ControlsBar.Visibility = Visibility.Collapsed;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                Topmost = true;

                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                }

                // Get the current monitor's working area using WPF
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var screen = System.Windows.Forms.Screen.FromHandle(hwnd);

                // Convert from device pixels to WPF units (DPI-aware)
                var source = PresentationSource.FromVisual(this);
                var dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                var dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

                Left = screen.Bounds.Left * dpiX;
                Top = screen.Bounds.Top * dpiY;
                Width = screen.Bounds.Width * dpiX;
                Height = screen.Bounds.Height * dpiY;
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _isFullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        private void VideoArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;

            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                var text = e.Data.GetData(DataFormats.Text) as string;
                if (IsYouTubeUrl(text))
                {
                    e.Effects = DragDropEffects.Copy;
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = e.Data.GetData(DataFormats.UnicodeText) as string;
                if (IsYouTubeUrl(text))
                {
                    e.Effects = DragDropEffects.Copy;
                }
            }

            e.Handled = true;
        }

        private async void VideoArea_Drop(object sender, DragEventArgs e)
        {
            string url = null;

            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                url = e.Data.GetData(DataFormats.Text) as string;
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                url = e.Data.GetData(DataFormats.UnicodeText) as string;
            }

            if (!string.IsNullOrEmpty(url))
            {
                url = ExtractYouTubeUrl(url);
                if (!string.IsNullOrEmpty(url))
                {
                    await PlayYouTubeUrl(url);
                }
            }
        }

        private bool IsYouTubeUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.Contains("youtube.com") || text.Contains("youtu.be");
        }

        private string ExtractYouTubeUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            // Match YouTube URLs
            var pattern = @"(https?://)?(www\.)?(youtube\.com/watch\?v=|youtu\.be/|youtube\.com/shorts/)[\w-]+";
            var match = Regex.Match(text, pattern);

            if (match.Success)
            {
                var url = match.Value;
                if (!url.StartsWith("http"))
                {
                    url = "https://" + url;
                }
                return url;
            }

            return text.Trim();
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object parameter) => _execute(parameter);
    }
}
