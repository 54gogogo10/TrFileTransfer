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
        private readonly UdpClient _udp;
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
            _clientEp = clientEp;
            _saveDirectory = saveDirectory;
            _chunkTrackers = chunkTrackers ?? new ConcurrentDictionary<string, ChunkTracker>();
            IsRunning = true; // accept Data packets immediately, before RunAsync starts
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
            _signal.Release(); // wake up RunAsync
        }

        public async Task RunAsync(byte[] helloPacket, CancellationToken ct)
        {
            IsRunning = true;
            try
            {
                await ProcessTransfer(helloPacket, ct);
            }
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
            byte type;
            int seq, bodyLen;
            if (!UdpProtocol.ParseHeader(helloPacket, out type, out seq, out bodyLen) || type != UdpProtocol.TypeHello)
                return;

            if (bodyLen < 11) return;
            byte transferType = helloPacket[UdpProtocol.HeaderSize];

            // Chunked file (type 0x02): totalSize(8) + chunkOffset(8) + chunkSize(8) + nameLen(2) + name
            long chunkOffset = 0;
            long totalFileSize = 0;
            bool isChunked = (transferType == 0x02);

            long fileSize;
            int nameLen;
            string fileName;

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
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "received_file";
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
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            else
            {
                fileName = Path.GetFileName(fileName);
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "received_file";
                savePath = Utils.GetUniqueSavePath(_saveDirectory, fileName);
            }

            Log(L.UdpS_Receiving(fileName, Utils.FormatSize(fileSize)));

            var helloAck = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, 0, null);
            try
            {
                _udp.Send(helloAck, helloAck.Length, _clientEp);
                Log(string.Format("HELLO_ACK sent to {0}", _clientEp));
            }
            catch (Exception ex)
            {
                Log(string.Format("HELLO_ACK send failed: {0}", ex.Message));
                var errHandler = OnError;
                if (errHandler != null) errHandler(ex.Message);
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesReceived = 0;
            int expectedSeq = 0;
            int totalChunks = (int)((fileSize + UdpProtocol.MaxChunkSize - 1) / UdpProtocol.MaxChunkSize);
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();
            int retryCount = 0;
            int lastNakSeq = -1;
            int outOfOrderCount = 0;
            int unackedSinceLastAck = 0;
            const int AckBatchCount = 512;
            long lastAckTimestamp = Stopwatch.GetTimestamp();
            const int NakThreshold = 3;
            bool finProcessed = false;

            const int WriteBufSize = 2 * 1024 * 1024;
            byte[] writeBuf = new byte[WriteBufSize];
            int writeBufPos = 0;

            using (var sha256 = SHA256.Create())
            using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write,
                FileShare.None, 65536, FileOptions.SequentialScan))
            {
                while (!finProcessed && !ct.IsCancellationRequested && !_disposed)
                {
                    bool timedOut = false;
                    try
                    {
                        await _signal.WaitAsync(UdpProtocol.TimeoutMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }

                    byte[] packet = null;
                    lock (_lock)
                    {
                        if (_packets.Count > 0)
                            packet = _packets.Dequeue();
                    }

                    if (packet == null)
                    {
                        if (_disposed) break;
                        retryCount++;
                        if (retryCount > UdpProtocol.MaxRetries) { Log(L.UdpS_DataTimeout); break; }
                        timedOut = true;
                    }
                    else
                    {
                        if (!UdpProtocol.ParseHeader(packet, out type, out seq, out bodyLen))
                            continue;

                        if (type == UdpProtocol.TypeData)
                        {
                            retryCount = 0;
                            int dataOffset = UdpProtocol.HeaderSize;

                            if (seq == expectedSeq && expectedSeq < totalChunks)
                            {
                                int dataLen = bodyLen;
                                if (dataOffset + dataLen > packet.Length)
                                    dataLen = packet.Length - dataOffset;
                                if (dataLen <= 0) continue;

                                sha256.TransformBlock(packet, dataOffset, dataLen, null, 0);
                                Buffer.BlockCopy(packet, dataOffset, writeBuf, writeBufPos, dataLen);
                                writeBufPos += dataLen;
                                if (writeBufPos + UdpProtocol.MaxChunkSize > WriteBufSize)
                                {
                                    await fileStream.WriteAsync(writeBuf, 0, writeBufPos, ct).ConfigureAwait(false);
                                    writeBufPos = 0;
                                }
                                bytesReceived += dataLen;
                                expectedSeq++;
                                lastNakSeq = -1;
                                outOfOrderCount = 0;
                                unackedSinceLastAck++;

                                if (progressTimer.ElapsedMilliseconds >= 100 || expectedSeq >= totalChunks)
                                {
                                    progressTimer.Restart();
                                    var progressHandler = OnProgress;
                                    if (progressHandler != null)
                                        progressHandler(new TransferProgress
                                        {
                                            BytesTransferred = bytesReceived,
                                            TotalBytes = fileSize,
                                            SpeedBytesPerSecond = bytesReceived / sw.Elapsed.TotalSeconds,
                                            Elapsed = sw.Elapsed,
                                            FileName = fileName
                                        });
                                }
                            }
                            else
                            {
                                if (seq < expectedSeq)
                                    unackedSinceLastAck = AckBatchCount; // duplicate: force immediate cumulative ACK
                                outOfOrderCount++;
                                if (outOfOrderCount >= NakThreshold
                                    && expectedSeq != lastNakSeq && expectedSeq < totalChunks)
                                {
                                    var nak = UdpProtocol.BuildPacket(UdpProtocol.TypeNak, expectedSeq, null);
                                    await _udp.SendAsync(nak, nak.Length, _clientEp).ConfigureAwait(false);
                                    lastNakSeq = expectedSeq;
                                }
                            }
                        }
                        else if (type == UdpProtocol.TypeFolderEnd)
                        {
                            var fack = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, 0, null);
                            await _udp.SendAsync(fack, fack.Length, _clientEp).ConfigureAwait(false);
                            if (writeBufPos > 0)
                            {
                                await fileStream.WriteAsync(writeBuf, 0, writeBufPos, ct).ConfigureAwait(false);
                                writeBufPos = 0;
                            }
                            finProcessed = true;
                        }
                        else if (type == UdpProtocol.TypeFin)
                        {
                            if (unackedSinceLastAck > 0)
                            {
                                var flushAck = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, expectedSeq - 1, null);
                                await _udp.SendAsync(flushAck, flushAck.Length, _clientEp).ConfigureAwait(false);
                                unackedSinceLastAck = 0;
                                lastAckTimestamp = Stopwatch.GetTimestamp();
                            }
                            if (writeBufPos > 0)
                            {
                                await fileStream.WriteAsync(writeBuf, 0, writeBufPos, ct).ConfigureAwait(false);
                                writeBufPos = 0;
                            }

                            if (expectedSeq >= totalChunks)
                            {
                                sha256.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                                if (bodyLen >= 32)
                                {
                                    var receivedHash = new byte[32];
                                    Buffer.BlockCopy(packet, UdpProtocol.HeaderSize, receivedHash, 0, 32);
                                    var computedHash = sha256.Hash;
                                    var finAckType = Utils.ConstantTimeEquals(receivedHash, computedHash)
                                        ? UdpProtocol.TypeFinAck : UdpProtocol.TypeFin;
                                    var finAck = UdpProtocol.BuildPacket(finAckType, 0,
                                        finAckType == UdpProtocol.TypeFinAck ? null : new byte[1]);
                                    if (finAckType != UdpProtocol.TypeFinAck)
                                    {
                                        Log(L.S_HashFailed(fileName));
                                        var errHandler = OnError;
                                        if (errHandler != null) errHandler(L.S_HashFailed(fileName));
                                        return;
                                    }

                                    // For chunked: write to ChunkTracker BEFORE sending FIN_ACK
                                    if (isChunked)
                                    {
                                        finProcessed = true;
                                    }
                                    else
                                    {
                                        await _udp.SendAsync(finAck, finAck.Length, _clientEp).ConfigureAwait(false);
                                    }
                                }
                                else
                                {
                                    if (isChunked)
                                    {
                                        finProcessed = true;
                                    }
                                    else
                                    {
                                        var finAck = UdpProtocol.BuildPacket(UdpProtocol.TypeFinAck, 0, null);
                                        await _udp.SendAsync(finAck, finAck.Length, _clientEp).ConfigureAwait(false);
                                    }
                                }
                                if (!isChunked) finProcessed = true;
                            }
                            else
                            {
                                var ack = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, expectedSeq - 1, null);
                                await _udp.SendAsync(ack, ack.Length, _clientEp).ConfigureAwait(false);
                            }
                        }
                    }

                    if (timedOut && expectedSeq > 0)
                    {
                        // Re-send last cumulative ACK on timeout to help client advance window
                        try
                        {
                            var ack = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, expectedSeq - 1, null);
                            _udp.Send(ack, ack.Length, _clientEp);
                        }
                        catch { }
                    }

                    if (unackedSinceLastAck >= AckBatchCount)
                    {
                        var batchAck = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, expectedSeq - 1, null);
                        await _udp.SendAsync(batchAck, batchAck.Length, _clientEp).ConfigureAwait(false);
                        unackedSinceLastAck = 0;
                        lastAckTimestamp = Stopwatch.GetTimestamp();
                    }
                }

                if (writeBufPos > 0)
                    await fileStream.WriteAsync(writeBuf, 0, writeBufPos, ct).ConfigureAwait(false);
            }

            // Send final cumulative ACK for remaining unacknowledged chunks
            if (unackedSinceLastAck > 0 && expectedSeq > 0)
            {
                try
                {
                    var finalAck = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, expectedSeq - 1, null);
                    _udp.Send(finalAck, finalAck.Length, _clientEp);
                }
                catch { }
            }

            if (!finProcessed) return;

            sw.Stop();

            if (isChunked)
            {
                // Copy chunk data from temp file to ChunkTracker
                try
                {
                    byte[] chunkData = File.ReadAllBytes(savePath);
                    try { File.Delete(savePath); } catch { }

                    ChunkTracker tracker = ChunkTracker.GetOrCreate(
                        _chunkTrackers, fileName, totalFileSize, _saveDirectory);

                    bool isComplete = tracker.WriteChunk(chunkOffset, chunkData, 4096);

                    // Send FIN_ACK after chunk data is safely persisted
                    var chunkFinAck = UdpProtocol.BuildPacket(UdpProtocol.TypeFinAck, 0, null);
                    await _udp.SendAsync(chunkFinAck, chunkFinAck.Length, _clientEp).ConfigureAwait(false);

                    if (isComplete)
                    {
                        tracker.Dispose();
                        ChunkTracker removed;
                        _chunkTrackers.TryRemove(fileName, out removed);
                        Log(L.S_TransferDone(fileName, Utils.FormatSize(totalFileSize), 0, ""));
                        var completeHandler = OnTransferComplete;
                        if (completeHandler != null) completeHandler();
                    }
                }
                catch (Exception ex)
                {
                    // Notify client of hash failure (best-effort, fire-and-forget)
                    try
                    {
                        var failFin = UdpProtocol.BuildPacket(UdpProtocol.TypeFin, 0, new byte[1]);
                        _udp.SendAsync(failFin, failFin.Length, _clientEp);
                    }
                    catch { }
                    Log(L.S_HashFailed(fileName + ": " + ex.Message));
                    var errHandler = OnError;
                    if (errHandler != null) errHandler(fileName + ": " + ex.Message);
                }
            }
            else
            {
                Log(L.S_TransferDone(fileName, Utils.FormatSize(fileSize),
                    sw.Elapsed.TotalSeconds,
                    Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

                if (!isFolderFile)
                {
                    var completeHandler = OnTransferComplete;
                    if (completeHandler != null) completeHandler();
                }
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
