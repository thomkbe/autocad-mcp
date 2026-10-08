using System.Windows.Threading;
using AutoCadMcp.Protocol;

namespace AutoCadMcp.Plugin;

/// <summary>
/// Runs work on AutoCAD's main thread, the only thread allowed to touch drawings.
/// Work the main thread hasn't started within <paramref name="startTimeout"/> is cancelled,
/// so a busy AutoCAD never executes a request after the caller has stopped waiting for it.
/// </summary>
internal sealed class MainThreadInvoker(Dispatcher dispatcher, TimeSpan startTimeout)
{
    public async Task<PipeResponse> InvokeAsync(Func<PipeResponse> work)
    {
        var operation = dispatcher.InvokeAsync(work, DispatcherPriority.Normal);

        using var delay = new CancellationTokenSource();
        var first = await Task.WhenAny(operation.Task, Task.Delay(startTimeout, delay.Token)).ConfigureAwait(false);
        delay.Cancel();

        // Abort only succeeds while the work is still queued; once started we wait it out.
        if (first != operation.Task && operation.Abort())
        {
            return PipeResponse.Failure(
                $"AutoCAD did not respond within {startTimeout.TotalSeconds:0} s (it may be busy or showing a dialog). " +
                "The request was not executed.");
        }

        return await operation.Task.ConfigureAwait(false);
    }
}
