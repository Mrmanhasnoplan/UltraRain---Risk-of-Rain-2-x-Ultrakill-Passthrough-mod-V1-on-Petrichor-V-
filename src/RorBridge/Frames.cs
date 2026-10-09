using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using UnityEngine;
using Path = System.IO.Path;

namespace V1Bridge.Ror
{
    /// <summary>ULTRAKILL's frames, read from the file UkBridge maps (layout in Proto). A worker thread wakes on
    /// UkBridge's event, joins each frame's colour with its coverage into one RGBA image, and the main thread uploads
    /// the newest one. Coverage is the alpha ULTRAKILL's 3D target ended with, or the brightness of what it drew there
    /// without writing alpha (tracers, flashes, glows), so those lay their light over Risk of Rain 2's picture.</summary>
    internal static class Frames
    {
        static MemoryMappedFile mmf;
        static MemoryMappedViewAccessor view;
        static unsafe byte* basePtr;
        static EventWaitHandle frameEvent;
        static Thread worker;

        // two buffers: the worker fills one while the main thread may be uploading the other
        static byte[] filling = new byte[0], ready = new byte[0];
        static readonly object swap = new object();
        static Info readyInfo;
        static bool readyFresh;

        public struct Info
        {
            public int seq, w, h, inputSeq;
            public long drawnAt, inputAt;
            public bool poseValid;
            public Vector3 eye;
            public float yaw, pitch, roll, fov;
            /// <summary>Share of the picture ULTRAKILL covers (alpha over one half).</summary>
            public float coverage;
            public float mergeMs;
        }

        public static Texture2D Texture { get; private set; }
        /// <summary>The frame on screen now.</summary>
        public static Info Shown;
        public static int Uploaded;
        public static long LastUploadAt;
        public static string Problem;

        // latency samples of the frames shown, milliseconds
        public static readonly Stats FrameAge = new Stats(), InputAge = new Stats(), Merge = new Stats(), UploadMs = new Stats();

        public static long NowMicros() => Clock.Micros();
        public static bool Fresh => Uploaded > 0 && NowMicros() - LastUploadAt < 250_000;

        static unsafe bool Open()
        {
            if (basePtr != null) return true;
            try
            {
                string path = Path.Combine(Path.GetTempPath(), Proto.FrameFile);
                long need = Proto.FrameHeader + Proto.FrameSlots * Proto.FrameSlotBytes;
                if (!File.Exists(path) || new FileInfo(path).Length < need) return false;
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
                view = mmf.CreateViewAccessor(0, need, MemoryMappedFileAccess.Read);
                byte* p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                basePtr = p;
                frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Proto.FrameEvent);
                worker = new Thread(Work) { IsBackground = true, Name = "RorBridge frames" };
                worker.Start();
                Plugin.Log.LogInfo("frame file mapped");
                return true;
            }
            catch (Exception e)
            {
                if (Problem == null) Plugin.Log.LogWarning("frame file: " + e.Message);
                Problem = e.Message;
                return false;
            }
        }

