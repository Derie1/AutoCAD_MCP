using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge.Handlers;

internal static class DocumentHandlers
{
    /// <summary>Summary of the active drawing. Read-only: no document lock needed.</summary>
    public static JsonObject GetDocumentInfo(JsonObject _)
    {
        var doc = JsonUtil.ActiveDocument();
        var db = doc.Database;

        int modelEntities = 0, layers = 0, blocks = 0, layouts = 0;
        string currentLayer;
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsLayout)
                {
                    if (btr.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                        foreach (ObjectId _e in btr) modelEntities++;
                    else
                        layouts++;
                }
                else if (!btr.IsAnonymous && !btr.IsFromExternalReference && !btr.IsDependent)
                {
                    blocks++;
                }
            }

            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId _l in lt) layers++;

            currentLayer = ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name;
            tr.Commit();
        }

        var extMin = db.Extmin;
        var extMax = db.Extmax;
        bool extentsValid = extMin.X <= extMax.X && extMin.Y <= extMax.Y;

        return new JsonObject
        {
            ["name"] = Path.GetFileName(doc.Name),
            ["path"] = doc.IsNamedDrawing ? doc.Name : null,
            ["is_saved_to_file"] = doc.IsNamedDrawing,
            ["has_unsaved_changes"] = Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0,
            ["read_only"] = doc.IsReadOnly,
            ["dwg_version"] = db.OriginalFileVersion.ToString(),
            ["insunits"] = db.Insunits.ToString(),
            ["measurement"] = db.Measurement == MeasurementValue.Metric ? "metric" : "imperial",
            ["extents"] = extentsValid
                ? new JsonObject { ["min"] = JsonUtil.Point(extMin), ["max"] = JsonUtil.Point(extMax) }
                : null,
            ["current_layer"] = currentLayer,
            ["current_space"] = db.TileMode ? "model" : "paper",
            ["current_layout"] = LayoutManager.Current.CurrentLayout,
            ["counts"] = new JsonObject
            {
                ["model_space_entities"] = modelEntities,
                ["layers"] = layers,
                ["block_definitions"] = blocks,
                ["paper_layouts"] = layouts,
            },
            ["command_in_progress"] = string.IsNullOrEmpty(doc.CommandInProgress) ? null : doc.CommandInProgress,
            ["open_documents"] = AcApp.DocumentManager.Count,
        };
    }
}
