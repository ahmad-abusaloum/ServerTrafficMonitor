using System.Collections.Concurrent;
using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ServerTrafficMonitor.Models;

namespace ServerTrafficMonitor.Services;

/// <summary>
/// Captures inbound HTTP requests by consuming the kernel-mode HTTP stack's ETW
/// provider (Microsoft-Windows-HttpService). This is the same path IIS sits on,
/// so every request that reaches IIS on this server is observed here — URL, verb,
/// status code, client endpoint and duration — without touching the API's code.
///
/// Requests are correlated to their response via the ETW Activity Id and emitted
/// once complete. Requires the process to run elevated.
/// </summary>
public sealed class HttpEtwMonitor : IDisposable
{
    public const string SessionName = "ServerTrafficMonitor-Http";

    private TraceEventSession? _session;
    private Task? _processTask;
    private Task? _flushTask;
    private CancellationTokenSource? _flushCts;

    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();

    public event Action<HttpRecord>? RequestCaptured;
    public event Action<string>? StatusChanged;

    private sealed class Pending
    {
        public DateTime Start;
        public string Method = "";
        public string Url = "";
        public string Client = "";
        public int ClientPort;
    }

    public void Start()
    {
        try
        {
            if (!TraceEventSession.IsElevated().GetValueOrDefault())
            {
                StatusChanged?.Invoke("HTTP capture: needs Administrator");
                return;
            }

            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider("Microsoft-Windows-HttpService");
            _session.Source.Dynamic.All += OnEvent;

            _processTask = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { StatusChanged?.Invoke("HTTP capture stopped: " + ex.Message); }
            });

            _flushCts = new CancellationTokenSource();
            _flushTask = Task.Run(() => FlushLoopAsync(_flushCts.Token));

            StatusChanged?.Invoke("HTTP capture: running");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("HTTP capture unavailable: " + ex.Message);
        }
    }

    private void OnEvent(TraceEvent data)
    {
        try
        {
            string name = data.EventName ?? "";
            Guid id = data.ActivityID;

            // --- Request phase: client endpoint arrives with RecvReq, URL/verb with Parse ---
            if (name.Contains("RecvReq", StringComparison.OrdinalIgnoreCase))
            {
                var p = _pending.GetOrAdd(id, _ => new Pending { Start = data.TimeStamp });
                TryFillClient(data, p);
            }
            else if (name.Contains("Parse", StringComparison.OrdinalIgnoreCase))
            {
                var p = _pending.GetOrAdd(id, _ => new Pending { Start = data.TimeStamp });
                p.Url = Str(data, "Url") ?? p.Url;
                p.Method = VerbToString(data) ?? p.Method;
            }
            // --- Response phase: status code is available -> emit the completed record ---
            else if (name.Contains("FastResp", StringComparison.OrdinalIgnoreCase)
                  || name.Contains("FastSend", StringComparison.OrdinalIgnoreCase)
                  || name.Contains("SendComplete", StringComparison.OrdinalIgnoreCase)
                  || name.Contains("Resp", StringComparison.OrdinalIgnoreCase))
            {
                int status = Int(data, "HttpStatusCode", "StatusCode");
                if (id != Guid.Empty && _pending.TryRemove(id, out var p))
                {
                    double ms = (data.TimeStamp - p.Start).TotalMilliseconds;
                    Emit(p, status, ms);
                }
            }
        }
        catch
        {
            // Never let a malformed event take down the trace session.
        }
    }

    private void Emit(Pending p, int status, double ms)
    {
        if (string.IsNullOrEmpty(p.Url) && string.IsNullOrEmpty(p.Method) && status == 0)
            return; // nothing useful captured

        RequestCaptured?.Invoke(new HttpRecord
        {
            Timestamp = p.Start == default ? DateTime.Now : p.Start,
            Method = p.Method,
            Url = p.Url,
            StatusCode = status,
            ClientAddress = p.Client,
            ClientPort = p.ClientPort,
            DurationMs = ms > 0 ? ms : 0
        });
    }

    /// <summary>Emit requests that never produced a response event (so nothing is lost).</summary>
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var cutoff = DateTime.Now.AddSeconds(-30);
                foreach (var kvp in _pending)
                {
                    if (kvp.Value.Start < cutoff && _pending.TryRemove(kvp.Key, out var p))
                        Emit(p, 0, 0);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void TryFillClient(TraceEvent data, Pending p)
    {
        // HTTP.sys renders the client endpoint as a sockaddr blob in "RemoteAddr".
        try
        {
            if (data.PayloadByName("RemoteAddr") is byte[] sa && sa.Length >= 8)
            {
                int family = sa[0] | (sa[1] << 8);
                int port = (sa[2] << 8) | sa[3]; // big-endian
                if (family == 2 && sa.Length >= 8) // AF_INET
                {
                    p.Client = new IPAddress(new[] { sa[4], sa[5], sa[6], sa[7] }).ToString();
                    p.ClientPort = port;
                }
                else if (family == 23 && sa.Length >= 24) // AF_INET6
                {
                    var v6 = new byte[16];
                    Array.Copy(sa, 8, v6, 0, 16);
                    p.Client = new IPAddress(v6).ToString();
                    p.ClientPort = port;
                }
            }
        }
        catch { }
    }

    private static string? Str(TraceEvent d, params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                var v = d.PayloadByName(n);
                if (v is string s && s.Length > 0) return s;
                if (v != null) { var t = v.ToString(); if (!string.IsNullOrEmpty(t)) return t; }
            }
            catch { }
        }
        return null;
    }

    private static int Int(TraceEvent d, params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                var v = d.PayloadByName(n);
                if (v == null) continue;
                if (v is int i) return i;
                if (int.TryParse(v.ToString(), out var parsed)) return parsed;
            }
            catch { }
        }
        return 0;
    }

    private static string? VerbToString(TraceEvent d)
    {
        var raw = Str(d, "Verb", "HttpVerb");
        if (raw == null) return null;
        if (!int.TryParse(raw, out var v)) return raw; // already a string verb
        return v switch
        {
            3 => "OPTIONS", 4 => "GET", 5 => "HEAD", 6 => "POST", 7 => "PUT",
            8 => "DELETE", 9 => "TRACE", 10 => "CONNECT", 14 => "PROPFIND",
            15 => "PROPPATCH", 16 => "MKCOL", 17 => "LOCK", 18 => "UNLOCK", 19 => "SEARCH",
            _ => $"VERB({v})"
        };
    }

    public void Dispose()
    {
        try { _flushCts?.Cancel(); } catch { }
        try { _session?.Dispose(); } catch { }       // causes Source.Process() to return
        try { _processTask?.Wait(2000); } catch { }
        try { _flushTask?.Wait(1000); } catch { }
        _flushCts?.Dispose();
    }
}
