# Video Player — Style Guide

The desktop client (`app-flyleaf/`, WPF, net8.0-windows) follows the **Indigo Slate** dark
theme. This guide is the source of truth for the look. Read it before adding UI so new controls
match — and so recurring papercuts (like un-styled scrollbars) don't get reintroduced.

All shared brushes, control styles, and data/panel templates live in `MainWindow.xaml`
under `<Window.Resources>`. Prefer referencing an existing resource over hand-coding a color
or a control template inline.

---

## Palette

Defined as `SolidColorBrush` resources at the top of `MainWindow.xaml`. Use the resource key,
not the raw hex, wherever a `DynamicResource`/`StaticResource` reference is practical.

| Role | Key | Hex |
|------|-----|-----|
| App background | `BgAppBrush` | `#0F1118` |
| Menu / raised background | `BgMenuBrush` | `#151826` |
| Controls background | `BgControlsBrush` | `#141726` |
| Accent (indigo) | `AccentBrush` | `#7D97FF` |
| Accent light | `AccentLightBrush` | `#9DB0FF` |
| Accent tint (selection fill) | `AccentTintBrush` | `#217D97FF` |
| Text primary | `TextPrimaryBrush` | `#E7E9F1` |
| Text bright | `TextBrightBrush` | `#EEF1FB` |
| Text muted | `TextMutedBrush` | `#848B9F` |
| Text dim | `TextDimBrush` | `#565C6D` |
| Hairline divider | `HairlineBrush` | `#0FFFFFFF` |
| Surface (subtle fill) | `SurfaceBrush` | `#08FFFFFF` |
| Surface border | `SurfaceBorderBrush` | `#1AFFFFFF` |
| Track (slider/scroll trough) | `TrackBrush` | `#21FFFFFF` |

Translucent whites (`#08FFFFFF`, `#12FFFFFF`, `#14FFFFFF`, `#1AFFFFFF`, `#24FFFFFF`) are the
standard way to build hover/selection/border layers over the dark background — they adapt to
whatever is underneath instead of introducing a new opaque grey.

## Typography

- Font family: **Instrument Sans**, bundled and referenced via the `AppFont` resource
  (`pack://application:,,,/Fonts/#Instrument Sans`). Do not depend on a system-installed font.
- List row title: 14px, `FontWeight="Medium"`, `#E7E9F1`.
- Secondary / subtitle: 12px, `#848B9F`.
- Accent meta line (progress, type): 12px, `#7D97FF`.
- Small chips / captions: 10–11px.
- For glyph icons that may not exist in Instrument Sans, set `FontFamily="Segoe UI Symbol"`.

## Shape & spacing

- Corner radius: cards/rows `10`, surfaces/inputs `8`, pills `14`, chips `4`.
- List item selection uses a rounded card (`#217D97FF` fill) plus a 3px left accent bar
  (`#7D97FF`) — see the `ListItemContainer` style. Reuse it via
  `ItemContainerStyle="{StaticResource ListItemContainer}"` on every list.

---

## Scrollbars — REQUIRED on every scrollable list

**The default WPF scrollbar (wide, light, chrome buttons) must never ship.** Every `ListBox`,
`ScrollViewer`, or other scrollable region uses the house **thin dark 9px** scrollbar.

A single keyed style, `ThinDarkScrollBar`, lives in `<Window.Resources>`. Apply it to a list
by adding an implicit `ScrollBar` style scoped to that control that inherits from it:

```xml
<ListBox ... >
    <ListBox.Resources>
        <Style TargetType="{x:Type ScrollBar}"
               BasedOn="{StaticResource ThinDarkScrollBar}"/>
    </ListBox.Resources>
</ListBox>
```

Also set `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` on vertical lists so no
horizontal bar appears.

Characteristics of the house scrollbar: 9px wide, transparent trough, rounded thumb
`#24FFFFFF` (→ `#3DFFFFFF` on hover), no arrow repeat buttons.

> Checklist when adding a new list/scroll region: **(1)** dark item container style,
> **(2)** `ThinDarkScrollBar` in the control's `Resources`, **(3)** horizontal scrollbar disabled.

---

## Buttons

- `PlexSegment` — pill segment (active/inactive states driven in code with the `Seg*` brushes).
- `IconButton` — square 8px-radius surface button for glyph actions (refresh, view toggle).
- `AccentButton` — filled indigo call-to-action.
- `TextButton` — borderless, muted → bright on hover.

Active/inactive toggling for segmented/icon toggles is done in code-behind using the shared
`SegActiveBg` / `SegInactiveBg` / `SegActiveFg` / `SegInactiveFg` brushes (see
`StylePlexSegments` and `ApplyPlexViewMode`).

