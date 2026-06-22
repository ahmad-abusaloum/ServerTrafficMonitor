using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ServerTrafficMonitor.Models;

/// <summary>
/// One inbound HTTP request observed via the HTTP.sys ETW provider
/// (i.e. a request that reached IIS / Kestrel-behind-HTTP.sys on this server).
/// Request fields are known on arrival; status/duration are filled in on response.
/// </summary>
public sealed class HttpRecord : INotifyPropertyChanged
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string ClientAddress { get; init; } = "";
    public int ClientPort { get; init; }

    // Used by the outbound view: which local process made the call, and the target host.
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string Host { get; init; } = "";
    public string ProcessDisplay =>
        ProcessId == 0 && ProcessName.Length == 0 ? "" : $"{ProcessName} ({ProcessId})";

    /// <summary>ETW activity id, used to correlate the start with its completion.</summary>
    public Guid CorrelationId { get; init; }

    private string _method = "";
    public string Method { get => _method; set => SetField(ref _method, value); }

    private string _url = "";
    public string Url { get => _url; set => SetField(ref _url, value); }

    private int _statusCode;
    public int StatusCode
    {
        get => _statusCode;
        set
        {
            if (SetField(ref _statusCode, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusCategory));
            }
        }
    }

    /// <summary>Coarse status bucket used purely for colour-coding the grid.</summary>
    public string StatusCategory => _statusCode switch
    {
        < 0 => "ServerError",         // failed request
        0 => "Pending",
        >= 200 and < 300 => "Success",
        >= 300 and < 400 => "Redirect",
        >= 400 and < 500 => "ClientError",
        >= 500 => "ServerError",
        _ => "Other"
    };

    private double _durationMs;
    public double DurationMs
    {
        get => _durationMs;
        set { if (SetField(ref _durationMs, value)) OnPropertyChanged(nameof(DurationText)); }
    }

    public string ClientEndpoint =>
        string.IsNullOrEmpty(ClientAddress) ? "" :
        ClientAddress.Contains(':') ? $"[{ClientAddress}]:{ClientPort}" : $"{ClientAddress}:{ClientPort}";

    public string StatusText => _statusCode == 0 ? "…" : _statusCode < 0 ? "FAIL" : _statusCode.ToString();

    public string DurationText => _durationMs <= 0 ? "" : $"{_durationMs:0} ms";

    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

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
