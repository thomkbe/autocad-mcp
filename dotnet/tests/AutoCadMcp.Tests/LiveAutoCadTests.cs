using System.Text.Json.Nodes;
using AutoCadMcp.Protocol;
using AutoCadMcp.Server;

namespace AutoCadMcp.Tests;

/// <summary>
/// Runs against a real AutoCAD with the plugin loaded and a drawing open. Opt in with
/// AUTOCAD_MCP_LIVE=1. Draws into the active drawing and erases what it drew.
/// </summary>
public sealed class LiveAutoCadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PluginClient ConnectOrSkip()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("AUTOCAD_MCP_LIVE") == "1",
            "Set AUTOCAD_MCP_LIVE=1 with AutoCAD running, the plugin loaded and a drawing open.");
        return new PluginClient(PipeProtocol.ResolvePipeName());
    }

    [Fact]
    public async Task Draws_lists_and_erases_in_the_active_drawing()
    {
        var plugin = ConnectOrSkip();

        var status = await plugin.SendAsync("status", null, Ct);
        Assert.True(status?["connected"]?.GetValue<bool>());
        Assert.NotNull(status?["activeDrawing"]);

        var line = await plugin.SendAsync("create_line",
            new JsonObject { ["startX"] = 0, ["startY"] = 0, ["endX"] = 100, ["endY"] = 50 }, Ct);
        var circle = await plugin.SendAsync("create_circle",
            new JsonObject { ["centerX"] = 50, ["centerY"] = 25, ["radius"] = 10 }, Ct);
        var lineHandle = line?["handle"]?.GetValue<string>();
        var circleHandle = circle?["handle"]?.GetValue<string>();
        Assert.Equal("LINE", line?["type"]?.GetValue<string>());
        Assert.Equal(Math.Sqrt(100 * 100 + 50 * 50), line?["length"]?.GetValue<double>() ?? 0, 5);
        Assert.Equal(10, circle?["radius"]?.GetValue<double>());

        var circles = await plugin.SendAsync("list_entities",
            new JsonObject { ["type"] = "circle", ["limit"] = 1000 }, Ct);
        Assert.Contains(circleHandle, circles?["entities"]?.AsArray().Select(e => e?["handle"]?.GetValue<string>()) ?? []);
        Assert.All(circles?["entities"]?.AsArray() ?? [], e => Assert.Equal("CIRCLE", e?["type"]?.GetValue<string>()));

        var layers = await plugin.SendAsync("list_layers", null, Ct);
        Assert.Contains("0", layers?["layers"]?.AsArray().Select(l => l?["name"]?.GetValue<string>()) ?? []);

        var erased = await plugin.SendAsync("erase_entities",
            new JsonObject { ["handles"] = new JsonArray(lineHandle, circleHandle) }, Ct);
        Assert.Equal(2, erased?["erased"]?.AsArray().Count);

        var again = await plugin.SendAsync("erase_entities",
            new JsonObject { ["handles"] = new JsonArray(lineHandle, circleHandle) }, Ct);
        Assert.Empty(again?["erased"]?.AsArray() ?? []);
        Assert.All(again?["failed"]?.AsArray() ?? [], f => Assert.Equal("no such object", f?["error"]?.GetValue<string>()));
    }

    [Fact]
    public async Task Reads_dictionaries_and_reports_plain_entities_as_having_no_data()
    {
        var plugin = ConnectOrSkip();

        // Handle C is the named objects dictionary in AutoCAD drawings; it always holds ACAD_LAYOUT.
        var namedObjects = await plugin.SendAsync("get_object_data",
            new JsonObject { ["handle"] = "C", ["maxDepth"] = 1 }, Ct);
        Assert.Equal("DICTIONARY", namedObjects?["type"]?.GetValue<string>());
        // maxDepth 1: objects one level down are read in full, the level below that is only listed.
        var layouts = namedObjects?["entries"]?["ACAD_LAYOUT"];
        Assert.Equal("DICTIONARY", layouts?["type"]?.GetValue<string>());
        var model = layouts?["entries"]?["Model"];
        Assert.Equal("LAYOUT", model?["type"]?.GetValue<string>());
        Assert.Equal("maxDepth reached", model?["notExpanded"]?.GetValue<string>());

        var line = await plugin.SendAsync("create_line",
            new JsonObject { ["startX"] = 0, ["startY"] = 0, ["endX"] = 1, ["endY"] = 1 }, Ct);
        var handle = line?["handle"]?.GetValue<string>();
        try
        {
            Assert.Null(line?["hasExtensionDictionary"]);
            var data = await plugin.SendAsync("get_object_data", new JsonObject { ["handle"] = handle }, Ct);
            Assert.Equal("AcDbLine", data?["class"]?.GetValue<string>());
            Assert.Null(data?["extensionDictionary"]);
            Assert.Null(data?["xdata"]);
        }
        finally
        {
            await plugin.SendAsync("erase_entities", new JsonObject { ["handles"] = new JsonArray(handle) }, Ct);
        }

        var missing = await Assert.ThrowsAsync<PluginException>(() => plugin.SendAsync("get_object_data",
            new JsonObject { ["handle"] = "FFFFFFF" }, Ct));
        Assert.Contains("no such object", missing.Message);
    }

    [Fact]
    public async Task Rejects_bad_requests_with_clear_messages()
    {
        var plugin = ConnectOrSkip();

        var noLayer = await Assert.ThrowsAsync<PluginException>(() => plugin.SendAsync("create_line",
            new JsonObject { ["startX"] = 0, ["startY"] = 0, ["endX"] = 1, ["endY"] = 1, ["layer"] = $"missing-{Guid.NewGuid():N}" }, Ct));
        Assert.Contains("does not exist", noLayer.Message);

        var badRadius = await Assert.ThrowsAsync<PluginException>(() => plugin.SendAsync("create_circle",
            new JsonObject { ["centerX"] = 0, ["centerY"] = 0, ["radius"] = -1 }, Ct));
        Assert.Contains("greater than zero", badRadius.Message);

        var missingArg = await Assert.ThrowsAsync<PluginException>(() => plugin.SendAsync("create_line",
            new JsonObject { ["startX"] = 0 }, Ct));
        Assert.Contains("Missing required argument 'startY'", missingArg.Message);

        var unknown = await Assert.ThrowsAsync<PluginException>(() => plugin.SendAsync("explode_everything", null, Ct));
        Assert.Contains("Unknown command", unknown.Message);
    }
}
