using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>Reliable UDP file/folder sender (Go-Back-N ARQ, SHA256).</summary>
    public class TransferUdpClient
    {
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private readonly string _serverIp;
        private readonly int _port;
        private readonly string _filePath;
        private readonly int _windowSize;
        private readonly int _localPort;
        private volatile bool _isRunning;

        /// <summary>Fired for every log message.</summary>
        public event Action<string> OnLog;
        /// <summary>Fired periodically during transfer with progress info.</summary>
        public event Action<TransferProgress> OnProgress;
        /// <summary>Fired when a non-fatal error occurs.</summary>
        public event Action<string> OnError;
        /// <summary>Fired when the transfer completes successfully.</summary>
        public event Action OnTransferComplete;
        /// <summary>Fired when the transfer starts.</summary>
        public event Action OnStarted;
        /// <summary>Fired when the transfer stops.</summary>
        public event Action OnStopped;

        /// <summary>Whether a transfer is currently in progress.</summary>
        public bool IsRunning { get { return _isRunning; } }

        /// <summary>Creates a reliable UDP client for sending files or folders.</summary>
        public TransferUdpClient(string serverIp, int port, string filePath, int windowSize = UdpProtocol.DefaultWindowSize)
            : this(serverIp, port, filePath, 0, windowSize) { }

        public TransferUdpClient(string serverIp, int port, string filePath, int localPort, int windowSize = UdpProtocol.DefaultWindowSize)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _localPort = localPort;
            _windowSize = windowSize;
        }

        /// <summary>Sends the file specified in the constructor over reliable UDP.</summary>
        public async Task SendAsync()
        {
            await RunUdpTransfer(SendUdpInternal);
        }

        /// <summary>Sends a folder recursively over reliable UDP.</summary>
        public async Task SendFolderAsync(string folderPath)
        {
            await RunUdpTransfer(ct => SendUdpFolderInternal(folderPath, ct));
        }

        /// <summary>Sends a chunk of a file (transferType 0x02) for concurrent UDP transfer.</summary>
        public async Task SendChunkedAsync(long offset, long chunkSize, long totalSize)
        {
            await RunUdpTransfer(ct => SendChunkedUdpInternal(offset, chunkSize, totalSize, ct));
        }

        private async Task RunUdpTransfer(Func<CancellationToken, Task> transferAction)
        {
            _cts = new CancellationTokenSource();
            _isRunning = true;

            var startedHandler = OnStarted;
            if (startedHandler != null) startedHandler();

            try
            {
                await transferAction(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log(L.C_TransferCancelled);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log(L.C_Error(ex.Message));
                var handler = OnError;
                if (handler != null) handler(ex.Message);
                throw;
            }
            finally
            {
                _isRunning = false;
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
                try { _udp.Close(); } catch { }
            }
        }

        /// <summary>Cancels the current transfer and closes the UDP socket. Safe from any thread.</summary>
        public void Cancel()
        {
            var cts = _cts;
            if (cts != null) cts.Cancel();
            try { _udp.Close(); } catch { }
        }

        private UdpClient CreateUdpClient()
        {
            var udp = _localPort > 0
                ? new UdpClient(_localPort)
                : new UdpClient();
            udp.Client.SendBufferSize = 4 * 1024 * 1024;
            udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            return udp;
        }

        private async Task SendUdpInternal(CancellationToken ct)
        {
            _udp = CreateUdpClient();
            var serverEp = new IPEndPoint(IPAddress.Parse(_serverIp), _port);

            var fileInfo = new FileInfo(_filePath);
            long fileSize = fileInfo.Length;
            string fileName = fileInfo.Name;
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

            Log(L.UdpC_Connecting(_serverIp, _port));

            var helloBody = BuildHelloBody(0x00, fileSize, nameBytes);
            var helloPacket = UdpProtocol.BuildPacket(UdpProtocol.TypeHello, 0, helloBody);
            await _udp.SendAsync(helloPacket, helloPacket.Length, serverEp).ConfigureAwait(false);

            Log(L.UdpC_Sending(fileName, Utils.FormatSize(fileSize), _windowSize));

            if (!await WaitForAckAsync()) return;

            Log("HELLO ACK received, starting data transfer...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = await SendUdpFileDataAsync(_udp, serverEp, _filePath, fileSize, fileName, ct, reportProgress: true);
            sw.Stop();
            if (!ok) return;

            Log(L.C_TransferDone(fileName, Utils.FormatSize(fileSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            var completeHandler = OnTransferComplete;
            if (completeHandler != null) completeHandler();
        }

        private async Task SendUdpFolderInternal(string folderPath, CancellationToken ct)
        {
            _udp = CreateUdpClient();
            var serverEp = new IPEndPoint(IPAddress.Parse(_serverIp), _port);

            string folderName = Path.GetFileName(folderPath);
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "folder";

            var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Log(L.S_ZeroFiles);
                var errHandler = OnError;
                if (errHandler != null) errHandler(L.S_ZeroFiles);
                return;
            }

            var fileEntries = new FileEntry[files.Length];
            long totalSize = 0;
            for (int i = 0; i < files.Length; i++)
            {
                var fi = new FileInfo(files[i]);
                long size = fi.Length;
                fileEntries[i] = new FileEntry
                {
                    Path = files[i],
                    Size = size,
                    RelativePath = folderName + "\\" + files[i].Substring(folderPath.Length).TrimStart('\\', '/')
                };
                totalSize += size;
            }

            Log(L.UdpC_SendingFolder(folderName, files.Length, Utils.FormatSize(totalSize)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalSent = 0;

            foreach (var entry in fileEntries)
            {
                if (ct.IsCancellationRequested) break;

                byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(entry.RelativePath);

                var helloBody = BuildHelloBody(0x01, entry.Size, pathBytes);
                var helloPacket = UdpProtocol.BuildPacket(UdpProtocol.TypeHello, 0, helloBody);
                await _udp.SendAsync(helloPacket, helloPacket.Length, serverEp).ConfigureAwait(false);

                if (!await WaitForAckAsync()) return;

                bool ok = await SendUdpFileDataAsync(_udp, serverEp, entry.Path, entry.Size,
                    entry.RelativePath, ct, reportProgress: false);
                if (!ok) return;

                totalSent += entry.Size;

                var progressHandler = OnProgress;
                if (progressHandler != null)
                    progressHandler(new TransferProgress
                    {
                        BytesTransferred = totalSent,
                        TotalBytes = totalSize,
                        SpeedBytesPerSecond = totalSent / sw.Elapsed.TotalSeconds,
                        Elapsed = sw.Elapsed,
                        FileName = folderName
                    });
            }

            var folderEndPacket = UdpProtocol.BuildPacket(UdpProtocol.TypeFolderEnd, 0, null);
            await _udp.SendAsync(folderEndPacket, folderEndPacket.Length, serverEp).ConfigureAwait(false);

            if (!await WaitForAckAsync())
            {
                Log(L.UdpC_FolderNotConfirmed);
                return;
            }

            sw.Stop();
            Log(L.C_FolderTransferDone(folderName, files.Length, Utils.FormatSize(totalSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(totalSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            var completeHandler = OnTransferComplete;
            if (completeHandler != null) completeHandler();
        }

        private static byte[] BuildHelloBody(byte transferType, long fileSize, byte[] nameBytes)
        {
            var body = new byte[1 + 8 + 2 + nameBytes.Length];
            body[0] = transferType;
            Buffer.BlockCopy(BitConverter.GetBytes(fileSize), 0, body, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes((short)nameBytes.Length), 0, body, 9, 2);
            Buffer.BlockCopy(nameBytes, 0, body, 11, nameBytes.Length);
            return body;
        }

        private async Task SendChunkedUdpInternal(long offset, long chunkSize, long totalSize, CancellationToken ct)
        {
            _udp = CreateUdpClient();
            var serverEp = new IPEndPoint(IPAddress.Parse(_serverIp), _port);

            string fileName = Path.GetFileName(_filePath);
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

            // Build HELLO body: transferType(1) + totalSize(8) + chunkOffset(8) + chunkSize(8) + nameLen(2) + name
            var body = new byte[1 + 8 + 8 + 8 + 2 + nameBytes.Length];
            body[0] = 0x02;
            Buffer.BlockCopy(BitConverter.GetBytes(totalSize), 0, body, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, body, 9, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(chunkSize), 0, body, 17, 8);
            Buffer.BlockCopy(BitConverter.GetBytes((short)nameBytes.Length), 0, body, 25, 2);
            Buffer.BlockCopy(nameBytes, 0, body, 27, nameBytes.Length);

            var hello = UdpProtocol.BuildPacket(UdpProtocol.TypeHello, 0, body);
            await _udp.SendAsync(hello, hello.Length, serverEp);

            // Wait for HELLO ACK
            bool helloAcked = false;
            int timeoutCount = 0;
            int dynTimeout = UdpProtocol.TimeoutMs;
            while (!helloAcked && timeoutCount <= UdpProtocol.MaxRetries && !ct.IsCancellationRequested)
            {
                _udp.Client.ReceiveTimeout = dynTimeout;
                try
                {
                    var result = await _udp.ReceiveAsync().ConfigureAwait(false);
                    byte type; int seq, bl;
                    if (UdpProtocol.ParseHeader(result.Buffer, out type, out seq, out bl))
                    {
                        if (type == UdpProtocol.TypeAck && seq == 0)
                            helloAcked = true;
                    }
                }
                catch (SocketException) { timeoutCount++; }
            }
            if (!helloAcked) return;

            bool ok = await SendUdpFileDataAsync(_udp, serverEp, _filePath, chunkSize,
                fileName, ct, reportProgress: true, fileOffset: offset);
            if (ok)
            {
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
        }

        private async Task<bool> SendUdpFileDataAsync(UdpClient udp, IPEndPoint serverEp, string filePath,
            long fileSize, string displayName, CancellationToken ct, bool reportProgress, long fileOffset = 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            int totalChunks = (int)((fileSize + UdpProtocol.MaxChunkSize - 1) / UdpProtocol.MaxChunkSize);
            int hashedUpTo = -1;
            var sha256 = SHA256.Create();
            FileStream fs = null;
            byte[] fileHash = null;
            bool finAckReceived = false;

            try
            {
                fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 65536, FileOptions.SequentialScan);
                if (fileOffset > 0) fs.Seek(fileOffset, SeekOrigin.Begin);

                int sendBase = 0;
                int nextSeqNum = 0;
                int timeoutCount = 0;
                double rttSmooth = UdpProtocol.TimeoutMs / 4.0;
                int dynTimeout = UdpProtocol.TimeoutMs;
                var windowSendTimestamp = 0L;
                bool canMeasureRtt = false;
                const int ReadBufSize = 1048576; // 1 MB — match TCP buffer size
                byte[] readBuf = new byte[ReadBufSize];
                int bufStartSeq = -1;
                int bufDataLen = 0;
                var retransmitQ = new System.Collections.Generic.Queue<int>();
                var sendTasks = new System.Collections.Generic.List<System.Threading.Tasks.Task<int>>();
                var sendSemaphore = new SemaphoreSlim(64);

                Log(string.Format("Data phase: {0} chunks, window={1}", totalChunks, _windowSize));

                while (true)
                {
                    while (sendBase < totalChunks && !ct.IsCancellationRequested)
                    {
                        sendTasks.Clear();
                        while ((retransmitQ.Count > 0 || nextSeqNum < sendBase + _windowSize) && nextSeqNum < totalChunks)
                        {
                            // Selective retransmit: pick from NAK queue first, then new chunks
                            int currentSeq;
                            if (retransmitQ.Count > 0)
                            {
                                currentSeq = retransmitQ.Dequeue();
                                if (currentSeq < sendBase) continue; // already ACKed, skip
                            }
                            else
                            {
                                currentSeq = nextSeqNum;
                                nextSeqNum++;
                            }
                            int offset = currentSeq * UdpProtocol.MaxChunkSize;
                            int chunkSize = (currentSeq < totalChunks - 1)
                                ? UdpProtocol.MaxChunkSize
                                : (int)(fileSize - (long)offset);

                            int bufOff = offset - bufStartSeq * UdpProtocol.MaxChunkSize;
                            if (bufStartSeq < 0 || bufOff < 0 || bufOff + chunkSize > bufDataLen)
                            {
                                fs.Seek(fileOffset + offset, SeekOrigin.Begin);
                                int toRead = (int)Math.Min(ReadBufSize, fileSize - offset);
                                int totalRead = 0;
                                while (totalRead < toRead)
                                {
                                    int n = await fs.ReadAsync(readBuf, totalRead, toRead - totalRead, ct).ConfigureAwait(false);
                                    if (n == 0) break;
                                    totalRead += n;
                                }
                                bufStartSeq = currentSeq;
                                bufDataLen = totalRead;
                                bufOff = 0;
                            }

                            var dataPacket = UdpProtocol.BuildPacketFromBuffer(UdpProtocol.TypeData, currentSeq,
                                readBuf, bufOff, chunkSize);
                            await sendSemaphore.WaitAsync();
                            var sendTask = udp.SendAsync(dataPacket, dataPacket.Length, serverEp);
                            sendTasks.Add(sendTask);

                            // Track this task for semaphore release on completion
                            var capturedTask = sendTask;
                            var _ = capturedTask.ContinueWith(t => sendSemaphore.Release());

                            if (currentSeq > hashedUpTo)
                            {
                                sha256.TransformBlock(readBuf, bufOff, chunkSize, null, 0);
                                hashedUpTo = currentSeq;
                            }
                        }

                        if (sendTasks.Count > 0)
                        {
                            await System.Threading.Tasks.Task.WhenAll(sendTasks.ToArray()).ConfigureAwait(false);
                            windowSendTimestamp = Stopwatch.GetTimestamp();
                            canMeasureRtt = true;
                            Log(string.Format("Sent {0} chunks (seq {1}-{2}), waiting ACK...",
                                sendTasks.Count, sendBase, sendBase + sendTasks.Count - 1));
                        }
                        udp.Client.ReceiveTimeout = dynTimeout;
                        try
                        {
                            var result = await udp.ReceiveAsync().ConfigureAwait(false);
                            byte type;
                            int seq, bodyLen;
                            if (!UdpProtocol.ParseHeader(result.Buffer, out type, out seq, out bodyLen))
                                continue;

                            if (type == UdpProtocol.TypeAck)
                            {
                                if (seq >= sendBase)
                                {
                                    sendBase = seq + 1;
                                    timeoutCount = 0;
                                    if (canMeasureRtt)
                                    {
                                        canMeasureRtt = false;
                                        double rtt = (Stopwatch.GetTimestamp() - windowSendTimestamp)
                                            * 1000.0 / Stopwatch.Frequency;
                                        if (rtt > 0 && rtt < 10000)
                                        {
                                            rttSmooth = rttSmooth * 0.875 + rtt * 0.125;
                                            dynTimeout = Math.Max((int)(rttSmooth * 4), UdpProtocol.MinTimeoutMs);
                                        }
                                    }
                                }
                            }
                            else if (type == UdpProtocol.TypeNak)
                            {
                                // Selective retransmit: only the NAK'd chunk, not the whole window
                                if (seq >= sendBase && seq < nextSeqNum)
                                {
                                    retransmitQ.Enqueue(seq);
                                    timeoutCount = 0;
                                }
                            }

                            if (reportProgress && (progressTimer.ElapsedMilliseconds >= 100 || sendBase >= totalChunks))
                            {
                                progressTimer.Restart();
                                long acked = sendBase >= totalChunks
                                    ? fileSize
                                    : Math.Min((long)sendBase * UdpProtocol.MaxChunkSize, fileSize);
                                var handler = OnProgress;
                                if (handler != null)
                                    handler(new TransferProgress
                                    {
                                        BytesTransferred = acked,
                                        TotalBytes = fileSize,
                                        SpeedBytesPerSecond = acked / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                                        Elapsed = sw.Elapsed,
                                        FileName = displayName
                                    });
                            }
                        }
                        catch (SocketException)
                        {
                            timeoutCount++;
                            if (timeoutCount > UdpProtocol.MaxRetries)
                            {
                                Log(L.UdpC_TooManyRetransmissions);
                                return false;
                            }
                            retransmitQ.Enqueue(sendBase);
                            Log(L.UdpC_TimeoutRetransmitting(sendBase));
                        }
                        catch (ObjectDisposedException)
                        {
                            return false;
                        }
                    }

                    if (ct.IsCancellationRequested) return false;
                    if (sendBase < totalChunks) continue; // spurious wake from cancelled receive

                    // --- All data ACKed; compute hash (once) and enter FIN phase ---
                    if (fileHash == null)
                    {
                        sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                        fileHash = sha256.Hash;

                        if (reportProgress)
                        {
                            var finalHandler = OnProgress;
                            if (finalHandler != null)
                                finalHandler(new TransferProgress
                                {
                                    BytesTransferred = fileSize,
                                    TotalBytes = fileSize,
                                    SpeedBytesPerSecond = fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                                    Elapsed = sw.Elapsed,
                                    FileName = displayName
                                });
                        }
                    }

                    // --- FIN phase ---
                    finAckReceived = false;
                    bool needsResend = false;
                    for (int finRetries = 0; finRetries < 5 && !ct.IsCancellationRequested && !finAckReceived && !needsResend; finRetries++)
                    {
                        var finPacket = UdpProtocol.BuildPacket(UdpProtocol.TypeFin, 0, fileHash);
                        await udp.SendAsync(finPacket, finPacket.Length, serverEp).ConfigureAwait(false);

                        long deadlineTimestamp = Stopwatch.GetTimestamp()
                            + (long)(dynTimeout * Stopwatch.Frequency / 1000.0);
                        while (Stopwatch.GetTimestamp() < deadlineTimestamp
                            && !finAckReceived && !needsResend && !ct.IsCancellationRequested)
                        {
                            int remaining = (int)Math.Max(
                                (deadlineTimestamp - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency, 1);
                            udp.Client.ReceiveTimeout = remaining;
                            try
                            {
                                var result = await udp.ReceiveAsync().ConfigureAwait(false);
                                byte dtype; int dseq, dbl;
                                if (UdpProtocol.ParseHeader(result.Buffer, out dtype, out dseq, out dbl))
                                {
                                    if (dtype == UdpProtocol.TypeFinAck)
                                        finAckReceived = true;
                                    else if (dtype == UdpProtocol.TypeFin)
                                    {
                                        Log(L.S_HashFailed(displayName));
                                        var errHandler = OnError;
                                        if (errHandler != null) errHandler(L.S_HashFailed(displayName));
                                        return false;
                                    }
                                    else if (dtype == UdpProtocol.TypeAck)
                                    {
                                        if (dseq + 1 < totalChunks)
                                        {
                                            sendBase = dseq + 1;
                                            nextSeqNum = sendBase;
                                            timeoutCount = 0;
                                            needsResend = true;
                                        }
                                    }
                                }
                            }
                            catch (SocketException) { break; }
                            catch (ObjectDisposedException) { return false; }
                        }
                    }

                    if (needsResend) continue; // jump back to data phase
                    if (finAckReceived) return true;
                    // FIN retries exhausted
                    Log(L.UdpC_ServerNotResponding);
                    return false;
                }
            }
            finally
            {
                if (sha256 != null) sha256.Dispose();
                if (fs != null) fs.Dispose();
            }
        }

        private async Task<bool> WaitForAckAsync()
        {
            _udp.Client.ReceiveTimeout = UdpProtocol.TimeoutMs;
            try
            {
                while (true)
                {
                    var result = await _udp.ReceiveAsync().ConfigureAwait(false);
                    byte ackType;
                    int ackSeq, ackBodyLen;
                    if (UdpProtocol.ParseHeader(result.Buffer, out ackType, out ackSeq, out ackBodyLen)
                        && ackType == UdpProtocol.TypeAck && ackSeq == 0)
                        return true;
                }
            }
            catch (SocketException)
            {
                Log(L.UdpC_ServerNotResponding);
                return false;
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
