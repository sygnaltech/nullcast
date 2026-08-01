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
    /// YouTube Music tab. Two data layers: <see cref="YtMusicInnerTube"/> (the real YT Music web
    /// API, authenticated with the user's cookies.txt) reaches the library — Liked Music, the
    /// user's playlists, and each playlist's tracks — while <see cref="YtMusicService"/> (yt-dlp)
    /// handles catalog search and public-playlist expansion. Playback of any track goes through
    /// the player's normal yt-dlp path.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Rows bound to the YT Music results list (playlists in the top view, tracks when drilled in).</summary>
        public ObservableCollection<YtMusicItem> YtMusicItems { get; } = new();

        private YtMusicService _ytmusic;
        private YtMusicInnerTube _ytMusicApi;
        private BrowserHelperClient _browserHelper;
        private bool _ytmusicLoaded;         // playlists view populated at least once

        /// <summary>
        /// Fresh cookies.txt pulled from the browser-helper broker (live browser session), or null.
        /// Preferred over the manual <see cref="AppSettings.CookieFilePath"/> by both the InnerTube
        /// client and yt-dlp (<c>ResolveCookieArgs</c>), so auth uses always-current cookies.
        /// </summary>
        private string _liveCookiePath;

        /// <summary>Called when the YT Music tab is selected. Lazily builds the services + playlist view.</summary>
        private void EnterYtMusicTab()
        {
            _ytmusic       ??= new YtMusicService((args, url, useCookies) => RunYtDlp(args, url, useCookies));
            _browserHelper ??= new BrowserHelperClient();
            // Cookie source prefers live browser-helper cookies, else the manually loaded file.
            _ytMusicApi    ??= new YtMusicInnerTube(() => _liveCookiePath ?? _settings.CookieFilePath);

            if (!_ytmusicLoaded)
                _ = ShowYtMusicPlaylistsAsync();
        }

        /// <summary>
        /// Best-effort refresh of <see cref="_liveCookiePath"/> from the browser-helper broker.
        /// Silent no-op if the broker isn't installed/running or hasn't shared youtube.com — in
        /// which case the manual cookies.txt remains the source.
        /// </summary>
        private async Task RefreshLiveCookiesAsync()
        {
            if (_browserHelper?.IsInstalled != true) return;
            var path = await _browserHelper.FetchCookiesFileAsync("youtube.com");
            if (!string.IsNullOrEmpty(path))
            {
                _liveCookiePath = path;
                App.Log("[YTMusic] Using live cookies from browser-helper.");
            }
        }

        /// <summary>
        /// Top view: Liked Music + the user's library playlists (via the authenticated YT Music
        /// API) plus any pinned playlists. Falls back to pinned-only when there's no cookies.txt.
        /// </summary>
        private async Task ShowYtMusicPlaylistsAsync()
        {
            _ytmusicLoaded = true;
            YtMusicBackButton.Visibility = Visibility.Collapsed;
            YtMusicItems.Clear();

            await RefreshLiveCookiesAsync();
            bool authed = _ytMusicApi.IsConfigured;

            if (authed)
            {
                YtMusicStatusText.Visibility = Visibility.Visible;
                YtMusicStatusText.Text = "Loading your library…";

                // Liked Music first, then the user's playlists.
                YtMusicItems.Add(new YtMusicItem
                {
                    Kind = YtMusicKind.Playlist, PlaylistId = "LM",
                    Title = "Liked Music", Subtitle = "Your liked songs",
                });

                var library = await _ytMusicApi.GetLibraryPlaylistsAsync();
                foreach (var pl in library) YtMusicItems.Add(pl);
            }

            // Pinned playlists (works with or without auth).
            foreach (var p in _settings.YtMusicPlaylists)
                YtMusicItems.Add(new YtMusicItem
                {
                    Kind       = YtMusicKind.Playlist,
                    PlaylistId = p.Id,
                    Title      = string.IsNullOrEmpty(p.Title) ? p.Id : p.Title,
                    Subtitle   = "Playlist",
                });

            if (YtMusicItems.Count > 0)
            {
                YtMusicStatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                YtMusicStatusText.Visibility = Visibility.Visible;
                YtMusicStatusText.Text = authed
                    ? "No playlists yet. Search above to play any song, or pin a playlist by URL."
                    : "Search above to play any song, or pin a public playlist by its URL. " +
                      "For Liked Music and your library, load a cookies.txt via File ▸ Load cookies.txt.";
            }
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
            YtMusicBackButton.Visibility = Visibility.Visible;
            YtMusicStatusText.Visibility = Visibility.Visible;
            YtMusicStatusText.Text = $"Loading {playlist.Title}…";
            YtMusicItems.Clear();

            await RefreshLiveCookiesAsync();

            // Authenticated YT Music API first — it's the only thing that can read the library
            // (Liked Music, your playlists). If it comes back empty (e.g. a public playlist, or no
            // cookies), fall back to yt-dlp's public expansion.
            var items = new System.Collections.Generic.List<YtMusicItem>();
            if (_ytMusicApi.IsConfigured)
                items = await _ytMusicApi.GetPlaylistTracksAsync(playlist.PlaylistId);

            if (items.Count == 0 && _ytdlpReady)
                items = await _ytmusic.GetPlaylistTracksAsync(playlist.PlaylistId);

            RenderYtMusicTracks(items,
                "Couldn't load this playlist. If it's private or Liked Music, load a current " +
                "cookies.txt (File ▸ Load cookies.txt) exported while signed into YouTube Music.");
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

        private async void YtMusicBack_Click(object sender, RoutedEventArgs e) => await ShowYtMusicPlaylistsAsync();

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
                await ShowYtMusicPlaylistsAsync();
                return;
            }

            _settings.YtMusicPlaylists.Add(pref);
            SaveSettings();
            await ShowYtMusicPlaylistsAsync();
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
