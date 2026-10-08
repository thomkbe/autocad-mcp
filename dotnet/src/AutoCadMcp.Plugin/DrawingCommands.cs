using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

namespace AutoCadMcp.Plugin;

/// <summary>The active document plus the open transaction a handler works in.</summary>
internal sealed record DrawingContext(Document Document, Transaction Transaction)
{
    public Database Database => Document.Database;
}

internal delegate JsonNode? CommandHandler(DrawingContext ctx, JsonObject args);

/// <summary>
/// Command handlers. Each runs on the main thread with the document locked inside a
/// transaction that <see cref="CommandRouter"/> commits when the handler returns.
/// </summary>
internal static class DrawingCommands
{
    public static readonly IReadOnlyDictionary<string, CommandHandler> Handlers = new Dictionary<string, CommandHandler>
    {
        ["create_line"] = CreateLine,
        ["create_circle"] = CreateCircle,
        ["list_entities"] = ListEntities,
        ["erase_entities"] = EraseEntities,
        ["list_layers"] = ListLayers,
        ["create_layer"] = CreateLayer,
        ["zoom_extents"] = ZoomExtents,
        ["get_object_data"] = GetObjectData,
        ["select_entities"] = SelectEntities,
        ["get_selection"] = GetSelection,
    };

    /// <summary>Works without a drawing open and while AutoCAD is busy.</summary>
    public static JsonNode Status()
    {
        var docs = AcadApp.DocumentManager;
        var doc = docs.MdiActiveDocument;
        return new JsonObject
        {
            ["connected"] = true,
            ["product"] = AcadApp.GetSystemVariable("PRODUCT")?.ToString(),
            ["acadVersion"] = AcadApp.GetSystemVariable("ACADVER")?.ToString(),
            ["pluginVersion"] = typeof(DrawingCommands).Assembly.GetName().Version?.ToString(3),
            ["openDrawings"] = docs.Count,
            ["activeDrawing"] = doc is null ? null : new JsonObject
            {
                ["name"] = doc.Name,
                ["units"] = doc.Database.Insunits.ToString(),
                ["busy"] = !doc.Editor.IsQuiescent,
                ["commandInProgress"] = doc.CommandInProgress is { Length: > 0 } name ? name : null,
            },
        };
    }

    private static JsonNode CreateLine(DrawingContext ctx, JsonObject args)
    {
        var start = new Point3d(args.RequireDouble("startX"), args.RequireDouble("startY"), 0);
        var end = new Point3d(args.RequireDouble("endX"), args.RequireDouble("endY"), 0);
        if (start.IsEqualTo(end))
            throw new CommandException("The start and end points are the same.");

        var line = new Line(start, end);
        AddToModelSpace(ctx, line, args.OptionalString("layer"));
        return Describe(line);
    }

    private static JsonNode CreateCircle(DrawingContext ctx, JsonObject args)
    {
        var center = new Point3d(args.RequireDouble("centerX"), args.RequireDouble("centerY"), 0);
        var radius = args.RequireDouble("radius");
        if (radius <= 0)
            throw new CommandException("radius must be greater than zero.");

        var circle = new Circle(center, Vector3d.ZAxis, radius);
        AddToModelSpace(ctx, circle, args.OptionalString("layer"));
        return Describe(circle);
    }

    private static JsonNode ListEntities(DrawingContext ctx, JsonObject args)
    {
        var layer = args.OptionalString("layer");
        var type = args.OptionalString("type")?.ToUpperInvariant();
        var limit = Math.Clamp(args.OptionalInt("limit") ?? 100, 1, 1000);

        var modelSpace = (BlockTableRecord)ctx.Transaction.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(ctx.Database), OpenMode.ForRead);
        var entities = new JsonArray();
        var matched = 0;
        foreach (ObjectId id in modelSpace)
        {
            // Filter on the class first: it doesn't require opening the object.
            if (type is not null && id.ObjectClass.DxfName != type)
                continue;
            var entity = (Entity)ctx.Transaction.GetObject(id, OpenMode.ForRead);
            if (layer is not null && !entity.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase))
                continue;

