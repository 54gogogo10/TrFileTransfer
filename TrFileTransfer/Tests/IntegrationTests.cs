using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer.Tests
{
    public static class IntegrationTests
    {
        private static int FindFreePort()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        public static void RunAll(TestRunner runner)
        {
            runner.Run("Integration_TCP_SingleFile", TcpSingleFile);
            runner.Run("Integration_TCP_Folder", TcpFolder);
            runner.Run("Integration_UDT_SingleFile", UdtSingleFile);
            runner.Run("Integration_UDT_Concurrent10", UdtConcurrent10);
        }

        private static void TcpSingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(Path.GetTempPath(), "tr_it_send_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(Path.GetTempPath(), "tr_it_recv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "hello.txt");
                var rng = new Random(42);
                var content = new byte[1024 * 50];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                var client = new TransferClient("127.0.0.1", port, testFile);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sendTask = client.SendAsync();

                if (!serverDone.WaitOne(30000))
                    throw new Exception("Server did not complete within 30s");
                if (!serverOk)
                    throw new Exception("Server error: " + (serverError ?? "unknown"));

                sendTask.Wait(30000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Client did not fire completion event");
                if (!clientOk)
                    throw new Exception("Client transfer failed");

                Thread.Sleep(300); // allow file flush to settle

                var receivedFile = Path.Combine(recvDir, "hello.txt");
                Assert.True(File.Exists(receivedFile), "received file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "content SHA256 match");
            }
            finally
            {
                try { if (server != null) server.Stop(); } catch { }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void TcpFolder()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(Path.GetTempPath(), "tr_it_fsend_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(Path.GetTempPath(), "tr_it_frecv_" + Guid.NewGuid().ToString("N"));
            string folderPath = Path.Combine(sendDir, "myFolder");
            Directory.CreateDirectory(folderPath);

            TransferServer server = null;
            try
            {
                var rng = new Random(123);
                var fileAContent = new byte[1024 * 10];
                var fileBContent = new byte[1024 * 15];
                rng.NextBytes(fileAContent);
                rng.NextBytes(fileBContent);
                File.WriteAllBytes(Path.Combine(folderPath, "a.bin"), fileAContent);
                File.WriteAllBytes(Path.Combine(folderPath, "b.bin"), fileBContent);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                server = new TransferServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("Server did not start within 5s");

                var client = new TransferClient("127.0.0.1", port, folderPath);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sendTask = client.SendFolderAsync(folderPath);

                if (!serverDone.WaitOne(30000))
                    throw new Exception("Server did not complete within 30s");
                if (!serverOk)
                    throw new Exception("Server error: " + (serverError ?? "unknown"));

                sendTask.Wait(30000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Client did not fire completion event");
                if (!clientOk)
                    throw new Exception("Client folder transfer failed");

                Thread.Sleep(300);

                Assert.True(Directory.Exists(recvDir), "receive dir exists");
                var receivedA = Path.Combine(recvDir, "myFolder", "a.bin");
                var receivedB = Path.Combine(recvDir, "myFolder", "b.bin");
                Assert.True(File.Exists(receivedA), "a.bin exists");
                Assert.True(File.Exists(receivedB), "b.bin exists");
                Assert.True(Utils.ConstantTimeEquals(fileAContent, File.ReadAllBytes(receivedA)), "a.bin match");
                Assert.True(Utils.ConstantTimeEquals(fileBContent, File.ReadAllBytes(receivedB)), "b.bin match");
            }
            finally
            {
                try { if (server != null) server.Stop(); } catch { }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void UdtSingleFile()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(Path.GetTempPath(), "tr_it_udt_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(Path.GetTempPath(), "tr_it_udt_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                var testFile = Path.Combine(sendDir, "udt_test.bin");
                var rng = new Random(99);
                var content = new byte[1024 * 50]; // 50 KB
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("UDT server did not start within 5s");

                var client = new TransferUdtClient("127.0.0.1", port, testFile);
                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                client.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                client.OnError += msg => clientDone.Set();

                var sendTask = client.SendAsync();

                if (!serverDone.WaitOne(60000))
                    throw new Exception("UDT server did not complete within 60s");
                if (!serverOk)
                    throw new Exception("UDT server error: " + (serverError ?? "unknown"));

                sendTask.Wait(60000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("UDT client did not fire completion event");
                if (!clientOk)
                    throw new Exception("UDT client transfer failed");

                Thread.Sleep(500);

                var receivedFile = Path.Combine(recvDir, "udt_test.bin");
                Assert.True(File.Exists(receivedFile), "received UDT file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "UDT file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "UDT content SHA256 match");
            }
            finally
            {
                try { if (server != null) server.Stop(); } catch { }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }

        private static void UdtConcurrent10()
        {
            int port = FindFreePort();
            string sendDir = Path.Combine(Path.GetTempPath(), "tr_it_udtc_s_" + Guid.NewGuid().ToString("N"));
            string recvDir = Path.Combine(Path.GetTempPath(), "tr_it_udtc_r_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sendDir);
            Directory.CreateDirectory(recvDir);

            TransferUdtServer server = null;
            try
            {
                // 10 MB file with 10 concurrent chunks (1 MB each)
                var testFile = Path.Combine(sendDir, "udt_concurrent_test.bin");
                var rng = new Random(42);
                var content = new byte[10 * 1024 * 1024];
                rng.NextBytes(content);
                File.WriteAllBytes(testFile, content);

                var serverStarted = new ManualResetEvent(false);
                var serverDone = new ManualResetEvent(false);
                bool serverOk = false;
                string serverError = null;

                server = new TransferUdtServer("127.0.0.1", port, recvDir);
                server.OnStarted += () => serverStarted.Set();
                server.OnTransferComplete += () => { serverOk = true; serverDone.Set(); };
                server.OnError += msg => { serverError = msg; serverDone.Set(); };
                server.Start();

                if (!serverStarted.WaitOne(5000))
                    throw new Exception("UDT server did not start within 5s");

                var clientDone = new ManualResetEvent(false);
                bool clientOk = false;
                var concurrent = new ConcurrentTransfer("127.0.0.1", port, testFile, 10, false);
                concurrent.OnTransferComplete += () => { clientOk = true; clientDone.Set(); };
                concurrent.OnError += msg => { clientDone.Set(); };

                var sendTask = concurrent.SendAsync();

                if (!serverDone.WaitOne(120000))
                    throw new Exception("UDT server did not complete within 120s");
                if (!serverOk)
                    throw new Exception("UDT server error: " + (serverError ?? "unknown"));

                sendTask.Wait(120000);
                if (!clientDone.WaitOne(5000))
                    throw new Exception("Concurrent client did not fire completion event");
                if (!clientOk)
                    throw new Exception("Concurrent transfer failed");

                Thread.Sleep(500);

                var receivedFile = Path.Combine(recvDir, "udt_concurrent_test.bin");
                Assert.True(File.Exists(receivedFile), "received concurrent file exists");
                var receivedContent = File.ReadAllBytes(receivedFile);
                Assert.Equal(content.Length, receivedContent.Length, "concurrent file size matches");
                Assert.True(Utils.ConstantTimeEquals(content, receivedContent), "concurrent content SHA256 match");
            }
            finally
            {
                try { if (server != null) server.Stop(); } catch { }
                try { Directory.Delete(sendDir, true); } catch { }
                try { Directory.Delete(recvDir, true); } catch { }
            }
        }
    }
}
