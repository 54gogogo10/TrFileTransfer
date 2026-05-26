using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    #pragma warning disable 1591
    public class ConcurrentTransfer
    {
        private readonly string _serverIp;
        private readonly int _port;
        private readonly string _filePath;
        private readonly int _concurrency;
        private readonly bool _isUdp;

        private long _totalBytes;
        private long _transferredBytes;
        private readonly object _progressLock = new object();

        public event Action<string> OnLog;
        public event Action<TransferProgress> OnProgress;
        public event Action<string> OnError;
        public event Action OnTransferComplete;

        public ConcurrentTransfer(string serverIp, int port, string filePath,
            int concurrency, bool isTcp)
        {
            _serverIp = serverIp;
            _port = port;
            _filePath = filePath;
            _concurrency = Math.Max(1, Math.Min(64, concurrency));
            _isUdp = !isTcp;
        }

        public async Task SendAsync()
        {
            var fileInfo = new FileInfo(_filePath);
            long totalSize = fileInfo.Length;
            string fileName = fileInfo.Name;

            if (totalSize == 0)
            {
                var errHandler = OnError;
                if (errHandler != null) errHandler("File is empty");
                return;
            }

            int chunks = Math.Min(_concurrency,
                (int)((totalSize + UdpProtocol.MaxChunkSize - 1) / UdpProtocol.MaxChunkSize));
            chunks = Math.Max(1, chunks);
            long chunkSize = (totalSize + chunks - 1) / chunks;
            _totalBytes = totalSize;

            Log(string.Format("Concurrent send: {0} in {1} chunks", fileName, chunks));

            var tasks = new List<Task>();
            var cts = new CancellationTokenSource();

            for (int i = 0; i < chunks; i++)
            {
                long offset = i * chunkSize;
                long size = Math.Min(chunkSize, totalSize - offset);
                if (size <= 0) break;

                int localPort = FindLocalPort(i);
                var task = SendChunkAsync(offset, size, totalSize, localPort);
                tasks.Add(task);
            }

            try
            {
                await Task.WhenAll(tasks);
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
            catch (Exception ex)
            {
                cts.Cancel();
                var errHandler = OnError;
                if (errHandler != null) errHandler("Concurrent transfer failed: " + ex.Message);
            }
        }

        public async Task SendFolderAsync()
        {
            if (!Directory.Exists(_filePath))
            {
                var errHandler = OnError;
                if (errHandler != null) errHandler("Folder not found: " + _filePath);
                return;
            }

            var files = Directory.GetFiles(_filePath, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                var errHandler = OnError;
                if (errHandler != null) errHandler("No files in folder");
                return;
            }

            // Pre-compute total size for accurate progress
            long folderTotal = 0;
            foreach (var f in files)
            {
                try { folderTotal += new FileInfo(f).Length; } catch { }
            }
            _totalBytes = folderTotal;
            Log(string.Format("Concurrent folder: {0} files ({1}), {2} parallel",
                files.Length, Utils.FormatSize(folderTotal), _concurrency));

            var semaphore = new SemaphoreSlim(_concurrency);
            var tasks = new List<Task>();

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                long fileSize = 0;
                try { fileSize = new FileInfo(file).Length; } catch { }
                int localPort = FindLocalPort(i);
                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        await SendFileAsync(file, localPort);
                        lock (_progressLock)
                        {
                            _transferredBytes += fileSize;
                            ReportProgress(Path.GetFileName(file));
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                tasks.Add(task);
            }

            try
            {
                await Task.WhenAll(tasks);
                var completeHandler = OnTransferComplete;
                if (completeHandler != null) completeHandler();
            }
            catch (Exception ex)
            {
                var errHandler = OnError;
                if (errHandler != null) errHandler("Concurrent folder transfer failed: " + ex.Message);
            }
        }

        private async Task SendChunkAsync(long offset, long size, long totalSize,
            int localPort)
        {
            try
            {
                if (_isUdp)
                {
                    var client = new TransferUdpClient(_serverIp, _port, _filePath, localPort);
                    await client.SendChunkedAsync(offset, size, totalSize);
                }
                else
                {
                    var client = new TransferClient(_serverIp, _port, _filePath, localPort);
                    await client.SendChunkedAsync(offset, size, totalSize);
                }

                lock (_progressLock)
                {
                    _transferredBytes += size;
                    ReportProgress(_filePath);
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("Chunk offset={0} failed: {1}", offset, ex.Message));
                throw;
            }
        }

        private async Task SendFileAsync(string filePath, int localPort)
        {
            if (_isUdp)
            {
                var client = new TransferUdpClient(_serverIp, _port, filePath, localPort);
                await client.SendAsync();
            }
            else
            {
                var client = new TransferClient(_serverIp, _port, filePath, localPort);
                await client.SendAsync();
            }
        }

        private int FindLocalPort(int index)
        {
            int basePort = _port + index + 1;
            return Utils.FindFreePort(basePort, _isUdp);
        }

        private void ReportProgress(string displayName)
        {
            var progressHandler = OnProgress;
            if (progressHandler != null)
            {
                progressHandler(new TransferProgress
                {
                    BytesTransferred = _transferredBytes,
                    TotalBytes = _totalBytes,
                    SpeedBytesPerSecond = 0,
                    Elapsed = TimeSpan.Zero,
                    FileName = displayName
                });
            }
        }

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
