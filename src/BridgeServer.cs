using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcadMcpBridge;

/// <summary>
/// Small JSON-over-HTTP server on http://localhost:PORT/.
///   GET  /health        -> health_check (does not touch AutoCAD)
///   POST /&lt;tool_name&gt;  -> JSON object of arguments in, JSON object out
/// Every response is {"ok": true, ...} or {"ok": false, "error": "...", "code": "..."}.
/// </summary>
internal sealed class BridgeServer
{
    private readonly BridgeConfig _config;
    private readonly IReadOnlyDictionary<string, Route> _routes;
    private HttpListener? _listener;
    private long _requests;

    public BridgeServer(BridgeConfig config, IReadOnlyDictionary<string, Route> routes)
    {
        _config = config;
        _routes = routes;
        Prefix = $"http://localhost:{config.Port}/";
    }

    public string Prefix { get; }
    public int Port => _config.Port;
    public int RouteCount => _routes.Count;
    public long RequestCount => Interlocked.Read(ref _requests);
    public bool IsListening => _listener?.IsListening == true;

    public void Start()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        try { listener.Start(); }
        catch (HttpListenerException ex)
        {
            throw new BridgeException("port_in_use",
                $"cannot listen on {Prefix}: {ex.Message}. Another program may use port {_config.Port}; " +
                $"change \"port\" in {BridgeConfig.ConfigPath}");
        }
        _listener = listener;
        _ = Task.Run(() => AcceptLoop(listener));
        Log.Info($"listening on {Prefix}");
    }

    public void Stop()
    {
        var l = _listener;
        _listener = null;
        if (l == null) return;
        try { l.Stop(); l.Close(); } catch { }
        Log.Info("stopped");
    }

    private async Task AcceptLoop(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; } // listener stopped
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        Interlocked.Increment(ref _requests);
        var req = ctx.Request;
        string name = req.Url?.AbsolutePath.Trim('/') ?? "";
        JsonObject result;
        int status = 200;
        try
        {
            if (!string.IsNullOrEmpty(_config.Token) && req.Headers["X-Bridge-Token"] != _config.Token)
                throw new BridgeException("unauthorized", "missing or wrong X-Bridge-Token", 401);

            if (name == "health") name = "health_check";
            if (!_routes.TryGetValue(name, out var route))
                throw new BridgeException("unknown_tool", $"no route '{name}'", 404);

            JsonObject args = await ReadArgs(req).ConfigureAwait(false);
            var timeout = TimeSpan.FromSeconds(_config.RequestTimeoutSeconds);
            result = await route.Invoke(args, timeout).ConfigureAwait(false);
            result["ok"] = true;
        }
        catch (Exception ex)
        {
            var (code, message, httpStatus) = ex switch
            {
                BridgeException b => (b.Code, b.Message, b.HttpStatus),
                Autodesk.AutoCAD.Runtime.Exception a => ("autocad_error", $"{a.ErrorStatus}: {a.Message}", 500),
                JsonException j => ("bad_request", "invalid JSON: " + j.Message, 400),
                _ => ("internal_error", ex.GetType().Name + ": " + ex.Message, 500),
            };
            if (httpStatus >= 500) Log.Error($"{name} failed", ex);
            status = httpStatus;
            result = new JsonObject { ["ok"] = false, ["code"] = code, ["error"] = message };
        }

        try
        {
            byte[] body = Encoding.UTF8.GetBytes(result.ToJsonString(Json.Options));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch (Exception ex) { Log.Error("cannot write response", ex); }
    }

    private static async Task<JsonObject> ReadArgs(HttpListenerRequest req)
    {
        if (!req.HasEntityBody) return new JsonObject();
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        string text = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject
            ?? throw new BridgeException("bad_request", "request body must be a JSON object", 400);
    }
}

/// <summary>
/// A tool handler, in one of three forms:
///   Http(...)  runs on the HTTP thread, must not touch the AutoCAD API;
///   Main(...)  runs on AutoCAD's main thread (all reads and writes);
///   Async(...) starts on the main thread and returns a task that AutoCAD completes later
///              (commands run through ExecuteInCommandContextAsync, e.g. UNDO).
///              The task is awaited off the main thread, so AutoCAD is never blocked.
/// </summary>
internal sealed class Route
{
    private readonly Func<JsonObject, JsonObject>? _sync;
    private readonly Func<JsonObject, Task<JsonObject>>? _async;
    private readonly bool _onMainThread;

    private Route(string name, bool onMainThread, Func<JsonObject, JsonObject>? sync, Func<JsonObject, Task<JsonObject>>? async)
    {
        Name = name; _onMainThread = onMainThread; _sync = sync; _async = async;
    }

    public string Name { get; }

    public static Route Http(string name, Func<JsonObject, JsonObject> run) => new(name, false, run, null);
    public static Route Main(string name, Func<JsonObject, JsonObject> run) => new(name, true, run, null);
    public static Route Async(string name, Func<JsonObject, Task<JsonObject>> run) => new(name, true, null, run);

    public async Task<JsonObject> Invoke(JsonObject args, TimeSpan timeout)
    {
        if (!_onMainThread) return _sync!(args);
        if (_sync != null) return await MainThread.InvokeAsync(() => _sync(args), timeout).ConfigureAwait(false);

        Task<JsonObject> started = await MainThread.InvokeAsync(() => _async!(args), timeout).ConfigureAwait(false);
        var finished = await Task.WhenAny(started, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != started)
            throw new BridgeException("timeout", $"AutoCAD did not finish '{Name}' within {timeout.TotalSeconds:0} s");
        return await started.ConfigureAwait(false);
    }
}

internal sealed class BridgeException(string code, string message, int httpStatus = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int HttpStatus { get; } = httpStatus;
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
