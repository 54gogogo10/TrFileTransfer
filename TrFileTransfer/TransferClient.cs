using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>TCP file/folder sender with SHA256 integrity verification.</summary>
    public class TransferClient
    {
        private CancellationTokenSource _cts;
        private readonly string _serverIp;
        private readonly int _port;
        private readonly string _filePath;
        private readonly int _bufferSize;
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
        /// Creates a TCP client for sending files or folders.
        /// </summary>
        /// <param name="serverIp">Target server IPv4 address.</param>
        /// <param name="port">Target server port.</param>
        /// <param name="filePath">Path to the file or folder to send.</param>
        /// <param name="bufferSize">I/O buffer size in bytes (default 1 MB).</param>
        public TransferClient(string serverIp, int port, string filePath, int bufferSize = 1024 * 1024)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _bufferSize = bufferSize;
        }

        /// <summary>Sends the file specified in the constructor over TCP.</summary>
        public async Task SendAsync()
        {
            await RunTransfer(SendFileInternal);
        }

        /// <summary>Sends a folder recursively over TCP.</summary>
        /// <param name="folderPath">Path to the folder to send.</param>
        public async Task SendFolderAsync(string folderPath)
        {
            await RunTransfer(ct => SendFolderInternal(folderPath, ct));
        }

        private async Task RunTransfer(Func<CancellationToken, Task> transferAction)
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
            }
            finally
            {
                _isRunning = false;
                var stoppedHandler = OnStopped;
                if (stoppedHandler != null) stoppedHandler();
            }
        }

        /// <summary>Cancels the current transfer. Safe to call from any thread.</summary>
        public void Cancel()
        {
            var cts = _cts;
            if (cts != null) cts.Cancel();
        }

        private async Task SendFileInternal(CancellationToken ct)
        {
            using (var client = new TcpClient())
            {
                client.NoDelay = true;
                client.SendBufferSize = _bufferSize;
                client.ReceiveBufferSize = _bufferSize;

                Log(L.C_Connecting(_serverIp, _port));
                await client.ConnectAsync(_serverIp, _port);
                Log(L.C_Connected(_serverIp, _port));

                var stream = client.GetStream();
                var fileInfo = new FileInfo(_filePath);
                long fileSize = fileInfo.Length;
                string fileName = fileInfo.Name;
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);

                var header = new byte[1 + 12 + nameBytes.Length];
                header[0] = 0x00; // single file
                Buffer.BlockCopy(BitConverter.GetBytes(fileSize), 0, header, 1, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(nameBytes.Length), 0, header, 9, 4);
                Buffer.BlockCopy(nameBytes, 0, header, 13, nameBytes.Length);
                await stream.WriteAsync(header, 0, header.Length, ct);
                await stream.FlushAsync(ct);

                Log(L.C_Sending(fileName, Utils.FormatSize(fileSize)));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                await SendFilePayload(stream, _filePath, fileSize, fileName, ct);
                sw.Stop();

                Log(L.C_TransferDone(fileName, Utils.FormatSize(fileSize),
                    sw.Elapsed.TotalSeconds,
                    Utils.FormatSize((long)(fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
        }

        private async Task SendFolderInternal(string folderPath, CancellationToken ct)
        {
            using (var client = new TcpClient())
            {
                client.NoDelay = true;
                client.SendBufferSize = _bufferSize;
                client.ReceiveBufferSize = _bufferSize;

                Log(L.C_Connecting(_serverIp, _port));
                await client.ConnectAsync(_serverIp, _port);
                Log(L.C_Connected(_serverIp, _port));

                var stream = client.GetStream();

                string folderName = Path.GetFileName(folderPath);
                if (string.IsNullOrWhiteSpace(folderName))
                    folderName = "folder";
                byte[] folderNameBytes = System.Text.Encoding.UTF8.GetBytes(folderName);

                var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    Log(L.S_ZeroFiles);
                    var errHandler = OnError;
                    if (errHandler != null) errHandler(L.S_ZeroFiles);
                    return;
                }

                // Pre-compute file entries to avoid duplicate FileInfo creation
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
                        RelativePath = files[i].Substring(folderPath.Length).TrimStart('\\', '/')
                    };
                    totalSize += size;
                }

                Log(L.C_SendingFolder(folderName, files.Length, Utils.FormatSize(totalSize)));

                // Folder header: type(1) + folderNameLen(2) + folderName + fileCount(4)
                var header = new byte[1 + 2 + folderNameBytes.Length + 4];
                int pos = 0;
                header[pos++] = 0x01; // folder
                Buffer.BlockCopy(BitConverter.GetBytes((short)folderNameBytes.Length), 0, header, pos, 2); pos += 2;
                Buffer.BlockCopy(folderNameBytes, 0, header, pos, folderNameBytes.Length); pos += folderNameBytes.Length;
                Buffer.BlockCopy(BitConverter.GetBytes(files.Length), 0, header, pos, 4);
                await stream.WriteAsync(header, 0, header.Length, ct);
                await stream.FlushAsync(ct);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long totalSent = 0;

                foreach (var entry in fileEntries)
                {
                    if (ct.IsCancellationRequested) break;

                    // File entry header: fileSize(8) + pathLen(2) + relativePath
                    byte[] relPathBytes = System.Text.Encoding.UTF8.GetBytes(entry.RelativePath);
                    var fileHeader = new byte[8 + 2 + relPathBytes.Length];
                    Buffer.BlockCopy(BitConverter.GetBytes(entry.Size), 0, fileHeader, 0, 8);
                    Buffer.BlockCopy(BitConverter.GetBytes((short)relPathBytes.Length), 0, fileHeader, 8, 2);
                    Buffer.BlockCopy(relPathBytes, 0, fileHeader, 10, relPathBytes.Length);
                    await stream.WriteAsync(fileHeader, 0, fileHeader.Length, ct);

                    await SendFilePayload(stream, entry.Path, entry.Size, entry.RelativePath, ct);
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

                sw.Stop();
                Log(L.C_FolderTransferDone(folderName, files.Length, Utils.FormatSize(totalSize),
                    sw.Elapsed.TotalSeconds,
                    Utils.FormatSize((long)(totalSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001)))));

                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
        }

        private async Task SendFilePayload(NetworkStream stream, string filePath, long fileSize,
            string displayName, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesSent = 0;
            var bufA = new byte[_bufferSize];
            var bufB = new byte[_bufferSize];
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();

            using (var sha256 = SHA256.Create())
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, _bufferSize, FileOptions.SequentialScan))
            {
                int read = await fileStream.ReadAsync(bufA, 0, bufA.Length, ct);
                if (read == 0)
                {
                    sha256.TransformFinalBlock(new byte[0], 0, 0);
                    await stream.WriteAsync(sha256.Hash, 0, 32, ct);
                    await stream.FlushAsync(ct);
                    return;
                }

                var cur = bufA;
                var nxt = bufB;

                while (read > 0 && !ct.IsCancellationRequested)
                {
                    var nextReadTask = fileStream.ReadAsync(nxt, 0, nxt.Length, ct);

                    sha256.TransformBlock(cur, 0, read, null, 0);
                    await stream.WriteAsync(cur, 0, read, ct);
                    bytesSent += read;

                    read = await nextReadTask;

                    var tmp = cur; cur = nxt; nxt = tmp;

                    if (progressTimer.ElapsedMilliseconds >= 100 || read == 0)
                    {
                        progressTimer.Restart();
                        var handler = OnProgress;
                        if (handler != null)
                            handler(new TransferProgress
                            {
                                BytesTransferred = bytesSent,
                                TotalBytes = fileSize,
                                SpeedBytesPerSecond = bytesSent / sw.Elapsed.TotalSeconds,
                                Elapsed = sw.Elapsed,
                                FileName = displayName
                            });
                    }
                }
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                await stream.FlushAsync(ct);

                await stream.WriteAsync(sha256.Hash, 0, 32, ct);
                await stream.FlushAsync(ct);
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }

    }
}
