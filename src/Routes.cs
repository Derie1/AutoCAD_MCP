using AcadMcpBridge.Handlers;

namespace AcadMcpBridge;

/// <summary>All HTTP routes. A new tool = a handler here + its description in acad_mcp_server.py.</summary>
internal static class Routes
{
    public static IReadOnlyDictionary<string, Route> All()
    {
        var list = new List<Route>
        {
            Route.Http("health_check", SystemHandlers.Health),
            Route.Main("get_document_info", DocumentHandlers.GetDocumentInfo),

            // query
            Route.Main("find_entities", QueryHandlers.FindEntities),

            // draw
            Route.Main("create_line", DrawHandlers.CreateLine),

            // edit
            Route.Async("undo", EditHandlers.Undo),
        };
        return list.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }
}
