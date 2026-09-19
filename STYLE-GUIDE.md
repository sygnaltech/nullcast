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

## Title bar (custom caption)

The window uses a `WindowChrome` with `UseAeroCaptionButtons="False"`, so the caption is ours:
a 32px `CaptionBar` row at the top of `RootGrid` on `BgApp` (`#0F1118`), sitting above the
`#151826` menu bar. Icon + "Nullcast" on the left; the version (`TextMuted` `#848B9F`) and the
window buttons on the right. `TextDim` was tried first and is too dark to read against the
caption background at 12px.

- `CaptionButton` / `CaptionCloseButton` — 46×32, flat until hovered. The hover fill lives in
  `Tag` so the close button only has to override that one brush (`#E81123`).
- Glyphs are `Path` geometry (`CaptionGlyph`), not Segoe MDL2 text — the icon font differs
  between Windows 10 and 11. `Stroke` binds to the parent button's `Foreground` so hover
  brightening carries through.
- Anything clickable in the strip needs `WindowChrome.IsHitTestVisibleInChrome="True"`;
  everything else is caption, and drags the window.
- Two behaviours are maintained in code-behind (`MainWindow.xaml.cs`): the maximise glyph swaps
  to the restore glyph on state change, and `UpdateMaximizedInset()` pulls the content in by the
  measured overhang when maximized — a `WindowChrome` window is sized to the work area *plus* its
  frame, which would otherwise put the caption buttons under the screen edge. Fullscreen detaches
  the chrome entirely, or its caption band would keep stealing the top 32px of the video.

### Dialogs get the same caption

A `Window` that ships with the system caption paints light chrome above the Indigo Slate palette
and looks broken, so **every** window here runs its own. `ServicesSettingsDialog` is the pattern
for a fixed-size dialog: the same `WindowChrome` (`CaptionHeight="32"`, `UseAeroCaptionButtons="False"`)
but `ResizeBorderThickness="0"` — `ResizeMode="NoResize"` means there is no resize border to
reserve — and a 32px `#0F1118` strip holding the title on the left and a single close button on
the right. No minimise/maximise: a fixed dialog has nothing to do with them.

## Icons — geometry, not glyphs

Icons are `Path` geometry stored as a keyed `Geometry` resource (e.g. `IconGear`), **not** text
glyphs like `⚙`. A character renders at whatever weight and alignment the font-fallback chain
happens to land on, which is why the old gear looked thin and off-centre next to real UI.

- Author against the source SVG's `viewBox` and let `Stretch="Uniform"` plus explicit
  `Width`/`Height` do the sizing — don't rescale the path by hand.
- **`Stretch` normalizes to the path's content bounds, not its `viewBox`**, so two icons given
  the same `Width` are only the same visual size if they fill their boxes equally. Check a new
  glyph's bounds (`[System.Windows.Media.Geometry]::Parse(data).Bounds`) before assuming it can
  share a number, and size it so the *notional* 24-unit box lands around 21px. The top bar's four:

  | Icon | Content bounds | `Width` |
  |------|----------------|---------|
  | `IconHistory` | 21 of 24 | 18 |
  | `IconQueue` | 18 of 24 | 16 |
  | `IconGear` | 209 of 256 | 17 |
  | `IconExternalLink` | 16 of 24 | 15 |

- **Expand packed SVG path data rather than pasting it verbatim.** SVG permits compacted arc flag
  groups (`a2 2 0 0 1-2 2`) and chained relative moves (`m-1 5`); WPF's parser is not reliable on
  the first, and the second hides where a subpath actually starts. Write flags with separators and
  resolve a trailing relative `m` to an absolute `M` — then confirm with
  `Geometry.Parse(data).Bounds`, which catches a mis-traced start point immediately.
- Prefix the data with **`F1`** to force the nonzero fill rule. WPF's mini-language defaults to
  even-odd, SVG defaults to nonzero, and the difference shows up as filled-in ring cut-outs.
- A `Path` colours from `Fill`, not `Foreground`, so inheritance does not reach it. Bind it:
  `Fill="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"` — that keeps
  the template's hover trigger working, since the trigger sets the button's `Foreground`.

## Settings navigation — a left rail, not tabs

The settings dialog navigates with a **vertical rail** down its left edge, not a tab strip. A row
of tabs held three or four before it wrapped, and a wrapped tab row loses the one thing tabs are
for: showing the whole set at once. There are now ten pages in three groups (General · Providers ·
Advanced), and a rail carries the grouping and grows down instead of across.

