using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCadMcp.Plugin;
using AutoCadMcp.Protocol;
using AutoCadMcp.Server;

namespace AutoCadMcp.Tests;

/// <summary>The real plugin pipe server talking to the real MCP-side client.</summary>
public sealed class PipeTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static string UniquePipeName() => $"autocad-mcp-test-{Guid.NewGuid():N}";

    [Fact]
    public async Task Round_trips_command_arguments_and_result()
    {
        var name = UniquePipeName();
        PipeRequest? received = null;
        using var server = new PipeServer(name, request =>
        {
            received = request;
            return Task.FromResult(PipeResponse.Success(new JsonObject { ["handle"] = "2A" }));
        });
        server.Start();

        var result = await new PluginClient(name).SendAsync(
            "create_line", new JsonObject { ["startX"] = 1.5, ["layer"] = "Walls" }, Ct);

        Assert.Equal("2A", result?["handle"]?.GetValue<string>());
        Assert.Equal("create_line", received?.Command);
        Assert.Equal(1.5, received?.Args?["startX"]?.GetValue<double>());
        Assert.Equal("Walls", received?.Args?["layer"]?.GetValue<string>());
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Failure_response_surfaces_the_plugin_message()
    {
        var name = UniquePipeName();
        using var server = new PipeServer(name, _ =>
            Task.FromResult(PipeResponse.Failure("Layer 'X' does not exist.")));
        server.Start();

        var ex = await Assert.ThrowsAsync<PluginException>(
            () => new PluginClient(name).SendAsync("create_line", null, Ct));

        Assert.Equal("Layer 'X' does not exist.", ex.Message);
    }

    [Fact]
    public async Task Handler_exception_is_answered_not_dropped()
    {
        var name = UniquePipeName();
        using var server = new PipeServer(name, _ => throw new InvalidOperationException("boom"));
        server.Start();

        var ex = await Assert.ThrowsAsync<PluginException>(
            () => new PluginClient(name).SendAsync("status", null, Ct));

        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Missing_plugin_is_reported_as_unavailable()
    {
        await Assert.ThrowsAsync<PluginUnavailableException>(
            () => new PluginClient(UniquePipeName()).SendAsync("status", null, Ct));
    }

    [Fact]
    public async Task Malformed_request_gets_an_error_response()
    {
        var name = UniquePipeName();
        using var server = new PipeServer(name, _ => Task.FromResult(PipeResponse.Success(null)));
        server.Start();

        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, Ct);
        await pipe.WriteAsync(PipeProtocol.Encoding.GetBytes("not json\n"), Ct);
        using var reader = new StreamReader(pipe, PipeProtocol.Encoding);
        var response = JsonSerializer.Deserialize<PipeResponse>((await reader.ReadLineAsync(Ct))!, PipeProtocol.Json);

        Assert.False(response?.Ok);
        Assert.StartsWith("Malformed request", response?.Error);
    }

    [Fact]
    public void Detects_a_pipe_already_served_by_another_instance()
    {
        var name = UniquePipeName();
        Assert.False(PipeServer.IsPipeInUse(name));

        using var server = new PipeServer(name, _ => Task.FromResult(PipeResponse.Success(null)));
        server.Start();

        Assert.True(SpinWait.SpinUntil(() => PipeServer.IsPipeInUse(name), TimeSpan.FromSeconds(5)));
        Assert.Equal(0, server.RequestCount); // Probes are not requests.
    }
}
