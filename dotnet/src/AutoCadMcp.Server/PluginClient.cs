using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCadMcp.Protocol;

namespace AutoCadMcp.Server;

/// <summary>Talks to the AutoCAD plugin over its named pipe, one connection per request.</summary>
public sealed class PluginClient(string pipeName)
{
    private const int ConnectTimeoutMs = 2000;

    // Longer than the plugin's main-thread timeout, so its "AutoCAD is busy" answer arrives first.
    private static readonly TimeSpan ResponseTimeout = PipeProtocol.MainThreadTimeout + TimeSpan.FromSeconds(50);

    public string PipeName => pipeName;

    /// <returns>The command's result.</returns>
    /// <exception cref="PluginUnavailableException">No plugin is listening.</exception>
    /// <exception cref="PluginException">The command failed or the connection broke.</exception>
    public async Task<JsonNode?> SendAsync(string command, JsonObject? args, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or UnauthorizedAccessException)
        {
            throw new PluginUnavailableException(
                $"Could not reach the AutoCAD plugin on pipe '{pipeName}'. Start AutoCAD 2025 or 2026 and " +
                "NETLOAD AutoCadMcp.Plugin.dll (see dotnet/README.md).");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseTimeout);
        try
        {
            await pipe.WriteAsync(PipeProtocol.Frame(new PipeRequest(command, args)), timeout.Token);
            await pipe.FlushAsync(timeout.Token);

            using var reader = new StreamReader(pipe, PipeProtocol.Encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token)
                ?? throw new PluginException("The AutoCAD plugin closed the connection without answering.");
            var response = JsonSerializer.Deserialize<PipeResponse>(line, PipeProtocol.Json)
                ?? throw new PluginException("The AutoCAD plugin sent an empty response.");

            return response.Ok
                ? response.Result
                : throw new PluginException(response.Error ?? "The AutoCAD plugin reported an unspecified error.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PluginException(
                $"AutoCAD did not answer within {ResponseTimeout.TotalSeconds:0} s; the command may still be running.");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new PluginException($"Lost contact with the AutoCAD plugin: {ex.Message}");
        }
    }
}

public class PluginException(string message) : Exception(message);

public sealed class PluginUnavailableException(string message) : PluginException(message);
