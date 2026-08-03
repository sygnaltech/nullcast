using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using FlyleafLib.MediaPlayer;
using VideoPlayer.Models;

namespace VideoPlayer
{
    // ──────────────────────────────────────────────────────────────────────
    // Transport queue: shuffle, repeat, previous/next, and auto-advance.
    //
    // Four sources feed playback (playlist bookmarks, YouTube Music tracks,
    // podcast episodes, Plex). The first three are "flat" lists that share one
    // generic queue here. Plex keeps its own episode-aware queue + "up next"
    // countdown (see MainWindow.xaml.cs); the transport buttons bridge to it,
    // and Repeat-One is honoured there too, but shuffle/wrap live in the flat
    // queue only.
    // ──────────────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        private enum RepeatMode { Off = 0, All = 1, One = 2 }
        private enum QueueKind  { None, Flat, Plex }

        private RepeatMode _repeatMode = RepeatMode.Off;
        private bool       _shuffle;
        private QueueKind  _queueKind = QueueKind.None;

        // Flat queue: the source items in natural order, plus a play-order
        // permutation (identity when not shuffling) and a pointer into it.
        private List<object> _queue     = new();
        private List<int>    _order     = new();
        private int          _orderPos  = -1;
        private ListBox      _queueListBox;      // for keeping the sidebar selection in sync
        private bool         _navigatingQueue;   // suppresses re-capture while we drive playback
        private readonly Random _shuffleRng = new();

        private static readonly Brush ToggleMuted  = new SolidColorBrush(Color.FromRgb(0x84, 0x8B, 0x9F));
        private static readonly Brush ToggleAccent = new SolidColorBrush(Color.FromRgb(0x7D, 0x97, 0xFF));

        // ── Queue capture ────────────────────────────────────────────────

        /// <summary>
        /// Establish the flat queue from a source list and the item the user just picked.
        /// No-op while we're the ones driving playback (Next/Prev/auto-advance).
        /// </summary>
        private void SetFlatQueue(List<object> items, int index, ListBox listBox)
        {
            _queue        = items ?? new List<object>();
            _queueKind    = _queue.Count > 0 ? QueueKind.Flat : QueueKind.None;
            _queueListBox = listBox;
            RebuildOrder(index);
        }

        /// <summary>Rebuild the play order, keeping <paramref name="currentNatural"/> as the current item.</summary>
        private void RebuildOrder(int currentNatural)
        {
            int n = _queue.Count;
            _order = Enumerable.Range(0, n).ToList();

            if (_shuffle && n > 1)
            {
                for (int i = n - 1; i > 0; i--)
                {
                    int j = _shuffleRng.Next(i + 1);
                    (_order[i], _order[j]) = (_order[j], _order[i]);
                }
                // Whatever is playing should stay put and lead the shuffled run.
                if (currentNatural >= 0)
                {
                    int at = _order.IndexOf(currentNatural);
                    if (at > 0) (_order[0], _order[at]) = (_order[at], _order[0]);
                }
            }

            _orderPos = currentNatural >= 0 ? _order.IndexOf(currentNatural) : -1;
        }

        // ── Navigation ───────────────────────────────────────────────────

        /// <summary>
        /// Step the flat queue by <paramref name="dir"/> (+1 next, -1 prev), wrapping only when
        /// Repeat-All is on. Returns false when there's nowhere to go (end of a non-repeating queue).
        /// </summary>
        private async Task<bool> StepFlatAsync(int dir)
        {
            if (_queueKind != QueueKind.Flat || _queue.Count == 0 || _orderPos < 0) return false;

            int pos = _orderPos + dir;
            if (pos < 0)             pos = _repeatMode == RepeatMode.All ? _queue.Count - 1 : -1;
            if (pos >= _queue.Count) pos = _repeatMode == RepeatMode.All ? 0 : -1;
            if (pos < 0) return false;

            _orderPos = pos;
            await PlayQueueEntryAsync(_queue[_order[_orderPos]]);
            return true;
        }

