using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

class QuickTest {
    static int FindFreePort() {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    static void Main() {
        Console.WriteLine("Start");
        int port = FindFreePort();
        string sendDir = Path.Combine(Path.GetTempPath(), "q_" + Guid.NewGuid().ToString("N"));
        string recvDir = Path.Combine(Path.GetTempPath(), "q_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sendDir); Directory.CreateDirectory(recvDir);
        
        try {
            var testFile = Path.Combine(sendDir, "t.bin");
            File.WriteAllBytes(testFile, new byte[51200]);
            
            var serverStarted = new ManualResetEvent(false);
            var serverDone = new ManualResetEvent(false);
            bool serverOk = false;
            string serverError = null;
            
            var server = new TrFileTransfer.TransferUdtServer("127.0.0.1", port, recvDir);
            server.OnLog += msg => Console.WriteLine("[SRV] " + msg);
            server.OnStarted += () => { Console.WriteLine("[SRV] Started"); serverStarted.Set(); };
            server.OnTransferComplete += () => { Console.WriteLine("[SRV] Complete"); serverOk = true; serverDone.Set(); };
            server.OnError += msg => { Console.WriteLine("[SRV] Error: " + msg); serverError = msg; serverDone.Set(); };
            server.Start();
            
            if (!serverStarted.WaitOne(5000)) { Console.WriteLine("FAIL: server start timeout"); return; }
            
            var clientDone = new ManualResetEvent(false);
            bool clientOk = false;
            var client = new TrFileTransfer.TransferUdtClient("127.0.0.1", port, testFile);
            client.OnLog += msg => Console.WriteLine("[CLI] " + msg);
            client.OnTransferComplete += () => { Console.WriteLine("[CLI] Complete"); clientOk = true; clientDone.Set(); };
            client.OnError += msg => { Console.WriteLine("[CLI] Error: " + msg); };
            client.OnStopped += () => { Console.WriteLine("[CLI] Stopped"); if (!clientOk) clientDone.Set(); };
            
            var sendTask = client.SendAsync();
            Console.WriteLine("[CLI] SendAsync returned");
            
            if (!serverDone.WaitOne(30000)) Console.WriteLine("FAIL: server timeout");
            else if (!serverOk) Console.WriteLine("FAIL: server error: " + (serverError ?? "?"));
            else Console.WriteLine("PASS: server OK");
            
            sendTask.Wait(10000);
            Console.WriteLine("Client ok: " + clientOk);
        } catch (Exception ex) {
            Console.WriteLine("EXCEPTION: " + ex);
        } finally {
            try { Directory.Delete(sendDir, true); } catch { }
            try { Directory.Delete(recvDir, true); } catch { }
        }
    }
}
