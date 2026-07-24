using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Local HTTP control surface for the player (F-654). Exposes REST commands and a
    /// Server-Sent-Events state stream under <c>/api/v1</c>, backed by the MainWindow
    /// facade. Modeled on the proven <see cref="PlaylistAuthService"/> HttpListener pattern,
    /// but long-lived.
    ///
    /// Safe by default: binds loopback only unless <see cref="ApiConfig.Bind"/> is "lan",
    /// in which case a bearer token is required (auto-generated if absent). A failed LAN
    /// bind (missing Windows urlacl / elevation) degrades to loopback rather than failing
    /// open — it never silently widens exposure.
    /// </summary>
    public class RemoteControlService
    {
        private const string ApiPrefix = "/api/v1";

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly MainWindow _win;
        private readonly ApiConfig _cfg;

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;

        private readonly List<HttpListenerResponse> _sseClients = new();
        private readonly object _sseLock = new();

        public RemoteControlService(MainWindow win, ApiConfig cfg)
        {
            _win = win;
            _cfg = cfg;
        }

        // ──────────────────────────────────────────────────────
        // Lifecycle
        // ──────────────────────────────────────────────────────

        public void Start()
        {
            // LAN exposure must be token-guarded; mint one on first use if the user hasn't set one.
            if (_cfg.IsLan && string.IsNullOrEmpty(_cfg.Token))
            {
                _cfg.SetToken(Guid.NewGuid().ToString("N"));
                _cfg.Save();
                App.Log("[API] LAN mode enabled with no token — generated one. See api.json / logs to retrieve it.");
            }

            _listener = new HttpListener();
            bool lan = _cfg.IsLan;

            if (lan)
            {
                // "+" = all interfaces. Requires a urlacl reservation or elevation on Windows.
                _listener.Prefixes.Add($"http://+:{_cfg.Port}/");
                try
                {
                    _listener.Start();
                }
                catch (HttpListenerException ex)
                {
                    // Do NOT fail open. Fall back to loopback and tell the user exactly how to
                    // grant the reservation if they really want LAN access.
                    App.Log($"[API] LAN bind failed ({ex.Message}). Falling back to loopback. " +
                            $"To allow LAN access, run (elevated): " +
                            $"netsh http add urlacl url=http://+:{_cfg.Port}/ user=Everyone");
                    _listener = new HttpListener();
                    lan = false;
                    _listener.Prefixes.Add($"http://127.0.0.1:{_cfg.Port}/");
                    _listener.Start();
                }
            }
            else
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{_cfg.Port}/");
                _listener.Start();
            }

            _cts = new CancellationTokenSource();
            _win.ApiStateChanged += OnStateChanged;
            _ = AcceptLoopAsync(_cts.Token);

            App.Log($"[API] Listening on http://{(lan ? "+" : "127.0.0.1")}:{_cfg.Port}{ApiPrefix} " +
                    $"(token {(_cfg.RequiresToken ? "required" : "not required")}).");
        }

        public void Stop()
        {
            try { _win.ApiStateChanged -= OnStateChanged; } catch { }
            try { _cts?.Cancel(); } catch { }

            lock (_sseLock)
            {
                foreach (var r in _sseClients)
                    try { r.Close(); } catch { }
                _sseClients.Clear();
            }

            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        // ──────────────────────────────────────────────────────
        // Accept loop
        // ──────────────────────────────────────────────────────

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener is { IsListening: true })
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    break; // listener stopped/disposed
                }

                _ = HandleAsync(ctx); // handle concurrently; commands serialize on the UI thread anyway
            }
        }

        // ──────────────────────────────────────────────────────
        // Request handling
        // ──────────────────────────────────────────────────────

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            try
            {
                ApplyCors(req, res);

                var path = (req.Url?.AbsolutePath ?? "").TrimEnd('/');
                var method = req.HttpMethod;

                // CORS preflight
                if (method == "OPTIONS")
                {
                    res.StatusCode = 204;
                    res.Close();
                    return;
                }

                if (!path.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    WriteError(res, 404, "not_found", "Unknown route. The API is served under /api/v1.");
                    return;
                }

                var route = path.Substring(ApiPrefix.Length);
                if (route.Length == 0) route = "/";

                // /ping is unauthenticated so discovery works before a token is presented.
                bool isPing = route.Equals("/ping", StringComparison.OrdinalIgnoreCase);
                if (!isPing && _cfg.RequiresToken && !IsAuthorized(req))
                {
                    WriteError(res, 401, "unauthorized", "Missing or invalid bearer token.");
                    return;
                }

                switch (route.ToLowerInvariant())
                {
                    case "/ping" when method == "GET":
                        WriteJson(res, 200, BuildPing());
                        return;

                    case "/state" when method == "GET":
                        WriteJson(res, 200, _win.ApiSnapshot());
                        return;

                    case "/events" when method == "GET":
                        StartSse(res);
                        return;

                    case "/play" when method == "POST":
                    {
                        var body = await ReadBodyAsync<PlayRequest>(req);
                        StateSnapshot snap = body?.Source != null
                            ? await _win.ApiPlaySourceAsync(body.Source, body.StartPositionMs)
                            : await _win.ApiResumeAsync();
                        WriteJson(res, 200, snap);
                        return;
                    }

                    case "/pause" when method == "POST":
                        WriteJson(res, 200, await _win.ApiPauseAsync());
                        return;

                    case "/playpause" when method == "POST":
                        WriteJson(res, 200, await _win.ApiTogglePlayPauseAsync());
                        return;

                    case "/stop" when method == "POST":
                        WriteJson(res, 200, await _win.ApiStopAsync());
                        return;

                    case "/seek" when method == "POST":
                    {
                        var body = await ReadBodyAsync<SeekRequest>(req);
                        if (body == null || (body.PositionMs == null && body.DeltaMs == null))
                            throw new ApiRequestException("bad_request", "Provide 'positionMs' or 'deltaMs'.");
                        WriteJson(res, 200, await _win.ApiSeekAsync(body.PositionMs, body.DeltaMs));
                        return;
                    }

                    case "/volume" when method == "POST":
                    {
                        var body = await ReadBodyAsync<VolumeRequest>(req);
                        if (body == null || (body.Level == null && body.Delta == null && body.Mute == null))
                            throw new ApiRequestException("bad_request", "Provide 'level', 'delta', or 'mute'.");
                        WriteJson(res, 200, await _win.ApiVolumeAsync(body.Level, body.Delta, body.Mute));
                        return;
                    }

                    default:
                        WriteError(res, 404, "not_found", $"No route for {method} {ApiPrefix}{route}.");
                        return;
                }
            }
            catch (ApiRequestException ex)
            {
                WriteError(res, StatusForCode(ex.Code), ex.Code, ex.Message);
            }
            catch (JsonException ex)
            {
                WriteError(res, 400, "bad_request", $"Invalid JSON: {ex.Message}");
            }
            catch (Exception ex)
            {
                App.Log($"[API] Handler error: {ex.Message}");
                try { WriteError(res, 500, "internal", ex.Message); } catch { }
            }
        }

        private object BuildPing()
        {
            var snap = _win.ApiSnapshot();
            return new { app = snap.App.Name, version = snap.App.Version, protocol = snap.App.Protocol, state = snap.State };
        }

        private bool IsAuthorized(HttpListenerRequest req)
        {
            var header = req.Headers["Authorization"];
            if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return false;
            var presented = header.Substring("Bearer ".Length).Trim();
            var expected = _cfg.Token;
            return !string.IsNullOrEmpty(expected) && presented == expected;
        }

        // ──────────────────────────────────────────────────────
        // Server-Sent Events
        // ──────────────────────────────────────────────────────

        private void StartSse(HttpListenerResponse res)
        {
            res.StatusCode = 200;
            res.ContentType = "text/event-stream";
            res.Headers["Cache-Control"] = "no-cache";
            res.SendChunked = true;
            res.KeepAlive = true;

            lock (_sseLock)
                _sseClients.Add(res);

            // Send the current state immediately so a new subscriber is in sync.
            WriteSse(res, _win.ApiSnapshot());
            // Intentionally leave the response open; Stop() / prune closes it.
        }

        private void OnStateChanged(StateSnapshot snap)
        {
            List<HttpListenerResponse> clients;
            lock (_sseLock)
            {
                if (_sseClients.Count == 0) return;
                clients = _sseClients.ToList();
            }

            var dead = new List<HttpListenerResponse>();
            foreach (var c in clients)
                if (!WriteSse(c, snap))
                    dead.Add(c);

            if (dead.Count > 0)
                lock (_sseLock)
                    foreach (var d in dead)
                    {
                        _sseClients.Remove(d);
                        try { d.Close(); } catch { }
                    }
        }

        /// <summary>Write one SSE frame. Returns false if the client is gone.</summary>
        private static bool WriteSse(HttpListenerResponse res, StateSnapshot snap)
        {
            try
            {
                var json = JsonSerializer.Serialize(snap, Json);
                var bytes = Encoding.UTF8.GetBytes($"event: state\ndata: {json}\n\n");
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.OutputStream.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ──────────────────────────────────────────────────────
        // Response helpers
        // ──────────────────────────────────────────────────────

        private void ApplyCors(HttpListenerRequest req, HttpListenerResponse res)
        {
            var origins = _cfg.AllowedOrigins;
            if (origins == null || origins.Count == 0) return;

            var origin = req.Headers["Origin"];
            if (string.IsNullOrEmpty(origin)) return;

            if (origins.Contains("*"))
                res.Headers["Access-Control-Allow-Origin"] = "*";
            else if (origins.Contains(origin))
                res.Headers["Access-Control-Allow-Origin"] = origin;
            else
                return;

            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
        }

        private static async Task<T?> ReadBodyAsync<T>(HttpListenerRequest req)
        {
            if (!req.HasEntityBody) return default;
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body)) return default;
            return JsonSerializer.Deserialize<T>(body, Json);
        }

        private static void WriteJson(HttpListenerResponse res, int status, object payload)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json));
            res.StatusCode = status;
            res.ContentType = "application/json";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        private static void WriteError(HttpListenerResponse res, int status, string code, string message)
            => WriteJson(res, status, new ApiError(code, message));

        private static int StatusForCode(string code) => code switch
        {
            "bad_request"    => 400,
            "unauthorized"   => 401,
            "not_found"      => 404,
            "conflict"       => 409,
            "resolve_failed" => 502,
            _                => 500,
        };
    }
}
