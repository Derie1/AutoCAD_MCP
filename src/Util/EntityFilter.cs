using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcpBridge;

/// <summary>
/// Entity filter shared by find_entities and the bulk-edit tools. Arguments:
///   types   DXF names, e.g. "LINE", ["LWPOLYLINE", "INSERT"]; aliases: TABLE, BLOCK, POLYLINE, TEXT_ANY
///   layers  layer names with * and ? wildcards
///   blocks  block names (INSERT only, dynamic blocks by their definition name), wildcards allowed
///   window  {"min": [x, y], "max": [x, y], "mode": "inside" | "crossing"}  (XY only)
///   space   "all" (default), "model", "paper" (all layouts) or a layout name
/// </summary>
internal sealed class EntityFilter
{
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TABLE"] = ["ACAD_TABLE"],
        ["BLOCK"] = ["INSERT"],
        ["POLYLINE"] = ["LWPOLYLINE", "POLYLINE"],
        ["TEXT_ANY"] = ["TEXT", "MTEXT"],
    };

    private readonly HashSet<string>? _types;
    private readonly Regex? _layers;
    private readonly Regex? _blocks;
    private readonly Point2d _wMin, _wMax;
    private readonly bool _hasWindow, _crossing;
    private readonly string _space;

    public EntityFilter(JsonObject a)
    {
        var types = Args.StrList(a, "types");
        if (types != null)
            _types = new HashSet<string>(types.SelectMany(t => Aliases.TryGetValue(t, out var x) ? x : [t.ToUpperInvariant()]),
                                         StringComparer.OrdinalIgnoreCase);
        _layers = Wildcards(Args.StrList(a, "layers"));
        _blocks = Wildcards(Args.StrList(a, "blocks"));
        if (_blocks != null) _types ??= new HashSet<string>(["INSERT"], StringComparer.OrdinalIgnoreCase);

        var w = Args.Obj(a, "window");
        if (w != null)
        {
            var p1 = Args.Point(w, "min", required: true)!.Value;
            var p2 = Args.Point(w, "max", required: true)!.Value;
            _wMin = new Point2d(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y));
            _wMax = new Point2d(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y));
            _hasWindow = true;
            string mode = Args.Str(w, "mode") ?? "inside";
            _crossing = mode switch
            {
                "inside" => false,
                "crossing" => true,
                _ => throw new BridgeException("bad_request", "'window.mode' must be \"inside\" or \"crossing\""),
            };
        }
        _space = Args.Str(a, "space") ?? "all";
    }

    /// <summary>Block table records (model space / layouts) to scan, in layout tab order.</summary>
    public IEnumerable<BlockTableRecord> Spaces(Database db, Transaction tr)
    {
        var layouts = new List<Layout>();
        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dict)
            layouts.Add((Layout)tr.GetObject(entry.Value, OpenMode.ForRead));
        layouts.Sort((x, y) => x.TabOrder.CompareTo(y.TabOrder));

        bool any = false;
        foreach (var layout in layouts)
        {
            bool isModel = layout.ModelType;
            bool take = _space.ToLowerInvariant() switch
            {
                "all" => true,
                "model" => isModel,
                "paper" => !isModel,
                _ => layout.LayoutName.Equals(_space, StringComparison.OrdinalIgnoreCase),
            };
            if (!take) continue;
            any = true;
            yield return (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        }
        if (!any && _space is not ("all" or "model" or "paper"))
            throw new BridgeException("not_found", $"no layout named '{_space}'", 404);
    }

    /// <summary>Cheap test on the class name, before opening the object.</summary>
    public bool TypeMatches(ObjectId id) => _types == null || _types.Contains(Entities.DxfName(id));

    public bool Matches(Entity e, Transaction tr)
    {
        if (_layers != null && !_layers.IsMatch(e.Layer)) return false;
        if (_blocks != null && (e is not BlockReference br || !_blocks.IsMatch(Entities.EffectiveBlockName(br, tr)))) return false;
        if (_hasWindow)
        {
            Extents3d ext;
            try { ext = e.GeometricExtents; } catch { return false; }
            bool inside = ext.MinPoint.X >= _wMin.X && ext.MinPoint.Y >= _wMin.Y && ext.MaxPoint.X <= _wMax.X && ext.MaxPoint.Y <= _wMax.Y;
            bool overlaps = ext.MinPoint.X <= _wMax.X && ext.MaxPoint.X >= _wMin.X && ext.MinPoint.Y <= _wMax.Y && ext.MaxPoint.Y >= _wMin.Y;
            if (_crossing ? !overlaps : !inside) return false;
        }
        return true;
    }

    /// <summary>All matching entity ids, in drawing order of each space.</summary>
    public List<ObjectId> Collect(Database db, Transaction tr)
    {
        var result = new List<ObjectId>();
        foreach (var btr in Spaces(db, tr))
            foreach (ObjectId id in btr)
            {
                if (id.IsErased || !TypeMatches(id)) continue;
                if (tr.GetObject(id, OpenMode.ForRead) is Entity e && Matches(e, tr)) result.Add(id);
            }
        return result;
    }

    private static Regex? Wildcards(List<string>? patterns)
    {
        if (patterns == null || patterns.Count == 0) return null;
        var parts = patterns.Select(p => "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
        return new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
