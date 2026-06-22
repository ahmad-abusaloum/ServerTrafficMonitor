namespace ServerTrafficMonitor.Models;

/// <summary>
/// Direction of a connection relative to THIS server.
/// </summary>
public enum TrafficDirection
{
    Unknown = 0,

    /// <summary>A remote client connected to a port we are listening on (e.g. a request hitting IIS).</summary>
    Inbound = 1,

    /// <summary>We initiated the connection to a remote endpoint (e.g. our API calling another API).</summary>
    Outbound = 2,

    /// <summary>A local socket in LISTEN state, waiting for connections.</summary>
    Listening = 3,

    /// <summary>Loopback traffic (127.0.0.1 / ::1) between processes on this machine.</summary>
    Local = 4
}
