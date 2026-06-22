using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ServerTrafficMonitor.Models;

namespace ServerTrafficMonitor.Services;

/// <summary>
/// Captures OUTBOUND HTTP requests made by .NET applications on this server
/// (e.g. your ASP.NET Core API calling other APIs via HttpClient).
///
/// It subscribes to the .NET runtime's "System.Net.Http" EventSource over ETW.
/// Every .NET process that issues HttpClient requests emits a request-start event
/// (scheme + host + port + pathAndQuery) and a request-stop event (statusCode), so we
/// get the full target URL and response status for outbound calls — with NO proxy,
/// NO TLS certificate, and NO code change. Requires Administrator.
///
/// IMPORTANT: over ETW these EventSource events are named by Task/Opcode
/// ("Request/Start", "Request/Stop") rather than by method name, so we detect them by
/// their PAYLOAD FIELDS (pathAndQuery / statusCode) instead of the event name — that is
/// robust across .NET versions and naming schemes.
///
/// Note: the runtime telemetry does not expose the HTTP verb (GET/POST).
/// </summary>
public sealed class OutboundHttpEtwMonitor : IDisposable
{
    public const string SessionName = "ServerTrafficMonitor-OutHttp";
    private const string Provider = "System.Net.Http";

    private TraceEventSession? _session;
    private Task? _processTask;
    private readonly ProcessResolver _proc = new();

    /// <summary>Fired when a request starts (URL known, status pending).</summary>
    public event Action<Guid, HttpRecord>? RequestStarted;
    /// <summary>Fired when a request completes: (activityId, statusCode [-1 = failed], stopTime).</summary>
    public event Action<Guid, int, DateTime>? RequestCompleted;
    public event Action<string>? StatusChanged;

    public void Start()
    {
        try
        {
            if (!TraceEventSession.IsElevated().GetValueOrDefault())
            {
                StatusChanged?.Invoke("Outbound HTTP: needs Administrator");
                return;
            }

            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(Provider, TraceEventLevel.Informational, ulong.MaxValue);
            _session.Source.Dynamic.All += OnEvent;

            _processTask = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { StatusChanged?.Invoke("Outbound HTTP stopped: " + ex.Message); }
            });

            StatusChanged?.Invoke("Outbound HTTP: running");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Outbound HTTP unavailable: " + ex.Message);
        }
    }

    private void OnEvent(TraceEvent data)
    {
        try
        {
            string[]? fields = data.PayloadNames;
            Guid id = data.ActivityID;

            // Request start: uniquely identified by the "pathAndQuery" payload field.
            if (Has(fields, "pathAndQuery"))
            {
                string scheme = Str(data, "scheme") ?? "http";
                string host = Str(data, "host") ?? "";
                int port = Int(data, "port");
                string path = Str(data, "pathAndQuery") ?? "";
                int pid = data.ProcessID;

                RequestStarted?.Invoke(id, new HttpRecord
                {
                    Timestamp = data.TimeStamp,
                    Url = BuildUrl(scheme, host, port, path),
                    Host = host,
                    ProcessId = pid,
                    ProcessName = _proc.GetName(pid),
                    StatusCode = 0,
                    CorrelationId = id,
                });
                return;
            }

            // Request stop: uniquely identified by the "statusCode" payload field.
            if (Has(fields, "statusCode"))
            {
                RequestCompleted?.Invoke(id, Int(data, "statusCode"), data.TimeStamp);
                return;
            }

            // Request failed (no status code): identified by event name.
            string name = data.EventName ?? "";
            if (name.IndexOf("Fail", StringComparison.OrdinalIgnoreCase) >= 0)
                RequestCompleted?.Invoke(id, -1, data.TimeStamp);
        }
        catch
        {
            // Keep the trace alive regardless of a single malformed event.
        }
    }

    private static bool Has(string[]? names, string field)
        => names != null && Array.IndexOf(names, field) >= 0;

    private static string BuildUrl(string scheme, string host, int port, string path)
    {
        bool defaultPort = port == 0
            || (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && port == 443)
            || (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && port == 80);
        string authority = defaultPort ? host : $"{host}:{port}";
        return $"{scheme}://{authority}{path}";
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
                if (v is uint u) return (int)u;
                if (int.TryParse(v.ToString(), out var parsed)) return parsed;
            }
            catch { }
        }
        return 0;
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        try { _processTask?.Wait(2000); } catch { }
    }
}
