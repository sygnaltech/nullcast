using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using FlyleafLib.MediaPlayer;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer
{
    /// <summary>
    /// Remote Control API facade (F-654). Bridges the <see cref="RemoteControlService"/>
    /// HTTP layer to the single <see cref="Player"/> instance owned by MainWindow.
    ///
    /// Every command self-marshals onto the WPF UI thread (player mutation and the
    /// volume slider must run there) and returns the resulting <see cref="StateSnapshot"/>,
    /// so a caller learns the effect in one round-trip. State reads (<see cref="ApiSnapshot"/>)
    /// touch no UI elements and are safe from any thread. This file is additive: when the
    /// API is disabled, none of it runs and existing playback behaviour is unchanged.
    /// </summary>
    public partial class MainWindow
    {
        private RemoteControlService? _remote;
        private ApiConfig? _apiConfig;

        /// <summary>Raised (from any thread) when transport / now-playing / volume changes.
        /// The service pushes each snapshot to its SSE subscribers.</summary>
        public event Action<StateSnapshot>? ApiStateChanged;

        // Throttle high-frequency CurTime ticks pushed to SSE subscribers to ~1 Hz.
        private long _lastPositionEmitTick;

        // ──────────────────────────────────────────────────────
        // Lifecycle — called from InitializePlaylistAsync / Window_Closing
        // ──────────────────────────────────────────────────────

        private void InitRemoteControl()
        {
            _apiConfig = ApiConfig.Load();

            // Push events regardless of transport so the wiring is in place if the API is
            // toggled on later in the session; the service only forwards when subscribers exist.
            if (_player != null)
            {
                _player.PropertyChanged += Player_PropertyChangedForApi;
                if (_player.Audio != null)
                    _player.Audio.PropertyChanged += Audio_PropertyChangedForApi;
            }

            if (!_apiConfig.Enabled)
            {
                App.Log("[API] Disabled (api.json enabled=false).");
                return;
            }

            try
            {
                _remote = new RemoteControlService(this, _apiConfig);
                _remote.Start();
            }
            catch (Exception ex)
            {
                // The app must remain fully functional even if the API can't bind.
                App.Log($"[API] Failed to start: {ex.Message}");
                _remote = null;
            }
        }

        private void ShutdownRemoteControl()
        {
            try { _remote?.Stop(); } catch { }
            _remote = null;
        }

        private void Player_PropertyChangedForApi(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(Player.Status):
                case nameof(Player.Duration):
                    RaiseStateChanged();
                    break;

                case nameof(Player.CurTime):
                    var now = Environment.TickCount64;
                    if (now - _lastPositionEmitTick >= 1000)
                    {
                        _lastPositionEmitTick = now;
                        RaiseStateChanged();
                    }
                    break;
            }
        }

        private void Audio_PropertyChangedForApi(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(Audio.Volume) or nameof(Audio.Mute))
                RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            var handler = ApiStateChanged;
            if (handler == null) return;
            try { handler(ApiSnapshot()); } catch { }
        }

        /// <summary>Notify subscribers that the now-playing item changed (called from play paths).</summary>
        internal void ApiNotifyItemChanged() => RaiseStateChanged();

        // ──────────────────────────────────────────────────────
        // State snapshot (thread-safe; reads no UI elements)
        // ──────────────────────────────────────────────────────

        public StateSnapshot ApiSnapshot()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            var snap = new StateSnapshot
            {
                App = new AppInfo { Version = v?.ToString(3) ?? "" },
                Ts  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Speed = _playbackSpeed,
                Fullscreen = _isFullscreen,
                WorkspaceId = _selectedWorkspace?.Id,
            };

            var p = _player;
            if (p == null) { snap.State = "idle"; return snap; }

            bool hasItem = _activePlex != null || _activeMuid != null || !string.IsNullOrEmpty(_currentUrl);

            snap.State = p.Status switch
            {
                Status.Playing => "playing",
                Status.Paused  => "paused",
                Status.Ended   => "ended",
                Status.Stopped => hasItem ? "stopped" : "idle",
                _              => hasItem ? "loading" : "idle",
            };

            // Flyleaf times are 100-ns ticks → milliseconds is /10000.
            snap.PositionMs = p.CurTime  / 10000L;
            snap.DurationMs = p.Duration / 10000L;
            if (p.Audio != null)
            {
                snap.Volume = p.Audio.Volume;
                snap.Muted  = p.Audio.Mute;
            }

            if (hasItem)
                snap.Item = BuildNowPlaying();

            return snap;
        }

        private NowPlaying? BuildNowPlaying()
        {
            if (_activePlex != null)
            {
                return new NowPlaying
                {
                    Type      = "plex",
                    Title     = _activePlex.Title,
                    RatingKey = _activePlex.RatingKey,
                    // Never expose the tokenized Direct-Play URL; use the stable plex id.
                    SourceUrl = string.IsNullOrEmpty(_activePlex.RatingKey) ? null : $"plex://{_activePlex.RatingKey}",
                };
            }

            if (_activeMuid != null)
            {
                var bm = PlaylistItems.FirstOrDefault(b => b.Muid == _activeMuid);
                return new NowPlaying
                {
                    Type      = "bookmark",
                    Muid      = _activeMuid,
                    Title     = bm?.Title,
                    SourceUrl = bm?.Url,
                };
            }

            return new NowPlaying
            {
                Type      = "url",
                Title     = DeriveTitleFromUrl(_currentUrl),
                SourceUrl = _currentUrl,
            };
        }

        // ──────────────────────────────────────────────────────
        // Commands (marshalled to the UI thread)
        // ──────────────────────────────────────────────────────

        public Task<StateSnapshot> ApiResumeAsync() => OnUi(() =>
        {
            if (_player?.Status != Status.Playing) PlayPause_Click(null!, null!);
            return ApiSnapshot();
        });

        public Task<StateSnapshot> ApiPauseAsync() => OnUi(() =>
        {
            if (_player?.Status == Status.Playing) PlayPause_Click(null!, null!);
            return ApiSnapshot();
        });

        public Task<StateSnapshot> ApiTogglePlayPauseAsync() => OnUi(() =>
        {
            PlayPause_Click(null!, null!);
            return ApiSnapshot();
        });

        public Task<StateSnapshot> ApiStopAsync() => OnUi(() =>
        {
            _player?.Stop();
            return ApiSnapshot();
        });

        public Task<StateSnapshot> ApiSeekAsync(long? positionMs, long? deltaMs) => OnUi(() =>
        {
            if (_player != null)
            {
                var durMs = _player.Duration / 10000L;
                if (durMs > 0)
                {
                    long targetMs;
                    if (positionMs.HasValue) targetMs = positionMs.Value;
                    else if (deltaMs.HasValue) targetMs = (_player.CurTime / 10000L) + deltaMs.Value;
                    else return ApiSnapshot();

                    targetMs = Math.Clamp(targetMs, 0, durMs);
                    _player.Seek((int)targetMs);
                }
            }
            return ApiSnapshot();
        });

        public Task<StateSnapshot> ApiVolumeAsync(int? level, int? delta, bool? mute) => OnUi(() =>
        {
            if (_player != null)
            {
                // Drive the slider so the on-screen UI stays in sync (its handler sets Audio.Volume).
                if (level.HasValue)
                    VolumeSlider.Value = Math.Clamp(level.Value, (int)VolumeSlider.Minimum, (int)VolumeSlider.Maximum);
                else if (delta.HasValue)
                    VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta.Value, VolumeSlider.Minimum, VolumeSlider.Maximum);

                if (mute.HasValue && _player.Audio != null)
                    _player.Audio.Mute = mute.Value;
            }
            return ApiSnapshot();
        });

        /// <summary>Start playback of a typed media reference. Resolution (yt-dlp / Plex) runs async.</summary>
        public Task<StateSnapshot> ApiPlaySourceAsync(MediaReference source, long? startPositionMs) => OnUiAsync(async () =>
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Type))
                throw new ApiRequestException("bad_request", "A 'source' with a 'type' is required.");

            switch (source.Type.ToLowerInvariant())
            {
                case "youtube":
                case "url":
                {
                    var url = source.Url;
                    if (string.IsNullOrWhiteSpace(url))
                        throw new ApiRequestException("bad_request", "'url' is required for this source type.");
                    if (source.Quality is int q && q > 0) _selectedHeight = q;
                    _activeMuid = null;
                    _seekOnPlay = startPositionMs;
                    await PlayUrl(url);
                    break;
                }

                case "file":
                {
                    var path = source.Path ?? source.Url;
                    if (string.IsNullOrWhiteSpace(path))
                        throw new ApiRequestException("bad_request", "'path' is required for a file source.");
                    _activeMuid = null;
                    _seekOnPlay = startPositionMs;
                    await PlayUrl(path, forceDirect: true);
                    break;
                }

                case "plex":
                {
                    if (string.IsNullOrWhiteSpace(source.RatingKey))
                        throw new ApiRequestException("bad_request", "'ratingKey' is required for a plex source.");
                    if (_plex?.IsConfigured != true)
                        throw new ApiRequestException("not_found", "No Plex server is configured.");

                    var item = await _plex.GetItemAsync(source.RatingKey);
                    if (item == null)
                        throw new ApiRequestException("not_found", $"Plex item '{source.RatingKey}' was not found or has no playable file.");

                    PlayPlexItem(item);
                    if (startPositionMs.HasValue) _seekOnPlay = startPositionMs;  // override Plex resume
                    break;
                }

                case "bookmark":
                {
                    if (string.IsNullOrWhiteSpace(source.Muid))
                        throw new ApiRequestException("bad_request", "'muid' is required for a bookmark source.");

                    var bm = PlaylistItems.FirstOrDefault(b => b.Muid == source.Muid)
                             ?? (_api != null ? await _api.GetBookmarkAsync(source.Muid) : null);
                    if (bm == null || string.IsNullOrEmpty(bm.Url))
                        throw new ApiRequestException("not_found", $"Bookmark '{source.Muid}' was not found.");

                    await PlayBookmark(bm);
                    if (startPositionMs.HasValue) _seekOnPlay = startPositionMs;   // override saved position
                    break;
                }

                default:
                    throw new ApiRequestException("bad_request", $"Unknown source type '{source.Type}'.");
            }

            ApiNotifyItemChanged();
            return ApiSnapshot();
        });

        // ──────────────────────────────────────────────────────
        // UI-thread marshalling helpers
        // ──────────────────────────────────────────────────────

        private Task<T> OnUi<T>(Func<T> action) => Dispatcher.InvokeAsync(action).Task;

        private Task<T> OnUiAsync<T>(Func<Task<T>> action) => Dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    /// <summary>A request-shaped failure the API layer maps to an HTTP status + error code.</summary>
    public class ApiRequestException : Exception
    {
        public string Code { get; }
        public ApiRequestException(string code, string message) : base(message) => Code = code;
    }
}
