using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Reads the Nullcast.TV catalog over its public REST API.
    ///
    /// <para>
    /// The whole catalog hangs off one idea: <b>a channel is a saved way of looking at it</b>.
    /// A <c>query</c> or <c>curated</c> channel answers with a page of films; a <c>facet</c>
    /// channel asked without a value answers with a <i>directory</i> of its values, and asked
    /// with one answers with that value's films. <see cref="TvChannelFeed"/> carries both, and
    /// <c>kind</c> is the only thing a caller branches on.
    /// </para>
    ///
    /// <para>
    /// Every response carries an opaque <c>cursor</c>; <c>null</c> means the end. It happens to
    /// be an offset today and may become a keyset cursor, so it is passed back verbatim and
    /// never computed.
    /// </para>
    ///
    /// <para>
    /// Requests always carry the caller's bearer token. Anonymous reads work, but 18+ films are
    /// silently filtered out of every feed and count — which would make the catalog quietly
    /// disagree with itself — so this client treats "enabled" as "signed in".
    /// </para>
    /// </summary>
    public class NullcastTvService
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Nullcast-Player");
            return c;
        }

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly NullcastTvAuthService _auth;

        public NullcastTvService(NullcastTvAuthService auth) => _auth = auth;

        public bool IsSignedIn => _auth?.IsSignedIn == true;

        // ──────────────────────────────────────────────────────
        // Catalog
        // ──────────────────────────────────────────────────────

        /// <summary>Every browse group the catalog publishes, in the server's own order.</summary>
        public async Task<List<TvApiChannel>> GetChannelsAsync()
        {
            var list = await GetAsync<List<TvApiChannel>>("/public/channels");
            return list ?? new List<TvApiChannel>();
        }

        /// <summary>
        /// One page of a channel. Pass <paramref name="facetValue"/> to ask a facet channel for
        /// a value's films rather than its directory, and <paramref name="cursor"/> (verbatim,
        /// from a previous page) to continue.
        /// </summary>
        public async Task<TvChannelFeed> GetChannelPageAsync(
            string slug, string facetValue = null, string cursor = null)
        {
            var query = new List<string>();
            if (!string.IsNullOrEmpty(facetValue))
                query.Add("facet=" + Uri.EscapeDataString(facetValue));
            if (!string.IsNullOrEmpty(cursor))
                query.Add("cursor=" + Uri.EscapeDataString(cursor));

            var path = $"/public/channels/{Uri.EscapeDataString(slug)}";
            if (query.Count > 0) path += "?" + string.Join("&", query);

            return await GetAsync<TvChannelFeed>(path) ?? new TvChannelFeed();
        }

        /// <summary>
        /// One film by id. Used to replay a <c>nulltv://</c> entry out of local History, where
        /// only the id was recorded — the platform URL is the catalog's to hand back, and
        /// caching it locally would pin a link that can rot.
        /// </summary>
        public async Task<TvApiVideo> GetVideoAsync(string id) =>
            await GetAsync<TvApiVideo>($"/public/videos/{Uri.EscapeDataString(id)}");

        /// <summary>Every series, alphabetically, each with its on-air episode count.</summary>
        public async Task<List<TvApiSeries>> GetSeriesAsync()
        {
            var list = await GetAsync<List<TvApiSeries>>("/public/series");
            return list ?? new List<TvApiSeries>();
        }

        /// <summary>One series and its episodes, in running order.</summary>
        public async Task<TvSeriesFeed> GetSeriesFeedAsync(string slug) =>
            await GetAsync<TvSeriesFeed>($"/public/series/{Uri.EscapeDataString(slug)}");

        /// <summary>
        /// Search films, facet values and series in one round trip. A query under the server's
        /// minimum length answers 200 with three empty lists rather than an error.
        /// </summary>
        public async Task<TvSearchResults> SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new TvSearchResults();
            var path = "/public/search?q=" + Uri.EscapeDataString(query.Trim());
            return await GetAsync<TvSearchResults>(path) ?? new TvSearchResults();
        }

        // ──────────────────────────────────────────────────────
        // Materialisation (DTO → the row the list binds to)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// The image to show for a film. <c>posterUrl</c> is the frame the maker chose and the
        /// only image URL under Nullcast's control — a platform CDN URL can rot without warning
        /// — so it wins whenever it exists. It arrives RELATIVE and must be joined to the origin.
        /// </summary>
        public static string ResolveThumbUrl(TvApiVideo v)
        {
            if (v == null) return null;
            if (!string.IsNullOrEmpty(v.PosterUrl)) return AbsoluteUrl(v.PosterUrl);
            return string.IsNullOrEmpty(v.ThumbUrl) ? null : v.ThumbUrl;
        }

        /// <summary>
        /// A facet value's or series' thumbnail. Already resolved server-side by the same rule,
        /// so it is either an absolute platform URL or a relative <c>/api/poster/…</c> path.
        /// </summary>
        public static string AbsoluteUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;
            return NullcastTvAuthService.Origin + (url.StartsWith('/') ? url : "/" + url);
        }

        public static List<TvItem> ToItems(IEnumerable<TvApiVideo> videos) =>
            videos == null
                ? new List<TvItem>()
                : videos.Select(v => TvItem.FromVideo(v, ResolveThumbUrl)).ToList();

        public static List<TvItem> ToItems(IEnumerable<TvFacetValue> facets, string channelSlug, string facet) =>
            facets == null
                ? new List<TvItem>()
                : facets.Select(f => TvItem.FromFacet(f, channelSlug, facet, AbsoluteUrl(f.ThumbUrl))).ToList();

        public static List<TvItem> ToItems(IEnumerable<TvApiSeries> series) =>
            series == null
                ? new List<TvItem>()
                : series.Select(s => TvItem.FromSeries(s, AbsoluteUrl(s.ThumbUrl))).ToList();

        // ──────────────────────────────────────────────────────
        // Connection check (used by the settings page)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Verify the stored credential by asking the catalog for its channel list. Returns a
        /// friendly (ok, message) pair and never throws.
        /// </summary>
        public async Task<(bool Ok, string Message)> TestConnectionAsync()
        {
            if (!IsSignedIn) return (false, "Not signed in to Nullcast.TV.");
            try
            {
                var channels = await GetChannelsAsync();
                return Describe(channels.Count);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Verify a bearer token that has not been saved yet — the settings dialog uses this so
        /// "Test connection" can check what is in the box without committing it, the same way
        /// the Plex page tests a typed server and token.
        /// </summary>
        public static async Task<(bool Ok, string Message)> TestCredentialAsync(string token)
        {
            token = (token ?? "").Trim();
            if (token.Length == 0) return (false, "Enter a personal access token.");

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, NullcastTvAuthService.ApiBase + "/me");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await Http.SendAsync(req);

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return (false, "Nullcast.TV rejected that token.");
                if (!resp.IsSuccessStatusCode)
                    return (false, $"Nullcast.TV responded {(int)resp.StatusCode} {resp.StatusCode}.");

                var body = await resp.Content.ReadAsStringAsync();
                var name = JsonSerializer.Deserialize<TvSessionInfo>(body, Json)?.User?.Name;
                return (true, string.IsNullOrWhiteSpace(name)
                    ? "Token accepted."
                    : $"Token accepted — signed in as {name}.");
            }
            catch (Exception ex)
            {
                return (false, $"Could not reach Nullcast.TV: {ex.Message}");
            }
        }

        private static (bool Ok, string Message) Describe(int channelCount) =>
            channelCount > 0
                ? (true, $"Connected — {channelCount} browse groups available.")
                : (true, "Connected, but the catalog published no channels.");

        // ──────────────────────────────────────────────────────
        // Transport
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// GET a versioned API path and deserialize it. Throws a message fit to show a person:
        /// this is the only place that knows what the catalog's status codes mean.
        /// </summary>
        private async Task<T> GetAsync<T>(string path)
        {
            var url   = NullcastTvAuthService.ApiBase + path;
            var token = _auth == null ? "" : await _auth.GetAccessTokenAsync();
            var sw    = Stopwatch.StartNew();

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req);
            }
            catch (Exception ex)
            {
                App.Log($"[Nullcast.TV] GET {path} failed after {sw.ElapsedMilliseconds}ms: {ex.Message}");
                throw new Exception($"Could not reach Nullcast.TV: {ex.Message}");
            }

            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync();
                App.Log($"[Nullcast.TV] GET {path} → {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms");

                if (resp.IsSuccessStatusCode)
                {
                    try
                    {
                        return JsonSerializer.Deserialize<T>(body, Json);
                    }
                    catch (JsonException ex)
                    {
                        throw new Exception($"Nullcast.TV returned an unexpected response: {ex.Message}");
                    }
                }

                throw new Exception(DescribeFailure(resp.StatusCode, body));
            }
        }

        /// <summary>Turn a catalog error into a sentence worth putting on screen.</summary>
        private static string DescribeFailure(HttpStatusCode status, string body)
        {
            var detail = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e))
                    detail = e.GetString() ?? "";
            }
            catch { /* a non-JSON body is nothing to report */ }

            return status switch
            {
                HttpStatusCode.Unauthorized =>
                    "Nullcast.TV rejected the credential — sign in again from Settings.",
                HttpStatusCode.Forbidden =>
                    // The film is on air; it simply needs an identified viewer. Never a dead end.
                    string.IsNullOrEmpty(detail) ? "That film needs a signed-in account." : detail,
                HttpStatusCode.NotFound =>
                    "No such channel or series on Nullcast.TV.",
                _ => string.IsNullOrEmpty(detail)
                        ? $"Nullcast.TV responded {(int)status} {status}."
                        : $"Nullcast.TV: {detail}",
            };
        }
    }
}