- The rail is a **grouped `ListBox`** (`NavList`) on `#0C0E15` with a `#1AFFFFFF` right hairline,
  206px wide. Group headers are 11px `SemiBold` `#565C6D`, matching the Plex category popup.
- Rows use `NavItemContainer` — the same house list-item look as everywhere else: rounded card,
  `#217D97FF` tint and a 3px `#7D97FF` left bar when selected, `#12FFFFFF` on hover.
- The right pane is a stack of `StackPanel` pages inside one `ScrollViewer`;
  `Nav_SelectionChanged` shows exactly one. Page titles are 17px `SemiBold` `#EEF1FB`.
- The dialog is its own `Window`, so it cannot see `MainWindow`'s resources —
  **`ThinDarkScrollBar` is duplicated into its `Window.Resources`** and applied to both the rail
  and the page `ScrollViewer`. Do not ship a settings scroll region without it.

Footer buttons (Cancel/Save) live **outside** the pages — one Save commits every page. When
validation fails for a field on another page, call `SelectPage` to go there before showing the
message, or the button just appears dead.

**Two commit semantics, on purpose.** Settings are gathered on Save and discarded by Cancel;
sign-in and sign-out are remote actions that have already happened by the time the button returns,
so they are reported out through `PlaylistAuthChanged` / `NullcastTvAuthChanged` and applied
whichever way the dialog closed. A "Test connection" button therefore tests what is in the box
**without persisting it**.

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
- Full-screen browse (`EnterBrowseFullscreen`/`ExitBrowseFullscreen`) expands the side panel across
  the video column (via the named `VideoColumn`/`PanelColumn`/`SidePanelColumn`), pauses playback,
  and resumes it on exit if it had been playing. It's a transient state — not persisted — and is
  auto-dropped when you switch away from a browse tab. **Shared with Nullcast.TV**: the takeover is
  a layout change and knows nothing about either catalog.

Posters come from Plex's photo transcoder (`PlexService.ResolveThumbUrl`) so artwork is
downloaded pre-sized rather than at full resolution.

### The Nullcast.TV results panel — 16:9, not 2:3

Nullcast.TV is browsed exactly like Plex (segment bar → channel picker → filter → breadcrumb →
results) and reuses `PlexSegment`, `CategoryToggle`, `IconButton`, `ListItemContainer` and
`PlexGenreChip` unchanged. **What it does not reuse is the artwork templates**, because its stills
are 16:9 and a 16:9 still cropped into a 2:3 poster frame is letterboxed down to a strip.

- `TvListItemTemplate` — 64×36 leading still; otherwise identical in rhythm to the Plex row.
- `TvTileItemTemplate` — 134px tile with a **134 × 75** still (16:9 to the pixel). Runtime badge
  bottom-right, episode badge bottom-left, drill chevron top-right.

  **Tile width is a fixed budget, not a free choice.** The sidebar's content column offers about
  **292 DIP** once the vertical tab strip, the list's 8px padding and the 9px scrollbar are taken
  out, so a tile plus its margin must be ≤ 146 for two columns — which is why both the Plex poster
  tile and this one are 134 wide. A tile even a few DIP wider does not get narrower columns, it
  silently drops to **one**, which reads as a layout bug rather than a sizing one.
- `TvEpisodeItemTemplate` — the lean, image-free row for reading a series in order, for the same
  reason the Plex episode row is image-free.
- `ApplyTvViewMode()` swaps template + panel and lights one of the three toolbar buttons via the
  shared `StyleViewToggleButton`; list/tile persists in `AppSettings.NullcastTvTileView`, kept
  separate from `PlexTileView` because the two catalogs are worth browsing differently.
- The catalog pages on an opaque cursor, so a **"Load more"** button sits under the list and is
  shown only when the last response actually reported more. Appending keeps the scroll position.

### Sidebar tabs (vertical strip)

The side-panel tabs run as a **vertical strip down the left edge** of the panel, not a
horizontal row — this scales to many tabs without clipping. Each tab is a `VerticalTabButton`:
its label is rotated 90° (`RotateTransform Angle="-90"`, reads bottom-to-top) and the active
tab shows a **3px left accent bar** (`#7D97FF`) plus bright text. Active state is still driven
in code by `StyleTab` (it sets `Foreground` + `BorderBrush`; on the vertical button `BorderBrush`
paints the left bar instead of the old underline). The strip lives in an auto-width column with
the content in the `*` column beside it. There is **no divider between the strip and the content**
— the panel's own left border (video ↔ panel) is the only separator. The strip is wrapped in a
`ScrollViewer` with the scrollbar **hidden** (`VerticalScrollBarVisibility="Hidden"`), so a very
short window can still wheel-scroll to a tab but no stray scrollbar ever shows between tabs and
content. Order: Nullcast.TV · Playlist · Plex · Podcasts · YT Music · History — Nullcast's own
catalog leads, everything between is somebody else's service, and History sits **last** because it
is an aggregator of whatever did play rather than a source.

