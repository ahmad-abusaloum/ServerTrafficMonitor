using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ServerTrafficMonitor.Services;

/// <summary>
/// Best-effort reverse DNS (PTR) lookup for remote IPs, with positive + negative
/// caching and bounded concurrency so the UI never blocks on name resolution.
/// </summary>
public sealed class DnsResolver
{
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(8); // max concurrent lookups

    /// <summary>
    /// Returns the cached host name immediately if known. Otherwise returns null and
    /// starts a background lookup; <paramref name="onResolved"/> is invoked (on a thread-pool
    /// thread) with the host name if/when one is found.
    /// </summary>
    public string? TryGetOrResolve(IPAddress ip, Action<string> onResolved)
    {
        if (!ShouldResolve(ip))
            return null;

        string key = ip.ToString();
        if (_cache.TryGetValue(key, out var cached))
            return string.IsNullOrEmpty(cached) ? null : cached;

        if (_inFlight.TryAdd(key, 0))
            _ = ResolveAsync(ip, key, onResolved);

        return null;
    }

    private async Task ResolveAsync(IPAddress ip, string key, Action<string> onResolved)
    {
        string host = "";
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var lookup = Dns.GetHostEntryAsync(ip);
                var done = await Task.WhenAny(lookup, Task.Delay(2500)).ConfigureAwait(false);
                if (done == lookup && lookup.IsCompletedSuccessfully)
                {
                    var name = lookup.Result.HostName;
                    if (!string.IsNullOrWhiteSpace(name) && name != key)
                        host = name;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // No PTR record / resolution failure -> negative cache (empty string).
        }

        _cache[key] = host;           // cache result (empty = "no name")
        _inFlight.TryRemove(key, out _);
        if (host.Length > 0)
            onResolved(host);
    }

    private static bool ShouldResolve(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal) return false;

        // 0.0.0.0 style or empty
        var bytes = ip.GetAddressBytes();
        bool allZero = true;
        foreach (var b in bytes) { if (b != 0) { allZero = false; break; } }
        return !allZero;
    }
}
