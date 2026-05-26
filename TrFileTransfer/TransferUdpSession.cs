using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>Per-client UDP transfer session. One instance per connected client.</summary>
    #pragma warning disable 1591
    public class TransferUdpSession
    {
        private readonly UdpClient _udp;        // shared receive socket
        private readonly UdpClient _sendUdp;     // dedicated send socket, avoids contention with ReceiveAsync
        private readonly IPEndPoint _clientEp;
        private readonly string _saveDirectory;
        private readonly Queue<byte[]> _packets = new Queue<byte[]>();
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private volatile bool _disposed;

        public event Action<string> OnLog;
        public event Action<TransferProgress> OnProgress;
        public event Action<string> OnError;
        public event Action OnTransferComplete;
        public event Action OnStopped;

        public bool IsRunning { get; private set; }

        private readonly ConcurrentDictionary<string, ChunkTracker> _chunkTrackers;

        public TransferUdpSession(UdpClient udp, IPEndPoint clientEp, string saveDirectory,
            ConcurrentDictionary<string, ChunkTracker> chunkTrackers = null)
        {
            _udp = udp;
            _sendUdp = new UdpClient(); // dedicated socket for sending, avoids contention with _udp.ReceiveAsync
            _clientEp = clientEp;
            _saveDirectory = saveDirectory;
            _chunkTrackers = chunkTrackers ?? new ConcurrentDictionary<string, ChunkTracker>();
            IsRunning = true;
        }

        public bool EnqueuePacket(byte[] packet)
        {
            if (_disposed || !IsRunning) return false;
            lock (_lock) { _packets.Enqueue(packet); }
            _signal.Release();
            return true;
        }

        public void Stop()
        {
            _disposed = true;
            _signal.Release();
        }

        public async Task RunAsync(byte[] helloPacket, CancellationToken ct)
        {
            try { await ProcessTransfer(helloPacket, ct); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                var errHandler = OnError;
                if (errHandler != null) errHandler(ex.Message);
                Log("Session error: " + ex.Message);
            }
            finally
            {
                IsRunning = false;
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
            }
        }

        private async Task ProcessTransfer(byte[] helloPacket, CancellationToken ct)
        {
            byte type; int seq, bodyLen;
            if (!UdpProtocol.ParseHeader(helloPacket, out type, out seq, out bodyLen) || type != UdpProtocol.TypeHello) return;
            if (bodyLen < 11) return;

            byte transferType = helloPacket[UdpProtocol.HeaderSize];
            long chunkOffset = 0, totalFileSize = 0;
            bool isChunked = (transferType == 0x02);
            long fileSize; int nameLen; string fileName;

            if (isChunked)
            {
                if (bodyLen < 27) return;
                totalFileSize = BitConverter.ToInt64(helloPacket, UdpProtocol.HeaderSize + 1);
                chunkOffset = BitConverter.ToInt64(helloPacket, UdpProtocol.HeaderSize + 9);
                fileSize = BitConverter.ToInt64(helloPacket, UdpProtocol.HeaderSize + 17);
                nameLen = BitConverter.ToInt16(helloPacket, UdpProtocol.HeaderSize + 25);
                if (totalFileSize <= 0 || chunkOffset < 0 || fileSize <= 0 || nameLen <= 0 || nameLen > 4096) return;
                if (UdpProtocol.HeaderSize + 27 + nameLen > helloPacket.Length) return;
                fileName = System.Text.Encoding.UTF8.GetString(helloPacket, UdpProtocol.HeaderSize + 27, nameLen);
                fileName = Path.GetFileName(fileName);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "received_file";
            }
            else
            {
                fileSize = BitConverter.ToInt64(helloPacket, UdpProtocol.HeaderSize + 1);
                nameLen = BitConverter.ToInt16(helloPacket, UdpProtocol.HeaderSize + 9);
                if (fileSize < 0 || nameLen <= 0 || nameLen > 4096) return;
                if (UdpProtocol.HeaderSize + 11 + nameLen > helloPacket.Length) return;
                fileName = System.Text.Encoding.UTF8.GetString(helloPacket, UdpProtocol.HeaderSize + 11, nameLen);
            }
            bool isFolderFile = (transferType == 0x01);

            string savePath;
            if (isFolderFile)
            {
                fileName = Utils.SanitizeRelativePath(fileName);
                savePath = Path.Combine(_saveDirectory, fileName);
                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            else
            {
                fileName = Path.GetFileName(fileName);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "received_file";
                savePath = Utils.GetUniqueSavePath(_saveDirectory, fileName);
            }

            Log(L.UdpS_Receiving(fileName, Utils.FormatSize(fileSize)));
            var helloAck = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, 0, null);
            try { _sendUdp.Send(helloAck, helloAck.Length, _clientEp); } catch { return; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesReceived = 0;
            int totalChunks = (int)((fileSize + UdpProtocol.MaxChunkSize - 1) / UdpProtocol.MaxChunkSize);
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();
            var received = new bool[totalChunks];
            bool finReceived = false;
            bool allReceived = false;
            byte[] clientHash = null;

            // Phase 1: receive data packets, write each chunk at its correct file offset
            using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.RandomAccess))
            {

                while (!finReceived && !ct.IsCancellationRequested && !_disposed)
                {
                    try { await _signal.WaitAsync(UdpProtocol.TimeoutMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }

                    byte[] packet = null;
                    lock (_lock) { if (_packets.Count > 0) packet = _packets.Dequeue(); }
                    if (packet == null) { Log(L.UdpS_DataTimeout); break; }

                    byte pktType; int pktSeq, pktBodyLen;
                    if (!UdpProtocol.ParseHeader(packet, out pktType, out pktSeq, out pktBodyLen)) continue;

                    if (pktType == UdpProtocol.TypeData)
                    {
                        int dataLen = pktBodyLen;
                        int dataOffset = UdpProtocol.HeaderSize;
                        if (dataOffset + dataLen > packet.Length) dataLen = packet.Length - dataOffset;
                        if (dataLen <= 0) continue;

                        received[pktSeq] = true;
                        bytesReceived += dataLen;

                        // Write at correct file offset (supports out-of-order arrival)
                        long destOffset = (long)pktSeq * UdpProtocol.MaxChunkSize;
                        fileStream.Seek(destOffset, SeekOrigin.Begin);
                        await fileStream.WriteAsync(packet, dataOffset, dataLen, ct).ConfigureAwait(false);

                        if (progressTimer.ElapsedMilliseconds >= 100)
                        {
                            progressTimer.Restart();
                            var ph = OnProgress;
                            if (ph != null) ph(new TransferProgress {
                                BytesTransferred = bytesReceived, TotalBytes = fileSize,
                                SpeedBytesPerSecond = bytesReceived / sw.Elapsed.TotalSeconds,
                                Elapsed = sw.Elapsed, FileName = fileName });
                        }
                    }
                    else if (pktType == UdpProtocol.TypeFin)
                    {
                        finReceived = true;
                        if (pktBodyLen >= 32)
                        {
                            clientHash = new byte[32];
                            Buffer.BlockCopy(packet, UdpProtocol.HeaderSize, clientHash, 0, 32);
                        }
                    }
                    else if (pktType == UdpProtocol.TypeFolderEnd)
                    {
                        finReceived = true;
                    }
                }

                if (!finReceived) return;

                // Phase 2: check missing and request retransmission
                while (!ct.IsCancellationRequested)
                {
                    var missing = new List<int>();
                    for (int i = 0; i < totalChunks; i++)
                        if (!received[i]) missing.Add(i);

                    if (missing.Count == 0)
                    {
                        fileStream.Flush();
                        allReceived = true;
                        break;
                    }

                    Log(string.Format("Missing {0}/{1} chunks, requesting retransmit...",
                        missing.Count, totalChunks));
                    var reportBody = new byte[4 + missing.Count * 4];
                    Buffer.BlockCopy(BitConverter.GetBytes(missing.Count), 0, reportBody, 0, 4);
                    for (int i = 0; i < missing.Count; i++)
                        Buffer.BlockCopy(BitConverter.GetBytes(missing[i]), 0, reportBody, 4 + i * 4, 4);
                    var report = UdpProtocol.BuildPacket(UdpProtocol.TypeMissingReport, 0, reportBody);
                    try { _sendUdp.Send(report, report.Length, _clientEp); } catch { }

                    // Wait for retransmitted chunks (write them to file at correct offset)
                    long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30;
                    while (missing.Count > 0 && Stopwatch.GetTimestamp() < deadline
                        && !ct.IsCancellationRequested && !_disposed)
                    {
                        try { await _signal.WaitAsync(3000, ct).ConfigureAwait(false); }
                        catch { break; }

                        byte[] pkt = null;
                        lock (_lock) { if (_packets.Count > 0) pkt = _packets.Dequeue(); }
                        if (pkt == null) break;

                        byte rt; int rs, rbl;
                        if (!UdpProtocol.ParseHeader(pkt, out rt, out rs, out rbl)) continue;
                        if (rt == UdpProtocol.TypeFin) { finReceived = true; break; }
                        if (rt == UdpProtocol.TypeData && missing.Contains(rs))
                        {
                            missing.Remove(rs);
                            received[rs] = true;
                            int dLen = rbl;
                            int dOff = UdpProtocol.HeaderSize;
                            if (dOff + dLen > pkt.Length) dLen = pkt.Length - dOff;
                            long destOff = (long)rs * UdpProtocol.MaxChunkSize;
                            fileStream.Seek(destOff, SeekOrigin.Begin);
                            fileStream.Write(pkt, dOff, dLen);
                            bytesReceived += dLen;
                        }
                    }
                }
            } // fileStream disposed here — safe for file ops below

            if (allReceived)
            {
                sw.Stop();

                bool hashOk;
                using (var fs = new FileStream(savePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var sha256 = SHA256.Create())
                {
                    var actualHash = sha256.ComputeHash(fs);
                    hashOk = clientHash != null
                        ? Utils.ConstantTimeEquals(clientHash, actualHash)
                        : false;
                }

                if (!hashOk)
                {
                    var finNak = UdpProtocol.BuildPacket(UdpProtocol.TypeFin, 0, new byte[1]);
                    try { _sendUdp.Send(finNak, finNak.Length, _clientEp); } catch { }
                    return;
                }

                if (isChunked)
                {
                    ChunkTracker tracker = ChunkTracker.GetOrCreate(
                        _chunkTrackers, fileName, totalFileSize, _saveDirectory);
                    byte[] chunkData = File.ReadAllBytes(savePath);
                    try { File.Delete(savePath); } catch { }
                    bool cComplete = tracker.WriteChunk(chunkOffset, chunkData, 65536);
                    if (cComplete)
                    {
                        tracker.Dispose();
                        ChunkTracker removed;
                        _chunkTrackers.TryRemove(fileName, out removed);
                    }
                }

                var finAck = UdpProtocol.BuildPacket(UdpProtocol.TypeFinAck, 0, null);
                try { _sendUdp.Send(finAck, finAck.Length, _clientEp); } catch { }
                Log(L.S_TransferDone(fileName, Utils.FormatSize(fileSize),
                    sw.Elapsed.TotalSeconds, Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
        }

        private void Log(string msg) { Utils.LogTo(OnLog, msg); }
    }
}