Every tab except History is **hidden outright when its provider is switched off** in
Settings ▸ Providers (`ApplyProviderTabs`). History is always present — which is also what
guarantees the strip can never be empty.

## The top-bar glyph row

The right end of the top bar holds a row of flat glyph buttons, right-aligned and ordered
left-to-right: **History · Queue · Open on Nullcast.TV · Settings**. All four share the
`TopBarIconButton` style — transparent until hovered, when the button's `Foreground` brightens to
`#E7E9F1` and each glyph's `Fill` binding carries that through.

- The History and Queue buttons are **shortcuts, not a second home** for those views: they select
  the History tab and its sub-tab, then call `RevealSidePanel()` so the panel is actually on
  screen. Skipping that last step makes them look broken in the two states where the sidebar is
  hidden — collapsed, and maximized (where the panel is normally summoned by the right screen
  edge).
- The Nullcast.TV link is the only one that hides itself: it appears only while a Nullcast.TV film
  is playing, because there is nowhere for it to go otherwise.
- Each button sets its own `Width`/`Height` on its `Path`. **They are deliberately not equal** —
  see the `Stretch` note under Icons.

### Menu items — checkmarks & separators

Checkable `MenuItem`s (File ▸ *Use Edge cookies*, *Auto-play next episode*) render a `✓` glyph
(`CheckGlyph`, accent `#7D97FF`) in the item's left icon slot, shown via an `IsChecked=True`
trigger in the custom `MenuItem` template. Without it the toggle state is invisible — the custom
template owns the whole visual tree, so the system check adorner never appears.

Separators **inside menus** must be styled via `x:Key="{x:Static MenuItem.SeparatorStyleKey}"`,
not the implicit `{x:Type Separator}` style (WPF menus resolve the keyed one). Ours is a dim
`#12FFFFFF` 1px line inset `Margin="12,5"` so items aren't smashed against a bright default bar.

### Context-menu submenus

`VideoMenuItem` carries **two** templates. The default one is the leaf row (icon + header). A
`Role="SubmenuHeader"` `Style.Trigger` swaps in a second template that adds a right-aligned
chevron and, critically, the `PART_Popup` flyout — WPF picks the template by `Role`, so a header
left on the leaf template renders fine but **never opens**. The flyout reuses the context-menu
card look (`#E61C1C22`, 12px radius, hairline border, drop shadow) and caps at `MaxHeight="420"`
with the house `ThinDarkScrollBar`.

Two gotchas when building submenus in code (see `MainWindow.Playlists.cs`):

- A childless `MenuItem` has `Role="SubmenuItem"`. Seed a placeholder child at construction so
  the header starts out as a `SubmenuHeader`.
- When repopulating, **append the new items before removing the old ones**. Emptying `Items`
  clears `HasItems`, which flips `Role` back and re-applies the popup-less leaf template while
  the menu is on screen.

Submenu children are *not* covered by the `ContextMenu`'s `ItemContainerStyle` — set
`ItemContainerStyle` on the header `MenuItem` itself so its rows get the same look.

### Toast (transient confirmation)

`ToastPopup` is the house confirmation strip: a top-level `Popup` placed centre-on-`RootGrid`
(same trick as the search palette, so it paints over the Flyleaf D3D surface and behaves
identically in windowed and fullscreen) holding a bottom-centre card at `Margin="0,0,0,104"` —
clear of the controls bar. Card styling matches the up-next panel: `#F00F1118`, 10px radius,
`#1AFFFFFF` hairline, soft shadow, 13px `#E7E9F1` text.

Raise one with `ShowToast(message, tone)`. It is **not interactive** (`IsHitTestVisible=False`)
and never asks a question — a status dot carries the tone, staying inside the palette:

| Tone | Dot | Meaning |
|------|-----|---------|
| `Success` | `#7D97FF` | something happened |
| `Neutral` | `#848B9F` | no-op (e.g. "Already in that playlist") |
| `Error`   | `#ff6666` | it failed (held ~4.2s instead of ~2.6s) |

Calls are re-entrant: a second toast replaces the message in place and restarts the hold rather
than queueing. Use it for background results the user can't otherwise see — an item sent to a
playlist from another tab — not for anything that needs acknowledgement.
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
