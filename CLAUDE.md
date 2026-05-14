# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build

```
# From TrFileTransfer/ directory, run:
build.bat
```

This invokes `csc.exe` from the installed .NET Framework 4.x to produce `TrFileTransfer.exe`. References: `System.dll`, `System.Core.dll`, `System.Windows.Forms.dll`, `System.Drawing.dll`. No Visual Studio or MSBuild required — only the .NET Framework runtime (which includes `csc.exe`).

The compiler is C# 5 (max language version supported by the in-box `csc.exe`), but the produced exe runs on .NET Framework 4.5+ (4.6.1 recommended). Avoid C# 6+ syntax: no string interpolation (`$`), no expression-bodied members, no null-conditional (`?.`), no exception filters (`when`), no digit separators.

## Architecture

Nine source files compiled into a single WinForms exe:

- **Program.cs** — Entry point. `[STAThread]` Main that launches `MainForm`.
- **MainForm.cs** — GUI. Mode selector (Server/Client), protocol selector (TCP/UDP), server bind-address dropdown (populated from active NICs), folder mode checkbox (client only), progress bar, speed/ETA display, scrolled log. All UI constructed programmatically (no designer). Language dropdown in top-right corner. Log capped at 500 entries, progress events throttled at ~100ms. Event wiring is extracted into `WireClientEvents`/`WireUdpClientEvents` helpers. When the server completes a transfer, the UI stays in "Listening..." state (not re-enabling all inputs) — the server is still running and accepting new transfers. `ResetClientUI` resets client controls and status text to "Ready".
- **TransferServer.cs** — Async TCP server. Binds to a configurable address+port, accepts one connection at a time. `HandleClient` reads a 1-byte type prefix and dispatches to `HandleFileTransfer` (single file) or `HandleFolderTransfer` (folder with per-file relative paths and SHA256). Uses `Task.Factory.StartNew(LongRunning)` for accept loop. Sets `NoDelay = true` and 256KB buffer sizes.
- **TransferClient.cs** — Async TCP client. `SendAsync()` for single files, `SendFolderAsync()` for folders. Both delegate to a shared `RunTransfer` wrapper for lifecycle management. Sends file metadata + data + per-file SHA256 hash. Sets `NoDelay = true` and 256KB buffer sizes. The `SendFilePayload` helper is shared by both single and folder transfers.
- **TransferUdpServer.cs** — Reliable UDP server. Always listens on all interfaces (no bind-address parameter — unlike TCP server, the constructor only takes `port` and `saveDirectory`). `HandleTransfer` parses HELLO body type byte: `0x00` = single file (sanitizes filename), `0x01` = folder file (preserves relative path, creates subdirectories via `Utils.SanitizeRelativePath`). Go-Back-N ARQ. The inner receive loop runs until FIN is processed (not just until all data chunks arrive), ensuring the hash is verified and FIN_ACK is sent before the method returns. If a new HELLO or FolderEnd packet arrives while still in the loop, it is saved to `_pendingPacketData`/`_pendingPacketEp` and dispatched by `ReceiveLoop` on the next iteration — no packet is ever silently consumed. 1MB socket buffers.
- **TransferUdpClient.cs** — Reliable UDP client. `SendAsync()` for single files, `SendFolderAsync()` for folders. Both delegate to a shared `RunUdpTransfer` wrapper. Uses sliding window (32 chunks × 32 KB), timeout-based retransmission, Go-Back-N on packet loss. `WaitForAckAsync()` drains stale non-ACK packets and returns when ACK(seq=0) arrives. `SendUdpFileDataAsync()` has a `reportProgress` parameter — false during folder transfers to suppress per-file progress events (avoiding progress bar jumping). Folder transfer pre-computes file sizes into a `FileEntry` struct to avoid duplicate `FileInfo` creation. `BuildHelloBody` helper constructs the HELLO body shared by single-file and folder modes. After all folder files are sent, a `TypeFolderEnd` packet is sent and the client waits for server ACK before firing `OnTransferComplete`. 1MB socket buffers.
- **UdpProtocol.cs** — Shared UDP wire protocol constants and helpers. Packet format and types defined here.
- **Shared.cs** — `TransferProgress` class, `Utils` static helpers (`FormatSize`, `ConstantTimeEquals`, `LogTo`, `SanitizeRelativePath`, `GetUniqueSavePath`). All four transfer files use these instead of duplicating.
- **L10N.cs** — Localization strings. Static `L` class with properties that return English or Chinese text based on `L.IsChinese`. See "Localization conventions" below for naming rules.

### Localization conventions

