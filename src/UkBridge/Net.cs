using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace V1Bridge.Uk
{
    /// <summary>ULTRAKILL's end of the link: listens on loopback, one Risk of Rain 2 at a time. Lines land in
    /// <see cref="Inbox"/> and are handled on the main thread.</summary>
    internal static class Net
    {
        public static readonly ConcurrentQueue<string> Inbox = new ConcurrentQueue<string>();
        static TcpListener listener;
        static StreamWriter writer;
        static readonly object wlock = new object();
        public static volatile bool Connected;
        /// <summary>RoR2 has connected at least once (its going away afterwards means the session is over).</summary>
        public static volatile bool EverConnected;

        public static void Start(int port)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        var c = listener.AcceptTcpClient();
                        c.NoDelay = true;
                        var s = c.GetStream();
                        lock (wlock) writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                        Connected = true;
                        EverConnected = true;
                        Inbox.Enqueue("CONNECTED");
                        var r = new StreamReader(s, Encoding.UTF8);
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            // answered from this thread: a ping measures the link, not ULTRAKILL's frame time
                            if (line.StartsWith(Proto.Ping + " "))
                            {
                                Send(Proto.Pong + line.Substring(Proto.Ping.Length));
                                Flush();
                                continue;
                            }
                            Inbox.Enqueue(line);
                        }
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("net: " + e.Message); }
                    Connected = false;
                    lock (wlock) writer = null;
                    Inbox.Enqueue("DISCONNECTED");
                }
            }) { IsBackground = true, Name = "UkBridge net" }.Start();
        }

        public static void Send(string line)
        {
            lock (wlock)
            {
                if (writer == null) return;
                try { writer.WriteLine(line); } catch { writer = null; }
            }
        }

        public static void Flush()
        {
            lock (wlock)
            {
                try { writer?.Flush(); } catch { writer = null; }
            }
        }
    }
}
