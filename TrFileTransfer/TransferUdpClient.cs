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
    #pragma warning disable 1591
    public class TransferUdpClient
    {
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private readonly string _serverIp;
        private readonly int _port;
        private readonly string _filePath;
        private readonly int _localPort;
        private volatile bool _isRunning;

        public event Action<string> OnLog;
        public event Action<TransferProgress> OnProgress;
        public event Action<string> OnError;
        public event Action OnTransferComplete;
        public event Action OnStarted;
        public event Action OnStopped;

        public bool IsRunning { get { return _isRunning; } }

        public TransferUdpClient(string serverIp, int port, string filePath, int windowSize = UdpProtocol.DefaultWindowSize)
            : this(serverIp, port, filePath, 0, windowSize) { }

        public TransferUdpClient(string serverIp, int port, string filePath, int localPort, int windowSize = UdpProtocol.DefaultWindowSize)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _localPort = localPort;
        }

        public async Task SendAsync()
        {
            await RunUdpTransfer(SendUdpInternal);
        }

        public async Task SendFolderAsync(string folderPath)
        {
            await RunUdpTransfer(ct => SendUdpFolderInternal(folderPath, ct));
        }

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

            try { await transferAction(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { Log(L.C_TransferCancelled); }
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

        public void Cancel()
        {
            var cts = _cts;
            if (cts != null) cts.Cancel();
            try { _udp.Close(); } catch { }
        }

        private UdpClient CreateUdpClient()
        {
            var udp = _localPort > 0 ? new UdpClient(_localPort) : new UdpClient();
            udp.Client.SendBufferSize = 32 * 1024 * 1024;
            udp.Client.ReceiveBufferSize = 32 * 1024 * 1024;
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

            Log(L.UdpC_Sending(fileName, Utils.FormatSize(fileSize), 0));

            int dataPort = await WaitForAckAsync();
            if (dataPort == 0) return;
            if (dataPort != _port) serverEp = new IPEndPoint(serverEp.Address, dataPort);

            Log(string.Format("HELLO ACK received (data port={0}), starting...", dataPort));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = await SendUdpFileDataAsync(_udp, serverEp, _filePath, fileSize, fileName, ct, reportProgress: true);
            sw.Stop();
            if (!ok) return;

            Log(L.C_TransferDone(fileName, Utils.FormatSize(fileSize),
                sw.Elapsed.TotalSeconds, Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));
            var completeHandler = OnTransferComplete;
            if (completeHandler != null) completeHandler();
        }

        private async Task SendUdpFolderInternal(string folderPath, CancellationToken ct)
        {
            _udp = CreateUdpClient();
            var serverEp = new IPEndPoint(IPAddress.Parse(_serverIp), _port);

            string folderName = Path.GetFileName(folderPath);
            if (string.IsNullOrWhiteSpace(folderName)) folderName = "folder";

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
                    Path = files[i], Size = size,
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
                int dp = await WaitForAckAsync();
                if (dp == 0) return;
                var fep = dp != _port ? new IPEndPoint(serverEp.Address, dp) : serverEp;
                bool ok = await SendUdpFileDataAsync(_udp, fep, entry.Path, entry.Size,
                    entry.RelativePath, ct, reportProgress: false);
                if (!ok) return;
                totalSent += entry.Size;
                var ph = OnProgress;
                if (ph != null) ph(new TransferProgress {
                    BytesTransferred = totalSent, TotalBytes = totalSize,
                    SpeedBytesPerSecond = totalSent / sw.Elapsed.TotalSeconds,
                    Elapsed = sw.Elapsed, FileName = folderName });
            }

            var folderEndPacket = UdpProtocol.BuildPacket(UdpProtocol.TypeFolderEnd, 0, null);
            await _udp.SendAsync(folderEndPacket, folderEndPacket.Length, serverEp).ConfigureAwait(false);
            if (await WaitForAckAsync() == 0) { Log(L.UdpC_FolderNotConfirmed); return; }
            sw.Stop();
            Log(L.C_FolderTransferDone(folderName, files.Length, Utils.FormatSize(totalSize),
                sw.Elapsed.TotalSeconds, Utils.FormatSize((long)(totalSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));
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

            var body = new byte[1 + 8 + 8 + 8 + 2 + nameBytes.Length];
            body[0] = 0x02;
            Buffer.BlockCopy(BitConverter.GetBytes(totalSize), 0, body, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, body, 9, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(chunkSize), 0, body, 17, 8);
            Buffer.BlockCopy(BitConverter.GetBytes((short)nameBytes.Length), 0, body, 25, 2);
            Buffer.BlockCopy(nameBytes, 0, body, 27, nameBytes.Length);

            var hello = UdpProtocol.BuildPacket(UdpProtocol.TypeHello, 0, body);
            await _udp.SendAsync(hello, hello.Length, serverEp);

            bool helloAcked = false;
            int dataPort = _port;
            int timeoutCount = 0;
            while (!helloAcked && timeoutCount <= UdpProtocol.MaxRetries && !ct.IsCancellationRequested)
            {
                _udp.Client.ReceiveTimeout = UdpProtocol.TimeoutMs;
                try
                {
                    var result = await _udp.ReceiveAsync().ConfigureAwait(false);
                    byte t; int s, bl;
                    if (UdpProtocol.ParseHeader(result.Buffer, out t, out s, out bl)
                        && t == UdpProtocol.TypeAck && s == 0)
                    {
                        helloAcked = true;
                        if (bl >= 4) dataPort = BitConverter.ToInt32(result.Buffer, UdpProtocol.HeaderSize);
                    }
                }
                catch (SocketException) { timeoutCount++; }
            }
            if (!helloAcked) return;
            if (dataPort != _port) serverEp = new IPEndPoint(serverEp.Address, dataPort);

            bool ok = await SendUdpFileDataAsync(_udp, serverEp, _filePath, chunkSize,
                fileName, ct, reportProgress: true, fileOffset: offset);
            if (ok)
            {
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
        }

        // Blast-then-repair: send all chunks fast, then server reports missing ones for retransmission
        private async Task<bool> SendUdpFileDataAsync(UdpClient udp, IPEndPoint serverEp, string filePath,
            long fileSize, string displayName, CancellationToken ct, bool reportProgress, long fileOffset = 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();
            int totalChunks = (int)((fileSize + UdpProtocol.MaxChunkSize - 1) / UdpProtocol.MaxChunkSize);
            var sha256 = SHA256.Create();
            FileStream fs = null;

            try
            {
                fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 65536, FileOptions.SequentialScan);
                if (fileOffset > 0) fs.Seek(fileOffset, SeekOrigin.Begin);

                const int ReadBufSize = 1048576;
                byte[] readBuf = new byte[ReadBufSize];
                int bufStartSeq = -1, bufDataLen = 0;
                var sendSemaphore = new SemaphoreSlim(64);

                Log(string.Format("Blasting {0} chunks...", totalChunks));
                var sendTasks = new System.Collections.Generic.List<System.Threading.Tasks.Task<int>>();

                for (int seq = 0; seq < totalChunks && !ct.IsCancellationRequested; seq++)
                {
                    int mOffset = seq * UdpProtocol.MaxChunkSize;
                    int mSize = (seq < totalChunks - 1) ? UdpProtocol.MaxChunkSize
                        : (int)(fileSize - (long)mOffset);

                    int bufOff = mOffset - bufStartSeq * UdpProtocol.MaxChunkSize;
                    if (bufStartSeq < 0 || bufOff < 0 || bufOff + mSize > bufDataLen)
                    {
                        fs.Seek(fileOffset + mOffset, SeekOrigin.Begin);
                        int toRead = (int)Math.Min(ReadBufSize, fileSize - mOffset);
                        int totalRead = 0;
                        while (totalRead < toRead)
                        {
                            int n = await fs.ReadAsync(readBuf, totalRead, toRead - totalRead, ct).ConfigureAwait(false);
                            if (n == 0) break;
                            totalRead += n;
                        }
                        bufStartSeq = seq; bufDataLen = totalRead; bufOff = 0;
                    }

                    var dp = UdpProtocol.BuildPacketFromBuffer(UdpProtocol.TypeData, seq, readBuf, bufOff, mSize);
                    await sendSemaphore.WaitAsync();
                    var st = udp.SendAsync(dp, dp.Length, serverEp);
                    sendTasks.Add(st);
                    var ct2 = st; var _ = ct2.ContinueWith(t => sendSemaphore.Release());
                    sha256.TransformBlock(readBuf, bufOff, mSize, null, 0);

                    if (reportProgress && progressTimer.ElapsedMilliseconds >= 100)
                    {
                        progressTimer.Restart();
                        var ph = OnProgress;
                        if (ph != null) ph(new TransferProgress {
                            BytesTransferred = (long)(seq + 1) * UdpProtocol.MaxChunkSize,
                            TotalBytes = fileSize, SpeedBytesPerSecond = 0, Elapsed = sw.Elapsed,
                            FileName = displayName });
                    }
                }

                if (sendTasks.Count > 0)
                    await System.Threading.Tasks.Task.WhenAll(sendTasks.ToArray()).ConfigureAwait(false);
                Log("All blasted. Computing hash...");

                sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                byte[] fileHash = sha256.Hash;

                // FIN + missing-report repair loop
                while (!ct.IsCancellationRequested)
                {
                    var finPacket = UdpProtocol.BuildPacket(UdpProtocol.TypeFin, 0, fileHash);
                    await udp.SendAsync(finPacket, finPacket.Length, serverEp).ConfigureAwait(false);
                    udp.Client.ReceiveTimeout = 5000;

                    try
                    {
                        var result = await udp.ReceiveAsync().ConfigureAwait(false);
                        byte dtype; int dseq, dbl;
                        if (!UdpProtocol.ParseHeader(result.Buffer, out dtype, out dseq, out dbl)) continue;

                        if (dtype == UdpProtocol.TypeFinAck) return true;
                        if (dtype == UdpProtocol.TypeFin)
                        {
                            var eh = OnError; if (eh != null) eh(L.S_HashFailed(displayName));
                            return false;
                        }
                        if (dtype == UdpProtocol.TypeMissingReport)
                        {
                            int count = BitConverter.ToInt32(result.Buffer, UdpProtocol.HeaderSize);
                            Log(string.Format("Retransmitting {0} missing chunks...", count));
                            for (int i = 0; i < count; i++)
                            {
                                int ms = BitConverter.ToInt32(result.Buffer, UdpProtocol.HeaderSize + 4 + i * 4);
                                int mo = ms * UdpProtocol.MaxChunkSize;
                                int mc = (ms < totalChunks - 1) ? UdpProtocol.MaxChunkSize
                                    : (int)(fileSize - (long)mo);
                                fs.Seek(fileOffset + mo, SeekOrigin.Begin);
                                byte[] cb = new byte[mc];
                                int rt = 0;
                                while (rt < mc) {
                                    int n = await fs.ReadAsync(cb, rt, mc - rt, ct).ConfigureAwait(false);
                                    if (n == 0) break; rt += n;
                                }
                                var cp = UdpProtocol.BuildPacketFromBuffer(UdpProtocol.TypeData, ms, cb, 0, mc);
                                await udp.SendAsync(cp, cp.Length, serverEp).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (SocketException) { Log("FIN timeout, retrying..."); }
                    catch (ObjectDisposedException) { return false; }
                }
                return false;
            }
            finally
            {
                if (sha256 != null) sha256.Dispose();
                if (fs != null) fs.Dispose();
            }
        }

        private async Task<int> WaitForAckAsync()
        {
            _udp.Client.ReceiveTimeout = UdpProtocol.TimeoutMs;
            try
            {
                while (true)
                {
                    var result = await _udp.ReceiveAsync().ConfigureAwait(false);
                    byte ackType; int ackSeq, ackBodyLen;
                    if (UdpProtocol.ParseHeader(result.Buffer, out ackType, out ackSeq, out ackBodyLen)
                        && ackType == UdpProtocol.TypeAck && ackSeq == 0)
                    {
                        if (ackBodyLen >= 4)
                            return BitConverter.ToInt32(result.Buffer, UdpProtocol.HeaderSize);
                        return _port; // old server without port assignment
                    }
                }
            }
            catch (SocketException) { Log(L.UdpC_ServerNotResponding); return 0; }
        }

        private void Log(string msg) { Utils.LogTo(OnLog, msg); }
    }
}