            matched++;
            if (entities.Count < limit)
                entities.Add(Describe(entity));
        }

        return new JsonObject { ["matched"] = matched, ["returned"] = entities.Count, ["entities"] = entities };
    }

    private static JsonNode EraseEntities(DrawingContext ctx, JsonObject args)
    {
        var erased = new JsonArray();
        var failed = new JsonArray();
        foreach (var handle in args.RequireStringArray("handles"))
        {
            if (TryErase(ctx, handle) is { } error)
                failed.Add(new JsonObject { ["handle"] = handle, ["error"] = error });
            else
                erased.Add(handle);
        }
        return new JsonObject { ["erased"] = erased, ["failed"] = failed };
    }

    private static JsonNode GetObjectData(DrawingContext ctx, JsonObject args)
    {
        var handle = args.RequireString("handle");
        var maxDepth = Math.Clamp(args.OptionalInt("maxDepth") ?? 6, 1, 20);
        var joinStrings = args.OptionalBool("joinStrings") ?? false;
        if (TryResolveHandle(ctx, handle, out var id) is { } error)
            throw new CommandException($"Handle '{handle}': {error}.");

        return new ObjectDataReader(ctx, maxDepth, joinStrings).Read(ctx.Transaction.GetObject(id, OpenMode.ForRead));
    }

    private static string? TryResolveHandle(DrawingContext ctx, string handleText, out ObjectId id)
    {
        id = ObjectId.Null;
        if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return "not a hexadecimal handle";
        if (!ctx.Database.TryGetObjectId(new Handle(value), out id) || id.IsErased)
            return "no such object";
        return null;
    }

    private static string? TryErase(DrawingContext ctx, string handleText)
    {
        if (TryResolveHandle(ctx, handleText, out var id) is { } error)
            return error;
        if (ctx.Transaction.GetObject(id, OpenMode.ForRead) is not Entity entity)
            return "not a drawing entity";

        // Only top-level entities; erasing inside a block definition would change every insert.
        var owner = (BlockTableRecord)ctx.Transaction.GetObject(entity.OwnerId, OpenMode.ForRead);
        if (!owner.IsLayout)
            return "not in model or paper space";

        try
        {
            entity.UpgradeOpen();
            entity.Erase();
            return null;
        }
        catch (AcadException ex) when (ex.ErrorStatus == ErrorStatus.OnLockedLayer)
        {
            return $"on locked layer '{entity.Layer}'";
        }
    }

    private static JsonNode ListLayers(DrawingContext ctx, JsonObject args)
    {
        var layerTable = (LayerTable)ctx.Transaction.GetObject(ctx.Database.LayerTableId, OpenMode.ForRead);
        var layers = new JsonArray();
        foreach (ObjectId id in layerTable)
        {
            var layer = (LayerTableRecord)ctx.Transaction.GetObject(id, OpenMode.ForRead);
            layers.Add(new JsonObject
            {
                ["name"] = layer.Name,
                ["color"] = layer.Color.IsByAci
                    ? layer.Color.ColorIndex
                    : $"{layer.Color.Red},{layer.Color.Green},{layer.Color.Blue}",
                ["current"] = id == ctx.Database.Clayer,
                ["on"] = !layer.IsOff,
                ["frozen"] = layer.IsFrozen,
                ["locked"] = layer.IsLocked,
            });
        }
        return new JsonObject { ["layers"] = layers };
    }

    private static JsonNode CreateLayer(DrawingContext ctx, JsonObject args)
    {
        var name = args.RequireString("name");
        var colorIndex = args.OptionalInt("color") ?? 7;
        if (colorIndex is < 1 or > 255)
            throw new CommandException("color must be an AutoCAD Color Index from 1 to 255.");
        try
        {
            SymbolUtilityServices.ValidateSymbolName(name, false);
        }
        catch (AcadException)
        {
            throw new CommandException($"'{name}' is not a valid layer name.");
        }

        var db = ctx.Database;
        var layerTable = (LayerTable)ctx.Transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
        var created = !layerTable.Has(name);
        ObjectId id;
        if (created)
        {
            var layer = new LayerTableRecord
            {
                Name = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex),
            };
            layerTable.UpgradeOpen();
            id = layerTable.Add(layer);
            ctx.Transaction.AddNewlyCreatedDBObject(layer, true);
        }
        else
        {
            id = layerTable[name];
        }

        if (args.OptionalBool("makeCurrent") == true)
            db.Clayer = id;

        return new JsonObject { ["name"] = name, ["created"] = created, ["current"] = db.Clayer == id };
    }

    private static JsonNode ZoomExtents(DrawingContext ctx, JsonObject args)
    {
        var db = ctx.Database;
        if (!db.TileMode)
            throw new CommandException("zoom_extents only supports the Model tab so far.");

        db.UpdateExt(true);
        if (db.Extmin.X > db.Extmax.X)
            return new JsonObject { ["zoomed"] = false, ["reason"] = "The drawing is empty." };

        ZoomTo(ctx, new Extents3d(db.Extmin, db.Extmax), margin: 1.05);
        return new JsonObject { ["zoomed"] = true, ["min"] = Point(db.Extmin), ["max"] = Point(db.Extmax) };
    }

    private static JsonNode SelectEntities(DrawingContext ctx, JsonObject args)
    {
        var handles = args.RequireStringArray("handles", allowEmpty: true);
        var ids = new List<ObjectId>();
        var selected = new JsonArray();
        var failed = new JsonArray();
        Extents3d? bounds = null;
        foreach (var handle in handles)
        {
            if (TryGetSelectable(ctx, handle, out var entity) is { } error)
            {
                failed.Add(new JsonObject { ["handle"] = handle, ["error"] = error });
                continue;
            }
            ids.Add(entity!.ObjectId);
            selected.Add(handle);
            if (entity.Bounds is { } entityBounds)
            {
                var union = bounds ?? entityBounds;
                union.AddExtents(entityBounds);
                bounds = union;
            }
        }

        // An empty request clears the selection; a request where nothing resolved leaves it alone.
        if (ids.Count > 0 || handles.Count == 0)
            ctx.Document.Editor.SetImpliedSelection(ids.ToArray());

        var zoomed = args.OptionalBool("zoom") == true && bounds is not null;
        if (zoomed)
            ZoomTo(ctx, bounds!.Value, margin: 3);

        var result = new JsonObject { ["selected"] = selected, ["failed"] = failed, ["zoomed"] = zoomed };
        if (Convert.ToInt32(AcadApp.GetSystemVariable("PICKFIRST")) == 0)
            result["warning"] = "PICKFIRST is 0, so AutoCAD may not show or keep this selection.";
        return result;
    }

    private static JsonNode GetSelection(DrawingContext ctx, JsonObject args)
    {
        var implied = ctx.Document.Editor.SelectImplied();
        var ids = implied.Status == PromptStatus.OK ? implied.Value.GetObjectIds() : [];
        var entities = new JsonArray();
        foreach (var id in ids.Take(100))
        {
            if (ctx.Transaction.GetObject(id, OpenMode.ForRead) is Entity entity)
                entities.Add(Describe(entity));
        }
        return new JsonObject { ["count"] = ids.Length, ["entities"] = entities };
    }

    private static string? TryGetSelectable(DrawingContext ctx, string handle, out Entity? entity)
    {
        entity = null;
        if (TryResolveHandle(ctx, handle, out var id) is { } error)
            return error;
        entity = ctx.Transaction.GetObject(id, OpenMode.ForRead) as Entity;
        if (entity is null)
            return "not a drawing entity";
        if (entity.OwnerId != ctx.Database.CurrentSpaceId)
            return "not in the current space; switch to the tab that contains it";
        return null;
    }

    /// <summary>Centers the current view on WCS extents, scaled up by <paramref name="margin"/>.</summary>
    private static void ZoomTo(DrawingContext ctx, Extents3d worldExtents, double margin)
    {
        var editor = ctx.Document.Editor;
        using var view = editor.GetCurrentView();

        // Bring the WCS extents into the view's display coordinate system.
        var displayToWorld = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
            * Matrix3d.Displacement(view.Target - Point3d.Origin)
            * Matrix3d.PlaneToWorld(view.ViewDirection);
        var extents = worldExtents;
        extents.TransformBy(displayToWorld.Inverse());

        var width = (extents.MaxPoint.X - extents.MinPoint.X) * margin;
        var height = (extents.MaxPoint.Y - extents.MinPoint.Y) * margin;
        if (width < 1e-9 && height < 1e-9)
            width = height = 1; // A single point.
        view.Width = Math.Max(width, 1e-9);
        view.Height = Math.Max(height, 1e-9);
        view.CenterPoint = new Point2d(
            (extents.MinPoint.X + extents.MaxPoint.X) / 2,
            (extents.MinPoint.Y + extents.MaxPoint.Y) / 2);
        editor.SetCurrentView(view);
    }

    private static void AddToModelSpace(DrawingContext ctx, Entity entity, string? layer)
    {
        try
        {
            entity.SetDatabaseDefaults(ctx.Database);
            if (layer is not null)
                entity.LayerId = RequireLayer(ctx, layer);

            var modelSpace = (BlockTableRecord)ctx.Transaction.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(ctx.Database), OpenMode.ForWrite);
            modelSpace.AppendEntity(entity);
            ctx.Transaction.AddNewlyCreatedDBObject(entity, true);
        }
        catch
        {
            if (entity.ObjectId.IsNull)
                entity.Dispose(); // Never made it into the database.
            throw;
        }
    }

    private static ObjectId RequireLayer(DrawingContext ctx, string name)
    {
        var layerTable = (LayerTable)ctx.Transaction.GetObject(ctx.Database.LayerTableId, OpenMode.ForRead);
        return layerTable.Has(name)
            ? layerTable[name]
            : throw new CommandException($"Layer '{name}' does not exist. Create it with create_layer first.");
    }

    internal static JsonObject Describe(Entity entity)
    {
        var json = new JsonObject
        {
            ["handle"] = entity.Handle.ToString(),
            ["type"] = entity.ObjectId.ObjectClass.DxfName,
            ["layer"] = entity.Layer,
        };

        // Flags only when set, to keep long listings short. get_object_data reads the contents.
        if (!entity.ExtensionDictionary.IsNull)
            json["hasExtensionDictionary"] = true;
        using (var xdata = entity.XData)
        {
            if (xdata is not null)
                json["hasXData"] = true;
        }

        switch (entity)
        {
            case Line line:
                json["start"] = Point(line.StartPoint);
                json["end"] = Point(line.EndPoint);
                json["length"] = Round(line.Length);
                break;
            case Arc arc:
                json["center"] = Point(arc.Center);
                json["radius"] = Round(arc.Radius);
                json["startAngleDeg"] = Round(arc.StartAngle * 180 / Math.PI);
                json["endAngleDeg"] = Round(arc.EndAngle * 180 / Math.PI);
                break;
            case Circle circle:
                json["center"] = Point(circle.Center);
                json["radius"] = Round(circle.Radius);
                break;
            case Polyline polyline:
                json["vertices"] = polyline.NumberOfVertices;
                json["closed"] = polyline.Closed;
                json["length"] = Round(polyline.Length);
                break;
            case DBText text:
                json["text"] = text.TextString;
                json["position"] = Point(text.Position);
                break;
            case MText mtext:
                json["text"] = mtext.Text;
                json["position"] = Point(mtext.Location);
                break;
            case BlockReference block:
                json["block"] = block.Name;
                json["position"] = Point(block.Position);
                break;
            default:
                if (entity.Bounds is { } bounds)
                {
                    json["min"] = Point(bounds.MinPoint);
                    json["max"] = Point(bounds.MaxPoint);
                }
                break;
        }
        return json;
    }

    private static JsonArray Point(Point3d p) => new(Round(p.X), Round(p.Y), Round(p.Z));

    private static double Round(double value) => Math.Round(value, 6);
}
