namespace VideoPlayer.Models
{
    /// <summary>A podcast show returned by the Apple iTunes Search API.</summary>
    public class PodcastShow
    {
        public string Title { get; set; }
        public string Author { get; set; }
        public string FeedUrl { get; set; }
        public string ArtworkUrl { get; set; }

        // Convenience for list binding.
        public string DisplayTitle => Title;
        public string DisplaySubtitle => Author;
    }

    /// <summary>A single episode parsed from a show's RSS feed.</summary>
    public class PodcastEpisode
    {
        public string Title { get; set; }
        public string AudioUrl { get; set; }
        public string Subtitle { get; set; }   // "12 Mar 2026 • Show name"
        public string ArtworkUrl { get; set; }
        public string ShowTitle { get; set; }

        public string DisplayTitle => Title;
        public string DisplaySubtitle => Subtitle;
    }
}
