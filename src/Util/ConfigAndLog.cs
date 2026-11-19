using System.Text.Json.Nodes;

namespace AcadMcpBridge;

/// <summary>
/// Settings from %LOCALAPPDATA%\AcadMcpBridge\config.json (optional):
///   { "port": 8766, "token": "", "requestTimeoutSeconds": 30, "allowCommands": false }
/// The MCP server must use the same port (ACAD_MCP_PORT) and token (ACAD_MCP_TOKEN).
/// allowCommands: true allows run_command / eval_lisp from the start, without the ribbon toggle.
/// </summary>
internal sealed class BridgeConfig
{
    public const int DefaultPort = 8766; // 8765 belongs to the Navisworks MCP bridge

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AcadMcpBridge");
    public static string ConfigPath => Path.Combine(Folder, "config.json");

    public int Port { get; private init; } = DefaultPort;
    public string Token { get; private init; } = "";
    public int RequestTimeoutSeconds { get; private init; } = 30;
    public bool AllowCommands { get; private init; }

    public static BridgeConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new BridgeConfig();
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject ?? new JsonObject();
            return new BridgeConfig
            {
                Port = o["port"]?.GetValue<int>() ?? DefaultPort,
                Token = o["token"]?.GetValue<string>() ?? "",
                RequestTimeoutSeconds = Math.Clamp(o["requestTimeoutSeconds"]?.GetValue<int>() ?? 30, 1, 3600),
                AllowCommands = o["allowCommands"]?.GetValue<bool>() ?? false,
            };
        }
        catch (Exception ex)
        {
            throw new BridgeException("bad_config", $"cannot read {ConfigPath}: {ex.Message}");
        }
    }
}

/// <summary>Plain text log in %LOCALAPPDATA%\AcadMcpBridge\log.txt, rolled at 1 MB.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string LogPath => Path.Combine(BridgeConfig.Folder, "log.txt");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(BridgeConfig.Folder);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break AutoCAD */ }
    }
}