The GUI has a language dropdown (English / 中文) that sets `L.IsChinese`. All localized strings live in `L10N.cs`. Naming conventions by consumer:
- `L.S_*` — TCP server log messages
- `L.C_*` — TCP client log messages
- `L.UdpS_*` — UDP server log messages
- `L.UdpC_*` — UDP client log messages
- All other `L.*` properties — MainForm UI strings

When adding new log messages, add them to `L10N.cs` following these conventions. Never use inline `L.IsChinese ? "..." : "..."` ternaries. `PopulateBindAddresses()` re-reads NICs and updates the first-item label when language changes. Language switch is blocked during active transfers (the combo box is disabled).

### File name collision resolution

Both TCP and UDP servers use `Utils.GetUniqueSavePath` to append `_1`, `_2`, etc. to the base filename (before the extension) when a file or directory with the same name already exists in the save directory.

### Thread safety pattern

All four transfer classes use a `volatile bool _isRunning` flag and `CancellationTokenSource`. UI event handlers are marshaled via `this.Invoke()` in `MainForm.cs`. Local variable snapshot pattern is used before raising events: `var handler = OnXxx; if (handler != null) handler(...);`.

## TCP wire protocol

All transfers begin with a 1-byte type: `0x00` = single file, `0x01` = folder.

### Single file (type 0x00)

```
[1 byte:  0x00]
[8 bytes: Int64 fileSize, little-endian]
[4 bytes: Int32 fileNameBytesLength]
[N bytes: UTF-8 fileName]
[M bytes: file content (fileSize bytes)]
[32 bytes: SHA256 hash of file content]
```

### Folder (type 0x01)

```
[1 byte:  0x01]
[2 bytes: Int16 folderNameBytesLength]
[N bytes: UTF-8 folderName]
[4 bytes: Int32 fileCount]
For each file:
    [8 bytes: Int64 fileSize]
    [2 bytes: Int16 relativePathBytesLength]
    [N bytes: UTF-8 relativePath (relative to folder root, e.g. "subdir/file.txt")]
    [M bytes: file content (fileSize bytes)]
    [32 bytes: SHA256 hash of this file's content]
```

The server creates subdirectories in the save path as needed from each file's relative path. If a file's SHA256 fails, the entire folder transfer is aborted. File name collision handling appends `_1`, `_2` to the folder save directory name.

## UDP wire protocol

Packet header (14 bytes): `magic(4) + type(1) + reserved(1) + seq(4) + bodyLen(4)` where magic = `0x55445054`.

| Type | Value | Body |
|---|---|---|
| HELLO | 0 | transferType(1) + fileSize(8) + nameLen(2) + fileName(N) |
| DATA | 1 | file chunk (≤ 32768 bytes) |
| ACK | 2 | empty (cumulative: highest consecutive seq received) |
| FIN | 3 | SHA256 hash (32 bytes) |
| FIN_ACK | 4 | empty |
| FOLDER_END | 5 | empty |

HELLO body transferType: `0x00` = single file (server sanitizes filename with `Path.GetFileName`), `0x01` = folder file (server preserves relative path, creates subdirectories).

Flow (single file): HELLO → HELLO_ACK (regular ACK packet with seq=0) → DATA[0..N] ↔ ACK[0..N] → FIN → FIN_ACK. Window size 32, timeout 3 s, max 15 retries. Server only ACKs in-order packets; client retransmits from last ACK on timeout (Go-Back-N).

For folder transfers over UDP, the client sends each file as a separate HELLO→DATA→FIN→FIN_ACK session (one per file) with `transferType=0x01` and the relative path (prepended with the source folder name, e.g. `"a\subdir\file.txt"`) as fileName. The server handles each independently, creating subdirectories as needed. After all files are sent, the client sends a `TypeFolderEnd` packet; the server responds with ACK(seq=0) and fires `OnTransferComplete`.

FIN_ACK is dual-purpose: server responds with `TypeFinAck` on hash match, or `TypeFin` (with 1-byte body) on hash failure after FIN. Client retries FIN up to 5 times.

## Edge cases & exception handling

- `ObjectDisposedException` and `InvalidOperationException` are caught silently in all transfer loops and outer try-catch blocks — these are expected when `Stop()`/`Cancel()` closes a socket while I/O is in flight.
- `SocketException` in UDP inner loops is treated as timeout (retransmit/retry), not a fatal error.
- Bind failure (address not on any NIC) is caught in `TransferServer.Start()` and `TransferUdpServer.Start()` — fires `OnError` + `OnStopped`, UI re-enables controls.
- Language switch is blocked during active transfers.
- `TransferUdpServer.Stop()` calls `UdpClient.Close()` (not `Stop()`) to break out of `ReceiveAsync()`.

## Runtime requirements

- Windows 7 SP1 or later
- .NET Framework 4.5 or later (4.6.1+ recommended for Win7)
