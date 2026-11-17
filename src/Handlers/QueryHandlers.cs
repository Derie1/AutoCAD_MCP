using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcpBridge.Handlers;

internal static class QueryHandlers
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 2000;

    /// <summary>Entities matching EntityFilter, paged: {total, offset, count, items[]}.</summary>
    public static JsonObject FindEntities(JsonObject a)
    {
        var filter = new EntityFilter(a);
        int offset = Args.Int(a, "offset", 0, 0);
        int limit = Args.Int(a, "limit", DefaultLimit, 1, MaxLimit);

        var db = JsonUtil.ActiveDocument().Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var ids = filter.Collect(db, tr);
        var items = new JsonArray();
        foreach (var id in ids.Skip(offset).Take(limit))
            items.Add(Entities.Summary((Entity)tr.GetObject(id, OpenMode.ForRead), tr));
        tr.Commit();

        return new JsonObject
        {
            ["total"] = ids.Count,
            ["offset"] = offset,
            ["count"] = items.Count,
            ["has_more"] = offset + items.Count < ids.Count,
            ["items"] = items,
        };
    }
}
