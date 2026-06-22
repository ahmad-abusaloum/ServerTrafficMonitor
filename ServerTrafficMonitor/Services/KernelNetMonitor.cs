using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace ServerTrafficMonitor.Services;

/// <summary>
/// A single outbound TCP connection captured the instant it is established.
/// </summary>
public readonly record struct OutboundConnection(
    string Protocol,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    int ProcessId);

/// <summary>
/// Real-time capture of outbound TCP connects via the kernel network ETW provider.
/// This complements the 1-second table polling: a connection that opens and closes
/// in well under a second (a typical fast API call) would be missed by polling, but
/// is caught here the moment the TCP handshake completes — which is exactly what you
/// need to see every call your API makes to other APIs. Requires Administrator.
/// </summary>
public sealed class KernelNetMonitor : IDisposable
{
    public const string SessionName = "ServerTrafficMonitor-KernelNet";

    private TraceEventSession? _session;
    private Task? _processTask;

    public event Action<OutboundConnection>? OutboundConnected;
    public event Action<string>? StatusChanged;

    public void Start()
    {
        try
        {
            if (!TraceEventSession.IsElevated().GetValueOrDefault())
            {
                StatusChanged?.Invoke("Real-time capture: needs Administrator");
                return;
            }

            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var k = _session.Source.Kernel;
            k.TcpIpConnect += d => Raise("TCP", d.saddr.ToString(), d.sport, d.daddr.ToString(), d.dport, d.ProcessID);
            k.TcpIpConnectIPV6 += d => Raise("TCPv6", d.saddr.ToString(), d.sport, d.daddr.ToString(), d.dport, d.ProcessID);

            _processTask = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { StatusChanged?.Invoke("Real-time capture stopped: " + ex.Message); }
            });

            StatusChanged?.Invoke("Real-time capture: running");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Real-time capture unavailable: " + ex.Message);
        }
    }

    private void Raise(string proto, string local, int lport, string remote, int rport, int pid)
    {
        try
        {
            OutboundConnected?.Invoke(new OutboundConnection(proto, local, lport, remote, rport, pid));
        }
        catch { }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        try { _processTask?.Wait(2000); } catch { }
    }
}
