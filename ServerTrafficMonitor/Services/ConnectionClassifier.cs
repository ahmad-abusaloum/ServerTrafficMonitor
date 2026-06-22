using System.Net;
using ServerTrafficMonitor.Models;
using ServerTrafficMonitor.Native;

namespace ServerTrafficMonitor.Services;

/// <summary>
/// Turns raw OS connection rows into a direction + human-readable state.
/// </summary>
public static class ConnectionClassifier
{
    private const int TcpListen = 2;

    public static string StateText(string protocol, int state)
    {
        if (protocol.StartsWith("UDP", StringComparison.Ordinal))
            return "—";

        return state switch
        {
            1 => "Closed",
            2 => "Listen",
            3 => "SynSent",
            4 => "SynReceived",
            5 => "Established",
            6 => "FinWait1",
            7 => "FinWait2",
            8 => "CloseWait",
            9 => "Closing",
            10 => "LastAck",
            11 => "TimeWait",
            12 => "DeleteTcb",
            _ => $"State({state})"
        };
    }

    /// <summary>
    /// Builds the set of local TCP ports that something on this machine is LISTENing on.
    /// A connection whose local port is in this set was accepted by us => inbound.
    /// </summary>
    public static HashSet<int> BuildListeningPorts(IReadOnlyList<RawConnection> snapshot)
    {
        var ports = new HashSet<int>();
        foreach (var c in snapshot)
            if (c.Protocol.StartsWith("TCP", StringComparison.Ordinal) && c.State == TcpListen)
                ports.Add(c.LocalPort);
        return ports;
    }

    public static TrafficDirection Classify(in RawConnection c, HashSet<int> listeningPorts)
    {
        bool isTcp = c.Protocol.StartsWith("TCP", StringComparison.Ordinal);

        if (isTcp && c.State == TcpListen)
            return TrafficDirection.Listening;

        bool hasRemote = c.RemotePort != 0 && !IsZero(c.RemoteAddress);

        // UDP with no peer is just a bound socket.
        if (!hasRemote)
            return listeningPorts.Contains(c.LocalPort) || !isTcp
                ? TrafficDirection.Listening
                : TrafficDirection.Outbound;

        if (IPAddress.IsLoopback(c.LocalAddress) && IPAddress.IsLoopback(c.RemoteAddress))
            return TrafficDirection.Local;

        // The decisive heuristic: did the remote connect to a port we listen on?
        if (listeningPorts.Contains(c.LocalPort))
            return TrafficDirection.Inbound;

        return TrafficDirection.Outbound;
    }

    private static bool IsZero(IPAddress ip)
    {
        foreach (var b in ip.GetAddressBytes())
            if (b != 0) return false;
        return true;
    }
}
