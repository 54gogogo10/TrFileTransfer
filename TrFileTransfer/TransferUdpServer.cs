using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>Reliable UDP file/folder receiver (Go-Back-N ARQ, SHA256 verification).</summary>
    public class TransferUdpServer
    {
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private readonly int _port;
        private readonly string _saveDirectory;
        private volatile bool _isRunning;

        // For passing a packet (HELLO or FolderEnd) captured inside HandleTransfer back to ReceiveLoop
        private byte[] _pendingPacketData;
        private IPEndPoint _pendingPacketEp;

        /// <summary>Fired for every log message.</summary>
        public event Action<string> OnLog;
        /// <summary>Fired periodically during transfer with progress info.</summary>
        public event Action<TransferProgress> OnProgress;
        /// <summary>Fired when a non-fatal error occurs.</summary>
        public event Action<string> OnError;
        /// <summary>Fired when a single transfer completes. Server keeps listening.</summary>
        public event Action OnTransferComplete;
        /// <summary>Fired when the server starts listening.</summary>
        public event Action OnStarted;
        /// <summary>Fired when the server stops.</summary>
        public event Action OnStopped;

        /// <summary>Whether the server is currently listening.</summary>
        public bool IsRunning { get { return _isRunning; } }

        /// <summary>
        /// Creates a UDP server that listens on all interfaces for incoming transfers.
        /// </summary>
        /// <param name="port">Port to listen on.</param>
        /// <param name="saveDirectory">Directory where received files are saved.</param>
        public TransferUdpServer(int port, string saveDirectory)
        {
            _port = port;
            _saveDirectory = saveDirectory;
        }

        /// <summary>Starts listening on the configured port. Fires OnStarted on success.</summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            try
            {
                _udp = new UdpClient();
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
                _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
                _udp.Client.SendBufferSize = 4 * 1024 * 1024;
            }
            catch (SocketException ex)
            {
                Log(L.S_BindFailed("0.0.0.0", _port.ToString(), ex.Message));
                var errHandler = OnError;
                if (errHandler != null) errHandler(ex.Message);
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
                return;
            }

            _isRunning = true;
            var handler = OnStarted;
            if (handler != null) handler();
            Log(L.UdpS_Started(_port.ToString(), _saveDirectory));

            Task.Factory.StartNew(() => ReceiveLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Stops the server and closes the UDP socket.</summary>
        public void Stop()
        {
            _isRunning = false;
            var cts = _cts;
            if (cts != null) cts.Cancel();
            try
            {
                var udp = _udp;
                if (udp != null) udp.Close();
            }
            catch { }
            var handler = OnStopped;
            if (handler != null) handler();
            Log(L.UdpS_Stopped);
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                // Process a pending packet captured inside HandleTransfer
                if (_pendingPacketData != null)
                {
                    var savedData = _pendingPacketData;
                    var savedEp = _pendingPacketEp;
                    _pendingPacketData = null;
                    _pendingPacketEp = null;

                    byte pendingType; int pendingSeq, pendingBodyLen;
                    if (UdpProtocol.ParseHeader(savedData, out pendingType, out pendingSeq, out pendingBodyLen))
                    {
                        if (pendingType == UdpProtocol.TypeHello)
                        {
                            Log(L.UdpS_QueuedHello(savedEp));
                            await HandleTransfer(savedEp, savedData, ct);
                        }
                        else if (pendingType == UdpProtocol.TypeFolderEnd)
                        {
                            Log(L.UdpS_QueuedFolderEnd);
                            var ack = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, 0, null);
                            await _udp.SendAsync(ack, ack.Length, savedEp);
                            var compHandler = OnTransferComplete;
                            if (compHandler != null) compHandler();
                        }
                    }
                    continue;
                }

                try
                {
                    _udp.Client.ReceiveTimeout = -1;
                    var result = await _udp.ReceiveAsync();
                    var clientEp = result.RemoteEndPoint;

                    byte pktType; int pktSeq, pktBodyLen;
                    if (UdpProtocol.ParseHeader(result.Buffer, out pktType, out pktSeq, out pktBodyLen))
                    {
                        if (pktType == UdpProtocol.TypeHello)
                        {
                            Log(L.UdpS_ReceivedHello(clientEp));
                            await HandleTransfer(clientEp, result.Buffer, ct);
                        }
                        else if (pktType == UdpProtocol.TypeFolderEnd)
                        {
                            Log(L.UdpS_FolderEndReceived);
                            var ack = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, 0, null);
                            await _udp.SendAsync(ack, ack.Length, clientEp);
                            var compHandler = OnTransferComplete;
                            if (compHandler != null) compHandler();
                        }
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch (SocketException)
                {
                    if (ct.IsCancellationRequested) break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Log(L.UdpS_ReceiveError(ex.Message));
                }
            }
        }

        private async Task HandleTransfer(IPEndPoint clientEp, byte[] helloPacket, CancellationToken ct)
        {
            byte type;
            int seq, bodyLen;
            if (!UdpProtocol.ParseHeader(helloPacket, out type, out seq, out bodyLen) || type != UdpProtocol.TypeHello)
                return;

            if (bodyLen < 11) return;

            byte transferType = helloPacket[UdpProtocol.HeaderSize];
            long fileSize = BitConverter.ToInt64(helloPacket, UdpProtocol.HeaderSize + 1);
            int nameLen = BitConverter.ToInt16(helloPacket, UdpProtocol.HeaderSize + 9);
            if (fileSize < 0 || nameLen <= 0 || nameLen > 4096) return;
            if (UdpProtocol.HeaderSize + 11 + nameLen > helloPacket.Length) return;

            string fileName = System.Text.Encoding.UTF8.GetString(helloPacket, UdpProtocol.HeaderSize + 11, nameLen);

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

            // Send HELLO ACK
            var helloAck = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, 0, null);
            await _udp.SendAsync(helloAck, helloAck.Length, clientEp);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesReceived = 0;
            int expectedSeq = 0;
            int totalChunks = (int)((fileSize + UdpProtocol.MaxChunkSize - 1) / UdpProtocol.MaxChunkSize);
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();
            int retryCount = 0;
            bool interruptedByPacket = false;
            bool finProcessed = false;

            using (var sha256 = SHA256.Create())
            using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write,
                FileShare.None, 65536, FileOptions.SequentialScan))
            {
                while (!finProcessed && !ct.IsCancellationRequested)
                {
                    try
                    {
                        _udp.Client.ReceiveTimeout = UdpProtocol.TimeoutMs;
                        var result = await _udp.ReceiveAsync();

                        if (!result.RemoteEndPoint.Equals(clientEp))
                            continue;

                        if (!UdpProtocol.ParseHeader(result.Buffer, out type, out seq, out bodyLen))
                            continue;

                        if (type == UdpProtocol.TypeData)
                        {
                            if (seq == expectedSeq && expectedSeq < totalChunks)
                            {
                                int dataOffset = UdpProtocol.HeaderSize;
                                int dataLen = bodyLen;
                                if (dataOffset + dataLen > result.Buffer.Length)
                                    dataLen = result.Buffer.Length - dataOffset;
                                if (dataLen <= 0) continue;

                                sha256.TransformBlock(result.Buffer, dataOffset, dataLen, null, 0);
                                await fileStream.WriteAsync(result.Buffer, dataOffset, dataLen, ct);
                                bytesReceived += dataLen;
                                expectedSeq++;
                                retryCount = 0;

                                var ack = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, expectedSeq - 1, null);
                                await _udp.SendAsync(ack, ack.Length, clientEp);

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
                                var ack = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, expectedSeq - 1, null);
                                await _udp.SendAsync(ack, ack.Length, clientEp);
                            }
                        }
                        else if (type == UdpProtocol.TypeFin)
                        {
                            if (expectedSeq >= totalChunks)
                            {
                                sha256.TransformFinalBlock(new byte[0], 0, 0);
                                if (bodyLen >= 32)
                                {
                                    var receivedHash = new byte[32];
                                    Buffer.BlockCopy(result.Buffer, UdpProtocol.HeaderSize, receivedHash, 0, 32);
                                    var computedHash = sha256.Hash;

                                    var finAckType = Utils.ConstantTimeEquals(receivedHash, computedHash)
                                        ? UdpProtocol.TypeFinAck : UdpProtocol.TypeFin;
                                    var finAck = UdpProtocol.BuildPacket(finAckType, 0,
                                        finAckType == UdpProtocol.TypeFinAck ? null : new byte[1]);
                                    await _udp.SendAsync(finAck, finAck.Length, clientEp);

                                    if (finAckType != UdpProtocol.TypeFinAck)
                                    {
                                        Log(L.S_HashFailed(fileName));
                                        var errHandler = OnError;
                                        if (errHandler != null) errHandler(L.S_HashFailed(fileName));
                                        return;
                                    }
                                }
                                else
                                {
                                    var finAck = UdpProtocol.BuildPacket(UdpProtocol.TypeFinAck, 0, null);
                                    await _udp.SendAsync(finAck, finAck.Length, clientEp);
                                }
                                finProcessed = true;
                            }
                        }
                        else if (type == UdpProtocol.TypeHello || type == UdpProtocol.TypeFolderEnd)
                        {
                            _pendingPacketData = new byte[result.Buffer.Length];
                            Buffer.BlockCopy(result.Buffer, 0, _pendingPacketData, 0, result.Buffer.Length);
                            _pendingPacketEp = result.RemoteEndPoint;
                            interruptedByPacket = true;
                            break;
                        }
                    }
                    catch (SocketException)
                    {
                        retryCount++;
                        if (retryCount > UdpProtocol.MaxRetries)
                        {
                            Log(L.UdpS_DataTimeout);
                            break;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }

                await fileStream.FlushAsync(ct);
            }

            if (interruptedByPacket)
                return;

            if (!finProcessed)
                return;

            sw.Stop();
            Log(L.S_TransferDone(fileName, Utils.FormatSize(fileSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            // Only signal completion for single-file transfers, not individual folder files
            if (!isFolderFile)
            {
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
