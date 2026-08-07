using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoPlayer.Controls
{
    /// <summary>
    /// Attached properties that load an <see cref="Image"/>'s bitmap off the render-critical path.
    ///
    /// Binding a remote URL straight to <c>Image.Source</c> (even with <c>IsAsync=True</c>) makes WPF
    /// decode each poster at full size and rescale it on the render thread — the same high-priority
    /// thread DWM composites from. When the Plex tab holds hundreds of posters and the sidebar pops
    /// out over the Direct3D video surface (which re-realizes the whole grid), that saturates the
    /// render thread and stalls DWM, freezing the system cursor.
    ///
    /// This loader instead:
    ///   • downloads and decodes on a thread-pool thread, capped to <see cref="DecodeWidthProperty"/>
    ///     pixels wide, then <c>Freeze()</c>s the result so only the final assignment touches the UI;
    ///   • caches the frozen bitmap per (url, width), so the popout's re-realization is a dictionary
    ///     hit — no re-download, no re-decode;
    ///   • de-dupes in-flight requests and caps concurrent downloads, so a grid of identical show
    ///     posters fetches once and never opens hundreds of sockets at once.
    ///
    /// Usage: <c>&lt;Image ctrl:AsyncImage.Url="{Binding ThumbUrl}" ctrl:AsyncImage.DecodeWidth="160"/&gt;</c>
    /// </summary>
    public static class AsyncImage
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        // Frozen bitmaps, keyed by "url|width". Frozen ⇒ safe to share across the tiles that reuse art.
        private static readonly ConcurrentDictionary<string, ImageSource> _cache = new();
        // In-flight loads, so N tiles requesting the same poster trigger one download.
        private static readonly ConcurrentDictionary<string, Task<ImageSource>> _inflight = new();
        // Cap concurrent network fetches; the render thread never sees a burst regardless.
        private static readonly SemaphoreSlim _gate = new(6, 6);

        // ── Url ──────────────────────────────────────────────────
        public static readonly DependencyProperty UrlProperty =
            DependencyProperty.RegisterAttached(
                "Url", typeof(string), typeof(AsyncImage),
                new PropertyMetadata(null, OnUrlChanged));

        public static void   SetUrl(DependencyObject o, string value) => o.SetValue(UrlProperty, value);
        public static string GetUrl(DependencyObject o) => (string)o.GetValue(UrlProperty);

        // ── DecodeWidth ──────────────────────────────────────────
        public static readonly DependencyProperty DecodeWidthProperty =
            DependencyProperty.RegisterAttached(
                "DecodeWidth", typeof(int), typeof(AsyncImage),
                new PropertyMetadata(0));

        public static void SetDecodeWidth(DependencyObject o, int value) => o.SetValue(DecodeWidthProperty, value);
        public static int  GetDecodeWidth(DependencyObject o) => (int)o.GetValue(DecodeWidthProperty);

        private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image img) return;

            var url = e.NewValue as string;
            if (string.IsNullOrEmpty(url))
            {
                img.Source = null;
                return;
            }

            int    decodeWidth = GetDecodeWidth(img);
            string key         = decodeWidth > 0 ? $"{url}|{decodeWidth}" : url;

            // Fast path: already decoded (the common case once a library has been browsed once,
            // and every case when the grid is re-realized by the sidebar popout).
            if (_cache.TryGetValue(key, out var cached))
            {
                img.Source = cached;
                return;
            }

            // Clear any stale art from a recycled/virtualized container while the new one loads.
            img.Source = null;

            try
            {
                var src = await LoadAsync(url, decodeWidth, key);
                // The container may have been recycled to a different item mid-flight — only apply
                // if this Image still wants this url.
                if (src != null && GetUrl(img) == url)
                    img.Source = src;
            }
            catch
            {
                // Leave the template's placeholder in place.
            }
        }

        private static Task<ImageSource> LoadAsync(string url, int decodeWidth, string key) =>
            _inflight.GetOrAdd(key, _ => LoadCoreAsync(url, decodeWidth, key));

        private static async Task<ImageSource> LoadCoreAsync(string url, int decodeWidth, string key)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                var src   = await Task.Run(() => Decode(bytes, decodeWidth)).ConfigureAwait(false);
                _cache[key] = src;
                return src;
            }
            finally
            {
                _gate.Release();
                _inflight.TryRemove(key, out _);
            }
        }

        /// <summary>Decode to at most <paramref name="decodeWidth"/> px wide and freeze — off the UI thread.</summary>
        private static ImageSource Decode(byte[] bytes, int decodeWidth)
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption   = BitmapCacheOption.OnLoad;     // decode now, then release the stream
            bmp.CreateOptions = BitmapCreateOptions.None;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();                                     // cross-thread safe, no per-frame locking
            return bmp;
        }
    }
}
