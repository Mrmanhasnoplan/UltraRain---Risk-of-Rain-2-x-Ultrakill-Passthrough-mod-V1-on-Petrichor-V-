using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace V1Bridge.Ror
{
    /// <summary>Risk of Rain 2's end of the link: keeps trying 127.0.0.1 until UkBridge answers, says HELLO, and hands
    /// lines to the main thread.</summary>
    internal static class UkLink
    {
        static readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        static StreamWriter writer;
        static readonly object wlock = new object();
        public static volatile bool Connected;
        /// <summary>UkBridge's HELLO came back with our protocol number.</summary>
        public static bool Ready { get; private set; }
        /// <summary>key=value pairs from UkBridge's HELLO (unity, pid, guards, scene...), plus "version".</summary>
        public static readonly Dictionary<string, string> Peer = new Dictionary<string, string>();
        public static string Problem;
        /// <summary>Lines nothing here consumed, for whoever is listening (the test mode).</summary>
        public static event Action<string, string> OnMessage;

        // ping round trips, in milliseconds (answered on UkBridge's socket thread, timed on ours)
        public static readonly List<double> PingMs = new List<double>();
        static readonly object plock = new object();

        public static void Start(int port)
        {
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var c = new TcpClient())
                        {
                            c.Connect("127.0.0.1", port);
                            c.NoDelay = true;
                            var s = c.GetStream();
                            lock (wlock) writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                            Connected = true;
                            inbox.Enqueue("CONNECTED");
                            Send(Proto.Hello + " " + Proto.Protocol + " rorbridge " + Proto.Version + " pid=" + Process.GetCurrentProcess().Id);
                            var r = new StreamReader(s, Encoding.UTF8);
                            string line;
                            while ((line = r.ReadLine()) != null)
                            {
                                if (line.StartsWith(Proto.Pong + " "))
                                {
                                    if (long.TryParse(line.Substring(Proto.Pong.Length + 1), out var sent))
                                    {
                                        double ms = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
                                        lock (plock) PingMs.Add(ms);
                                    }
                                    continue;
                                }
                                inbox.Enqueue(line);
                            }
                        }
                    }
                    catch (SocketException) { /* ULTRAKILL isn't up (yet) */ }
                    catch (IOException) { }
                    catch (Exception e) { Plugin.Log.LogWarning("link: " + e.Message); }
                    lock (wlock) writer = null;
                    if (Connected) inbox.Enqueue("DISCONNECTED");
                    Connected = false;
                    Thread.Sleep(500);
                }
            }) { IsBackground = true, Name = "RorBridge link" }.Start();
        }

        public static void Send(string line)
        {
            lock (wlock)
            {
                if (writer == null) return;
                try { writer.WriteLine(line); } catch { writer = null; }
            }
        }

        public static void Ping() => Send(Proto.Ping + " " + Stopwatch.GetTimestamp());

        public static double[] PingSnapshot()
        {
            lock (plock) return PingMs.ToArray();
        }

        /// <summary>Main thread, every frame.</summary>
        public static void Pump()
        {
            while (inbox.TryDequeue(out var line))
            {
                int sp = line.IndexOf(' ');
                string cmd = sp < 0 ? line : line.Substring(0, sp);
                string rest = sp < 0 ? "" : line.Substring(sp + 1);
                try
                {
                    switch (cmd)
                    {
                        case "CONNECTED":
                            Plugin.Log.LogInfo("connected to UkBridge");
                            break;
                        case "DISCONNECTED":
                            Plugin.Log.LogInfo("UkBridge disconnected");
                            Ready = false;
                            break;
                        case Proto.Hello:
                            OnHello(rest);
                            break;
                    }
                    OnMessage?.Invoke(cmd, rest);
                }
                catch (Exception e) { Plugin.Log.LogWarning("handle '" + line + "': " + e); }
            }
        }

        static void OnHello(string rest)
        {
            var a = rest.Split(' ');
            Peer.Clear();
            if (a.Length > 2) Peer["version"] = a[2];
            for (int i = 3; i < a.Length; i++)
            {
                int eq = a[i].IndexOf('=');
                if (eq > 0) Peer[a[i].Substring(0, eq)] = a[i].Substring(eq + 1);
            }
            if (a.Length < 2 || a[0] != Proto.Protocol.ToString() || a[1] != "ukbridge")
            {
                Problem = "UkBridge speaks protocol '" + (a.Length > 0 ? a[0] : "?") + "', RorBridge " + Proto.Protocol + ": rebuild both plugins";
                Plugin.Log.LogError(Problem);
                return;
            }
            Problem = null;
            Ready = true;
            Plugin.Log.LogInfo("handshake done: UkBridge " + rest);
        }
    }
}
