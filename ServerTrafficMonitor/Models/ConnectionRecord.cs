using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ServerTrafficMonitor.Models;

/// <summary>
/// One network connection (TCP or UDP, v4 or v6) seen on this machine.
/// Mutable fields raise PropertyChanged so the live grid updates in place.
/// </summary>
public sealed class ConnectionRecord : INotifyPropertyChanged
{
    // --- Immutable identity (set once at creation) ---
    public required string Key { get; init; }
    public required string Protocol { get; init; }
    public required string LocalAddress { get; init; }
    public required int LocalPort { get; init; }
    public required string RemoteAddress { get; init; }
    public required int RemotePort { get; init; }
    public required int ProcessId { get; init; }
    public required TrafficDirection Direction { get; init; }
    public DateTime FirstSeen { get; init; } = DateTime.Now;

    // --- Mutable, observable fields ---
    private string _processName = "";
    public string ProcessName
    {
        get => _processName;
        set => SetField(ref _processName, value);
    }

    private string _state = "";
    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    private string _remoteHost = "";
    public string RemoteHost
    {
        get => _remoteHost;
        set => SetField(ref _remoteHost, value);
    }

    // Geo-IP of the remote endpoint — filled in asynchronously.
    private string _country = "";
    public string Country
    {
        get => _country;
        set { if (SetField(ref _country, value)) OnPropertyChanged(nameof(CountryDisplay)); }
    }

    private string _countryCode = "";
    public string CountryCode
    {
        get => _countryCode;
        set { if (SetField(ref _countryCode, value)) OnPropertyChanged(nameof(CountryDisplay)); }
    }

    public string CountryDisplay =>
        _country.Length == 0 ? "" :
        _countryCode.Length == 2 ? $"{_country} ({_countryCode})" : _country;

    private DateTime _lastSeen = DateTime.Now;
    public DateTime LastSeen
    {
        get => _lastSeen;
        set { if (SetField(ref _lastSeen, value)) OnPropertyChanged(nameof(DurationText)); }
    }

    private bool _isActive = true;
    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    // --- Computed display helpers ---
    public string LocalEndpoint => FormatEndpoint(LocalAddress, LocalPort);
    public string RemoteEndpoint => FormatEndpoint(RemoteAddress, RemotePort);

    /// <summary>Best label for the remote side: resolved host name if known, else IP.</summary>
    public string RemoteDisplay =>
        string.IsNullOrEmpty(RemoteHost) || RemoteHost == RemoteAddress
            ? RemoteAddress
            : $"{RemoteHost} ({RemoteAddress})";

    public string DirectionText => Direction switch
    {
        TrafficDirection.Inbound => "◀ Inbound",
        TrafficDirection.Outbound => "Outbound ▶",
        TrafficDirection.Listening => "Listening",
        TrafficDirection.Local => "Local",
        _ => "Unknown"
    };

    public string DurationText
    {
        get
        {
            var span = LastSeen - FirstSeen;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            return span.TotalSeconds < 1 ? "<1s"
                 : span.TotalMinutes < 1 ? $"{span.Seconds}s"
                 : span.TotalHours < 1 ? $"{span.Minutes}m {span.Seconds}s"
                 : $"{(int)span.TotalHours}h {span.Minutes}m";
        }
    }

    private static string FormatEndpoint(string addr, int port)
        => addr.Contains(':') ? $"[{addr}]:{port}" : $"{addr}:{port}";

    // --- INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
