# TrFileTransfer

LAN file transfer tool with SHA-256 integrity verification. Windows GUI (WinForms).

## Features

- **TCP** — connection-oriented, firewall-friendly
- **UDP** — reliable transfer via Go-Back-N ARQ (sliding window, retransmit, SHA-256)
- **Single file** or **folder** (recursive, preserving directory structure)
- **English / 中文** switchable UI
- Progress bar, speed display, ETA

## Quick Start

### Prerequisites

- Windows 7 SP1 or later
- .NET Framework 4.5 or later (4.6.1+ recommended)

### Build

```
cd TrFileTransfer
build.bat
```

Produces `TrFileTransfer.exe`. No Visual Studio required — uses the in-box `csc.exe` compiler.

### Usage

1. **Receiver** — select *Server*, choose TCP or UDP, pick a save directory, click *Start Server*
2. **Sender** — select *Client*, enter the server's IP, browse for a file or folder, click *Send*
3. Check *Folder mode* to send entire directories

## Protocol

See [WIRE-PROTOCOL.md](WIRE-PROTOCOL.md) for the complete TCP and UDP wire specification.

## Architecture

| File | Role |
|------|------|
| `Program.cs` | Entry point |
| `MainForm.cs` | WinForms UI (programmatic, no designer) |
| `TransferClient.cs` | TCP sender |
| `TransferServer.cs` | TCP receiver |
| `TransferUdpClient.cs` | Reliable UDP sender (Go-Back-N) |
| `TransferUdpServer.cs` | Reliable UDP receiver |
| `UdpProtocol.cs` | UDP wire constants and packet helpers |
| `Shared.cs` | `TransferProgress`, `Utils`, `FileEntry` |
| `L10N.cs` | English / Chinese strings |

## License

MIT
