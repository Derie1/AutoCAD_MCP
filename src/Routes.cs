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
            Route.Main("get_entity_properties", QueryHandlers.GetEntityProperties),
            Route.Main("get_selection", QueryHandlers.GetSelection),
            Route.Main("get_system_variable", QueryHandlers.GetSystemVariable),
            Route.Main("list_documents", CatalogHandlers.ListDocuments),
            Route.Main("list_layers", CatalogHandlers.ListLayers),
            Route.Main("list_blocks", CatalogHandlers.ListBlocks),
            Route.Main("list_layouts", CatalogHandlers.ListLayouts),
            Route.Main("list_xrefs", CatalogHandlers.ListXrefs),
            Route.Main("list_styles", CatalogHandlers.ListStyles),

            // tables and attributes
            Route.Main("get_table", TableHandlers.GetTable),
            Route.Main("extract_block_attributes", TableHandlers.ExtractBlockAttributes),

            // draw
            Route.Main("create_line", DrawHandlers.CreateLine),

            // edit
            Route.Async("undo", EditHandlers.Undo),
            Route.Main("set_entity_properties", EditHandlers.SetEntityProperties),
            Route.Main("transform_entities", EditHandlers.TransformEntities),
            Route.Main("delete_entities", EditHandlers.DeleteEntities),
            Route.Main("set_selection", EditHandlers.SetSelection),
            Route.Main("set_system_variable", EditHandlers.SetSystemVariable),

            // layers
            Route.Main("create_layer", LayerHandlers.CreateLayer),
            Route.Main("set_layer_state", LayerHandlers.SetLayerState),
        };
        return list.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }
}
