using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VideoPlayer.Models;

namespace VideoPlayer
{
    // ──────────────────────────────────────────────────────────
    // Ctrl+K global search palette.
    //
    // A single command-palette overlay that fans a query out across
    // every known source in parallel and shows a grouped, tab-filtered
    // result list. v1 covers Plex (server-side /hubs/search) and the
    // current workspace's bookmarks (client-side, already in memory);
    // podcasts/history slot in later by adding a source to RunSearchAsync
    // plus a Kind + tab button — nothing else here changes.
    // ──────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // Backing list for the grouped view bound to SearchResultsBox.
        private readonly ObservableCollection<SearchResult> _searchResults = new();
        private ListCollectionView _searchView;

        // 300ms keystroke debounce so we don't hammer the Plex/network on every key.
        private DispatcherTimer _searchDebounce;

        // Monotonic query id — a slow source that returns after a newer keystroke
        // is discarded rather than clobbering fresher results.
        private int _searchGeneration;
        private bool _searchBusy;

        // Active source tab: "All" | "Playlist" | "Plex" (matches SearchSourceKind names).
        private string _searchTab = "All";

        private const int MinQueryLength = 2;

        // ── Open / close ──────────────────────────────────────

        private void ToggleSearchPalette()
        {
            if (SearchPopup.IsOpen) CloseSearchPalette();
            else OpenSearchPalette();
        }

        private void OpenSearchPalette()
        {
            EnsureSearchView();

            // Plex tab only makes sense when Plex is wired up.
            bool plexOn = _services?.IsPlexConfigured == true;
            SearchTabPlex.Visibility = plexOn ? Visibility.Visible : Visibility.Collapsed;
            if (!plexOn && _searchTab == "Plex") _searchTab = "All";

            SearchBox.Text = "";
            _searchResults.Clear();
            SetSearchTab(_searchTab);

            SearchPopup.IsOpen = true;

            // Popups get focus asynchronously; grab the caret once it's up.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }), DispatcherPriority.Input);
        }

        private void CloseSearchPalette()
        {
            _searchDebounce?.Stop();
            SearchPopup.IsOpen = false;
        }

        private void SearchPopup_Closed(object sender, EventArgs e)
        {
            _searchDebounce?.Stop();
            // Bump the generation so any in-flight source result is ignored.
            _searchGeneration++;
            _searchBusy = false;
        }

        private void SearchScrim_MouseDown(object sender, MouseButtonEventArgs e)
            => CloseSearchPalette();

