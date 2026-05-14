# TrFileTransfer — Wire Protocol Specification

## TCP Protocol

Both single-file and folder transfers begin with a single type byte.

### Single File (type `0x00`)

```
Offset  Size  Field
------  ----  -----
0       1     Type = 0x00
1       8     File size, Int64 LE
9       4     File name length (bytes), Int32 LE
13      N     File name, UTF-8
13+N    M     File content (fileSize bytes)
13+N+M  32    SHA-256 hash of file content
```

### Folder (type `0x01`)

```
Offset  Size  Field
------  ----  -----
0       1     Type = 0x01
1       2     Folder name length (bytes), Int16 LE
3       N     Folder name, UTF-8
3+N     4     File count, Int32 LE
```

Immediately followed by each file entry:

```
Offset  Size  Field
------  ----  -----
0       8     File size, Int64 LE
8       2     Relative path length (bytes), Int16 LE
10      N     Relative path, UTF-8 (e.g. "subdir/file.txt")
10+N    M     File content (fileSize bytes)
10+N+M  32    SHA-256 hash of this file's content
```

All multi-byte integers are little-endian.

### Server Behaviour

- File names are sanitized with `Path.GetFileName()`; relative paths from
  folder transfers go through `SanitizeRelativePath` (replaces `..` / `.`).
- Name collisions append `_1`, `_2`, ... before the extension.
- If any file's SHA-256 fails, the entire folder transfer is aborted.

---

## UDP Protocol

### Packet Format (fixed 14-byte header)

```
Offset  Size  Field
------  ----  -----
0       4     Magic = 0x55445054 ("UDPT")
4       1     Type (see below)
5       1     Reserved (always 0)
6       4     Sequence number, Int32 LE
10      4     Body length, Int32 LE
14      N     Body (bodyLen bytes)
```

### Packet Types

| Type | Value | Body | Purpose |
|------|-------|------|---------|
| HELLO     | 0 | transferType(1) + fileSize(8) + nameLen(2) + fileName(N) | Initiate a file |
| DATA      | 1 | file chunk (≤ 32768 bytes) | File payload |
| ACK       | 2 | empty | Cumulative ack (highest consecutive seq received) |
| FIN       | 3 | SHA-256 hash (32 bytes) | End-of-file + integrity |
| FIN\_ACK  | 4 | empty | Server confirms hash match |
| FOLDER\_END | 5 | empty | Folder transfer complete |

HELLO body `transferType`: `0x00` = single file (server applies `Path.GetFileName`),
`0x01` = folder file (server preserves relative path, creates subdirectories).

### Flow — Single File

```
Client                          Server
  |                               |
  |--- HELLO(seq=0) ------------>|
  |                               |  Send HELLO_ACK (regular ACK, seq=0)
  |<-- ACK(seq=0) ---------------|
  |                               |
  |--- DATA[0]  ---------------->|
  |--- DATA[1]  ---------------->|
  |       ... (sliding window)    |
  |--- DATA[N]  ---------------->|
  |<-- ACK(seq=K) ---------------|  (cumulative, sent per in-order chunk)
  |       ...                     |
  |                               |
  |--- FIN(sha256) ------------->|
  |                               |  Verify hash; FIN_ACK or FIN
  |<-- FIN_ACK ------------------|
```

### Flow — Folder Transfer

Each file in the folder is sent as an independent HELLO → DATA… → FIN → FIN\_ACK
cycle with `transferType=0x01` and the relative path (prepended with source folder
name, e.g. `"MyFolder\subdir\file.txt"`). After the last file, the client sends a
single FOLDER\_END packet; the server responds with ACK(seq=0) and fires
`OnTransferComplete`.

### Go-Back-N ARQ Parameters

| Parameter | Value |
|-----------|-------|
| Window size | 32 chunks |
| Chunk size | 32 KB (max) |
| Retransmit timeout | 3 s |
| Max retransmits | 15 |
| FIN retries | 5 |
| Socket buffers | 4 MB (send & recv) |

The client sends up to 32 DATA packets in a burst, then waits for a cumulative ACK.
The server only acknowledges in-order chunks. Any gap or timeout triggers Go-Back-N
retransmission from the last acknowledged sequence number.

---

## SHA-256 Integrity

Both protocols append a 32-byte SHA-256 hash of the file content after the data
payload. The hash is computed incrementally during transfer (streaming). Comparison
uses a constant-time XOR loop to resist timing side-channels.
