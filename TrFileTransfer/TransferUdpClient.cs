using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>Reliable UDP file/folder sender (Go-Back-N ARQ, 32-chunk window, SHA256).</summary>
    public class TransferUdpClient
    {
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private readonly string _serverIp;
        private readonly int _port;
        private readonly string _filePath;
        private readonly int _windowSize;
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
        /// <summary>Fired when the transfer stops (completed, cancelled, or error).</summary>
        public event Action OnStopped;

        /// <summary>Whether a transfer is currently in progress.</summary>
        public bool IsRunning { get { return _isRunning; } }

        /// <summary>
        /// Creates a reliable UDP client for sending files or folders.
        /// </summary>
        /// <param name="serverIp">Target server IPv4 address.</param>
        /// <param name="port">Target server port.</param>
        /// <param name="filePath">Path to the file or folder to send.</param>
        /// <param name="windowSize">Sliding window size in chunks (default 32).</param>
        public TransferUdpClient(string serverIp, int port, string filePath, int windowSize = UdpProtocol.DefaultWindowSize)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _windowSize = windowSize;
        }

        /// <summary>Sends the file specified in the constructor over reliable UDP.</summary>
        public async Task SendAsync()
        {
            await RunUdpTransfer(SendUdpInternal);
        }

        /// <summary>Sends a folder recursively over reliable UDP.</summary>
        /// <param name="folderPath">Path to the folder to send.</param>
        public async Task SendFolderAsync(string folderPath)
        {
            await RunUdpTransfer(ct => SendUdpFolderInternal(folderPath, ct));
        }

        private async Task RunUdpTransfer(Func<CancellationToken, Task> transferAction)
        {
            _cts = new CancellationTokenSource();
            _isRunning = true;

            var startedHandler = OnStarted;
            if (startedHandler != null) startedHandler();

            try
            {
                await transferAction(_cts.Token);
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

        private async Task SendUdpInternal(CancellationToken ct)
        {
            _udp = new UdpClient();
            _udp.Client.SendBufferSize = 4 * 1024 * 1024;
            _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;

            var serverEp = new IPEndPoint(IPAddress.Parse(_serverIp), _port);

            var fileInfo = new FileInfo(_filePath);
            long fileSize = fileInfo.Length;
            string fileName = fileInfo.Name;
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

            Log(L.UdpC_Connecting(_serverIp, _port));

            var helloBody = BuildHelloBody(0x00, fileSize, nameBytes);
            var helloPacket = UdpProtocol.BuildPacket(UdpProtocol.TypeHello, 0, helloBody);
            await _udp.SendAsync(helloPacket, helloPacket.Length, serverEp);

            Log(L.UdpC_Sending(fileName, Utils.FormatSize(fileSize), _windowSize));

            if (!await WaitForAckAsync()) return;

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
            _udp = new UdpClient();
            _udp.Client.SendBufferSize = 4 * 1024 * 1024;
            _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;

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

            // Pre-compute file sizes to avoid creating FileInfo twice per file
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
                await _udp.SendAsync(helloPacket, helloPacket.Length, serverEp);

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

            // Send folder-end confirmation to server
            var folderEndPacket = UdpProtocol.BuildPacket(UdpProtocol.TypeFolderEnd, 0, null);
            await _udp.SendAsync(folderEndPacket, folderEndPacket.Length, serverEp);

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

        private async Task<bool> SendUdpFileDataAsync(UdpClient udp, IPEndPoint serverEp, string filePath,
            long fileSize, string displayName, CancellationToken ct, bool reportProgress)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            int totalChunks = (int)((fileSize + UdpProtocol.MaxChunkSize - 1) / UdpProtocol.MaxChunkSize);
            int hashedUpTo = -1;
            var sha256 = SHA256.Create();
            FileStream fs = null;
            bool finAckReceived = false;

            try
            {
                fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, UdpProtocol.MaxChunkSize, FileOptions.SequentialScan);

                int sendBase = 0;
                int nextSeqNum = 0;
                int timeoutCount = 0;
                long filePos = 0;

                while (sendBase < totalChunks && !ct.IsCancellationRequested)
                {
                    while (nextSeqNum < sendBase + _windowSize && nextSeqNum < totalChunks)
                    {
                        int offset = nextSeqNum * UdpProtocol.MaxChunkSize;
                        int chunkSize = (nextSeqNum < totalChunks - 1)
                            ? UdpProtocol.MaxChunkSize
                            : (int)(fileSize - (long)offset);

                        var chunkData = new byte[chunkSize];
                        if (offset != filePos)
                            fs.Seek(offset, SeekOrigin.Begin);
                        int totalRead = 0;
                        while (totalRead < chunkSize)
                        {
                            int n = await fs.ReadAsync(chunkData, totalRead, chunkSize - totalRead, ct);
                            if (n == 0) break;
                            totalRead += n;
                        }
                        filePos = offset + totalRead;

                        var dataPacket = UdpProtocol.BuildPacket(UdpProtocol.TypeData, nextSeqNum, chunkData);
                        await udp.SendAsync(dataPacket, dataPacket.Length, serverEp);

                        if (nextSeqNum > hashedUpTo)
                        {
                            sha256.TransformBlock(chunkData, 0, chunkSize, null, 0);
                            hashedUpTo = nextSeqNum;
                        }

                        nextSeqNum++;
                    }

                    udp.Client.ReceiveTimeout = UdpProtocol.TimeoutMs;
                    try
                    {
                        var result = await udp.ReceiveAsync();
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
                        nextSeqNum = sendBase;
                        Log(L.UdpC_TimeoutRetransmitting(sendBase));
                    }
                    catch (ObjectDisposedException)
                    {
                        return false;
                    }
                }

                // Send FIN and wait for FIN_ACK from server
                sha256.TransformFinalBlock(new byte[0], 0, 0);

                // Final progress update before FIN handshake
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

                finAckReceived = false;
                for (int finRetries = 0; finRetries < 5 && !ct.IsCancellationRequested && !finAckReceived; finRetries++)
                {
                    var finPacket = UdpProtocol.BuildPacket(UdpProtocol.TypeFin, 0, sha256.Hash);
                    await udp.SendAsync(finPacket, finPacket.Length, serverEp);

                    var deadline = DateTime.UtcNow.AddMilliseconds(UdpProtocol.TimeoutMs);
                    while (DateTime.UtcNow < deadline && !finAckReceived && !ct.IsCancellationRequested)
                    {
                        int remaining = Math.Max((int)(deadline - DateTime.UtcNow).TotalMilliseconds, 1);
                        udp.Client.ReceiveTimeout = remaining;
                        try
                        {
                            var result = await udp.ReceiveAsync();
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
                            }
                        }
                        catch (SocketException) { break; }
                        catch (ObjectDisposedException) { break; }
                    }
                }

                if (!finAckReceived)
                {
                    Log(L.UdpC_ServerNotResponding);
                    return false;
                }
            }
            finally
            {
                if (sha256 != null) sha256.Dispose();
                if (fs != null) fs.Dispose();
            }

            return finAckReceived;
        }

        private async Task<bool> WaitForAckAsync()
        {
            _udp.Client.ReceiveTimeout = UdpProtocol.TimeoutMs;
            try
            {
                while (true)
                {
                    var result = await _udp.ReceiveAsync();
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
