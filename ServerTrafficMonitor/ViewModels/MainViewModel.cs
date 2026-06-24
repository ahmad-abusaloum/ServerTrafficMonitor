using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ServerTrafficMonitor.Models;
using ServerTrafficMonitor.Native;
using ServerTrafficMonitor.Services;

namespace ServerTrafficMonitor.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private const int MaxConnectionRows = 10_000;
    private const int MaxHttpRows = 5_000;

    private readonly Dispatcher _dispatcher;
    private readonly ConnectionMonitor _connMonitor;
    private readonly HttpEtwMonitor _httpMonitor;
    private readonly KernelNetMonitor _kernelMonitor;
    private readonly OutboundHttpEtwMonitor _outHttpMonitor;
    private readonly ProcessResolver _proc = new();
    private readonly DnsResolver _dns = new();
    private readonly GeoIpResolver _geo = new();

    // Our own process — excluded so the tool's own DNS / Geo lookups don't appear as traffic.
    private static readonly int OwnPid = Environment.ProcessId;

    private readonly Dictionary<string, ConnectionRecord> _activeByKey = new();
    private readonly Dictionary<Guid, HttpRecord> _outPending = new();

    public ObservableCollection<ConnectionRecord> Connections { get; } = new();
    public ObservableCollection<HttpRecord> HttpRequests { get; } = new();
    public ObservableCollection<HttpRecord> OutboundHttp { get; } = new();

    public ICollectionView ConnectionsView { get; }
    public ICollectionView HttpView { get; }
    public ICollectionView OutboundHttpView { get; }

    public string[] DirectionOptions { get; } = { "All", "Inbound", "Outbound", "Listening", "Local" };
    public string[] ProtocolOptions { get; } = { "All", "TCP", "UDP" };

    public ICommand TogglePauseCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ClearHttpCommand { get; }
    public ICommand ClearOutboundHttpCommand { get; }
    public ICommand ExportCommand { get; }

    public MainViewModel()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        ConnectionsView = CollectionViewSource.GetDefaultView(Connections);
        ConnectionsView.Filter = ConnectionFilter;
        EnableLiveFilter(ConnectionsView, nameof(ConnectionRecord.IsActive),
                                          nameof(ConnectionRecord.RemoteHost),
                                          nameof(ConnectionRecord.State));

        HttpView = CollectionViewSource.GetDefaultView(HttpRequests);
        HttpView.Filter = HttpFilter;

        OutboundHttpView = CollectionViewSource.GetDefaultView(OutboundHttp);
        OutboundHttpView.Filter = OutboundHttpFilter;

        IsElevated = CheckElevated();

        _connMonitor = new ConnectionMonitor(TimeSpan.FromSeconds(1));
        _connMonitor.SnapshotReady += OnSnapshot;
        _connMonitor.Error += ex => Post(() => StatusMessage = "Connection poll error: " + ex.Message);

        _httpMonitor = new HttpEtwMonitor();
        _httpMonitor.RequestCaptured += rec => Post(() => AddHttp(rec));
        _httpMonitor.StatusChanged += s => Post(() => HttpStatus = s);

        _kernelMonitor = new KernelNetMonitor();
        _kernelMonitor.OutboundConnected += o => Post(() => OnOutboundConnect(o));
        _kernelMonitor.StatusChanged += s => Post(() => RealtimeStatus = s);

        _outHttpMonitor = new OutboundHttpEtwMonitor();
        _outHttpMonitor.RequestStarted += (id, rec) => Post(() => OnOutboundStarted(id, rec));
        _outHttpMonitor.RequestCompleted += (id, status, stop) => Post(() => OnOutboundCompleted(id, status, stop));
        _outHttpMonitor.StatusChanged += s => Post(() => OutboundHttpStatus = s);

        TogglePauseCommand = new RelayCommand(TogglePause);
        ClearCommand = new RelayCommand(ClearConnections);
        ClearHttpCommand = new RelayCommand(() => { HttpRequests.Clear(); UpdateStats(); });
        ClearOutboundHttpCommand = new RelayCommand(() => { OutboundHttp.Clear(); UpdateStats(); });
        ExportCommand = new RelayCommand(ExportCsv);
    }

    public void Start()
    {
        _connMonitor.Start();
        _httpMonitor.Start();
        _kernelMonitor.Start();
        _outHttpMonitor.Start();
        StatusMessage = "Monitoring…";
    }

    // ---------------------------------------------------------------- Snapshot reconcile
    private void OnSnapshot(IReadOnlyList<RawConnection> snap) => Post(() => Reconcile(snap));

    private void Reconcile(IReadOnlyList<RawConnection> snap)
    {
        var listening = ConnectionClassifier.BuildListeningPorts(snap);
        var now = DateTime.Now;
        var seen = new HashSet<string>(snap.Count);

        foreach (var c in snap)
        {
            if (c.ProcessId == OwnPid) continue; // hide the monitor's own sockets

            string key = MakeKey(c);
            if (!seen.Add(key)) continue; // ignore duplicate tuples within one snapshot

            if (_activeByKey.TryGetValue(key, out var existing))
            {
                existing.State = ConnectionClassifier.StateText(c.Protocol, c.State);
                existing.LastSeen = now;
                existing.IsActive = true;
                continue;
            }

            var rec = new ConnectionRecord
            {
                Key = key,
                Protocol = c.Protocol,
                LocalAddress = c.LocalAddress.ToString(),
                LocalPort = c.LocalPort,
                RemoteAddress = c.RemoteAddress.ToString(),
                RemotePort = c.RemotePort,
                ProcessId = c.ProcessId,
                Direction = ConnectionClassifier.Classify(c, listening),
                FirstSeen = now,
                ProcessName = _proc.GetName(c.ProcessId),
                State = ConnectionClassifier.StateText(c.Protocol, c.State),
            };

            var host = _dns.TryGetOrResolve(c.RemoteAddress, h => Post(() => rec.RemoteHost = h));
            if (host != null) rec.RemoteHost = host;

            var geo = _geo.TryGetOrResolve(rec.RemoteAddress,
                (code, country) => Post(() => { rec.CountryCode = code; rec.Country = country; }));
            if (geo is { } g) { rec.CountryCode = g.code; rec.Country = g.country; }

            _activeByKey[key] = rec;
            Connections.Insert(0, rec);
            TrimConnections();
        }

        // Anything previously active that vanished is now closed.
        if (_activeByKey.Count > seen.Count)
        {
            var gone = new List<string>();
            foreach (var kvp in _activeByKey)
                if (!seen.Contains(kvp.Key)) gone.Add(kvp.Key);

            foreach (var key in gone)
            {
                var rec = _activeByKey[key];
                rec.IsActive = false;
                rec.State = "Closed";
                rec.LastSeen = now;
                _activeByKey.Remove(key);
            }
        }

        LastUpdateText = now.ToString("HH:mm:ss");
        UpdateStats();
    }

    private void TrimConnections()
    {
        while (Connections.Count > MaxConnectionRows)
        {
            var oldest = Connections[^1];
            Connections.RemoveAt(Connections.Count - 1);
            _activeByKey.Remove(oldest.Key);
        }
    }

    private void AddHttp(HttpRecord rec)
    {
        HttpRequests.Insert(0, rec);
        while (HttpRequests.Count > MaxHttpRows)
            HttpRequests.RemoveAt(HttpRequests.Count - 1);
        UpdateStats();
    }

    private void OnOutboundStarted(Guid id, HttpRecord rec)
    {
        if (rec.ProcessId == OwnPid) return; // hide the monitor's own outbound calls

        OutboundHttp.Insert(0, rec);
        if (id != Guid.Empty) _outPending[id] = rec;

        while (OutboundHttp.Count > MaxHttpRows)
        {
            var removed = OutboundHttp[^1];
            OutboundHttp.RemoveAt(OutboundHttp.Count - 1);
            if (removed.CorrelationId != Guid.Empty) _outPending.Remove(removed.CorrelationId);
        }
        UpdateStats();
    }

    private void OnOutboundCompleted(Guid id, int status, DateTime stop)
    {
        if (id != Guid.Empty && _outPending.Remove(id, out var rec))
        {
            rec.DurationMs = (stop - rec.Timestamp).TotalMilliseconds;
            rec.StatusCode = status; // updates colour + text live
        }
    }

    private static string MakeKey(in RawConnection c)
        => MakeKey(c.Protocol, c.LocalAddress.ToString(), c.LocalPort, c.RemoteAddress.ToString(), c.RemotePort);

    private static string MakeKey(string protocol, string local, int lport, string remote, int rport)
        => $"{protocol}|{local}:{lport}|{remote}:{rport}";

    /// <summary>
    /// A real-time outbound connect arrived from the kernel ETW feed. Add it immediately
    /// (so fast calls are never missed); polling then maintains its state and closure.
    /// </summary>
    private void OnOutboundConnect(OutboundConnection o)
    {
        if (o.ProcessId == OwnPid) return; // hide the monitor's own connections

        string key = MakeKey(o.Protocol, o.LocalAddress, o.LocalPort, o.RemoteAddress, o.RemotePort);
        if (_activeByKey.ContainsKey(key)) return; // already tracked via polling

        var now = DateTime.Now;
        var rec = new ConnectionRecord
        {
            Key = key,
            Protocol = o.Protocol,
            LocalAddress = o.LocalAddress,
            LocalPort = o.LocalPort,
            RemoteAddress = o.RemoteAddress,
            RemotePort = o.RemotePort,
            ProcessId = o.ProcessId,
            Direction = TrafficDirection.Outbound,
            FirstSeen = now,
            ProcessName = _proc.GetName(o.ProcessId),
            State = "Established",
        };

        if (System.Net.IPAddress.TryParse(o.RemoteAddress, out var ip))
        {
            var host = _dns.TryGetOrResolve(ip, h => Post(() => rec.RemoteHost = h));
            if (host != null) rec.RemoteHost = host;
        }

        var geo = _geo.TryGetOrResolve(rec.RemoteAddress,
            (code, country) => Post(() => { rec.CountryCode = code; rec.Country = country; }));
        if (geo is { } g) { rec.CountryCode = g.code; rec.Country = g.country; }

        _activeByKey[key] = rec;
        Connections.Insert(0, rec);
        TrimConnections();
        UpdateStats();
    }

    // ---------------------------------------------------------------- Commands
    private void TogglePause()
    {
        _connMonitor.Paused = !_connMonitor.Paused;
        IsPaused = _connMonitor.Paused;
        StatusMessage = IsPaused ? "Paused" : "Monitoring…";
    }

    private void ClearConnections()
    {
        Connections.Clear();
        _activeByKey.Clear();
        UpdateStats();
    }

    private void ExportCsv()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = "connections-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Time,Direction,Protocol,Process,PID,Local,RemoteIP,RemoteHost,Country,RemotePort,State,Duration");
        foreach (ConnectionRecord r in ConnectionsView)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(r.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(r.Direction.ToString()),
                Csv(r.Protocol),
                Csv(r.ProcessName),
                Csv(r.ProcessId.ToString()),
                Csv(r.LocalEndpoint),
                Csv(r.RemoteAddress),
                Csv(r.RemoteHost),
                Csv(r.CountryDisplay),
                Csv(r.RemotePort.ToString()),
                Csv(r.State),
                Csv(r.DurationText),
            }));
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        StatusMessage = "Exported to " + dlg.FileName;
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    // ---------------------------------------------------------------- Filtering
    private bool ConnectionFilter(object o)
    {
        if (o is not ConnectionRecord r) return false;
        if (_activeOnly && !r.IsActive) return false;
        if (_selectedDirection != "All" && r.Direction.ToString() != _selectedDirection) return false;
        if (_selectedProtocol == "TCP" && !r.Protocol.StartsWith("TCP", StringComparison.Ordinal)) return false;
        if (_selectedProtocol == "UDP" && !r.Protocol.StartsWith("UDP", StringComparison.Ordinal)) return false;

        if (!string.IsNullOrWhiteSpace(_portFilter))
        {
            var pf = _portFilter.Trim();
            if (!r.LocalPort.ToString().Contains(pf) && !r.RemotePort.ToString().Contains(pf)) return false;
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var s = _searchText.Trim();
            if (IndexOf(r.RemoteHost, s) < 0 && IndexOf(r.RemoteAddress, s) < 0 &&
                IndexOf(r.LocalAddress, s) < 0 && IndexOf(r.ProcessName, s) < 0)
                return false;
        }
        return true;
    }

    private bool HttpFilter(object o)
    {
        if (o is not HttpRecord r) return false;
        if (string.IsNullOrWhiteSpace(_httpSearch)) return true;
        var s = _httpSearch.Trim();
        return IndexOf(r.Url, s) >= 0 || IndexOf(r.Method, s) >= 0 ||
               r.StatusCode.ToString().Contains(s);
    }

    private bool OutboundHttpFilter(object o)
    {
        if (o is not HttpRecord r) return false;
        if (string.IsNullOrWhiteSpace(_outboundHttpSearch)) return true;
        var s = _outboundHttpSearch.Trim();
        return IndexOf(r.Url, s) >= 0 || IndexOf(r.Host, s) >= 0 ||
               IndexOf(r.ProcessName, s) >= 0 || r.StatusCode.ToString().Contains(s);
    }

    private static int IndexOf(string hay, string needle)
        => hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- Bound state
    private string _searchText = "";
    public string SearchText { get => _searchText; set { if (SetField(ref _searchText, value)) ConnectionsView.Refresh(); } }

    private string _portFilter = "";
    public string PortFilter { get => _portFilter; set { if (SetField(ref _portFilter, value)) ConnectionsView.Refresh(); } }

    private string _selectedDirection = "All";
    public string SelectedDirection { get => _selectedDirection; set { if (SetField(ref _selectedDirection, value)) ConnectionsView.Refresh(); } }

    private string _selectedProtocol = "All";
    public string SelectedProtocol { get => _selectedProtocol; set { if (SetField(ref _selectedProtocol, value)) ConnectionsView.Refresh(); } }

    private bool _activeOnly;
    public bool ActiveOnly { get => _activeOnly; set { if (SetField(ref _activeOnly, value)) ConnectionsView.Refresh(); } }

    private string _httpSearch = "";
    public string HttpSearch { get => _httpSearch; set { if (SetField(ref _httpSearch, value)) HttpView.Refresh(); } }

    private string _outboundHttpSearch = "";
    public string OutboundHttpSearch { get => _outboundHttpSearch; set { if (SetField(ref _outboundHttpSearch, value)) OutboundHttpView.Refresh(); } }

    private bool _isPaused;
    public bool IsPaused { get => _isPaused; set { SetField(ref _isPaused, value); OnPropertyChanged(nameof(PauseLabel)); } }
    public string PauseLabel => _isPaused ? "▶  Resume" : "⏸  Pause";

    private string _statusMessage = "Starting…";
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    private string _httpStatus = "HTTP capture: starting…";
    public string HttpStatus { get => _httpStatus; set => SetField(ref _httpStatus, value); }

    private string _realtimeStatus = "Real-time capture: starting…";
    public string RealtimeStatus { get => _realtimeStatus; set => SetField(ref _realtimeStatus, value); }

    private string _outboundHttpStatus = "Outbound HTTP: starting…";
    public string OutboundHttpStatus { get => _outboundHttpStatus; set => SetField(ref _outboundHttpStatus, value); }

    private string _lastUpdateText = "—";
    public string LastUpdateText { get => _lastUpdateText; set => SetField(ref _lastUpdateText, value); }

    private string _stats = "";
    public string Stats { get => _stats; set => SetField(ref _stats, value); }

    // Selected rows -> drive the details panels in each tab.
    private ConnectionRecord? _selectedConnection;
    public ConnectionRecord? SelectedConnection { get => _selectedConnection; set => SetField(ref _selectedConnection, value); }

    private HttpRecord? _selectedHttp;
    public HttpRecord? SelectedHttp { get => _selectedHttp; set => SetField(ref _selectedHttp, value); }

    private HttpRecord? _selectedOutbound;
    public HttpRecord? SelectedOutbound { get => _selectedOutbound; set => SetField(ref _selectedOutbound, value); }

    public bool IsElevated { get; }
    public bool ShowElevationWarning => !IsElevated;

    private void UpdateStats()
    {
        int inbound = 0, outbound = 0;
        foreach (var r in _activeByKey.Values)
        {
            if (r.Direction == TrafficDirection.Inbound) inbound++;
            else if (r.Direction == TrafficDirection.Outbound) outbound++;
        }
        Stats = $"Rows: {Connections.Count:N0}   •   Active: {_activeByKey.Count:N0}   " +
                $"(Inbound {inbound:N0} / Outbound {outbound:N0})   •   " +
                $"HTTP in: {HttpRequests.Count:N0} / out: {OutboundHttp.Count:N0}";
    }

    // ---------------------------------------------------------------- Helpers
    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    private static void EnableLiveFilter(ICollectionView view, params string[] properties)
    {
        if (view is ICollectionViewLiveShaping live && live.CanChangeLiveFiltering)
        {
            foreach (var p in properties) live.LiveFilteringProperties.Add(p);
            live.IsLiveFiltering = true;
        }
    }

    private static bool CheckElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _connMonitor.Dispose();
        _httpMonitor.Dispose();
        _kernelMonitor.Dispose();
        _outHttpMonitor.Dispose();
    }
}
