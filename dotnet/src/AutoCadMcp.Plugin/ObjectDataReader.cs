using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadMcp.Plugin;

/// <summary>
/// Reads the data attached to an object: its extension dictionary (walked recursively) and
/// its XData. Xrecords come back as DXF group code/value pairs; other objects in a dictionary
/// are reported by class, since their fields are only known to the application defining them.
/// With <paramref name="joinStrings"/>, chunked text comes back as one value (see <see cref="StringJoiner"/>).
/// </summary>
internal sealed class ObjectDataReader(DrawingContext ctx, int maxDepth, bool joinStrings)
{
    private const int MaxObjects = 500;
    private const int MaxValuesPerBuffer = 1000;
    private const int MaxBinaryBytes = 1024;

    private int _objectsLeft = MaxObjects;

    public JsonObject Read(DBObject obj) => Describe(obj, depth: 0);

    private JsonObject Describe(DBObject obj, int depth)
    {
        _objectsLeft--;
        var json = obj is Entity entity
            ? DrawingCommands.Describe(entity)
            : new JsonObject { ["handle"] = obj.Handle.ToString(), ["type"] = obj.ObjectId.ObjectClass.DxfName };

        var rxClass = obj.GetRXClass();
        json["class"] = rxClass.Name;
        if (!string.IsNullOrEmpty(rxClass.AppName))
            json["app"] = rxClass.AppName;
        switch (obj)
        {
            case ProxyObject proxy:
                json["proxyFor"] = $"{proxy.OriginalClassName} ({proxy.OriginalDxfName})";
                break;
            case ProxyEntity proxy:
                json["proxyFor"] = $"{proxy.OriginalClassName} ({proxy.OriginalDxfName})";
                break;
        }

        switch (obj)
        {
            case Xrecord xrecord:
                using (var data = xrecord.Data)
                    AddValues(json, "values", data?.AsArray());
                break;
            case DBDictionary dictionary:
                json["entries"] = Entries(dictionary, depth);
                break;
        }

        using (var xdata = obj.XData)
        {
            if (xdata is not null)
                json["xdata"] = XDataByApp(xdata);
        }

        if (!obj.ExtensionDictionary.IsNull)
            json["extensionDictionary"] = Child(obj.ExtensionDictionary, depth);

        return json;
    }

    private JsonObject Entries(DBDictionary dictionary, int depth)
    {
        var entries = new JsonObject();
        foreach (DBDictionaryEntry entry in dictionary)
            entries[entry.Key] = Child(entry.Value, depth);
        return entries;
    }

    private JsonObject Child(ObjectId id, int depth)
    {
        if (depth + 1 > maxDepth || _objectsLeft <= 0)
        {
            return new JsonObject
            {
                ["handle"] = id.Handle.ToString(),
                ["type"] = id.ObjectClass.DxfName,
                ["notExpanded"] = _objectsLeft <= 0 ? $"object limit ({MaxObjects}) reached" : "maxDepth reached",
            };
        }
        return Describe(ctx.Transaction.GetObject(id, OpenMode.ForRead), depth + 1);
    }

    private JsonArray XDataByApp(ResultBuffer xdata)
    {
        var apps = new JsonArray();
        JsonObject? current = null;
        var values = new List<TypedValue>();

        void Flush()
        {
            if (current is null)
                return;
            AddValues(current, "values", values);
            apps.Add(current);
            values.Clear();
        }

        foreach (TypedValue value in xdata)
        {
            if (value.TypeCode == (short)DxfCode.ExtendedDataRegAppName)
            {
                Flush();
                current = new JsonObject { ["app"] = value.Value as string };
            }
            else
            {
                current ??= new JsonObject { ["app"] = null };
                values.Add(value);
            }
        }
        Flush();
        return apps;
    }

    private void AddValues(JsonObject target, string name, IEnumerable<TypedValue>? values)
    {
        var pairs = (values ?? []).Select(v => (v.TypeCode, (object?)v.Value));
        var items = joinStrings ? StringJoiner.Join(pairs) : pairs.Select(p => (p.TypeCode, p.Item2, Parts: 1));

        var array = new JsonArray();
        var total = 0;
        foreach (var (code, value, parts) in items)
        {
            if (++total > MaxValuesPerBuffer)
                continue;
            var json = Value(code, value);
            if (parts > 1)
                json["joined"] = parts;
            array.Add(json);
        }
        target[name] = array;
        if (total > MaxValuesPerBuffer)
            target[name + "Total"] = total;
    }

    private static JsonObject Value(short code, object? value)
    {
        var json = new JsonObject { ["code"] = code };
        switch (value)
        {
            case null:
                json["value"] = null;
                break;
            case ObjectId id:
                json["handle"] = id.IsNull ? null : id.Handle.ToString();
                break;
            case string s:
                json["value"] = s;
                break;
            case double d:
                json["value"] = double.IsFinite(d) ? d : d.ToString();
                break;
            case short or int or long or byte:
                json["value"] = JsonValue.Create(Convert.ToInt64(value));
                break;
            case Point3d p:
                json["value"] = new JsonArray(p.X, p.Y, p.Z);
                break;
            case Point2d p:
                json["value"] = new JsonArray(p.X, p.Y);
                break;
            case Vector3d v:
                json["value"] = new JsonArray(v.X, v.Y, v.Z);
                break;
            case byte[] bytes:
                json["hex"] = Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, MaxBinaryBytes));
                if (bytes.Length > MaxBinaryBytes)
                    json["bytes"] = bytes.Length;
                break;
            default:
                json["value"] = value.ToString();
                json["valueType"] = value.GetType().Name;
                break;
        }
        return json;
    }
}
