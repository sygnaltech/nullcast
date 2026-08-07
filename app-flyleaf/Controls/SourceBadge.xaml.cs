using System.Windows;
using System.Windows.Controls;
using VideoPlayer.Models;

namespace VideoPlayer.Controls
{
    /// <summary>
    /// A small "app tile" for a content integration: a coloured disc with the source's glyph
    /// on top, both drawn in the one brand colour (disc = low alpha, glyph = full strength).
    /// Point it at a source either directly (<see cref="SourceKey"/>) or by a recorded playback
    /// URL (<see cref="Url"/>, classified via <see cref="IntegrationSource.Resolve"/>).
    ///
    /// The badge is orientation-agnostic: in the sidebar it inherits the tab's -90° rotation
    /// (so it tilts with the label), and in History it renders upright to the item's left.
    /// </summary>
    public partial class SourceBadge : UserControl
    {
        public SourceBadge()
        {
            InitializeComponent();
            ApplySize();
            ApplySource(IntegrationSource.Playlist);
        }

        /// <summary>Explicit source key (e.g. "Plex"). Takes precedence over <see cref="Url"/>.</summary>
        public static readonly DependencyProperty SourceKeyProperty =
            DependencyProperty.Register(nameof(SourceKey), typeof(string), typeof(SourceBadge),
                new PropertyMetadata(null, OnSourceChanged));

        public string SourceKey
        {
            get => (string)GetValue(SourceKeyProperty);
            set => SetValue(SourceKeyProperty, value);
        }

        /// <summary>Recorded playback URL — classified into a source when no key is set.</summary>
        public static readonly DependencyProperty UrlProperty =
            DependencyProperty.Register(nameof(Url), typeof(string), typeof(SourceBadge),
                new PropertyMetadata(null, OnSourceChanged));

        public string Url
        {
            get => (string)GetValue(UrlProperty);
            set => SetValue(UrlProperty, value);
        }

        /// <summary>Overall diameter of the badge, in DIPs.</summary>
        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register(nameof(Size), typeof(double), typeof(SourceBadge),
                new PropertyMetadata(22.0, OnSizeChanged));

        public double Size
        {
            get => (double)GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var badge = (SourceBadge)d;
            badge.ApplySource(badge.Resolve());
        }

        private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((SourceBadge)d).ApplySize();

        private IntegrationSource Resolve() =>
            !string.IsNullOrEmpty(SourceKey) ? IntegrationSource.Get(SourceKey)
                                             : IntegrationSource.Resolve(Url);

        private void ApplySource(IntegrationSource src)
        {
            Disc.Fill    = src.CircleBrush;
            Disc.Stroke  = src.RingBrush;
            Glyph.Data   = src.Icon;
            Glyph.Fill   = src.IconBrush;
            ToolTip      = src.DisplayName;
        }

        private void ApplySize()
        {
            Root.Width  = Size;
            Root.Height = Size;
            // Glyph occupies ~54% of the disc, centred, so the disc reads as a tile.
            GlyphBox.Margin = new Thickness(Size * 0.23);
        }
    }
}
