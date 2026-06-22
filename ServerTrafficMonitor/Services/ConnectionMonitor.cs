using ServerTrafficMonitor.Native;

namespace ServerTrafficMonitor.Services;

/// <summary>
/// Polls the OS connection tables on a background thread and publishes immutable
/// snapshots. All UI-thread reconciliation happens in the view model.
/// </summary>
public sealed class ConnectionMonitor : IDisposable
{
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<IReadOnlyList<RawConnection>>? SnapshotReady;
    public event Action<Exception>? Error;

    /// <summary>When true, polling continues but snapshots are suppressed.</summary>
    public volatile bool Paused;

    public ConnectionMonitor(TimeSpan interval) => _interval = interval;

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Poll(); // immediate first snapshot
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Poll();
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    private void Poll()
    {
        if (Paused) return;
        try
        {
            SnapshotReady?.Invoke(IpHelper.GetAllConnections());
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(2000); } catch { }
        _cts?.Dispose();
    }
}
