using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace AcadMcpServer;

/// <summary>
/// Minimal MCP server over stdio: newline-delimited JSON-RPC 2.0 (initialize, ping, tools/list,
/// tools/call). Tool definitions come from the embedded tools.json; each call is forwarded to the
/// bridge route of the same name. stdout carries protocol messages only; diagnostics go to stderr.
/// </summary>
internal sealed class McpServer(string version, Bridge bridge)
{
    // Newest first; a client asking for one of these gets it back, any other gets the first.
    private static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Stream _out = Stream.Null;
    private JsonArray? _tools;
    private BatchRunner? _batch;
    private string _instructions = "";

    public async Task<int> RunAsync(Stream input, Stream output)
    {
        _out = output;
        LoadTools();
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        var running = new List<Task>();
        while (await reader.ReadLineAsync() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch (Exception) { await SendAsync(Error(null, -32700, "parse error")); continue; }

            // Requests run concurrently, so a slow AutoCAD call does not block ping or other calls.
            if (msg is JsonArray batch)
                foreach (var m in batch) running.Add(HandleAsync(m as JsonObject));
            else
                running.Add(HandleAsync(msg as JsonObject));
            running.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(running);
        _shutdown.Cancel();
        return 0;
    }

    private void LoadTools()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("tools.json")
                      ?? throw new InvalidOperationException("tools.json is not embedded");
        var doc = JsonNode.Parse(s)!.AsObject();
        _tools = doc["tools"]!.AsArray();
        _batch = new BatchRunner(_tools);
        _instructions = doc["instructions"]?.ToString() ?? "";
    }

    private async Task HandleAsync(JsonObject? msg)
    {
        if (msg == null) { await SendAsync(Error(null, -32600, "invalid request")); return; }
        var id = msg["id"]?.DeepClone();
        string? method = msg["method"]?.ToString();
        if (method == null) return;            // a response from the client: we send no requests
        if (id == null) return;                // notification (initialized, cancelled, ...)

        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(msg["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = _tools!.DeepClone() },
                "tools/call" => await CallToolAsync(msg["params"] as JsonObject),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new RpcException(-32601, $"method not found: {method}"),
            };
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (RpcException ex) { await SendAsync(Error(id, ex.Code, ex.Message)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"acad_mcp_server: {method} failed: {ex}");
            await SendAsync(Error(id, -32603, ex.Message));
        }
    }

    private JsonObject Initialize(JsonObject? p)
    {
        string requested = p?["protocolVersion"]?.ToString() ?? ProtocolVersions[0];
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersions.Contains(requested) ? requested : ProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "autocad", ["title"] = "AutoCAD MCP Bridge", ["version"] = version },
            ["instructions"] = _instructions,
        };
    }

    private async Task<JsonNode> CallToolAsync(JsonObject? p)
    {
        string name = p?["name"]?.ToString() ?? throw new RpcException(-32602, "tools/call needs a tool name");
        if (!_tools!.Any(t => t?["name"]?.ToString() == name)) throw new RpcException(-32602, $"unknown tool: {name}");
        var args = new JsonObject();
        if (p["arguments"] is JsonObject given)
            foreach (var (key, value) in given)
                if (value != null) args[key == "from_point" ? "from" : key] = value.DeepClone(); // "from" is reserved in many languages

        try
        {
            JsonObject result;
            if (name == BatchRunner.RunTool) result = await _batch!.RunAsync(args, _shutdown.Token);
            else if (name == BatchRunner.StatusTool) result = _batch!.Status(args);
            else if (name == "health_check")
            {
                result = await bridge.HealthAsync(_shutdown.Token);
                result.Remove("ok");
                result["server_version"] = version;
            }
            else result = await bridge.CallAsync(name, args, _shutdown.Token);

            // A picture (capture_view) goes to the model as MCP image content, the rest as text.
            if (result["image"] is JsonObject image && image["data"] is JsonValue data)
            {
                result.Remove("image");
                return new JsonObject
                {
                    ["content"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["data"] = data.ToString(),
                            ["mimeType"] = image["mime_type"]?.ToString() ?? "image/png",
                        },
                        new JsonObject { ["type"] = "text", ["text"] = result.ToJsonString(Json.Options) }),
                    ["isError"] = false,
                };
            }
            return ToolResult(result.ToJsonString(Json.Options), isError: false);
        }
        catch (ToolException ex)
        {
            return ToolResult($"Error executing tool {name}: {ex.Message}", isError: true);
        }
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private async Task SendAsync(JsonObject message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Json.Options) + "\n");
        await _writeLock.WaitAsync();
        try
        {
            await _out.WriteAsync(bytes);
            await _out.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    private sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}

internal static class Json
{
    /// <summary>Readable output: Cyrillic and other non-ASCII text is not escaped as \uXXXX.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly System.Text.Json.JsonSerializerOptions Indented = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };
}
