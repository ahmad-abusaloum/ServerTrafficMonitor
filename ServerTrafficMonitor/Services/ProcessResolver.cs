using System.Collections.Concurrent;
using System.Diagnostics;

namespace ServerTrafficMonitor.Services;

/// <summary>
/// Maps a process id to a friendly process name, cached with a short TTL so that
/// PID reuse on a busy server is eventually reflected without resolving on every poll.
/// </summary>
public sealed class ProcessResolver
{
    private readonly record struct Entry(string Name, long Stamp);
    private readonly ConcurrentDictionary<int, Entry> _cache = new();
    private const long TtlMs = 15_000;

    public string GetName(int pid)
    {
        if (pid == 0) return "System Idle";
        if (pid == 4) return "System";

        long now = Environment.TickCount64;
        if (_cache.TryGetValue(pid, out var e) && now - e.Stamp < TtlMs)
            return e.Name;

        string name = Resolve(pid);
        _cache[pid] = new Entry(name, now);
        return name;
    }

    private static string Resolve(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            // Process already exited, or access denied for a protected process.
            return $"PID {pid}";
        }
    }
}
