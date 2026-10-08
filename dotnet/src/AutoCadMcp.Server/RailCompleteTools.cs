using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AutoCadMcp.Server;

/// <summary>Client for the MCP bridge module that ships inside RailCOMPLETE (AutoCAD.Mcp).</summary>
public sealed class RailCompleteClient()
    : PluginClient(ResolvePipeName(), "Start AutoCAD 2025 or 2026 with a RailCOMPLETE build that includes the MCP Bridge module.")
{
    public const string DefaultPipeName = "railcomplete-mcp";
    public const string PipeNameVariable = "RAILCOMPLETE_MCP_PIPE";

    private static string ResolvePipeName() =>
        Environment.GetEnvironmentVariable(PipeNameVariable) is { Length: > 0 } name ? name : DefaultPipeName;
}

/// <summary>Read-only tools over RailCOMPLETE's own object model, served by its MCP Bridge module.</summary>
[McpServerToolType]
public sealed class RailCompleteTools(RailCompleteClient railComplete)
{
    [McpServerTool(Name = "rc_status", ReadOnly = true, Idempotent = true)]
    [Description("Check whether RailCOMPLETE's MCP bridge is reachable and describe the active drawing: licence level, " +
                 "DNA (name, administration, version) and object counts by type. Call this first when working with " +
                 "RailCOMPLETE data.")]
    public async Task<string> Status(CancellationToken cancellationToken)
    {
        try
        {
            return ToText(await railComplete.SendAsync("rc_status", null, cancellationToken));
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

    [McpServerTool(Name = "rc_list_types", ReadOnly = true, Idempotent = true)]
    [Description("List the DNA object types that occur in the drawing (all=true for every type in the DNA), with class, " +
                 "data type and count. With type, describe that one type instead: its custom properties and its Lua " +
                 "formulas, including model checks.")]
    public Task<string> ListTypes(
        [Description("DNA type name or display name to describe")] string? type = null,
        [Description("List every type in the DNA, not only those in the drawing")] bool all = false,
        CancellationToken cancellationToken = default) =>
        CallAsync("rc_list_types", new() { ["type"] = type, ["all"] = all }, cancellationToken);

    [McpServerTool(Name = "rc_find_objects", ReadOnly = true, Idempotent = true)]
    [Description("Find RailCOMPLETE objects in the active drawing. Filters combine. Returns id, handle, type, name and " +
                 "code; 'matched' is the total even when fewer are returned. XRef objects are left out unless asked for.")]
    public Task<string> FindObjects(
        [Description("DNA type name or display name, as listed by rc_list_types")] string? type = null,
        [Description("railML data type, e.g. tSignal, eTrack")] string? dataType = null,
        [Description("Object class, e.g. RailwayPlacedObject, Alignment, Area")] string? @class = null,
        [Description("Text contained in the name, code, id or description (case-insensitive)")] string? text = null,
        [Description("Read-only Lua condition evaluated for every object with `this` set to it, e.g. " +
                     "`this.name == '808'`; objects for which it is true are kept")] string? lua = null,
        [Description("Include objects from external references")] bool includeXRefs = false,
        [Description("Maximum number of objects to return, 1-1000 (default 100)")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        CallAsync("rc_find_objects", new()
        {
            ["type"] = type, ["dataType"] = dataType, ["class"] = @class, ["text"] = text, ["lua"] = lua,
            ["includeXRefs"] = includeXRefs, ["limit"] = limit,
        }, cancellationToken);

    [McpServerTool(Name = "rc_get_object", ReadOnly = true, Idempotent = true)]
    [Description("Read one RailCOMPLETE object by AutoCAD handle or RailCOMPLETE id: its properties grouped by category " +
                 "as the Properties palette shows them, its Lua formulas, and its model-check results (symbol, " +
                 "message, value).")]
    public Task<string> GetObject(
        [Description("AutoCAD handle (hex), e.g. from rc_find_objects")] string? handle = null,
        [Description("RailCOMPLETE object id (GUID), when no handle is given")] string? id = null,
        [Description("Only these properties (names or display names)")] string[]? properties = null,
        [Description("Also list properties whose value is empty")] bool includeEmpty = false,
        CancellationToken cancellationToken = default) =>
        CallAsync("rc_get_object", new()
        {
            ["handle"] = handle, ["id"] = id, ["properties"] = ToArray(properties), ["includeEmpty"] = includeEmpty,
        }, cancellationToken);

    [McpServerTool(Name = "rc_model_check_report", ReadOnly = true, Idempotent = true)]
    [Description("Report the model-check results stored on the drawing's objects: counts per status, counts per check, " +
                 "and the individual results with the chosen statuses (default warning, error and unfinished). A " +
                 "result's status is its stored symbol; when a DNA function stored no symbol, it is the OK/WARNING/" +
                 "ERROR/UNFINISHED keyword in the result text, marked statusFromText. These are the results as " +
                 "RailCOMPLETE last evaluated them; it re-evaluates when objects change.")]
    public Task<string> ModelCheckReport(
        [Description("Statuses to list: ok, warning, error, unfinished, unknown")] string[]? statuses = null,
        [Description("Only objects of this DNA type")] string? type = null,
        [Description("Maximum number of results to return, 1-2000 (default 200)")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        CallAsync("rc_model_check_report", new()
        {
            ["statuses"] = ToArray(statuses), ["type"] = type, ["limit"] = limit,
        }, cancellationToken);

    [McpServerTool(Name = "rc_eval_lua", ReadOnly = true)]
    [Description("Evaluate a RailCOMPLETE Lua snippet in the read-only formula context and return its result. With a " +
                 "handle or id, `this` is that object; otherwise `this` is the drawing's DocumentData. `DocumentData` " +
                 "is always defined (e.g. DocumentData.ObjectCollection). The formula API is available: relations, " +
                 "distances along track, nearby objects, DNA functions; look functions up with rc_lua_api. Write a " +
                 "bare expression or statements ending in return. Assignments to objects have no effect, and " +
                 "functions that reach outside the sandbox are refused.")]
    public Task<string> EvalLua(
        [Description("Lua code")] string code,
        [Description("AutoCAD handle of the object to use as `this`")] string? handle = null,
        [Description("RailCOMPLETE id of the object to use as `this`, when no handle is given")] string? id = null,
        CancellationToken cancellationToken = default) =>
        CallAsync("rc_eval_lua", new() { ["code"] = code, ["handle"] = handle, ["id"] = id }, cancellationToken);

    [McpServerTool(Name = "rc_lua_api", ReadOnly = true, Idempotent = true)]
    [Description("Search RailCOMPLETE's Lua API, the functions and variables rc_eval_lua can use, by name or description.")]
    public Task<string> LuaApi(
        [Description("Text to search for in names and descriptions")] string? search = null,
        [Description("Maximum number of entries to return, 1-300 (default 40)")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        CallAsync("rc_lua_api", new() { ["search"] = search, ["limit"] = limit }, cancellationToken);

    private async Task<string> CallAsync(string command, JsonObject args, CancellationToken cancellationToken)
    {
        try
        {
            return ToText(await railComplete.SendAsync(command, args, cancellationToken));
        }
        catch (PluginException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static JsonArray? ToArray(string[]? values) =>
        values is null ? null : new JsonArray(values.Select(v => (JsonNode?)v).ToArray());

    private static string ToText(JsonNode? result) => result?.ToJsonString() ?? "null";
}
