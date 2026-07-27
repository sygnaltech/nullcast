# Nullcast — Remote Control API

A local HTTP control surface for Nullcast, the Windows video player (`app-flyleaf/`). Other
applications on the machine — voice agents, a control panel, automation scripts — use it
to **control playback**, **tell the player what to play**, and **read / subscribe to**
the current playback state.

- **Transport:** plain HTTP (JSON) on `localhost`, plus a Server-Sent-Events (SSE) stream
  for live state. No SDK required — `curl`, `fetch`, `requests`, anything.
- **Scope:** same-machine by default; same-LAN is an explicit, token-guarded opt-in.
  Internet / cross-NAT control is **not** part of this API (see [Out of scope](#out-of-scope)).
- **Feature:** HAVOC **F-654**. Implemented in `app-flyleaf/`
  (`Services/RemoteControlService.cs`, `MainWindow.Api.cs`, `Models/ApiConfig.cs`,
  `Models/ApiContracts.cs`).

---

## 1. Connection

| | |
| --- | --- |
| Base URL (default) | `http://127.0.0.1:47893/api/v1` |
| Content type | `application/json` (request + response bodies) |
| Event stream | `GET /api/v1/events` → `text/event-stream` |
| Auth (default) | none on loopback |
| Auth (LAN mode) | `Authorization: Bearer <token>` (required) |

All times in this API are **milliseconds**. All timestamps are **Unix epoch milliseconds**.

The API only exists while the player app is running. It is enabled by default; it can be
turned off or reconfigured in `%AppData%\VideoPlayer\api.json` (see [Configuration](#6-configuration)).

### Quick check

```sh
curl http://127.0.0.1:47893/api/v1/ping
# {"app":"nullcast","version":"0.2.20","protocol":"v1","state":"idle"}
```

---

## 2. State model

Every command and `GET /state` returns the **same canonical state document**, so a caller
learns the effect of a command in a single round-trip:

```jsonc
{
  "state": "playing",          // idle | loading | playing | paused | stopped | ended
  "item": {                    // null when state is "idle"
    "type": "youtube",         // youtube | plex | url | file | bookmark
    "title": "Some video",
    "sourceUrl": "https://www.youtube.com/watch?v=…",  // never a tokenized Plex URL
    "muid": null,              // set when the item is a playlist bookmark
    "ratingKey": null          // set when the item is a Plex item
  },
  "positionMs": 42000,
  "durationMs": 613000,
  "volume": 85,                // 0–100
  "muted": false,
  "speed": 1.0,                // playback rate
  "fullscreen": false,
  "workspaceId": 12,           // active playlist workspace, or null
  "app": { "name": "nullcast", "version": "0.2.20", "protocol": "v1" },
  "ts": 1721890000000          // epoch ms when this snapshot was taken
}
```

### `state` values

| Value | Meaning |
| --- | --- |
| `idle` | Nothing loaded — player is fresh or fully cleared. |
| `loading` | A source is opening / resolving (yt-dlp or Plex). |
| `playing` | Actively playing. |
| `paused` | Paused with media loaded. |
| `stopped` | Stopped but an item is still associated. |
| `ended` | Playback reached the end. |

### `item.type` values

| Type | Identifier field | Notes |
| --- | --- | --- |
| `youtube` | `sourceUrl` | Any yt-dlp-supported page URL (YouTube, Reddit, Vimeo, …). |
| `url` | `sourceUrl` | A direct media URL (mp4/m3u8/…). |
| `file` | `sourceUrl` | A local file path. |
| `plex` | `ratingKey` | `sourceUrl` is `plex://<ratingKey>` — the token is never exposed. |
| `bookmark` | `muid` | A bookmark from the Sygnal Playlist service. |

> Note: the running player does not always distinguish `youtube` vs `url` vs `file` for the
> *currently playing* item once resolved; reads may report `url` for a direct/started source.
> The distinction always matters on the **way in** (the `play` request `type`), which is authoritative.

---

## 3. Commands

All commands are `POST` and return the resulting [state document](#2-state-model) with `200`.

### Transport

| Endpoint | Body | Effect |
| --- | --- | --- |
| `POST /play` | *(none)* | Resume if paused. |
| `POST /play` | `{ "source": … }` | Start a new item (see [Play by reference](#4-play-by-reference)). |
| `POST /pause` | *(none)* | Pause. |
| `POST /playpause` | *(none)* | Toggle play/pause. |
| `POST /stop` | *(none)* | Stop and clear now-playing. |
| `POST /seek` | `{ "positionMs": n }` **or** `{ "deltaMs": ±n }` | Absolute or relative seek (clamped to duration). |
| `POST /volume` | `{ "level": 0–100 }` and/or `{ "delta": ±n }` and/or `{ "mute": bool }` | Set volume / adjust / mute. |

### Examples

```sh
# Pause, then resume
curl -X POST http://127.0.0.1:47893/api/v1/pause
curl -X POST http://127.0.0.1:47893/api/v1/play

# Toggle
curl -X POST http://127.0.0.1:47893/api/v1/playpause

# Jump to 10:00, then nudge back 15s
curl -X POST http://127.0.0.1:47893/api/v1/seek   -H 'Content-Type: application/json' -d '{"positionMs":600000}'
curl -X POST http://127.0.0.1:47893/api/v1/seek   -H 'Content-Type: application/json' -d '{"deltaMs":-15000}'

# Set volume to 50, bump +10, mute
curl -X POST http://127.0.0.1:47893/api/v1/volume -H 'Content-Type: application/json' -d '{"level":50}'
curl -X POST http://127.0.0.1:47893/api/v1/volume -H 'Content-Type: application/json' -d '{"delta":10}'
curl -X POST http://127.0.0.1:47893/api/v1/volume -H 'Content-Type: application/json' -d '{"mute":true}'
```

---

## 4. Play by reference

`POST /play` with a `source` object starts playback of a specific item. `source.type`
selects which fields apply. An optional top-level `startPositionMs` sets the resume point.

```jsonc
{ "source": { … }, "startPositionMs": 723000 }   // startPositionMs optional
```

| `type` | Required | Optional | Resolved via |
| --- | --- | --- | --- |
| `youtube` | `url` | `quality` (max height, e.g. `1080`) | yt-dlp |
| `url` | `url` | — | direct FFmpeg open (falls back to yt-dlp for page URLs) |
| `file` | `path` | — | direct FFmpeg open |
| `plex` | `ratingKey` | — | Plex Direct Play (server must be configured in-app) |
| `bookmark` | `muid` | — | Sygnal Playlist bookmark (uses saved resume position unless overridden) |

Resolution (yt-dlp / Plex lookup) runs **asynchronously**; a `play` call returns quickly and
the state transitions through `loading` → `playing`. Subscribe to `/events` (or poll `/state`)
to observe the transition. Plex items auto-resume from their server-side `viewOffset` unless
`startPositionMs` is given.

### Examples

```sh
# YouTube at up to 1080p
curl -X POST http://127.0.0.1:47893/api/v1/play -H 'Content-Type: application/json' -d '{
  "source": { "type": "youtube", "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "quality": 1080 }
}'

# Plex item, resuming at 12:03
curl -X POST http://127.0.0.1:47893/api/v1/play -H 'Content-Type: application/json' -d '{
  "source": { "type": "plex", "ratingKey": "12345" }, "startPositionMs": 723000
}'

# Direct media URL
curl -X POST http://127.0.0.1:47893/api/v1/play -H 'Content-Type: application/json' -d '{
  "source": { "type": "url", "url": "https://example.com/clip.mp4" }
}'

# Local file
curl -X POST http://127.0.0.1:47893/api/v1/play -H 'Content-Type: application/json' -d '{
  "source": { "type": "file", "path": "C:\\media\\clip.mkv" }
}'

# Existing playlist bookmark
curl -X POST http://127.0.0.1:47893/api/v1/play -H 'Content-Type: application/json' -d '{
  "source": { "type": "bookmark", "muid": "abc123" }
}'
```

---

## 5. Live state (Server-Sent Events)

`GET /api/v1/events` opens a long-lived `text/event-stream`. The server immediately sends the
current snapshot, then pushes a new one on every meaningful change — transport changes,
now-playing changes, and volume/mute changes are sent immediately; position ticks are
throttled to about **1 per second**.

Each frame:

```
event: state
data: { …the state document… }

```

### Consuming it

```sh
curl -N http://127.0.0.1:47893/api/v1/events
```

```js
// Browser / Node (with an EventSource polyfill)
const es = new EventSource("http://127.0.0.1:47893/api/v1/events");
es.addEventListener("state", (e) => {
  const s = JSON.parse(e.data);
  console.log(s.state, s.item?.title, `${s.positionMs}/${s.durationMs}`);
});
```

```python
# Python — httpx streaming
import httpx, json
with httpx.stream("GET", "http://127.0.0.1:47893/api/v1/events", timeout=None) as r:
    for line in r.iter_lines():
        if line.startswith("data: "):
            print(json.loads(line[6:]))
```

> Browsers: `EventSource` cannot send an `Authorization` header. If you enable token auth
> and consume `/events` from a browser, front it with a proxy that injects the header, or use
> a `fetch`-based SSE reader. Server-side agents have no such limitation.

---

## 6. Configuration

Stored at `%AppData%\VideoPlayer\api.json`. Created with defaults on first run.

```jsonc
{
  "enabled": true,                    // master on/off
  "port": 47893,
  "bind": "loopback",                 // "loopback" (127.0.0.1) | "lan" (all interfaces)
  "require_token_on_loopback": false, // require a token even for local callers
  "token_encrypted": "",              // DPAPI-encrypted; managed by the app, not hand-edited
  "allowed_origins": []               // CORS: e.g. ["http://localhost:3000"] or ["*"]
}
```

- Changes take effect on **app restart**.
- `token_encrypted` is encrypted at rest (Windows DPAPI, bound to the current user + this app).
  Do not paste a raw token here — it won't be accepted. LAN mode auto-generates one on first
  enable; the generated value is written to the app's debug log
  (`%TEMP%\videoplayer-debug.log`) so you can retrieve it.

### Enabling LAN access

1. Set `"bind": "lan"` and restart the app once (a token is generated and logged).
2. Reserve the URL ACL so Windows lets a non-elevated process bind all interfaces — run
   **elevated**:
   ```powershell
   netsh http add urlacl url=http://+:47893/ user=Everyone
   ```
   (and open the port in Windows Firewall if needed).
3. If the reservation is missing, the app **falls back to loopback** rather than failing open,
   and logs the exact `netsh` command. It never silently widens exposure.
4. Every request must then carry `Authorization: Bearer <token>` (except `GET /ping`).

---

## 7. Errors

Non-2xx responses use a consistent envelope:

```json
{ "error": { "code": "not_found", "message": "Plex item '12345' was not found or has no playable file." } }
```

| HTTP | `code` | When |
| --- | --- | --- |
| 400 | `bad_request` | Malformed body / missing required field / bad JSON. |
| 401 | `unauthorized` | Token required but missing/invalid. |
| 404 | `not_found` | Unknown route, or unresolvable `ratingKey` / `muid` / Plex not configured. |
| 409 | `conflict` | Reserved for concurrent-operation conflicts. |
| 502 | `resolve_failed` | A source failed to resolve (reserved). |
| 500 | `internal` | Unexpected server error. |

> Note: media-load failures surfaced by the player (e.g. yt-dlp can't extract a URL) are
> shown to the user in the app UI and may not always map to a `resolve_failed` HTTP error on
> the `play` call, because resolution completes after the call returns. Observe `/events` to
> confirm a `play` actually reached `playing`.

---

## 8. Endpoint reference

| Method | Path | Auth¹ | Body | Returns |
| --- | --- | --- | --- | --- |
| GET | `/api/v1/ping` | no | — | `{app, version, protocol, state}` |
| GET | `/api/v1/state` | yes | — | state document |
| GET | `/api/v1/events` | yes | — | SSE `state` stream |
| POST | `/api/v1/play` | yes | `PlayRequest` or empty | state document |
| POST | `/api/v1/pause` | yes | — | state document |
| POST | `/api/v1/playpause` | yes | — | state document |
| POST | `/api/v1/stop` | yes | — | state document |
| POST | `/api/v1/seek` | yes | `SeekRequest` | state document |
| POST | `/api/v1/volume` | yes | `VolumeRequest` | state document |

¹ "Auth: yes" means *only* when a token is required (LAN mode, or `require_token_on_loopback`).
On a default loopback setup, no request needs a token.

---

## 9. Design notes (for maintainers)

- **Single player, UI thread.** The app owns one FlyleafLib `Player` on the WPF UI thread.
  Every command marshals onto `MainWindow.Dispatcher` (`MainWindow.Api.cs`); state reads
  (`ApiSnapshot()`) touch no UI elements and run on any thread. Time values convert from
  Flyleaf's 100-ns ticks to ms at the boundary (`/10000`).
- **Reuses existing play paths.** `play` dispatches to the same `PlayUrl` / `PlayPlexItem` /
  `PlayBookmark` code the UI uses — no parallel playback logic.
- **SSE fan-out.** `MainWindow` raises `ApiStateChanged`; `RemoteControlService` writes each
  snapshot to every open event stream and prunes dead ones. Position ticks are throttled to ~1 Hz.
- **Safe by default.** Loopback bind, no token, no CORS headers unless configured. LAN is
  opt-in and always token-guarded; a failed LAN bind degrades to loopback.
- **Additive.** When `enabled` is false the listener never starts and playback behaves exactly
  as before.

## Out of scope

Deliberately **not** in this API (candidates for future work):

- **Internet / cross-NAT control.** A cloud-relayed path (a command queue in the Sygnal
  Playlist Cloudflare service, reusing the existing OAuth identity) is the intended future
  route for controlling the player from outside the LAN.
- **Queue / playlist management** (enqueue, reorder, next/previous across a workspace).
- **Media search** over the API (searching Plex or bookmarks). Callers pass identifiers they
  already have.
- **Per-caller identity** beyond a single shared bearer token.