        /// <summary>Dispatch a flat-queue entry to the right per-source play path and sync the sidebar selection.</summary>
        private async Task PlayQueueEntryAsync(object entry)
        {
            _navigatingQueue = true;
            try
            {
                switch (entry)
                {
                    case Bookmark bm:        await PlayBookmark(bm);            break;
                    case YtMusicItem yt:     await PlayYtMusicTrackAsync(yt);  break;
                    case PodcastEpisode pe:  await PlayPodcastEpisodeAsync(pe); break;
                }
                if (_queueListBox != null && entry != null)
                    _queueListBox.SelectedItem = entry;
            }
            finally { _navigatingQueue = false; }
        }

        // ── End-of-track auto-advance (called from the Status.Ended handler) ──

        private async Task HandleFlatTrackEndedAsync()
        {
            // Repeat-One is handled in the Status.Ended handler (seek + replay in place), so by the
            // time we get here the only question is whether to auto-advance. Autoplay is the master
            // switch; it shares the AutoPlayNextEpisode setting so the bar toggle and the File-menu
            // item stay in lockstep. StepFlatAsync only wraps past the ends when Repeat-All is on.
            if (_queueKind != QueueKind.Flat || _orderPos < 0) return;
            if (!_settings.AutoPlayNextEpisode) return;
            await StepFlatAsync(+1);
        }

        // ── Transport buttons ────────────────────────────────────────────

        private async void NextButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_queueKind == QueueKind.Plex) { StepPlex(+1); return; }
            await StepFlatAsync(+1);
        }

        private async void PrevButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_queueKind == QueueKind.Plex) { StepPlex(-1); return; }

            // Standard player feel: >3s in, "previous" restarts the current track first.
            if (_player != null && _player.CurTime > 3 * 10_000_000L)
            {
                _player.SeekAccurate(0);
                return;
            }
            if (!await StepFlatAsync(-1))
                _player?.SeekAccurate(0);   // already at the start of the queue → restart
        }

        /// <summary>Move the Plex episode queue by one and play, reusing its existing countdown machinery.</summary>
        private void StepPlex(int dir)
        {
            if (_plexPlayQueue.Count == 0) return;
            int idx = _plexPlayIndex + dir;
            if (idx < 0 || idx >= _plexPlayQueue.Count) return;

            var target = _plexPlayQueue[idx];
            if (target == null) return;
            CancelNextEpisodeCountdown();
            PlayPlexItem(target);
        }

        // ── Toggle buttons ───────────────────────────────────────────────

        private void ShuffleButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _shuffle = !_shuffle;
            _settings.Shuffle = _shuffle;
            SaveSettings();

            if (_queueKind == QueueKind.Flat && _orderPos >= 0)
                RebuildOrder(_order[_orderPos]);   // re-permute, keeping the current track leading

            RefreshTransportToggles();
        }

        private void RepeatButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _repeatMode = (RepeatMode)(((int)_repeatMode + 1) % 3);
            _settings.RepeatMode = (int)_repeatMode;
            SaveSettings();
            RefreshTransportToggles();
        }

        private void AutoplayButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _settings.AutoPlayNextEpisode = !_settings.AutoPlayNextEpisode;
            AutoPlayNextMenuItem.IsChecked = _settings.AutoPlayNextEpisode;
            if (!_settings.AutoPlayNextEpisode) CancelNextEpisodeCountdown();
            SaveSettings();
            RefreshTransportToggles();
        }

        /// <summary>Push shuffle/repeat/autoplay state into the button visuals. Safe to call any time.</summary>
        private void RefreshTransportToggles()
        {
            if (ShuffleIcon == null) return;   // controls not realized yet

            ShuffleIcon.Stroke = _shuffle ? ToggleAccent : ToggleMuted;

            bool repeatOn = _repeatMode != RepeatMode.Off;
            RepeatIcon.Stroke = repeatOn ? ToggleAccent : ToggleMuted;
            RepeatOneBadge.Visibility = _repeatMode == RepeatMode.One
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            RepeatOneBadge.Foreground = ToggleAccent;

            AutoplayGlyph.Foreground = _settings.AutoPlayNextEpisode ? ToggleAccent : ToggleMuted;
        }
    }
}
