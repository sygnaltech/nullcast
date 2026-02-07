using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
            Loaded += (s, e) => VideoView.MediaPlayer = _mediaPlayer;

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
