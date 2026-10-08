using System.IO.Pipes;
using System.Text.Json;
using AutoCadMcp.Protocol;

namespace AutoCadMcp.Plugin;

/// <summary>
/// Serves the plugin's named pipe on thread-pool threads. The pipe only accepts clients
/// running as the current Windows user. Each connection carries one request.
/// </summary>
internal sealed class PipeServer(string pipeName, Func<PipeRequest, Task<PipeResponse>> handler) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;
    private int _requestCount;

    public string PipeName => pipeName;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public string? LastError { get; private set; }

    public void Start() => _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_shutdown.Token));

    public void Dispose() => _shutdown.Cancel();

    /// <summary>True if another process (e.g. a second AutoCAD) is already serving this pipe.</summary>
    public static bool IsPipeInUse(string pipeName)
    {
        using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            probe.Connect(200);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // Exists, but owned by a different user.
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                LastError = $"{DateTime.Now:T} accept failed: {ex.Message}";
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            _ = ServeAsync(pipe, ct);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, PipeProtocol.Encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                return; // Probe connection, or the client gave up.

            Interlocked.Increment(ref _requestCount);
            var response = await HandleLineAsync(line).ConfigureAwait(false);
            await pipe.WriteAsync(PipeProtocol.Frame(response), ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
            pipe.WaitForPipeDrain(); // Don't close before the client has read the answer.
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Client disconnected or AutoCAD is shutting down; nobody is left to answer.
        }
        catch (Exception ex)
        {
            LastError = $"{DateTime.Now:T} {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<PipeResponse> HandleLineAsync(string line)
    {
        PipeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PipeRequest>(line, PipeProtocol.Json);
        }
        catch (JsonException ex)
        {
            return PipeResponse.Failure($"Malformed request: {ex.Message}");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Command))
            return PipeResponse.Failure("Request has no command.");

        try
        {
            return await handler(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return PipeResponse.Failure($"Plugin error: {ex.Message}");
        }
    }
}
