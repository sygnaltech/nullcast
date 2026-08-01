using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer
{
    /// <summary>
    /// YouTube Music tab (Phase 1). Data comes from <see cref="YtMusicService"/> (yt-dlp-backed):
    /// search the catalog, browse Liked Music + pinned playlists, and play a track through the
    /// player's normal yt-dlp path. Playlist *management* and library auto-enumeration are Phase 2.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Rows bound to the YT Music results list (playlists in the top view, tracks when drilled in).</summary>
        public ObservableCollection<YtMusicItem> YtMusicItems { get; } = new();

        private YtMusicService _ytmusic;
        private bool _ytmusicLoaded;         // playlists view populated at least once

        /// <summary>Called when the YT Music tab is selected. Lazily builds the service + playlist view.</summary>
        private void EnterYtMusicTab()
        {
            _ytmusic ??= new YtMusicService((args, url, useCookies) => RunYtDlp(args, url, useCookies));

            if (!_ytmusicLoaded)
                ShowYtMusicPlaylists();
        }

        /// <summary>Top view: Liked Music + the user's pinned playlists.</summary>
        private void ShowYtMusicPlaylists()
        {
            _ytmusicLoaded = true;
            YtMusicBackButton.Visibility = Visibility.Collapsed;

            YtMusicItems.Clear();
            YtMusicItems.Add(new YtMusicItem
            {
                Kind       = YtMusicKind.Playlist,
                PlaylistId = YtMusicService.LikedMusicId,
                Title      = "Liked Music",
                Subtitle   = "Your liked songs",
            });

            foreach (var p in _settings.YtMusicPlaylists)
                YtMusicItems.Add(new YtMusicItem
                {
                    Kind       = YtMusicKind.Playlist,
                    PlaylistId = p.Id,
                    Title      = string.IsNullOrEmpty(p.Title) ? p.Id : p.Title,
                    Subtitle   = "Playlist",
                });

            YtMusicStatusText.Visibility = Visibility.Visible;
            YtMusicStatusText.Text =
                "Search above to play any song. Open a public playlist, or pin one by URL. " +
                "Liked Music / private playlists need Edge signed in and closed.";
        }

        private async void YtMusicSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RunYtMusicSearch(YtMusicSearchBox.Text);
        }

        private async Task RunYtMusicSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            if (!EnsureYtDlpReady()) return;

            YtMusicBackButton.Visibility = Visibility.Visible;
            YtMusicStatusText.Visibility = Visibility.Visible;
            YtMusicStatusText.Text = "Searching…";
            YtMusicItems.Clear();

            var items = await _ytmusic.SearchTracksAsync(query);
            RenderYtMusicTracks(items, "No results.");
        }

        private async Task LoadYtMusicPlaylist(YtMusicItem playlist)
        {
            if (!EnsureYtDlpReady()) return;

            YtMusicBackButton.Visibility = Visibility.Visible;
            YtMusicStatusText.Visibility = Visibility.Visible;
            YtMusicStatusText.Text = $"Loading {playlist.Title}…";
            YtMusicItems.Clear();

            var items = await _ytmusic.GetPlaylistTracksAsync(playlist.PlaylistId);
            RenderYtMusicTracks(items,
                "Couldn't load this playlist. Public playlists work as-is; private playlists and " +
                "Liked Music need you signed into Edge — and Edge fully closed, since yt-dlp can't " +
                "read its cookies while it's running.");
        }

        private void RenderYtMusicTracks(System.Collections.Generic.IReadOnlyList<YtMusicItem> items, string emptyMessage)
        {
            YtMusicItems.Clear();
            foreach (var it in items) YtMusicItems.Add(it);

            if (YtMusicItems.Count == 0)
            {
                YtMusicStatusText.Visibility = Visibility.Visible;
                YtMusicStatusText.Text = emptyMessage;
            }
            else
            {
                YtMusicStatusText.Visibility = Visibility.Collapsed;
            }
        }

        private async void YtMusicResultsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            if (YtMusicResultsBox.SelectedItem is not YtMusicItem item) return;

            if (item.IsPlaylist)
            {
                await LoadYtMusicPlaylist(item);
                return;
            }

            // Play a track through the normal yt-dlp path. Detach from the bookmark / Plex
            // save paths so neither position-reporting nor auto-play-next-episode misfires.
            _activeMuid = null;
            _activePlex = null;
            _seekOnPlay = null;
            await PlayUrl(item.Url, item.Title);
        }

        private void YtMusicBack_Click(object sender, RoutedEventArgs e) => ShowYtMusicPlaylists();

        private async void YtMusicPin_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureYtDlpReady()) return;

            string clip = "";
            try { clip = Clipboard.GetText(); } catch { }

            var id = YtMusicService.ExtractPlaylistId(clip);
            if (string.IsNullOrEmpty(id))
            {
                MessageBox.Show(
                    "Copy a YouTube Music playlist link (…/playlist?list=…) to the clipboard, then click Pin playlist.",
                    "Pin playlist", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_settings.YtMusicPlaylists.Any(p => p.Id == id))
            {
                MessageBox.Show("That playlist is already pinned.", "Pin playlist",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            YtMusicStatusText.Visibility = Visibility.Visible;
            YtMusicStatusText.Text = "Pinning playlist…";

            var pref = await _ytmusic.ResolvePlaylistAsync(clip);
            if (pref == null)
            {
                MessageBox.Show("Couldn't read that playlist link.", "Pin playlist",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowYtMusicPlaylists();
                return;
            }

            _settings.YtMusicPlaylists.Add(pref);
            SaveSettings();
            ShowYtMusicPlaylists();
        }

        /// <summary>Guards actions that shell out to yt-dlp; shows a hint if it isn't ready yet.</summary>
        private bool EnsureYtDlpReady()
        {
            if (_ytdlpReady) return true;
            YtMusicStatusText.Visibility = Visibility.Visible;
            YtMusicStatusText.Text = "yt-dlp is still initializing — try again in a moment.";
            return false;
        }
    }
}
