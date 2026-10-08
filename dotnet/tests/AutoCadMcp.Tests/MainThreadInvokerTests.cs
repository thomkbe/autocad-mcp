using System.Windows.Threading;
using AutoCadMcp.Plugin;
using AutoCadMcp.Protocol;

namespace AutoCadMcp.Tests;

/// <summary>A dedicated dispatcher thread stands in for AutoCAD's main thread.</summary>
public sealed class MainThreadInvokerTests : IDisposable
{
    private readonly Thread _mainThread;
    private readonly Dispatcher _dispatcher;

    public MainThreadInvokerTests()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        _mainThread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        _mainThread.SetApartmentState(ApartmentState.STA);
        _mainThread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    public void Dispose() => _dispatcher.InvokeShutdown();

    [Fact]
    public async Task Runs_work_on_the_main_thread()
    {
        var invoker = new MainThreadInvoker(_dispatcher, TimeSpan.FromSeconds(5));
        int? ranOn = null;

        var response = await invoker.InvokeAsync(() =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            return PipeResponse.Success("done");
        });

        Assert.True(response.Ok);
        Assert.Equal(_mainThread.ManagedThreadId, ranOn);
    }

    [Fact]
    public async Task Drops_work_a_busy_main_thread_never_started()
    {
        using var release = new ManualResetEventSlim();
        _ = _dispatcher.InvokeAsync(() => release.Wait()); // AutoCAD stuck in a long operation.
        var invoker = new MainThreadInvoker(_dispatcher, TimeSpan.FromMilliseconds(200));
        var ran = false;

        var response = await invoker.InvokeAsync(() =>
        {
            ran = true;
            return PipeResponse.Success(null);
        });
        release.Set();
        await _dispatcher.InvokeAsync(() => { }); // Let the queue drain.

        Assert.False(response.Ok);
        Assert.Contains("not executed", response.Error);
        Assert.False(ran);
    }

    [Fact]
    public async Task Waits_for_work_that_already_started()
    {
        var invoker = new MainThreadInvoker(_dispatcher, TimeSpan.FromMilliseconds(100));

        var response = await invoker.InvokeAsync(() =>
        {
            Thread.Sleep(400);
            return PipeResponse.Success("slow but done");
        });

        Assert.True(response.Ok);
    }
}
