using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AcadMcpServer;

/// <summary>
/// HTTP client of the AcadMcpBridge plugin (http://localhost:PORT/). Environment:
///   ACAD_MCP_PORT (8766), ACAD_MCP_TOKEN (sent as X-Bridge-Token), ACAD_MCP_TIMEOUT (seconds, 60).
/// </summary>
internal sealed class Bridge
{
    private readonly HttpClient _http;
    private readonly int _port;
    private volatile bool _verified;

    public Bridge()
    {
        _port = int.TryParse(Environment.GetEnvironmentVariable("ACAD_MCP_PORT"), out int p) ? p : 8766;
        double timeout = double.TryParse(Environment.GetEnvironmentVariable("ACAD_MCP_TIMEOUT"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t) ? t : 60;
        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}/"), Timeout = TimeSpan.FromSeconds(timeout) };
        var token = Environment.GetEnvironmentVariable("ACAD_MCP_TOKEN");
        if (!string.IsNullOrEmpty(token)) _http.DefaultRequestHeaders.Add("X-Bridge-Token", token);
    }

    public string NotRunning =>
        $"The AutoCAD MCP bridge is not reachable at http://localhost:{_port}. Start AutoCAD 2025-2027 and press " +
        "Start on the AI companion panel (Home tab), or type MCPSTART in the AutoCAD command line.";

    /// <summary>GET /health, unchecked: answers even while AutoCAD is busy.</summary>
    public async Task<JsonObject> HealthAsync(CancellationToken ct) => await SendAsync(HttpMethod.Get, "health", null, ct);

    /// <summary>POST /route; throws ToolException with the bridge's error code and message.</summary>
    public async Task<JsonObject> CallAsync(string route, JsonObject args, CancellationToken ct)
    {
        if (!_verified)
        {
            var health = await HealthAsync(ct);
            if (health["product"]?.ToString() != "AutoCAD")
                throw new ToolException($"Port {_port} is served by {health["product"]?.ToString() ?? "an unknown program"}, " +
                    "not by the AutoCAD bridge. Set ACAD_MCP_PORT and the plugin's config.json port to a free port.");
            _verified = true;
        }
        var result = await SendAsync(HttpMethod.Post, route, args, ct);
        if (result["ok"]?.GetValue<bool>() != true)
            throw new ToolException($"[{result["code"]?.ToString() ?? "error"}] {result["error"]?.ToString() ?? "unknown error"}");
        result.Remove("ok");
        return result;
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, route);
        if (body != null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new ToolException($"{NotRunning} ({ex.Message})"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new ToolException($"AutoCAD did not answer within {_http.Timeout.TotalSeconds:0} s; it may be busy (a dialog or a long command)."); }

        string text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            throw new ToolException($"Port {_port} answered with something that is not the AutoCAD bridge.");
        }
    }
}

/// <summary>An error to show to the model as the tool result (isError: true).</summary>
internal sealed class ToolException(string message) : Exception(message);
