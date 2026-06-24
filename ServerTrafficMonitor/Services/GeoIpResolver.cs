using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace ServerTrafficMonitor.Services;

/// <summary>
/// Resolves a public IP address to a country using the free ip-api.com endpoint,
/// with positive + negative caching and bounded concurrency. Private / LAN / loopback
/// addresses are answered locally ("Private / LAN") without any network call, and
/// all-zero / unspecified addresses return nothing. If the server has no internet the
/// lookup simply yields nothing and the tool keeps working.
///
/// Privacy: only public IPs are ever sent to ip-api.com; internal addresses never leave.
/// </summary>
public sealed class GeoIpResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly ConcurrentDictionary<string, (string code, string country)> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(4);

    /// <summary>
    /// Returns the cached (code, country) immediately if known. Otherwise returns null
    /// and starts a background lookup that invokes <paramref name="onResolved"/> when done.
    /// </summary>
    public (string code, string country)? TryGetOrResolve(string ip, Action<string, string> onResolved)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var addr))
            return null;

        if (IsAllZero(addr))
            return null; // 0.0.0.0 / :: (e.g. a listening socket) -> no country

        if (IsPrivate(addr))
            return ("LAN", "Private / LAN");

        if (_cache.TryGetValue(ip, out var hit))
            return hit.country.Length == 0 ? null : hit;

        if (_inFlight.TryAdd(ip, 0))
            _ = ResolveAsync(ip, onResolved);

        return null;
    }

    private async Task ResolveAsync(string ip, Action<string, string> onResolved)
    {
        string code = "", country = "";
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var url = $"http://ip-api.com/json/{ip}?fields=status,country,countryCode";
                var json = await Http.GetStringAsync(url).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var st) && st.GetString() == "success")
                {
                    country = root.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
                    code = root.TryGetProperty("countryCode", out var cc) ? cc.GetString() ?? "" : "";
                }
            }
            finally { _gate.Release(); }
        }
        catch
        {
            // offline / rate-limited / parse error -> negative cache, stay silent
        }

        _cache[ip] = (code, country);
        _inFlight.TryRemove(ip, out _);
        if (country.Length > 0)
            onResolved(code, country);
    }

    private static bool IsAllZero(IPAddress ip)
    {
        foreach (var b in ip.GetAddressBytes())
            if (b != 0) return false;
        return true;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254); // link-local
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            if ((b[0] & 0xFE) == 0xFC) return true; // unique local fc00::/7
        }
        return false;
    }
}
