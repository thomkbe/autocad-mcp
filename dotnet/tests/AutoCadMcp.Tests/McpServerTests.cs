using System.Text.Json.Nodes;
using AutoCadMcp.Plugin;
using AutoCadMcp.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AutoCadMcp.Tests;

/// <summary>Launches the real MCP server over stdio, with a fake plugin behind the pipe.</summary>
public sealed class McpServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_all_tools()
    {
        await using var client = await StartServerAsync(PipeTransportTests.UniquePipeName());

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(
            new[]
            {
                "create_circle", "create_layer", "create_line", "erase_entities", "get_object_data",
                "get_selection", "list_entities", "list_layers",
                "rc_eval_lua", "rc_find_objects", "rc_get_object", "rc_list_types", "rc_lua_api",
                "rc_model_check_report", "rc_status",
                "select_entities", "status", "zoom_extents",
            },
            tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task RailComplete_tools_go_to_the_RailComplete_pipe()
    {
        var autoCadPipe = PipeTransportTests.UniquePipeName();
        var railCompletePipe = PipeTransportTests.UniquePipeName();
        PipeRequest? received = null;
        using var bridge = new PipeServer(railCompletePipe, request =>
        {
            received = request;
            return Task.FromResult(PipeResponse.Success(new JsonObject { ["matched"] = 2 }));
        });
        bridge.Start();
        await using var client = await StartServerAsync(autoCadPipe, railCompletePipe);

        var result = await client.CallToolAsync("rc_find_objects", new Dictionary<string, object?>
        {
            ["type"] = "Signal", ["lua"] = "this.name == '808'", ["limit"] = 5,
        }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, JsonNode.Parse(TextOf(result))?["matched"]?.GetValue<int>());
        Assert.Equal("rc_find_objects", received?.Command);
        Assert.Equal("Signal", received?.Args?["type"]?.GetValue<string>());
        Assert.Equal("this.name == '808'", received?.Args?["lua"]?.GetValue<string>());
        Assert.Equal(5, received?.Args?["limit"]?.GetValue<int>());
    }

    [Fact]
    public async Task RailComplete_status_reports_disconnected_when_no_bridge_is_running()
    {
        await using var client = await StartServerAsync(PipeTransportTests.UniquePipeName());

        var result = await client.CallToolAsync("rc_status", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var status = JsonNode.Parse(TextOf(result));
        Assert.False(status?["connected"]?.GetValue<bool>());
        Assert.Contains("RailCOMPLETE", status?["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task Status_reports_disconnected_when_no_plugin_is_running()
    {
        await using var client = await StartServerAsync(PipeTransportTests.UniquePipeName());

        var result = await client.CallToolAsync("status", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.False(JsonNode.Parse(TextOf(result))?["connected"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Forwards_tool_arguments_to_the_plugin()
    {
        var pipeName = PipeTransportTests.UniquePipeName();
        PipeRequest? received = null;
        using var plugin = new PipeServer(pipeName, request =>
        {
            received = request;
            return Task.FromResult(PipeResponse.Success(new JsonObject { ["handle"] = "1F" }));
        });
        plugin.Start();
        await using var client = await StartServerAsync(pipeName);

        var result = await client.CallToolAsync("create_line", new Dictionary<string, object?>
        {
            ["startX"] = 0, ["startY"] = 0, ["endX"] = 10, ["endY"] = 5.5, ["layer"] = "Walls",
        }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("1F", JsonNode.Parse(TextOf(result))?["handle"]?.GetValue<string>());
        Assert.Equal("create_line", received?.Command);
        Assert.Equal(5.5, received?.Args?["endY"]?.GetValue<double>());
        Assert.Equal("Walls", received?.Args?["layer"]?.GetValue<string>());
    }

    [Fact]
    public async Task Plugin_errors_become_tool_errors_with_the_message()
    {
        var pipeName = PipeTransportTests.UniquePipeName();
        using var plugin = new PipeServer(pipeName, _ =>
            Task.FromResult(PipeResponse.Failure("AutoCAD is busy with the LINE command.")));
        plugin.Start();
        await using var client = await StartServerAsync(pipeName);

        var result = await client.CallToolAsync("list_layers", cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("busy with the LINE command", TextOf(result));
    }

    /// <param name="railCompletePipeName">Defaults to a fresh name, so tests never reach a real RailCOMPLETE bridge.</param>
    private static Task<McpClient> StartServerAsync(string pipeName, string? railCompletePipeName = null)
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "AutoCadMcp.Server.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "autocad-mcp-dotnet",
            Command = "dotnet",
            Arguments = [serverDll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                [PipeProtocol.PipeNameVariable] = pipeName,
                [AutoCadMcp.Server.RailCompleteClient.PipeNameVariable] = railCompletePipeName ?? PipeTransportTests.UniquePipeName(),
            },
            ShutdownTimeout = TimeSpan.FromSeconds(1),
        });
        return McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
