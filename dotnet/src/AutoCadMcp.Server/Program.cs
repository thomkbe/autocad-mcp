using AutoCadMcp.Protocol;
using AutoCadMcp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so every log line must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new PluginClient(PipeProtocol.ResolvePipeName()));
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "autocad-mcp-dotnet", Version = "0.1.0" };
        options.ServerInstructions =
            "Drives a running AutoCAD 2025/2026 through its .NET API. Call status first. " +
            "All tools act on the active drawing's model space; coordinates are in drawing units " +
            "(see activeDrawing.units in status). Entities are identified by their hex handle.";
    })
    .WithStdioServerTransport()
    .WithTools<AutoCadTools>();

await builder.Build().RunAsync();
