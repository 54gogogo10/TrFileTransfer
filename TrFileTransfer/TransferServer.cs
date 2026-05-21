using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>TCP file/folder receiver with SHA256 integrity verification.</summary>
    public class TransferServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly string _bindAddress;
        private readonly int _port;
        private readonly string _saveDirectory;
        private readonly int _bufferSize;
        private volatile bool _isRunning;

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
        /// <summary>Fired when a new client connects (with endpoint for per-client tracking).</summary>
        public event Action<IPEndPoint> OnClientConnected;
        /// <summary>Fired periodically during a client's transfer with endpoint.</summary>
        public event Action<IPEndPoint, TransferProgress> OnClientProgress;
        /// <summary>Fired when a single client's transfer completes.</summary>
        public event Action<IPEndPoint> OnClientTransferComplete;

        /// <summary>Whether the server is currently listening.</summary>
        public bool IsRunning { get { return _isRunning; } }

        /// <summary>
        /// Creates a TCP server that listens for incoming file transfers.
        /// </summary>
        /// <param name="bindAddress">IPv4 address to bind to, or "0.0.0.0" for all interfaces.</param>
        /// <param name="port">Port to listen on.</param>
        /// <param name="saveDirectory">Directory where received files are saved.</param>
        /// <param name="bufferSize">I/O buffer size in bytes (default 1 MB).</param>
        public TransferServer(string bindAddress, int port, string saveDirectory, int bufferSize = 1024 * 1024)
        {
            _bindAddress = bindAddress;
            _port = port;
            _saveDirectory = saveDirectory;
            _bufferSize = bufferSize;
        }

        /// <summary>Starts listening for incoming connections. Fires OnStarted on success.</summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            IPAddress bindIp;
            if (!IPAddress.TryParse(_bindAddress, out bindIp))
                bindIp = IPAddress.Any;

            try
            {
                _listener = new TcpListener(bindIp, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();
            }
            catch (Exception ex)
            {
                Log(L.S_BindFailed(_bindAddress, _port.ToString(), ex.Message));
                var errHandler = OnError;
                if (errHandler != null) errHandler(ex.Message);
                _isRunning = false;
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
                return;
            }

            _isRunning = true;

            var handler = OnStarted;
            if (handler != null) handler();

            Log(L.S_Started(_port.ToString(), _saveDirectory));

            Task.Factory.StartNew(() => AcceptLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Stops the server and closes the listening socket.</summary>
        public void Stop()
        {
            _isRunning = false;
            var cts = _cts;
            if (cts != null) cts.Cancel();
            try
            {
                var listener = _listener;
                if (listener != null) listener.Stop();
            }
            catch { }

            var handler = OnStopped;
            if (handler != null) handler();

            Log(L.S_Stopped);
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    client.SendBufferSize = _bufferSize;
                    client.ReceiveBufferSize = _bufferSize;
                    var clientEp = client.Client.RemoteEndPoint as IPEndPoint;
                    Log(L.S_ClientConnected(clientEp));
                    var _ = HandleClient(client, ct, clientEp);
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                        break;
                    if (!ct.IsCancellationRequested)
                    {
                        Log(L.S_AcceptError(ex.Message));
                        var handler = OnError;
                        if (handler != null) handler(ex.Message);
                    }
                }
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken ct, IPEndPoint clientEp)
        {
            var connectedHandler = OnClientConnected;
            if (connectedHandler != null) connectedHandler(clientEp);

            Action<TransferProgress> clientProgress = p =>
            {
                var ch = OnClientProgress; if (ch != null) ch(clientEp, p);
            };
            OnProgress += clientProgress;

            using (client)
            {
                try
                {
                    var stream = client.GetStream();

                    // Read transfer type byte
                    var typeBuf = new byte[1];
                    await ReadExactAsync(stream, typeBuf, 0, 1, ct);
                    byte transferType = typeBuf[0];

                    if (transferType == 0x01)
                    {
                        await HandleFolderTransfer(stream, ct);
                    }
                    else
                    {
                        await HandleFileTransfer(stream, ct);
                    }

                    var ccHandler = OnClientTransferComplete;
                    if (ccHandler != null) ccHandler(clientEp);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (IOException ex)
                {
                    Log(L.S_ConnectionError(ex.Message));
                    var handler = OnError;
                    if (handler != null) handler(ex.Message);
                }
                catch (Exception ex)
                {
                    Log(L.S_UnexpectedError(ex.Message));
                    var handler = OnError;
                    if (handler != null) handler(ex.Message);
                }
                finally
                {
                    OnProgress -= clientProgress;
                }
            }
        }

        private async Task HandleFileTransfer(NetworkStream stream, CancellationToken ct)
        {
            var headerBuf = new byte[12];
            await ReadExactAsync(stream, headerBuf, 0, 12, ct);

            long fileSize = BitConverter.ToInt64(headerBuf, 0);
            int nameLen = BitConverter.ToInt32(headerBuf, 8);

            if (fileSize < 0 || nameLen <= 0 || nameLen > 4096)
            {
                Log(L.S_InvalidHeader(fileSize, nameLen));
                return;
            }

            var nameBuf = new byte[nameLen];
            await ReadExactAsync(stream, nameBuf, 0, nameLen, ct);
            string fileName = System.Text.Encoding.UTF8.GetString(nameBuf);

            fileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = L.S_ReceivedFile;

            string savePath = Utils.GetUniqueSavePath(_saveDirectory, fileName);

            Log(L.S_Receiving(fileName, Utils.FormatSize(fileSize)));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool hashOk = await ReceiveFilePayload(stream, savePath, fileSize, fileName, ct);
            sw.Stop();

            if (hashOk)
            {
                Log(L.S_TransferDone(fileName, Utils.FormatSize(fileSize),
                    sw.Elapsed.TotalSeconds,
                    Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));
            }

            var completeHandler = OnTransferComplete;
            if (completeHandler != null) completeHandler();
        }

        private async Task HandleFolderTransfer(NetworkStream stream, CancellationToken ct)
        {
            // Read folder header: folderNameLen(2) + folderName + fileCount(4)
            var folderHeaderBuf = new byte[2];
            await ReadExactAsync(stream, folderHeaderBuf, 0, 2, ct);
            int folderNameLen = BitConverter.ToInt16(folderHeaderBuf, 0);
            if (folderNameLen <= 0 || folderNameLen > 4096) return;

            var folderNameBuf = new byte[folderNameLen];
            await ReadExactAsync(stream, folderNameBuf, 0, folderNameLen, ct);
            string folderName = System.Text.Encoding.UTF8.GetString(folderNameBuf);
            folderName = Path.GetFileName(folderName);
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "received_folder";

            var fileCountBuf = new byte[4];
            await ReadExactAsync(stream, fileCountBuf, 0, 4, ct);
            int fileCount = BitConverter.ToInt32(fileCountBuf, 0);
            if (fileCount <= 0) return;

            string folderSaveDir = Utils.GetUniqueSavePath(_saveDirectory, folderName);
            Directory.CreateDirectory(folderSaveDir);

            Log(L.S_ReceivingFolder(folderName, fileCount, "..."));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalSize = 0;
            int filesReceived = 0;

            for (int i = 0; i < fileCount && !ct.IsCancellationRequested; i++)
            {
                var fileHeaderBuf = new byte[10];
                await ReadExactAsync(stream, fileHeaderBuf, 0, 10, ct);
                long fileSize = BitConverter.ToInt64(fileHeaderBuf, 0);
                int pathLen = BitConverter.ToInt16(fileHeaderBuf, 8);
                if (fileSize < 0 || pathLen <= 0 || pathLen > 4096) return;

                var pathBuf = new byte[pathLen];
                await ReadExactAsync(stream, pathBuf, 0, pathLen, ct);
                string relativePath = System.Text.Encoding.UTF8.GetString(pathBuf);
                relativePath = Utils.SanitizeRelativePath(relativePath);

                string savePath = Path.Combine(folderSaveDir, relativePath);
                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                bool hashOk = await ReceiveFilePayload(stream, savePath, fileSize, relativePath, ct);
                if (!hashOk) return;

                totalSize += fileSize;
                filesReceived++;

                var progressHandler = OnProgress;
                if (progressHandler != null)
                    progressHandler(new TransferProgress
                    {
                        BytesTransferred = filesReceived,
                        TotalBytes = fileCount,
                        SpeedBytesPerSecond = (totalSize > 0 ? totalSize : 0) / sw.Elapsed.TotalSeconds,
                        Elapsed = sw.Elapsed,
                        FileName = folderName
                    });
            }

            sw.Stop();
            Log(L.S_FolderTransferDone(folderName, fileCount, Utils.FormatSize(totalSize),
                sw.Elapsed.TotalSeconds,
                Utils.FormatSize((long)(totalSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

            var completeHandler = OnTransferComplete;
            if (completeHandler != null) completeHandler();
        }

        // Returns true if hash verification passed
        private async Task<bool> ReceiveFilePayload(NetworkStream stream, string savePath, long fileSize,
            string displayName, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesRead = 0;
            var bufA = new byte[_bufferSize];
            var bufB = new byte[_bufferSize];
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            using (var sha256 = SHA256.Create())
            using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write,
                FileShare.None, _bufferSize, FileOptions.SequentialScan))
            {
                long remaining = fileSize;
                int toRead = (int)Math.Min(remaining, (long)bufA.Length);
                int read = await stream.ReadAsync(bufA, 0, toRead, ct);
                if (read == 0)
                    throw new IOException(L.S_ConnClosedPrematurely);

                remaining -= read;
                var cur = bufA;
                var nxt = bufB;

                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    int nextToRead = (int)Math.Min(remaining, (long)nxt.Length);
                    var nextReadTask = stream.ReadAsync(nxt, 0, nextToRead, ct);

                    sha256.TransformBlock(cur, 0, read, null, 0);
                    await fileStream.WriteAsync(cur, 0, read, ct);
                    bytesRead += read;

                    read = await nextReadTask;
                    if (read == 0)
                        throw new IOException(L.S_ConnClosedPrematurely);
                    remaining -= read;

                    var tmp = cur; cur = nxt; nxt = tmp;

                    if (progressTimer.ElapsedMilliseconds >= 100 || remaining == 0)
                    {
                        progressTimer.Restart();
                        var progressHandler = OnProgress;
                        if (progressHandler != null)
                            progressHandler(new TransferProgress
                            {
                                BytesTransferred = bytesRead,
                                TotalBytes = fileSize,
                                SpeedBytesPerSecond = bytesRead / sw.Elapsed.TotalSeconds,
                                Elapsed = sw.Elapsed,
                                FileName = displayName
                            });
                    }
                }

                // Process final chunk
                sha256.TransformBlock(cur, 0, read, null, 0);
                await fileStream.WriteAsync(cur, 0, read, ct);
                bytesRead += read;

                // Final progress update
                var finalProgressHandler = OnProgress;
                if (finalProgressHandler != null)
                    finalProgressHandler(new TransferProgress
                    {
                        BytesTransferred = bytesRead,
                        TotalBytes = fileSize,
                        SpeedBytesPerSecond = bytesRead / sw.Elapsed.TotalSeconds,
                        Elapsed = sw.Elapsed,
                        FileName = displayName
                    });

                sha256.TransformFinalBlock(new byte[0], 0, 0);
                await fileStream.FlushAsync(ct);

                var receivedHash = new byte[32];
                await ReadExactAsync(stream, receivedHash, 0, 32, ct);
                var computedHash = sha256.Hash;

                if (!Utils.ConstantTimeEquals(receivedHash, computedHash))
                {
                    Log(L.S_HashFailed(displayName));
                    var errHandler = OnError;
                    if (errHandler != null) errHandler(L.S_HashFailed(displayName));
                    return false;
                }
            }
            return true;
        }

        private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct);
                if (read == 0) throw new IOException(L.S_ConnClosedUnexpectedly);
                totalRead += read;
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
