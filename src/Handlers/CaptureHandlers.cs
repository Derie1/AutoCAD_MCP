using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsSystem;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using GsView = Autodesk.AutoCAD.GraphicsSystem.View;

namespace AcadMcpBridge.Handlers;

/// <summary>
/// capture_view: a PNG of the drawing for the assistant to look at. Rendered by off-screen
/// graphics devices with their own views, so the user's view in AutoCAD does not move.
/// </summary>
internal static class CaptureHandlers
{
    private const int DefaultSize = 1500;
    private const int MaxSize = 4000;

    public static JsonObject CaptureView(JsonObject a)
    {
        var doc = ViewHandlers.Ready();
        var db = doc.Database;
        string mode = ViewHandlers.Mode(a, "current");
        double padding = ViewHandlers.Padding(a);
        int longSide = Args.Int(a, "size", DefaultSize, 100, MaxSize);
        string background = (Args.Str(a, "background") ?? "dark").ToLowerInvariant();
        if (background is not ("dark" or "light"))
            throw new BridgeException("bad_request", "'background' must be dark or light");
        string? savePath = Args.Str(a, "save_path");
        if (savePath != null)
        {
            if (!Path.IsPathFullyQualified(savePath) || !savePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("bad_request", "'save_path' must be a full path to a .png file");
            if (!Directory.Exists(Path.GetDirectoryName(savePath)))
                throw new BridgeException("not_found", $"folder {Path.GetDirectoryName(savePath)} does not exist", 404);
        }

        // What to show, in the current view's own plane (DCS): center and size of the window.
        var current = doc.Editor.GetCurrentView();
        Point2d center;
        double width, height;
        int count = 0;
        Extents3d? region = null;
        if (mode == "current")
        {
            center = current.CenterPoint;
            width = current.Width;
            height = current.Height;
        }
        else
        {
            region = ViewHandlers.Region(a, db, mode, out count)
                     ?? throw new BridgeException("bad_request", "'mode' must be current, extents, window or entities");
            (center, width, height) = ViewHandlers.FitBox(current, region.Value, padding, 0);
        }

        var (px, py) = width >= height
            ? (longSide, Math.Max(1, (int)Math.Round(longSide * height / width)))
            : (Math.Max(1, (int)Math.Round(longSide * width / height)), longSide);

        byte[] png = Render(doc, current, center, width, height, px, py, background == "dark");
        if (savePath != null) File.WriteAllBytes(savePath, png);

        var info = ViewHandlers.Describe(doc);
        var result = new JsonObject
        {
            ["layout"] = info["layout"]?.DeepClone(),
            ["space"] = info["space"]?.DeepClone(),
            ["mode"] = mode,
        };
        if (mode is "extents" or "entities") result["objects"] = count;
        if (region != null)
            result["captured"] = new JsonObject { ["min"] = JsonUtil.Point(region.Value.MinPoint), ["max"] = JsonUtil.Point(region.Value.MaxPoint) };
        result["plan_view"] = info["plan_view"]?.DeepClone();
        result["image_width"] = px;
        result["image_height"] = py;
        result["drawing_units_per_pixel"] = Math.Round(width / px, 9);
        result["saved_to"] = savePath;
        result["image"] = new JsonObject { ["mime_type"] = "image/png", ["data"] = Convert.ToBase64String(png) };
        return result;
    }

    private static byte[] Render(Autodesk.AutoCAD.ApplicationServices.Document doc, ViewTableRecord current,
                                 Point2d center, double width, double height, int px, int py, bool dark)
    {
        var db = doc.Database;
        var gsm = doc.GraphicsManager;
        int vport = Convert.ToInt32(AcApp.GetSystemVariable("CVPORT"));
        var background = dark ? Color.FromArgb(33, 40, 48) : Color.White;

        var kd = new KernelDescriptor();
        kd.addRequirement(Autodesk.AutoCAD.UniqueString.Intern("3D Drawing"));
        var kernel = Manager.AcquireGraphicsKernel(kd);
        try
        {
            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            // The current space with the user's view direction and twist, in our own window.
            using var image = RenderView(gsm, kernel, px, py, background, space, null, v =>
            {
                gsm.SetViewFromViewport(v, vport);
                SetWindow(v, current.Target, current.ViewDirection, current.ViewTwist, center, width, height);
            });

            // A paper layout: the model inside each viewport is rendered separately and laid over the
            // viewport's rectangle with the background transparent, so frames and overlaps stay visible.
            if (vport == 1 && !db.TileMode)
            {
                using var g = Graphics.FromImage(image);
                foreach (var part in ViewportParts(db, tr, center, width, height, px, py))
                {
                    using var piece = RenderView(gsm, kernel, part.Pixels.Width, part.Pixels.Height, background,
                                                 part.ModelSpace, part.FrozenLayers,
                                                 v => SetWindow(v, part.Target, part.Direction, part.Twist,
                                                                part.ModelCenter, part.ModelWidth, part.ModelHeight));
                    piece.MakeTransparent(background);
                    g.DrawImage(piece, part.Pixels);
                }
            }
            tr.Commit();

            using var ms = new MemoryStream();
            image.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            Manager.ReleaseGraphicsKernel(kernel);
        }
    }

    private static Bitmap RenderView(Manager gsm, GraphicsKernel kernel, int px, int py, Color background,
                                     BlockTableRecord content, ObjectIdCollection? frozenLayers, Action<GsView> setup)
    {
        using var device = gsm.CreateAutoCADOffScreenDevice(kernel);
        device.OnSize(new Size(px, py));
        device.DeviceRenderType = RendererType.Default;
        device.BackgroundColor = background;
        using var model = gsm.CreateAutoCADModel(kernel);
        using var view = new GsView();
        setup(view);
        if (frozenLayers != null)
            foreach (ObjectId layerId in frozenLayers)
                view.FreezeLayer(layerId.OldIdPtr);
        view.Add(content, model);
        device.Add(view);
        try
        {
            device.Update();
            return device.GetSnapshot(new Rectangle(0, 0, px, py));
        }
        finally
        {
            view.EraseAll();
            device.Erase(view);
        }
    }

    /// <summary>Points the view at a window given in the DCS of (target, direction, twist).</summary>
    private static void SetWindow(GsView view, Point3d viewTarget, Vector3d direction, double twist,
                                  Point2d center, double width, double height)
    {
        var dcsToWcs = ViewHandlers.DcsToWcs(viewTarget, direction, twist);
        var target = new Point3d(center.X, center.Y, 0).TransformBy(dcsToWcs);
        var up = Vector3d.YAxis.TransformBy(dcsToWcs).GetNormal();
        view.SetView(target + direction.GetNormal() * Math.Max(width, height), target, up, width, height);
    }

    private sealed record ViewportPart(Rectangle Pixels, BlockTableRecord ModelSpace, ObjectIdCollection FrozenLayers,
                                       Point3d Target, Vector3d Direction, double Twist,
                                       Point2d ModelCenter, double ModelWidth, double ModelHeight);

    /// <summary>Visible viewports of the current layout inside the captured window, in drawing order.</summary>
    private static List<ViewportPart> ViewportParts(Database db, Transaction tr, Point2d center, double width, double height, int px, int py)
    {
        var parts = new List<ViewportPart>();
        double left = center.X - width / 2, bottom = center.Y - height / 2;
        var layout = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        var hiddenLayers = new HashSet<ObjectId>();
        foreach (ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
        {
            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (l.IsOff || l.IsFrozen) hiddenLayers.Add(id);
        }

        bool first = true;
        foreach (ObjectId id in layout)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp) continue;
            if (first) { first = false; continue; } // the sheet's own viewport
            if (!vp.On || !vp.Visible || hiddenLayers.Contains(vp.LayerId) || vp.Width <= 0 || vp.Height <= 0) continue;

            // Part of the viewport inside the captured window (paper units), snapped to whole pixels.
            double x0 = Math.Max(vp.CenterPoint.X - vp.Width / 2, left), x1 = Math.Min(vp.CenterPoint.X + vp.Width / 2, left + width);
            double y0 = Math.Max(vp.CenterPoint.Y - vp.Height / 2, bottom), y1 = Math.Min(vp.CenterPoint.Y + vp.Height / 2, bottom + height);
            int pxLeft = (int)Math.Round((x0 - left) / width * px), pxRight = (int)Math.Round((x1 - left) / width * px);
            int pxTop = (int)Math.Round((1 - (y1 - bottom) / height) * py), pxBottom = (int)Math.Round((1 - (y0 - bottom) / height) * py);
            if (pxRight - pxLeft < 2 || pxBottom - pxTop < 2) continue;
            x0 = left + pxLeft * width / px;
            x1 = left + pxRight * width / px;
            y1 = bottom + (1 - (double)pxTop / py) * height;
            y0 = bottom + (1 - (double)pxBottom / py) * height;

            // The matching window in the viewport's model view (its DCS).
            double scale = vp.ViewHeight / vp.Height; // model units per paper unit
            var modelCenter = new Point2d(vp.ViewCenter.X + ((x0 + x1) / 2 - vp.CenterPoint.X) * scale,
                                          vp.ViewCenter.Y + ((y0 + y1) / 2 - vp.CenterPoint.Y) * scale);
            parts.Add(new ViewportPart(new Rectangle(pxLeft, pxTop, pxRight - pxLeft, pxBottom - pxTop), modelSpace,
                                       vp.GetFrozenLayers(), vp.ViewTarget, vp.ViewDirection, vp.TwistAngle,
                                       modelCenter, (x1 - x0) * scale, (y1 - y0) * scale));
        }
        return parts;
    }
}