        private void EnsureSearchView()
        {
            if (_searchView != null) return;

            _searchView = new ListCollectionView(_searchResults);
            _searchView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(SearchResult.SourceBadge)));
            _searchView.Filter = SearchTabFilter;
            SearchResultsBox.ItemsSource = _searchView;

            _searchDebounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _searchDebounce.Tick += (_, __) =>
            {
                _searchDebounce.Stop();
                _ = RunSearchAsync();
            };
        }

        private bool SearchTabFilter(object o)
            => _searchTab == "All" || (o is SearchResult r && r.Kind.ToString() == _searchTab);

        // ── Tabs ──────────────────────────────────────────────

        private void SearchTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag) SetSearchTab(tag);
        }

        private void SetSearchTab(string tag)
        {
            _searchTab = tag;
            UpdateSearchTabVisuals();
            _searchView?.Refresh();
            if (SearchResultsBox.Items.Count > 0) SearchResultsBox.SelectedIndex = 0;
            UpdateSearchEmptyState();
        }

        private void UpdateSearchTabVisuals()
        {
            var tabs = new (Button Btn, string Tag)[]
            {
                (SearchTabAll, "All"),
                (SearchTabPlaylists, "Playlist"),
                (SearchTabPlex, "Plex"),
            };
            var active   = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xFB));
            var inactive = new SolidColorBrush(Color.FromRgb(0x84, 0x8B, 0x9F));
            var tint     = new SolidColorBrush(Color.FromArgb(0x21, 0x7D, 0x97, 0xFF));

            foreach (var (btn, tag) in tabs)
            {
                bool on = tag == _searchTab;
                btn.Foreground = on ? active : inactive;
                btn.Background = on ? tint : Brushes.Transparent;
            }
        }

        // ── Input / keyboard ──────────────────────────────────

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_searchDebounce == null) return;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    CloseSearchPalette();
                    e.Handled = true;
                    break;

                case Key.K when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                    // Ctrl+K again toggles the palette shut (window InputBinding can't
                    // see us here — the popup is its own focus scope).
                    CloseSearchPalette();
                    e.Handled = true;
                    break;

                case Key.Down:
                    MoveSearchSelection(+1);
                    e.Handled = true;
                    break;

                case Key.Up:
                    MoveSearchSelection(-1);
                    e.Handled = true;
                    break;

                case Key.Enter:
                    var sel = SearchResultsBox.SelectedItem as SearchResult
                              ?? SearchResultsBox.Items.OfType<SearchResult>().FirstOrDefault();
                    ActivateSearchResult(sel);
                    e.Handled = true;
                    break;
            }
        }

        private void MoveSearchSelection(int delta)
        {
            int count = SearchResultsBox.Items.Count;
            if (count == 0) return;

            int idx = SearchResultsBox.SelectedIndex;
            idx = idx < 0
                ? (delta > 0 ? 0 : count - 1)
                : Math.Max(0, Math.Min(count - 1, idx + delta));

            SearchResultsBox.SelectedIndex = idx;
            if (SearchResultsBox.SelectedItem != null)
                SearchResultsBox.ScrollIntoView(SearchResultsBox.SelectedItem);
        }

        private void SearchResult_Click(object sender, MouseButtonEventArgs e)
        {
            // Resolve the row under the pointer (not the group header).
            var item = ItemsControl.ContainerFromElement(SearchResultsBox, e.OriginalSource as DependencyObject)
                       as ListBoxItem;
            if (item?.DataContext is SearchResult r) ActivateSearchResult(r);
        }

        // ── Fan-out search ────────────────────────────────────

        private async Task RunSearchAsync()
        {
            var query = (SearchBox.Text ?? "").Trim();
            int gen = ++_searchGeneration;

            if (query.Length < MinQueryLength)
            {
                _searchBusy = false;
                _searchResults.Clear();
                UpdateSearchEmptyState();
                return;
            }

            Services.Telemetry.Track("search", new() { ["query"] = query, ["length"] = query.Length });

            // 1) Bookmarks — synchronous, in-memory filter of the current workspace.
            var bookmarks = FilterBookmarks(query);

            // 2) Plex — server-side search (only when configured). Kick it off, but
            //    render the instant bookmark hits first so the palette feels live.
            bool plexOn = _services?.IsPlexConfigured == true && _plex != null;
            Task<List<PlexItem>> plexTask = plexOn
                ? _plex.SearchAsync(query)
                : Task.FromResult(new List<PlexItem>());

            _searchBusy = plexOn;
            RebuildResults(bookmarks, null);
            UpdateSearchEmptyState();

            List<PlexItem> plex;
            try { plex = await plexTask; }
            catch { plex = new List<PlexItem>(); }

            if (gen != _searchGeneration) return; // superseded by a newer keystroke

            _searchBusy = false;
            RebuildResults(bookmarks, plex);
            UpdateSearchEmptyState();
        }

        private List<Bookmark> FilterBookmarks(string q)
        {
            bool Match(string s) =>
                !string.IsNullOrEmpty(s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

            return PlaylistItems
                .Where(b => Match(b.Title) || Match(b.Url) || Match(b.Type)
                            || (b.Tags != null && b.Tags.Any(Match)))
                .Take(50)
                .ToList();
        }

        private void RebuildResults(List<Bookmark> bookmarks, List<PlexItem> plex)
        {
            _searchResults.Clear();

            foreach (var b in bookmarks)
            {
                _searchResults.Add(new SearchResult
                {
                    Kind        = SearchSourceKind.Playlist,
                    SourceBadge = SearchResult.BadgeFor(SearchSourceKind.Playlist),
                    Title       = string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title,
                    Subtitle    = BookmarkSubtitle(b),
                    Payload     = b,
                });
            }

            if (plex != null)
            {
                foreach (var p in plex)
                {
                    _searchResults.Add(new SearchResult
                    {
                        Kind        = SearchSourceKind.Plex,
                        SourceBadge = SearchResult.BadgeFor(SearchSourceKind.Plex),
                        Title       = p.Title,
                        Subtitle    = PlexSubtitle(p),
                        ThumbUrl    = p.ThumbUrl,
                        Payload     = p,
                    });
                }
            }

            if (SearchResultsBox.Items.Count > 0) SearchResultsBox.SelectedIndex = 0;
        }

        private static string BookmarkSubtitle(Bookmark b)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(b.Type)) parts.Add(b.Type);
            if (b.HasPosition) parts.Add(b.PositionLabel);
            if (parts.Count == 0 && !string.IsNullOrWhiteSpace(b.Url)) parts.Add(b.Url);
            return string.Join("  ·  ", parts);
        }

        private static string PlexSubtitle(PlexItem p)
        {
            var parts = new[] { p.TypeLabel, p.Subtitle }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join("  ·  ", parts);
        }

        private void UpdateSearchEmptyState()
        {
            var q = (SearchBox.Text ?? "").Trim();

            if (q.Length < MinQueryLength)
            {
                SearchStatusText.Text = "Type to search your playlists and Plex library.";
                SearchStatusText.Visibility = Visibility.Visible;
                return;
            }

            bool any = SearchResultsBox.Items.Count > 0;
            SearchStatusText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            if (!any)
                SearchStatusText.Text = _searchBusy ? "Searching…" : $"No results for “{q}”.";
        }

        // ── Dispatch to the existing per-source playback entry points ──

        private async void ActivateSearchResult(SearchResult r)
        {
            if (r == null) return;
            CloseSearchPalette();

            switch (r.Payload)
            {
                case PlexItem px:
                    Services.Telemetry.Track("search_result_activated", new() { ["result_type"] = "plex", ["title"] = px.Title ?? "" });
                    PlayPlexItem(px);
                    break;
                case Bookmark bm:
                    Services.Telemetry.Track("search_result_activated", new() { ["result_type"] = "bookmark", ["title"] = bm.Title ?? "" });
                    await PlayBookmark(bm);
                    break;
            }
        }
    }
}