## The Plex results panel (reference implementation)

The Plex tab demonstrates the list conventions and the dual **list / tile** view:

- `PlexListItemTemplate` — compact row: small poster, title, subtitle, accent meta line,
  and a genre tag line. Best for scanning and sorting.
- `PlexTileItemTemplate` — poster tile (2:3 art, placeholder when absent), title, meta line,
  and wrapped genre chips (`PlexGenreChip`). Tile width (134) is tuned so **two** tiles fit the
  325px sidebar with the 9px scrollbar present, and more columns appear as the panel widens.
- **Episode-number badge** — inside a season, episodes carry a compact `E{n}` pill
  (`#CC0F1118` fill, `#9DB0FF` text) overlaid on the **bottom-left** of the poster in both the
  list and tile templates, so ordering reads at a glance. Bound to `PlexItem.HasEpisodeBadge` /
  `EpisodeBadge`; the list row's leading-art column collapses via `HasLeadArt` when there's
  neither a poster nor a badge.
- Panels: `PlexListPanel` (`VirtualizingStackPanel`) and `PlexTilePanel` (`WrapPanel`).
- The view toggle is a three-button `IconButton` group above the list — list, tiles, and
  **full-screen tiles**. `ApplyPlexViewMode()` swaps `ItemTemplate` + `ItemsPanel` and highlights
  the active button; list/tile is persisted in `AppSettings.PlexTileView`.
- Full-screen browse (`EnterPlexFullscreen`/`ExitPlexFullscreen`) expands the side panel across
  the video column (via the named `VideoColumn`/`PanelColumn`/`SidePanelColumn`), pauses playback,
  and resumes it on exit if it had been playing. It's a transient state — not persisted — and is
  auto-dropped when you switch away from the Plex tab.

Posters come from Plex's photo transcoder (`PlexService.ResolveThumbUrl`) so artwork is
downloaded pre-sized rather than at full resolution.

### Sidebar tabs (vertical strip)

The side-panel tabs run as a **vertical strip down the left edge** of the panel, not a
horizontal row — this scales to many tabs without clipping. Each tab is a `VerticalTabButton`:
its label is rotated 90° (`RotateTransform Angle="-90"`, reads bottom-to-top) and the active
tab shows a **3px left accent bar** (`#7D97FF`) plus bright text. Active state is still driven
in code by `StyleTab` (it sets `Foreground` + `BorderBrush`; on the vertical button `BorderBrush`
paints the left bar instead of the old underline). The strip lives in an auto-width column with
the content in the `*` column beside it, and is wrapped in a `ScrollViewer` (house
`ThinDarkScrollBar`) so a short window scrolls rather than hiding a tab. Order: Playlist ·
History · Plex · Podcasts · YT Music.

### Menu toggle checkmarks

Checkable `MenuItem`s (File ▸ *Use Edge cookies*, *Auto-play next episode*) render a `✓` glyph
(`CheckGlyph`, accent `#7D97FF`) in the item's left icon slot, shown via an `IsChecked=True`
trigger in the custom `MenuItem` template. Without it the toggle state is invisible — the custom
template owns the whole visual tree, so the system check adorner never appears.

### YT Music tab

The **YT Music** sidebar tab (a fifth `SidebarTab`) mirrors the Podcasts tab's structure —
search box, status line, and a single `ListBox` (`YtMusicItems`) whose rows reuse
`DisplayTitle` / `DisplaySubtitle` for both **playlist** rows (drill chevron, via
`IsPlaylist`) and **track** rows (play). Data is yt-dlp-backed (`YtMusicService`), not a REST
service, but it follows the same service + list-render conventions. A "Back to playlists" link
and a "＋ Pin playlist" action (clipboard URL → `AppSettings.YtMusicPlaylists`) sit in a thin
toolbar under the search box. Same `ThinDarkScrollBar` + `ListItemContainer` rules apply.

### Up-next auto-play card

At the very end of a TV episode (`Status.Ended`), if the next episode in the same show is
queued and auto-play is enabled, the `NextEpisodeOverlay` card appears **bottom-right** of the
video: a small raised panel (`#F00F1118`, 12px radius, soft drop shadow) with an "UP NEXT"
caption, the next title/subtitle, a **circular countdown** (indigo-stroked `Ellipse` over an
`AccentTint` fill, click to play now), and a `TextButton` **Cancel**. A `DispatcherTimer` counts
`3 → 0` then auto-advances. It's dismissed by Cancel, by starting any other playback
(`Status.Playing` clears it), or by toggling off File ▸ **Auto-play next episode**
(`AppSettings.AutoPlayNextEpisode`).
