using AcadMcpBridge.Handlers;

namespace AcadMcpBridge;

/// <summary>All HTTP routes. A new tool = a handler here + its schema in server/AcadMcpServer/tools.json.</summary>
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

            // view
            Route.Main("zoom", ViewHandlers.Zoom),
            Route.Main("set_current_layout", ViewHandlers.SetCurrentLayout),
            Route.Main("capture_view", CaptureHandlers.CaptureView),

            // tables and attributes
            Route.Main("get_table", TableHandlers.GetTable),
            Route.Main("extract_block_attributes", TableHandlers.ExtractBlockAttributes),
            Route.Main("set_table_cells", TableWriteHandlers.SetTableCells),
            Route.Main("modify_table", TableWriteHandlers.ModifyTable),
            Route.Main("create_table", TableWriteHandlers.CreateTable),

            // draw
            Route.Main("create_line", DrawHandlers.CreateLine),
            Route.Main("create_polyline", DrawHandlers.CreatePolyline),
            Route.Main("create_circle", DrawHandlers.CreateCircle),
            Route.Main("create_arc", DrawHandlers.CreateArc),
            Route.Main("create_text", DrawHandlers.CreateText),
            Route.Main("create_mtext", DrawHandlers.CreateMText),
            Route.Main("create_dimension", DrawHandlers.CreateDimension),
            Route.Main("insert_block", BlockHandlers.InsertBlock),
            Route.Main("update_block_attributes", BlockHandlers.UpdateBlockAttributes),

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
