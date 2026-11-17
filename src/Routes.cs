using AcadMcpBridge.Handlers;

namespace AcadMcpBridge;

/// <summary>All HTTP routes. A new tool = a handler here + its description in acad_mcp_server.py.</summary>
internal static class Routes
{
    public static IReadOnlyDictionary<string, Route> All()
    {
        var list = new List<Route>
        {
            new("health_check", OnMainThread: false, SystemHandlers.Health),
            new("get_document_info", OnMainThread: true, DocumentHandlers.GetDocumentInfo),
        };
        return list.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }
}
