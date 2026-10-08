using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AutoCadMcp.Server;

/// <summary>The MCP tools. Each one forwards to the plugin command of the same name.</summary>
[McpServerToolType]
public sealed class AutoCadTools(PluginClient plugin)
{
    [McpServerTool(Name = "status", ReadOnly = true, Idempotent = true)]
    [Description("Check whether AutoCAD is reachable. Reports the AutoCAD version, the active drawing, " +
                 "its units, and whether AutoCAD is busy with a command. Call this first.")]
    public async Task<string> Status(CancellationToken cancellationToken)
    {
        try
        {
            return ToText(await plugin.SendAsync("status", null, cancellationToken));
        }
        catch (PluginUnavailableException ex)
        {
            return ToText(new JsonObject { ["connected"] = false, ["message"] = ex.Message });
        }
        catch (PluginException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "create_line")]
    [Description("Draw a straight line in model space (Z = 0). Returns the new entity, including its handle.")]
    public Task<string> CreateLine(
        [Description("X of the start point, in drawing units")] double startX,
        [Description("Y of the start point, in drawing units")] double startY,
        [Description("X of the end point, in drawing units")] double endX,
        [Description("Y of the end point, in drawing units")] double endY,
        [Description("Layer to draw on; it must already exist. Defaults to the current layer.")] string? layer = null,
        CancellationToken cancellationToken = default) =>
        CallAsync("create_line", new()
        {
            ["startX"] = startX, ["startY"] = startY, ["endX"] = endX, ["endY"] = endY, ["layer"] = layer,
        }, cancellationToken);

    [McpServerTool(Name = "create_circle")]
    [Description("Draw a circle in model space (Z = 0). Returns the new entity, including its handle.")]
    public Task<string> CreateCircle(
        [Description("X of the center, in drawing units")] double centerX,
        [Description("Y of the center, in drawing units")] double centerY,
        [Description("Radius in drawing units; must be greater than zero")] double radius,
        [Description("Layer to draw on; it must already exist. Defaults to the current layer.")] string? layer = null,
        CancellationToken cancellationToken = default) =>
        CallAsync("create_circle", new()
        {
            ["centerX"] = centerX, ["centerY"] = centerY, ["radius"] = radius, ["layer"] = layer,
        }, cancellationToken);

    [McpServerTool(Name = "list_entities", ReadOnly = true, Idempotent = true)]
    [Description("List entities in model space with their handle, type, layer and key geometry. " +
                 "'matched' is the total number of matches, even when fewer are returned.")]
    public Task<string> ListEntities(
        [Description("Only entities on this layer (case-insensitive)")] string? layer = null,
        [Description("Only this DXF type, e.g. LINE, CIRCLE, ARC, LWPOLYLINE, TEXT, MTEXT, INSERT")] string? type = null,
        [Description("Maximum number of entities to return, 1-1000 (default 100)")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        CallAsync("list_entities", new() { ["layer"] = layer, ["type"] = type, ["limit"] = limit }, cancellationToken);

    [McpServerTool(Name = "erase_entities", Destructive = true)]
    [Description("Erase model or paper space entities by handle. Reports which handles were erased and why any " +
                 "were not (unknown handle, locked layer, ...).")]
    public Task<string> EraseEntities(
        [Description("Hex handles of the entities to erase, as returned by other tools")] string[] handles,
        CancellationToken cancellationToken = default) =>
        CallAsync("erase_entities", new() { ["handles"] = new JsonArray(handles.Select(h => (JsonNode?)h).ToArray()) },
            cancellationToken);

    [McpServerTool(Name = "list_layers", ReadOnly = true, Idempotent = true)]
    [Description("List the drawing's layers with color (ACI number or \"r,g,b\"), on/frozen/locked state, " +
                 "and which layer is current.")]
    public Task<string> ListLayers(CancellationToken cancellationToken = default) =>
        CallAsync("list_layers", null, cancellationToken);

    [McpServerTool(Name = "create_layer", Idempotent = true)]
    [Description("Create a layer. An existing layer with the same name is left unchanged (created = false), " +
                 "but can still be made current.")]
    public Task<string> CreateLayer(
        [Description("Layer name")] string name,
        [Description("AutoCAD Color Index 1-255 (1 red, 2 yellow, 3 green, 4 cyan, 5 blue, 6 magenta, 7 white/black). Default 7.")] int? color = null,
        [Description("Also make this the current layer")] bool makeCurrent = false,
        CancellationToken cancellationToken = default) =>
        CallAsync("create_layer", new() { ["name"] = name, ["color"] = color, ["makeCurrent"] = makeCurrent },
            cancellationToken);

    [McpServerTool(Name = "get_object_data", ReadOnly = true, Idempotent = true)]
    [Description("Read the data attached to an entity, or to any other object, by handle. Walks its extension " +
                 "dictionary recursively: Xrecords as DXF group code/value pairs, nested dictionaries, and other " +
                 "objects reported by class and defining application (with proxyFor when that application isn't " +
                 "loaded). Also returns XData grouped by application. Object references in the data come back as " +
                 "handles that can be passed to this tool again. list_entities marks entities that have data " +
                 "with hasExtensionDictionary / hasXData.")]
    public Task<string> GetObjectData(
        [Description("Hex handle of the entity or object")] string handle,
        [Description("How many levels below the object are read in full, 1-20 (default 6). Deeper objects are " +
                     "listed by handle and type only, marked notExpanded.")] int? maxDepth = null,
        CancellationToken cancellationToken = default) =>
        CallAsync("get_object_data", new() { ["handle"] = handle, ["maxDepth"] = maxDepth }, cancellationToken);

    [McpServerTool(Name = "zoom_extents", Idempotent = true)]
    [Description("Zoom the Model tab view so the whole drawing is visible.")]
    public Task<string> ZoomExtents(CancellationToken cancellationToken = default) =>
        CallAsync("zoom_extents", null, cancellationToken);

    private async Task<string> CallAsync(string command, JsonObject? args, CancellationToken cancellationToken)
    {
        try
        {
            return ToText(await plugin.SendAsync(command, args, cancellationToken));
        }
        catch (PluginException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static string ToText(JsonNode? result) => result?.ToJsonString() ?? "null";
}