        static unsafe void Work()
        {
            int last = 0;
            while (true)
            {
                frameEvent.WaitOne(100);
                try
                {
                    int* hdr = (int*)basePtr;
                    Thread.MemoryBarrier();
                    int seq = hdr[Proto.HdrSeq];
                    if (seq == last || seq == 0) continue;
                    int w = hdr[Proto.HdrW], h = hdr[Proto.HdrH], slot = hdr[Proto.HdrSlot];
                    if (hdr[Proto.HdrVersion] != Proto.FrameVersion || w <= 0 || h <= 0 || (long)w * h > Proto.FrameMaxPixels || slot < 0 || slot >= Proto.FrameSlots) continue;
                    var info = new Info { seq = seq, w = w, h = h, inputSeq = hdr[Proto.HdrInputSeq] };
                    info.drawnAt = *(long*)(basePtr + Proto.HdrDrawnAtByte);
                    info.inputAt = *(long*)(basePtr + Proto.HdrInputAtByte);
                    float* fp = (float*)(basePtr + Proto.HdrPoseByte);
                    info.poseValid = *(int*)(basePtr + Proto.HdrPoseValidByte) == 1;
                    info.eye = new Vector3(fp[0], fp[1], fp[2]);
                    info.yaw = fp[3];
                    info.pitch = fp[4];
                    info.roll = fp[5];
                    info.fov = fp[6];

                    long t0 = Stopwatch.GetTimestamp();
                    int n = w * h;
                    if (filling.Length != n * 4) filling = new byte[n * 4];
                    uint* c = (uint*)(basePtr + Proto.FrameHeader + slot * Proto.FrameSlotBytes);
                    uint* m = (uint*)((byte*)c + Proto.FrameMaxPixels * 4L);
                    int covered = 0;
                    fixed (byte* dst = filling)
                    {
                        uint* d = (uint*)dst;
                        for (int i = 0; i < n; i++)
                        {
                            // RGBA32 in memory: R is the low byte, A the high one
                            uint mk = m[i];
                            uint a = mk >> 24;
                            if (a < 255)
                            {
                                uint r = mk & 0xFF, g = (mk >> 8) & 0xFF, b = (mk >> 16) & 0xFF;
                                uint glow = r > g ? (r > b ? r : b) : (g > b ? g : b);
                                if (glow > a) a = glow;
                            }
                            if (a > 127) covered++;
                            d[i] = (c[i] & 0x00FFFFFFu) | (a << 24);
                        }
                    }
                    // the frame was replaced while it was being read: drop it, the next one is already signalled
                    Thread.MemoryBarrier();
                    if (hdr[Proto.HdrSeq] - seq >= Proto.FrameSlots - 1) continue;
                    info.coverage = covered / (float)n;
                    info.mergeMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                    lock (swap)
                    {
                        var x = ready;
                        ready = filling;
                        filling = x;
                        readyInfo = info;
                        readyFresh = true;
                    }
                    last = seq;
                }
                catch (Exception e) { Problem = e.Message; }
            }
        }

        /// <summary>Main thread, once a frame, before the cameras draw: put the newest frame in the texture. True when a
        /// new one went up.</summary>
        public static bool Update()
        {
            if (!Open()) return false;
            lock (swap)
            {
                if (!readyFresh) return false;
                readyFresh = false;
                var info = readyInfo;
                long t0 = Stopwatch.GetTimestamp();
                if (Texture == null || Texture.width != info.w || Texture.height != info.h)
                {
                    if (Texture != null) UnityEngine.Object.Destroy(Texture);
                    // the bytes are what ULTRAKILL would have put on screen: sRGB
                    Texture = new Texture2D(info.w, info.h, TextureFormat.RGBA32, false, false) { name = "ULTRAKILL frame", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                }
                Texture.LoadRawTextureData(ready);
                Texture.Apply(false, false);
                Shown = info;
                Uploaded++;
                LastUploadAt = NowMicros();
                UploadMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                FrameAge.Add((LastUploadAt - info.drawnAt) / 1000.0);
                if (info.inputAt > 0) InputAge.Add((LastUploadAt - info.inputAt) / 1000.0);
                Merge.Add(info.mergeMs);
                return true;
            }
        }
    }

    /// <summary>A window of the latest samples: average and 95th percentile.</summary>
    internal sealed class Stats
    {
        readonly double[] ring = new double[600];
        int count;

        public int Count => Math.Min(count, ring.Length);
        public void Add(double v) => ring[count++ % ring.Length] = v;
        public void Clear() => count = 0;

        public double Average
        {
            get
            {
                int n = Count;
                if (n == 0) return -1;
                double s = 0;
                for (int i = 0; i < n; i++) s += ring[i];
                return s / n;
            }
        }

        public double P95
        {
            get
            {
                int n = Count;
                if (n == 0) return -1;
                var a = new double[n];
                Array.Copy(ring, a, n);
                Array.Sort(a);
                return a[(int)((n - 1) * 0.95)];
            }
        }
    }
}
