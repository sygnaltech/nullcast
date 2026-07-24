using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CodeHollow.FeedReader;
using CodeHollow.FeedReader.Feeds;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Podcast discovery and episode listing. Discovery uses the free Apple iTunes Search
    /// API (no key required); episodes come from parsing the show's RSS feed. Playing an
    /// episode's audio enclosure is handled by the main player's direct-media path.
    /// </summary>
    public class PodcastService
    {
        private static readonly HttpClient _http = new();

        /// <summary>Searches Apple Podcasts for shows matching <paramref name="term"/>.</summary>
        public async Task<List<PodcastShow>> SearchAsync(string term)
        {
            var shows = new List<PodcastShow>();
            if (string.IsNullOrWhiteSpace(term)) return shows;

            var url = "https://itunes.apple.com/search?media=podcast&entity=podcast&limit=25&term="
                    + Uri.EscapeDataString(term.Trim());

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return shows;

            foreach (var r in results.EnumerateArray())
            {
                var feed = GetStr(r, "feedUrl");
                if (string.IsNullOrEmpty(feed)) continue;   // can't list episodes without a feed

                shows.Add(new PodcastShow
                {
                    Title      = GetStr(r, "collectionName") ?? GetStr(r, "trackName"),
                    Author     = GetStr(r, "artistName"),
                    FeedUrl    = feed,
                    ArtworkUrl = GetStr(r, "artworkUrl600")
                              ?? GetStr(r, "artworkUrl100")
                              ?? GetStr(r, "artworkUrl60"),
                });
            }
            return shows;
        }

        /// <summary>Parses a show's RSS feed into a list of playable episodes.</summary>
        public async Task<List<PodcastEpisode>> GetEpisodesAsync(PodcastShow show)
        {
            var episodes = new List<PodcastEpisode>();
            if (string.IsNullOrEmpty(show?.FeedUrl)) return episodes;

            var feed = await FeedReader.ReadAsync(show.FeedUrl);
            foreach (var item in feed.Items)
            {
                var audio = GetEnclosureUrl(item);
                if (string.IsNullOrEmpty(audio)) continue;   // skip non-audio items

                var date = item.PublishingDate?.ToLocalTime().ToString("d MMM yyyy");
                episodes.Add(new PodcastEpisode
                {
                    Title      = item.Title?.Trim(),
                    AudioUrl   = audio,
                    Subtitle   = string.IsNullOrEmpty(date) ? show.Title : $"{date}  •  {show.Title}",
                    ArtworkUrl = show.ArtworkUrl,
                    ShowTitle  = show.Title,
                });
            }
            return episodes;
        }

        private static string GetEnclosureUrl(FeedItem item)
        {
            return item.SpecificItem switch
            {
                Rss20FeedItem rss when rss.Enclosure != null      => rss.Enclosure.Url,
                MediaRssFeedItem media when media.Enclosure != null => media.Enclosure.Url,
                _ => null,
            };
        }

        private static string GetStr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
