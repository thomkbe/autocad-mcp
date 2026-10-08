using System.Windows.Threading;
using Autodesk.AutoCAD.Runtime;
using AutoCadMcp.Plugin;
using AutoCadMcp.Protocol;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(McpPlugin))]
[assembly: CommandClass(typeof(McpCommands))]

namespace AutoCadMcp.Plugin;

/// <summary>Starts the pipe server when the DLL is NETLOADed and stops it when AutoCAD exits.</summary>
public sealed class McpPlugin : IExtensionApplication
{
    internal static PipeServer? Server { get; private set; }

    internal static string? NotStartedReason { get; private set; }

    public void Initialize()
    {
        var pipeName = PipeProtocol.ResolvePipeName();
        if (PipeServer.IsPipeInUse(pipeName))
        {
            NotStartedReason = $"pipe '{pipeName}' is already served by another AutoCAD session";
            WriteMessage($"\nAutoCAD MCP: not started, {NotStartedReason}.\n");
            return;
        }

        // Initialize runs on AutoCAD's main thread, so this is the dispatcher that owns the drawings.
        var invoker = new MainThreadInvoker(Dispatcher.CurrentDispatcher, PipeProtocol.MainThreadTimeout);
        Server = new PipeServer(pipeName, request => invoker.InvokeAsync(() => CommandRouter.Execute(request)));
        Server.Start();
        WriteMessage($"\nAutoCAD MCP: listening on pipe '{pipeName}'. Type MCPSTATUS for details.\n");
    }

    public void Terminate() => Server?.Dispose();

    internal static void WriteMessage(string message) =>
        AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(message);
}

public static class McpCommands
{
    [CommandMethod("MCPSTATUS")]
    public static void Status()
    {
        var server = McpPlugin.Server;
        if (server is null)
        {
            McpPlugin.WriteMessage($"\nAutoCAD MCP: not running ({McpPlugin.NotStartedReason ?? "plugin not initialized"}).\n");
            return;
        }

        var lastError = server.LastError is { } error ? $"\nLast error: {error}" : "";
        McpPlugin.WriteMessage(
            $"\nAutoCAD MCP: listening on pipe '{server.PipeName}', {server.RequestCount} request(s) served.{lastError}\n");
    }
}
