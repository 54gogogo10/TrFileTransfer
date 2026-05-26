using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>Reliable UDP file/folder receiver — handles multiple concurrent clients.</summary>
    #pragma warning disable 1591
    public class TransferUdpServer
    {
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private readonly string _bindAddress;
        private readonly int _port;
        private readonly string _saveDirectory;
        private volatile bool _isRunning;
        private readonly ConcurrentDictionary<IPEndPoint, TransferUdpSession> _sessions
            = new ConcurrentDictionary<IPEndPoint, TransferUdpSession>();
        private readonly ConcurrentDictionary<string, ChunkTracker> _chunkTrackers
            = new ConcurrentDictionary<string, ChunkTracker>();

        /// <summary>Fired for every log message.</summary>
        public event Action<string> OnLog;
        /// <summary>Fired when a new transfer starts. The TransferUdpSession has its own OnProgress etc.</summary>
        public event Action<TransferUdpSession> OnSessionStarted;
        /// <summary>Fired when the server starts listening.</summary>
        public event Action OnStarted;
        /// <summary>Fired when the server stops.</summary>
        public event Action OnStopped;

        /// <summary>Whether the server is currently listening.</summary>
        public bool IsRunning { get { return _isRunning; } }

        public TransferUdpServer(string bindAddress, int port, string saveDirectory)
        {
            _bindAddress = bindAddress;
            _port = port;
            _saveDirectory = saveDirectory;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            try
            {
                IPAddress bindIp;
                if (!IPAddress.TryParse(_bindAddress, out bindIp))
                    bindIp = IPAddress.Any;
                _udp = new UdpClient();
                _udp.Client.SendBufferSize = 8 * 1024 * 1024;
                _udp.Client.ReceiveBufferSize = 8 * 1024 * 1024; // must be set BEFORE Bind to exceed 64KB
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(bindIp, _port));
            }
            catch (SocketException ex)
            {
                Log(L.S_BindFailed("0.0.0.0", _port.ToString(), ex.Message));
                var errHandler = OnStarted; // fire OnStopped on bind fail
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
            var sessionsToStop = new List<TransferUdpSession>(_sessions.Values);
            _sessions.Clear();
            foreach (var s in sessionsToStop)
                s.Stop();
            var handler = OnStopped;
            if (handler != null) handler();
            Log(L.UdpS_Stopped);
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _udp.Client.ReceiveTimeout = -1;
                    var result = await _udp.ReceiveAsync().ConfigureAwait(false);
                    var clientEp = result.RemoteEndPoint;

                    byte pktType; int pktSeq, pktBodyLen;
                    if (!UdpProtocol.ParseHeader(result.Buffer, out pktType, out pktSeq, out pktBodyLen))
                        continue;

                    if (pktType == UdpProtocol.TypeHello)
                    {
                        Log(string.Format("HELLO from {0}", clientEp));
                        TransferUdpSession session;
                        bool isNew = false;
                        if (!_sessions.TryGetValue(clientEp, out session))
                        {
                            session = new TransferUdpSession(clientEp, _saveDirectory, _chunkTrackers);
                            session.OnLog += msg => Utils.LogTo(OnLog, msg);
                            session.OnError += msg => Utils.LogTo(OnLog, msg);
                            session.OnStopped += () =>
                            {
                                TransferUdpSession removed;
                                _sessions.TryRemove(clientEp, out removed);
                            };
                            if (!_sessions.TryAdd(clientEp, session))
                                _sessions.TryGetValue(clientEp, out session);
                            isNew = true;
                        }
                        if (session != null && isNew)
                        {
                            var startedHandler = OnSessionStarted;
                            if (startedHandler != null) startedHandler(session);
                            var _ = session.RunAsync(result.Buffer, ct);
                        }
                        else if (session != null)
                        {
                            // Retransmitted HELLO — re-send ACK with data port
                            var ackBody = BitConverter.GetBytes(session.DataPort);
                            var helloAck = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, 0, ackBody);
                            try { await _udp.SendAsync(helloAck, helloAck.Length, clientEp).ConfigureAwait(false); }
                            catch { }
                        }
                    }
                    // Data/Fin go directly to session's dedicated receive socket — nothing to do here
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

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
