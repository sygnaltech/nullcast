using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer
{
    // ──────────────────────────────────────────────────────────
    // The Nullcast.TV tab.
    //
    // Browsed the same way as Plex — segment bar, channel picker,
    // filter box, breadcrumb, results — because the two are the same
    // kind of thing to a viewer and the muscle memory should carry
    // over. What is genuinely different is underneath:
    //
    //   · A CHANNEL is a saved way of looking at the catalog. A
    //     `query`/`curated` channel answers with films; a `facet`
    //     channel answers with a DIRECTORY of its values, and only
    //     answers with films once you name one.
    //   · Everything pages on an OPAQUE cursor. `null` means the end;
    //     a client that ignores it has the top twenty-four and thinks
    //     it has the catalog.
    //   · Films live on somebody else's platform, so playback is the
    //     ordinary yt-dlp path, not a direct stream.
    // ──────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // ── Services ──────────────────────────────────────────
        private NullcastTvAuthService _tvAuth;
        private NullcastTvService     _tv;

        /// <summary>Rows bound to the results list. One collection for films, facet values and series.</summary>
        public ObservableCollection<TvItem> NullcastTvItems { get; } = new();

        // ── Browse state ──────────────────────────────────────
        private TvSection _tvSection = TvSection.Browse;
        private bool      _tvChannelsLoaded;

        private readonly ObservableCollection<TvCategory> _tvCategories = new();
        private ICollectionView _tvCategoryView;     // grouped + filtered view over _tvCategories
        private TvCategory      _tvCategory;         // the channel currently being browsed

        /// <summary>
        /// The feed stack: [0] is the level the segment landed on, anything above it is a drill.
        /// Never deeper than two in practice (channel → facet value, series list → episodes),
        /// but a stack rather than a flag so the breadcrumb has something real to walk.
        /// </summary>
        private readonly List<TvFeed> _tvStack = new();

        private int  _tvLoadToken;      // stale-guard for async page loads
        private int  _tvRenderToken;    // stale-guard for the batched list fill
        private bool _tvLoadingMore;

        private DispatcherTimer _tvSearchDebounce;
        private ScrollViewer    _tvScroller;
        private string          _tvChannelFilter = "";

        /// <summary>What a level of the browse is looking at, and how to ask for its next page.</summary>
        private enum TvFeedKind { Channel, FacetValue, SeriesList, SeriesEpisodes, SearchResults }

        /// <summary>
        /// One level of the browse. Holds everything needed to render it, to page it, and to
        /// come back to it from a breadcrumb without refetching.
        /// </summary>
        private class TvFeed
        {
            public TvFeedKind Kind;
            /// <summary>Breadcrumb label for this level.</summary>
            public string Label = "";
            /// <summary>Channel slug (Channel / FacetValue) or series slug (SeriesEpisodes).</summary>
            public string Slug = "";
            /// <summary>The facet value being listed, when this is a value's feed.</summary>
            public string FacetValue = "";
            /// <summary>Which facet the parent channel groups on — labels the values we list.</summary>
            public string Facet = "";
            /// <summary>The search text, when this is a result list.</summary>
            public string Query = "";
            public List<TvItem> Items = new();
            /// <summary>Opaque continuation from the last page, or null at the end. Never computed.</summary>
            public string Cursor;
            /// <summary>True when this level's rows are a series' episodes (drives the lean template).</summary>
            public bool IsEpisodeList => Kind == TvFeedKind.SeriesEpisodes;
        }

        private TvFeed TvTop => _tvStack.Count > 0 ? _tvStack[^1] : null;

        // ──────────────────────────────────────────────────────
        // Wiring
        // ──────────────────────────────────────────────────────

        /// <summary>Build the service pair. Called once, from the startup path.</summary>
        private void InitializeNullcastTv()
        {
            _tvAuth = new NullcastTvAuthService(_services);
            _tv     = new NullcastTvService(_tvAuth);
        }

        /// <summary>
        /// Restore a saved sign-in, then refresh the identity so Settings can name the account.
        /// Best-effort: a catalog that is unreachable at launch must not hold up the window.
        /// </summary>
        private async Task LoadNullcastTvSessionAsync()
        {
            if (_tvAuth == null) return;
            await _tvAuth.LoadTokensAsync();
            if (!_tvAuth.IsSignedIn) return;

            try { await _tvAuth.RefreshProfileAsync(); }
            catch (Exception ex) { App.Log($"[Nullcast.TV] Profile refresh failed: {ex.Message}"); }
        }

        /// <summary>
        /// Set up the grouped, filterable channel picker and the search debounce. Deferred to
        /// the first visit rather than done in the constructor — a provider that is switched
        /// off should cost nothing at startup.
        /// </summary>
        private void EnsureTvViewsInitialized()
        {
            if (_tvCategoryView != null) return;

            var source = new CollectionViewSource { Source = _tvCategories };
            source.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TvCategory.Group)));
            _tvCategoryView = source.View;
            _tvCategoryView.Filter = TvChannelFilterPredicate;
            TvChannelList.ItemsSource = _tvCategoryView;

            _tvSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _tvSearchDebounce.Tick += async (_, __) =>
            {
                _tvSearchDebounce.Stop();
                await RunTvSearchAsync((TvSearchBox.Text ?? "").Trim());
            };
        }

        private void ShowNullcastTvTab_Click(object sender, RoutedEventArgs e)
            => SelectTab(SidebarTab.NullcastTv);

        /// <summary>Entered whenever the Nullcast.TV tab becomes active.</summary>
        private async void EnterNullcastTvTab()
        {
            EnsureTvViewsInitialized();
            ApplyTvViewMode();

            if (_tvAuth?.IsSignedIn != true)
            {
                UpdateTvTabState();
                return;
            }

            if (_tvChannelsLoaded) { UpdateTvTabState(); return; }
            await SelectTvSegment(TvSection.Browse);
        }

        // ──────────────────────────────────────────────────────
        // Tab state (signed out / loading / empty / results)
        // ──────────────────────────────────────────────────────

        /// <summary>Show the sign-in, empty or results state for the Nullcast.TV tab.</summary>
        private void UpdateTvTabState()
        {
            if (TvStatusText == null) return;   // called before the UI is ready

            bool signedIn = _tvAuth?.IsSignedIn == true;

            TvConnectButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            TvSegmentBar.Visibility    = signedIn ? Visibility.Visible   : Visibility.Collapsed;
            TvFilterHost.Visibility    = signedIn ? Visibility.Visible   : Visibility.Collapsed;
            if (TvSpinner != null) TvSpinner.Visibility = Visibility.Collapsed;

            if (!signedIn)
            {
                NullcastTvItems.Clear();
                TvChannelHost.Visibility   = Visibility.Collapsed;
                TvBreadcrumbBar.Visibility = Visibility.Collapsed;
                TvViewToolbar.Visibility   = Visibility.Collapsed;
                TvLoadMoreButton.Visibility = Visibility.Collapsed;
                // Not a nag: 18+ films are filtered out of an anonymous read without any
                // indication, so an unauthenticated catalog would quietly be the wrong catalog.
                TvStatusText.Text = "Sign in to browse Nullcast.TV. The catalog withholds 18+ " +
                                    "films from anonymous callers, so the player asks for an account.";
                TvStatusText.Visibility = Visibility.Visible;
                return;
            }

            TvViewToolbar.Visibility = NullcastTvItems.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;

            TvLoadMoreButton.Visibility = !string.IsNullOrEmpty(TvTop?.Cursor)
                ? Visibility.Visible : Visibility.Collapsed;

            if (NullcastTvItems.Count == 0)
            {
                TvStatusText.Text = _tvSection switch
                {
                    TvSection.Search => string.IsNullOrWhiteSpace(TvSearchBox?.Text)
                        ? "Search films, creators, models, genres and series."
                        : "No results.",
                    TvSection.Series => "No series on air.",
                    _                => "Nothing to show here.",
                };
                TvStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                TvStatusText.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowTvLoading(string text)
        {
            if (TvSpinner != null) TvSpinner.Visibility = Visibility.Visible;
            TvStatusText.Text = text;
            TvStatusText.Visibility = Visibility.Visible;
            TvLoadMoreButton.Visibility = Visibility.Collapsed;
        }

        private void ShowTvError(string message)
        {
            if (TvSpinner != null) TvSpinner.Visibility = Visibility.Collapsed;
            TvStatusText.Text = message;
            TvStatusText.Visibility = Visibility.Visible;
        }

        // ──────────────────────────────────────────────────────
        // Sign in
        // ──────────────────────────────────────────────────────

        private async void TvConnect_Click(object sender, RoutedEventArgs e)
        {
            TvConnectButton.IsEnabled = false;
            try
            {
                await _tvAuth.LoginAsync();
                Telemetry.Track("nullcast_tv_signed_in");
                _tvChannelsLoaded = false;
                await SelectTvSegment(TvSection.Browse);
            }
            catch (Exception ex)
            {
                // Leave the message standing: UpdateTvTabState would replace it with the generic
                // sign-in prompt, which is exactly the information the user already had.
                ShowTvError($"Could not sign in: {ex.Message}");
            }
            finally
            {
                TvConnectButton.IsEnabled = true;
            }
        }

        // ──────────────────────────────────────────────────────
        // Segments: Browse / Series / Search
        // ──────────────────────────────────────────────────────

        private async void TvSegment_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag) return;
            if (Enum.TryParse<TvSection>(tag, out var section))
                await SelectTvSegment(section);
        }

        private async Task SelectTvSegment(TvSection section)
        {
            _tvSection = section;
            _tvStack.Clear();
            StyleTvSegments();

            TvSearchBox.Text = "";

            switch (section)
            {
                case TvSection.Browse:
                    TvSearchPlaceholder.Text  = "Filter titles…";
                    TvChannelHost.Visibility  = Visibility.Visible;
                    await PopulateTvChannelsAsync();
                    break;

                case TvSection.Series:
                    TvSearchPlaceholder.Text  = "Filter series…";
                    TvChannelHost.Visibility  = Visibility.Collapsed;
                    await PushTvFeedAsync(new TvFeed { Kind = TvFeedKind.SeriesList, Label = "Series" },
                                          replaceStack: true);
                    break;

                case TvSection.Search:
                    TvSearchPlaceholder.Text  = "Search Nullcast.TV…";
                    TvChannelHost.Visibility  = Visibility.Collapsed;
                    NullcastTvItems.Clear();
                    RebuildTvBreadcrumb();
                    UpdateTvTabState();
                    TvSearchBox.Focus();
                    break;
            }
        }

        private void StyleTvSegments()
        {
            foreach (var b in TvSegmentBar.Children.OfType<Button>())
            {
                bool active = b.Tag as string == _tvSection.ToString();
                b.Background = active ? SegActiveBg : SegInactiveBg;
                b.Foreground = active ? SegActiveFg : SegInactiveFg;
            }
        }

        // ──────────────────────────────────────────────────────
        // Channel picker (the "Browse" segment's categories)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Fetch the channel list once and file it into the picker: orderings and curated
        /// shelves under "Views", facet channels under "Browse by" — the latter open a
        /// directory rather than a feed, which is the only branch a caller makes.
        /// </summary>
        private async Task PopulateTvChannelsAsync()
        {
            if (_tvChannelsLoaded && _tvCategories.Count > 0)
            {
                await ApplyTvCategory(_tvCategory ?? _tvCategories.FirstOrDefault());
                return;
            }

            ShowTvLoading("Loading channels…");
            List<TvApiChannel> channels;
            try
            {
                channels = await _tv.GetChannelsAsync();
            }
            catch (Exception ex)
            {
                ShowTvError(ex.Message);
                return;
            }

            _tvCategories.Clear();
            foreach (var ch in channels)
                _tvCategories.Add(new TvCategory(ch, ch.IsFacet ? "Browse by" : "Views"));

            _tvChannelsLoaded = true;

            if (_tvCategories.Count == 0)
            {
                ShowTvError("The catalog published no channels.");
                return;
            }

            // Lead with a "Views" channel when there is one — an ordering over the whole
            // catalog is a better first thing to see than a directory of creators.
            var first = _tvCategories.FirstOrDefault(c => !c.IsFacet) ?? _tvCategories[0];
            await ApplyTvCategory(first);
        }

        private void TvChannelButton_Click(object sender, RoutedEventArgs e)
        {
            if (TvChannelButton.IsChecked == true)
            {
                TvChannelPopup.IsOpen = true;
                TvChannelFilter.Text = "";
                TvChannelFilter.Focus();
            }
            else
            {
                TvChannelPopup.IsOpen = false;
            }
        }

        private void TvChannelPopup_Closed(object sender, EventArgs e)
            => TvChannelButton.IsChecked = false;

        private void TvChannelFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            _tvChannelFilter = (TvChannelFilter.Text ?? "").Trim();
            _tvCategoryView?.Refresh();
        }

        private async void TvChannelFilter_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    TvChannelPopup.IsOpen = false;
                    e.Handled = true;
                    break;
                case Key.Enter:
                    // Enter takes the first surviving row, so filtering and committing is one gesture.
                    var first = _tvCategoryView?.Cast<TvCategory>().FirstOrDefault();
                    if (first != null)
                    {
                        TvChannelPopup.IsOpen = false;
                        await ApplyTvCategory(first);
                    }
                    e.Handled = true;
                    break;
            }
        }

        private bool TvChannelFilterPredicate(object o) =>
            _tvChannelFilter.Length == 0
            || (o is TvCategory c && c.Label.Contains(_tvChannelFilter, StringComparison.OrdinalIgnoreCase));

        private async void TvChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            if (TvChannelList.SelectedItem is not TvCategory cat) return;
            TvChannelPopup.IsOpen = false;
            await ApplyTvCategory(cat);
        }

        private async Task ApplyTvCategory(TvCategory cat)
        {
            if (cat == null) return;
            _tvCategory = cat;
            TvChannelButton.Content = cat.Label;
            TvSearchBox.Text = "";

            await PushTvFeedAsync(new TvFeed
            {
                Kind  = TvFeedKind.Channel,
                Label = cat.Label,
                Slug  = cat.Slug,
                Facet = cat.Facet,
            }, replaceStack: true);
        }

        // ──────────────────────────────────────────────────────
        // Feed loading + paging
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Load a feed's first page and make it the level on show. <paramref name="replaceStack"/>
        /// starts a new browse (a segment or channel change); otherwise it is a drill and the
        /// level below stays put so the breadcrumb can come back to it without refetching.
        /// </summary>
        private async Task PushTvFeedAsync(TvFeed feed, bool replaceStack)
        {
            int token = ++_tvLoadToken;
            ShowTvLoading("Loading…");

            List<TvItem> items;
            string cursor;
            try
            {
                (items, cursor) = await FetchTvPageAsync(feed, null);
            }
            catch (Exception ex)
            {
                if (token != _tvLoadToken) return;
                ShowTvError(ex.Message);
                return;
            }

            if (token != _tvLoadToken) return;   // a newer load superseded this one

            feed.Items  = items;
            feed.Cursor = cursor;

            if (replaceStack) _tvStack.Clear();
            _tvStack.Add(feed);

            RebuildTvBreadcrumb();
            ApplyTvNarrow();
        }

        private async void TvLoadMore_Click(object sender, RoutedEventArgs e) => await LoadMoreTvAsync();

        /// <summary>Append the next page to the level on show. The cursor is passed back verbatim.</summary>
        private async Task LoadMoreTvAsync()
        {
            var feed = TvTop;
            if (feed == null || string.IsNullOrEmpty(feed.Cursor) || _tvLoadingMore) return;

            _tvLoadingMore = true;
            TvLoadMoreButton.IsEnabled = false;
            int token = ++_tvLoadToken;

            try
            {
                var (items, cursor) = await FetchTvPageAsync(feed, feed.Cursor);
                if (token != _tvLoadToken) return;

                feed.Items.AddRange(items);
                feed.Cursor = cursor;
                ApplyTvNarrow(keepScroll: true);
            }
            catch (Exception ex)
            {
                ShowTvError(ex.Message);
            }
            finally
            {
                _tvLoadingMore = false;
                TvLoadMoreButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// One page of whatever this level is looking at. Returns the rows and the next cursor
        /// (null at the end). The only place that knows how each feed kind is fetched.
        /// </summary>
        private async Task<(List<TvItem> Items, string Cursor)> FetchTvPageAsync(TvFeed feed, string cursor)
        {
            switch (feed.Kind)
            {
                case TvFeedKind.Channel:
                {
                    var page = await _tv.GetChannelPageAsync(feed.Slug, null, cursor);
                    // A facet channel asked without a value answers with its DIRECTORY, and
                    // `videos` is empty. Which list is populated is the answer, not a guess.
                    var facet = page.Channel?.Facet ?? feed.Facet;
                    feed.Facet = facet;
                    var items = page.Facets is { Count: > 0 }
                        ? NullcastTvService.ToItems(page.Facets, feed.Slug, facet)
                        : NullcastTvService.ToItems(page.Videos);
                    return (items, page.Cursor);
                }

                case TvFeedKind.FacetValue:
                {
                    var page = await _tv.GetChannelPageAsync(feed.Slug, feed.FacetValue, cursor);
                    return (NullcastTvService.ToItems(page.Videos), page.Cursor);
                }

                case TvFeedKind.SeriesList:
                {
                    // Not paged by the catalog — the whole list comes back at once.
                    var all = await _tv.GetSeriesAsync();
                    return (NullcastTvService.ToItems(all), null);
                }

                case TvFeedKind.SeriesEpisodes:
                {
                    var page = await _tv.GetSeriesFeedAsync(feed.Slug);
                    return (NullcastTvService.ToItems(page?.Episodes), null);
                }

                case TvFeedKind.SearchResults:
                {
                    var results = await _tv.SearchAsync(feed.Query);
                    var items = NullcastTvService.ToItems(results.Videos);
                    // Facet hits and series come after the films: they are destinations rather
                    // than something to put on, and burying the films under them would make the
                    // common case the one you scroll past.
                    foreach (var hit in results.Channels)
                        items.Add(TvItem.FromFacet(hit, SlugForFacet(hit.Facet), hit.Facet,
                                                   NullcastTvService.AbsoluteUrl(hit.ThumbUrl)));
                    items.AddRange(NullcastTvService.ToItems(results.Series));
                    return (items, null);
                }

                default:
                    return (new List<TvItem>(), null);
            }
        }

        /// <summary>
        /// The channel slug that fronts a facet, resolved from the loaded channel list rather
        /// than assumed. Search tags its hits with all four facets, but only three have a
        /// channel — <c>language</c> has none, so a language hit is not drillable and comes
        /// back empty-slugged rather than 404-ing.
        /// </summary>
        private string SlugForFacet(string facet)
        {
            if (string.IsNullOrEmpty(facet)) return "";
            return _tvCategories.FirstOrDefault(c =>
                       string.Equals(c.Facet, facet, StringComparison.OrdinalIgnoreCase))?.Slug ?? "";
        }

        // ──────────────────────────────────────────────────────
        // Breadcrumb
        // ──────────────────────────────────────────────────────

        private void RebuildTvBreadcrumb()
        {
            TvBreadcrumbBar.Children.Clear();

            if (_tvStack.Count < 2)
            {
                TvBreadcrumbBar.Visibility = Visibility.Collapsed;
                return;
            }

            for (int i = 0; i < _tvStack.Count; i++)
            {
                if (i > 0) AddTvSeparator();
                int index = i;
                bool isCurrent = i == _tvStack.Count - 1;
                AddTvCrumb(_tvStack[i].Label, isCurrent, isCurrent ? null : () => TvPopTo(index));
            }

            TvBreadcrumbBar.Visibility = Visibility.Visible;
        }

        private void TvPopTo(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= _tvStack.Count) return;
            _tvStack.RemoveRange(frameIndex + 1, _tvStack.Count - frameIndex - 1);
            RebuildTvBreadcrumb();
            ApplyTvNarrow();
        }

        private void AddTvCrumb(string text, bool isCurrent, Action onClick)
        {
            if (isCurrent || onClick == null)
            {
                TvBreadcrumbBar.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                return;
            }

            var b = new Button
            {
                Content  = text,
                Style    = (Style)FindResource("TextButton"),
                FontSize = 12,
                Padding  = new Thickness(0),
            };
            b.Click += (_, __) => onClick();
            TvBreadcrumbBar.Children.Add(b);
        }

        private void AddTvSeparator()
        {
            TvBreadcrumbBar.Children.Add(new TextBlock
            {
                Text = " › ",
                FontSize = 12,
                Foreground = (Brush)FindResource("TextDimBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // ──────────────────────────────────────────────────────
        // Filter box: narrows a browse, or runs a catalog search
        // ──────────────────────────────────────────────────────

        private void TvSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_tvSection == TvSection.Search)
            {
                _tvSearchDebounce?.Stop();
                _tvSearchDebounce?.Start();
            }
            else
            {
                // Browse: narrow what is already loaded, client-side. Instant, no round trip —
                // and honest about it, because it can only narrow the pages actually fetched.
                ApplyTvNarrow();
            }
        }

        private async Task RunTvSearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                _tvStack.Clear();
                NullcastTvItems.Clear();
                RebuildTvBreadcrumb();
                UpdateTvTabState();
                return;
            }

            Telemetry.Track("search", new() { ["source"] = "nullcast_tv", ["length"] = query.Length });
            await PushTvFeedAsync(new TvFeed
            {
                Kind  = TvFeedKind.SearchResults,
                Label = $"“{query}”",
                Query = query,
            }, replaceStack: true);
        }

        /// <summary>
        /// Client-side narrowing of the level on show (title/subtitle contains).
        /// <paramref name="keepScroll"/> is set when appending a page — sending the reader back
        /// to the top after they asked for more is the opposite of what they wanted.
        /// </summary>
        private void ApplyTvNarrow(bool keepScroll = false)
        {
            ApplyTvViewMode();

            var feed = TvTop;
            var source = feed?.Items ?? new List<TvItem>();

            // Always a copy: the render loop yields to the dispatcher between batches, and the
            // feed it came from can gain a page while it is suspended.
            var q = _tvSection == TvSection.Search ? "" : (TvSearchBox?.Text ?? "").Trim();
            var filtered = q.Length == 0
                ? new List<TvItem>(source)
                : source.Where(it =>
                      it.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || it.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            RenderTvItems(filtered, keepScroll);
        }

        /// <summary>
        /// Fill the bound collection in small batches, yielding between them. The tile grid is
        /// not UI-virtualized, so adding a few hundred rows at once would realize every tile and
        /// start every image download in one synchronous burst — the spinner stalls and the
        /// mouse locks. A render token cancels an in-flight fill the moment a newer one starts.
        /// </summary>
        private async void RenderTvItems(IReadOnlyList<TvItem> items, bool keepScroll = false)
        {
            int myToken = ++_tvRenderToken;

            double offset = 0;
            if (keepScroll)
            {
                _tvScroller ??= FindDescendant<ScrollViewer>(TvResultsBox);
                offset = _tvScroller?.VerticalOffset ?? 0;
            }

            NullcastTvItems.Clear();
            if (!keepScroll) ScrollTvToTop();

            const int batch = 24;
            for (int i = 0; i < items.Count; i++)
            {
                if (myToken != _tvRenderToken) return;
                NullcastTvItems.Add(items[i]);
                if ((i + 1) % batch == 0)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            if (myToken != _tvRenderToken) return;

            // Put the reader back where they were, once the rows they were looking at exist again.
            if (keepScroll && offset > 0) _tvScroller?.ScrollToVerticalOffset(offset);

            UpdateTvTabState();
        }

        private void ScrollTvToTop()
        {
            _tvScroller ??= FindDescendant<ScrollViewer>(TvResultsBox);
            _tvScroller?.ScrollToTop();
        }

        // ──────────────────────────────────────────────────────
        // View mode: compact list ⇄ 16:9 tiles (remembered)
        // ──────────────────────────────────────────────────────

        private void TvListView_Click(object sender, RoutedEventArgs e)       => SetTvViewMode(tiles: false);
        private void TvTileView_Click(object sender, RoutedEventArgs e)       => SetTvViewMode(tiles: true);
        private void TvFullscreenView_Click(object sender, RoutedEventArgs e) => ToggleBrowseFullscreen();

        private void SetTvViewMode(bool tiles)
        {
            if (_plexFullscreen) ExitBrowseFullscreen(resumeVideo: true, reapply: false);
            if (_settings.NullcastTvTileView != tiles)
            {
                _settings.NullcastTvTileView = tiles;
                SaveSettings();
                Telemetry.Track("nullcast_tv_view_mode", new() { ["mode"] = tiles ? "tiles" : "list" });
            }
            ApplyTvViewMode();
        }

        /// <summary>
        /// Swap the results list between the 16:9 tile grid, the compact row and the lean
        /// episode row, and reflect the active mode on the toolbar. Idempotent and safe before
        /// any data loads, so it can run on every render.
        /// </summary>
        private void ApplyTvViewMode()
        {
            if (TvResultsBox == null) return;

            bool episodes = TvTop?.IsEpisodeList == true;
            bool tiles    = !episodes && (_settings.NullcastTvTileView || _plexFullscreen);

            var tpl = (DataTemplate)FindResource(
                episodes ? "TvEpisodeItemTemplate"
                         : tiles ? "TvTileItemTemplate" : "TvListItemTemplate");
            var panel = (ItemsPanelTemplate)FindResource(tiles ? "TvTilePanel" : "TvListPanel");

            if (!ReferenceEquals(TvResultsBox.ItemTemplate, tpl))  TvResultsBox.ItemTemplate = tpl;
            if (!ReferenceEquals(TvResultsBox.ItemsPanel, panel))  TvResultsBox.ItemsPanel   = panel;

            StyleViewToggleButton(TvListViewBtn,       !_settings.NullcastTvTileView && !_plexFullscreen);
            StyleViewToggleButton(TvTileViewBtn,        _settings.NullcastTvTileView && !_plexFullscreen);
            StyleViewToggleButton(TvFullscreenViewBtn,  _plexFullscreen);
        }

        // ──────────────────────────────────────────────────────
        // Activation: drill in, or play
        // ──────────────────────────────────────────────────────

        private async void TvResultsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigatingQueue) return;   // Next/Prev drives the selection itself; don't re-capture
            if (e.AddedItems.Count == 0) return;
            if (TvResultsBox.SelectedItem is not TvItem item) return;

            if (item.IsContainer)
            {
                await TvDrillInto(item);
                return;
            }

            // Picking something to play collapses the full-screen takeover back to the split
            // view. Don't resume the old paused item — we're starting a new one.
            if (_plexFullscreen) ExitBrowseFullscreen(resumeVideo: false, reapply: true);

            // Next/Prev walk the films at the level on show, in the order they are listed.
            // Facet values and series are skipped — you cannot "play next" a directory.
            var films = NullcastTvItems.Where(i => i.IsPlayable).Cast<object>().ToList();
            SetFlatQueue(films, films.IndexOf(item), TvResultsBox);

            await PlayTvItemAsync(item);
        }

        private async Task TvDrillInto(TvItem item)
        {
            switch (item.Kind)
            {
                case TvItemKind.Facet:
                    if (string.IsNullOrEmpty(item.FacetSlug))
                    {
                        // A language hit: the facet is real and on every film, but the catalog
                        // seeds no channel in front of it, so there is nothing to open.
                        ShowTvError($"“{item.Title}” has no browsable channel on Nullcast.TV.");
                        return;
                    }
                    await PushTvFeedAsync(new TvFeed
                    {
                        Kind       = TvFeedKind.FacetValue,
                        Label      = item.Title,
                        Slug       = item.FacetSlug,
                        FacetValue = item.FacetValue,
                    }, replaceStack: false);
                    break;

                case TvItemKind.Series:
                    await PushTvFeedAsync(new TvFeed
                    {
                        Kind  = TvFeedKind.SeriesEpisodes,
                        Label = item.Title,
                        Slug  = item.SeriesSlug,
                    }, replaceStack: false);
                    break;
            }
        }

        /// <summary>
        /// Play a Nullcast.TV film. The catalog links to somebody else's platform, so this is
        /// the ordinary yt-dlp path — the one difference is the history key, which is the
        /// catalog id rather than the platform URL so the entry survives a link rotting and
        /// carries the right source badge.
        /// </summary>
        private async Task PlayTvItemAsync(TvItem item)
        {
            if (item == null) return;
            if (string.IsNullOrEmpty(item.SourceUrl))
            {
                ShowTvError("That film has no playable source URL.");
                return;
            }

            // The catalog is a separate ecosystem — detach from the bookmarks position-save
            // path. Nullcast.TV keeps no resume position of its own, so nothing to seek to.
            _activeMuid = null;
            _seekOnPlay = null;

            Telemetry.Track("media_play", new()
            {
                ["source"]      = "nullcast_tv",
                ["title"]       = item.Title ?? "",
                ["media_id"]    = item.VideoId ?? "",
                ["media_type"]  = item.Source ?? "",
                ["duration_ms"] = item.DurationMs,
            });

            await PlayUrl(item.SourceUrl, item.Title,
                          historyUrl: $"nulltv://{item.VideoId}");
        }

        // ──────────────────────────────────────────────────────
        // "Open on Nullcast.TV" — the top-bar link
        // ──────────────────────────────────────────────────────

        /// <summary>The catalog id of the film currently playing, or null when it isn't one.</summary>
        private string _activeTvVideoId;

        /// <summary>
        /// Record which Nullcast.TV film is playing (null for anything else) and show or hide
        /// the top-bar link accordingly. Called from the playback paths rather than from the
        /// tab, so the button follows what is actually on screen — including a film reached from
        /// History, the Ctrl+K palette, or auto-advance.
        /// </summary>
        private void SetActiveTvVideo(string videoId)
        {
            _activeTvVideoId = string.IsNullOrWhiteSpace(videoId) ? null : videoId;

            if (WatchOnNullcastButton != null)
                WatchOnNullcastButton.Visibility =
                    _activeTvVideoId == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void WatchOnNullcast_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTvVideoId == null) return;

            var url = $"{NullcastTvAuthService.Origin}/v/{_activeTvVideoId}";
            Telemetry.Track("nullcast_tv_open_web", new() { ["media_id"] = _activeTvVideoId });

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Log($"[Nullcast.TV] Could not open {url}: {ex.Message}");
            }
        }

        /// <summary>
        /// Replay a Nullcast.TV film recorded in local History. Only the catalog id was stored,
        /// so the platform URL is fetched back from the catalog — which is the point: it is the
        /// catalog's to hand out, and a cached copy would pin a link that can rot.
        /// </summary>
        private async Task PlayNullcastTvFromHistory(string nulltvUrl)
        {
            if (_tvAuth?.IsSignedIn != true)
            {
                MessageBox.Show("Sign in to Nullcast.TV (⚙ Settings) to play this film.",
                    "Nullcast.TV", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var id = nulltvUrl["nulltv://".Length..];
            TvApiVideo video;
            try
            {
                video = await _tv.GetVideoAsync(id);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Nullcast.TV",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (video == null || string.IsNullOrEmpty(video.SourceUrl))
            {
                MessageBox.Show("That film is no longer available on Nullcast.TV.",
                    "Nullcast.TV", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await PlayTvItemAsync(TvItem.FromVideo(video, NullcastTvService.ResolveThumbUrl));
        }
    }
}
