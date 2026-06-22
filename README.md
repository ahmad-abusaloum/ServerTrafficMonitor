# Server Traffic Monitor 🛰️

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

A Windows desktop tool that shows **every inbound and outbound connection on a server**, in real time — across **all** processes, not just one app. Think of it as a Fiddler-style view of the whole machine, but with a different approach: instead of acting as a proxy, it reads directly from the Windows networking stack (the TCP/UDP tables and ETW), so it sees the full picture **without any change to your application's code**.

Built for a very common need: *"My API is hosted on IIS. I want to see the requests coming **into** it, and the calls it makes **out** to other APIs — the URL, the status, the port, everything."*

---

## ✨ Features

### 🔌 Connections — all traffic
Every TCP/UDP socket (IPv4 and IPv6) on the machine, refreshed every second:

- **Direction** — Inbound ◀ / Outbound ▶ / Listening / Local (colour-coded)
- **Process & PID** — the program that owns the connection (e.g. `w3wp` for IIS)
- **Local** and **Remote** endpoints, with **reverse-DNS host names**
- **Port**, **TCP state** (Established / TimeWait / Listen …), and **duration**

### 🌐 Inbound HTTP (IIS / HTTP.sys)
Every HTTP request that reaches IIS on this server, with full detail:
**colour-coded status code** (2xx green, 4xx amber, 5xx red), method, **full URL**, client endpoint and processing time.

### 📡 Outbound HTTP (App → APIs)
Every HTTP request that .NET apps on the server make via `HttpClient`
(e.g. your API calling third-party APIs): **full target URL**, **status code**, host,
the **calling process**, and round-trip duration — **no proxy, no TLS certificate, no code changes.**

### 🔎 Filtering
Filter connections by free text (host / IP / process), port, direction, protocol, or "active only".
Filter the HTTP tabs by URL, host, status or process.

---

## 🧠 How it works

| Data source | What it provides |
|---|---|
| **IP Helper API** (`GetExtendedTcpTable` / `GetExtendedUdpTable`) | A full snapshot of every TCP/UDP socket with its owning process, polled every second. Always available. |
| **Kernel Network ETW** | Real-time capture of every **outbound** TCP connect the instant it happens, so fast/short-lived calls are never missed. |
| **HTTP.sys ETW** (`Microsoft-Windows-HttpService`) | **Inbound** HTTP requests (URL + status) from the same stack IIS runs on. |
| **`System.Net.Http` ETW** | **Outbound** HTTP requests (URL + status) emitted by the .NET runtime's own telemetry. |

All ETW sources require the app to run **as Administrator** (it requests elevation automatically).

---

## 🚀 Getting started

### Option A — Run the prebuilt single file (recommended)
A self-contained executable that bundles the .NET runtime — **nothing to install on the server**:

```
ServerTrafficMonitor/bin/Release/net9.0-windows/win-x64/publish/ServerTrafficMonitor.exe
```

Copy it to the server and run it (it will prompt for Administrator).

### Option B — Build from source

```powershell
git clone https://github.com/ahmad-abusaloum/ServerTrafficMonitor.git
cd ServerTrafficMonitor

# build
dotnet build ServerTrafficMonitor/ServerTrafficMonitor.csproj -c Release

# run the produced exe directly (it requests Administrator via its manifest)
./ServerTrafficMonitor/bin/Release/net9.0-windows/win-x64/ServerTrafficMonitor.exe
```

> Run the produced `.exe` rather than `dotnet run`, so Windows can elevate it (ETW capture needs Administrator).

### Produce the single-file build yourself

```powershell
dotnet publish ServerTrafficMonitor/ServerTrafficMonitor.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

---

## 📋 Typical scenario: an API on IIS

- **Requests coming into your API** → open the **Inbound HTTP** tab for the URL and status of each request. Type `w3wp` in the Connections search to isolate IIS traffic.
- **Requests your API sends to other APIs** → open the **Outbound HTTP** tab to see the full URL and status of every outgoing call (filter by the target domain or by the `w3wp` process).

---

## ⚠️ Capture limits

| | Inbound | Outbound |
|---|---|---|
| Full URL | ✅ (HTTP.sys) | ✅ for .NET apps (`System.Net.Http`) |
| Status code | ✅ | ✅ (.NET 8+) |
| Connection (IP / port / state) | ✅ | ✅ |
| HTTP method (GET/POST) | ✅ | ❌ (not exposed by the runtime) |
| Request/response body | ❌ | ❌ |

- Outbound HTTP capture works for **.NET** apps using `HttpClient`. Non-.NET apps (Node/Java/…) won't appear in the Outbound HTTP tab, but their connections still show in the Connections tab.
- The tool only sees traffic of processes on the **same machine** it runs on.
- Capturing the request/response **body** (full Fiddler parity) would require running an internal proxy with a TLS root certificate — a possible future addition.

---

## 🗂️ Project structure

```
ServerTrafficMonitor/
├─ Native/IpHelper.cs            # TCP/UDP tables via iphlpapi.dll (P/Invoke)
├─ Models/                       # ConnectionRecord, HttpRecord, TrafficDirection
├─ Services/
│  ├─ ConnectionMonitor.cs       # 1-second connection-table polling
│  ├─ KernelNetMonitor.cs        # ETW: real-time outbound connects
│  ├─ HttpEtwMonitor.cs          # ETW: inbound HTTP (URL + status)
│  ├─ OutboundHttpEtwMonitor.cs  # ETW: outbound HTTP (URL + status)
│  ├─ ConnectionClassifier.cs    # direction + TCP-state logic
│  ├─ DnsResolver.cs             # cached reverse DNS
│  └─ ProcessResolver.cs         # PID → process name
├─ ViewModels/MainViewModel.cs   # aggregation, filtering, commands
├─ MainWindow.xaml               # UI (three tabs + filters)
└─ app.manifest                  # requests Administrator
```

---

## 🧩 Requirements

- Windows 10 / 11 / Server 2016 or later
- **Administrator** privileges (required for ETW and for reading every connection's PID)
- No .NET installation needed when using the self-contained build

---

## 📄 License

Released under the [MIT License](LICENSE).

## 👤 Author

**Ahmad Abu Saloum**
