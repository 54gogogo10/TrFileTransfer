using System;
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
        private readonly int _port;
        private readonly string _saveDirectory;
        private volatile bool _isRunning;
        private readonly Dictionary<IPEndPoint, TransferUdpSession> _sessions = new Dictionary<IPEndPoint, TransferUdpSession>();
        private readonly object _sessionsLock = new object();

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

        public TransferUdpServer(int port, string saveDirectory)
        {
            _port = port;
            _saveDirectory = saveDirectory;
        }

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
            List<TransferUdpSession> sessionsToStop;
            lock (_sessionsLock)
            {
                sessionsToStop = new List<TransferUdpSession>(_sessions.Values);
                _sessions.Clear();
            }
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
                        TransferUdpSession session = null;
                        lock (_sessionsLock)
                        {
                            if (!_sessions.TryGetValue(clientEp, out session))
                            {
                                session = new TransferUdpSession(_udp, clientEp, _saveDirectory);
                                session.OnLog += msg => Utils.LogTo(OnLog, msg);
                                session.OnError += msg => Utils.LogTo(OnLog, msg);
                                session.OnStopped += () =>
                                {
                                    lock (_sessionsLock) { _sessions.Remove(clientEp); }
                                };
                                _sessions[clientEp] = session;
                            }
                        }
                        if (session != null)
                        {
                            var startedHandler = OnSessionStarted;
                            if (startedHandler != null) startedHandler(session);
                            var _ = session.RunAsync(result.Buffer, ct);
                        }
                    }
                    else if (pktType == UdpProtocol.TypeData || pktType == UdpProtocol.TypeFin || pktType == UdpProtocol.TypeFolderEnd)
                    {
                        TransferUdpSession session;
                        lock (_sessionsLock) { _sessions.TryGetValue(clientEp, out session); }
                        bool enqueued = false;
                        if (session != null)
                            enqueued = session.EnqueuePacket(result.Buffer);
                        if (!enqueued && pktType == UdpProtocol.TypeFolderEnd)
                        {
                            var ack = UdpProtocol.BuildPacket(UdpProtocol.TypeAck, 0, null);
                            try { await _udp.SendAsync(ack, ack.Length, clientEp); } catch { }
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

        private void Log(string msg)
        {
            Utils.LogTo(OnLog, msg);
        }
    }
}
