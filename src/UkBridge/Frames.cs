using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace V1Bridge.Uk
{
    // ULTRAKILL's picture, handed to Risk of Rain 2 through a memory-mapped file: the GPU readback of each frame lands
    // straight in a slot of the file (Ultracraft's scheme, MIT). Layout in Proto.
    public partial class Bridge
    {
        MemoryMappedFile mmf;
        MemoryMappedViewAccessor view;
        unsafe byte* frameBase;
        readonly NativeArray<byte>[] colorOut = new NativeArray<byte>[Proto.FrameSlots];
        readonly NativeArray<byte>[] maskOut = new NativeArray<byte>[Proto.FrameSlots];
        EventWaitHandle frameEvent;
        const int MaxInFlight = 2;
        int inFlight, nextSeq, publishedSeq;
        RenderTexture finalRT;
        Camera hookedCam;
        int wantW = 1280, wantH = 720, fpsCap = 120;
        bool idle;
        public int FramesPublished;

        /// <summary>Microseconds on the performance counter both games share.</summary>
        internal static long NowMicros() => Clock.Micros();

        unsafe void MapFrames()
        {
            var path = Path.Combine(Path.GetTempPath(), Proto.FrameFile);
            // never truncated: Risk of Rain 2 may still have it mapped from an earlier ULTRAKILL
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            long need = Proto.FrameHeader + Proto.FrameSlots * Proto.FrameSlotBytes;
            if (fs.Length < need) fs.SetLength(need);
            mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            view = mmf.CreateViewAccessor();
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            frameBase = p;
            for (int s = 0; s < Proto.FrameSlots; s++)
            {
                byte* slot = frameBase + Proto.FrameHeader + s * Proto.FrameSlotBytes;
                colorOut[s] = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(slot, Proto.FrameMaxPixels * 4, Allocator.None);
                maskOut[s] = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(slot + Proto.FrameMaxPixels * 4L, Proto.FrameMaxPixels * 4, Allocator.None);
            }
            int* hdr = (int*)frameBase;
            hdr[Proto.HdrSeq] = 0;
            hdr[Proto.HdrVersion] = Proto.FrameVersion;
            frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Proto.FrameEvent);
        }

        /// <summary>ULTRAKILL draws its final, post-processed image with the "Virtual Camera". It is pointed at our own
        /// texture, so the frame doesn't depend on the parked window.</summary>
        void LateUpdate()
        {
            if (!levelPrepared) return;
            if (Screen.width != wantW || Screen.height != wantH) Screen.SetResolution(wantW, wantH, FullScreenMode.Windowed);
            var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
            var vc = pp != null ? pp.virtualCam : null;
            if (vc == null || !Net.Connected) return;
            int w = Screen.width, h = Screen.height;
            if (finalRT == null || finalRT.width != w || finalRT.height != h)
            {
                if (finalRT != null)
                {
                    finalRT.Release();
                    Destroy(finalRT);
                }
                finalRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "V1Bridge frame" };
                finalRT.Create();
            }
            if (vc.targetTexture != finalRT) vc.targetTexture = finalRT;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            // nothing to show while Risk of Rain 2 isn't in a V1 run: tick over, leaving it the graphics card
            int cap = idle ? 10 : fpsCap;
            if (Application.targetFrameRate != cap) Application.targetFrameRate = cap;
            hookedCam = vc;
        }

        IEnumerator Capture()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                if (!Net.Connected || idle || inFlight >= MaxInFlight || finalRT == null || hookedCam == null) continue;
                int w = finalRT.width, h = finalRT.height;
                if (w * h > Proto.FrameMaxPixels) continue;
                var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
                var mask = pp != null ? pp.mainTex : null;
                if (mask == null || mask.width != w || mask.height != h) continue;
                int seq = ++nextSeq;
                var job = new Readback { seq = seq, slot = seq % Proto.FrameSlots, w = w, h = h, drawnAt = NowMicros(), inputSeq = appliedInputSeq, inputAt = appliedInputAt };
                var cc = MonoSingleton<CameraController>.Instance;
                if (cc != null && cc.cam != null)
                {
                    var ct = cc.cam.transform;
                    var e = ct.eulerAngles;
                    job.pose = new[] { ct.position.x, ct.position.y, ct.position.z, e.y, e.x, e.z, cc.cam.fieldOfView };
                }
                var co = colorOut[job.slot];
                var mo = maskOut[job.slot];
                inFlight++;
                AsyncGPUReadback.RequestIntoNativeArray(ref co, finalRT, 0, TextureFormat.RGBA32, r => ReadbackDone(job, r));
                AsyncGPUReadback.RequestIntoNativeArray(ref mo, mask, 0, TextureFormat.RGBA32, r => ReadbackDone(job, r));
            }
        }

        class Readback
        {
            public int seq, slot, w, h, done, inputSeq;
            public long drawnAt, inputAt;
            public bool failed;
            public float[] pose;
        }

        /// <summary>Both halves of a frame are in its slot: publish it (everything else first, the sequence number last).</summary>
        unsafe void ReadbackDone(Readback job, AsyncGPUReadbackRequest r)
        {
            if (r.hasError) job.failed = true;
            if (++job.done < 2) return;
            inFlight--;
            if (job.failed || job.seq <= publishedSeq) return;
            int* hdr = (int*)frameBase;
            Thread.MemoryBarrier();
            hdr[Proto.HdrW] = job.w;
            hdr[Proto.HdrH] = job.h;
            hdr[Proto.HdrSlot] = job.slot;
            hdr[Proto.HdrVersion] = Proto.FrameVersion;
            hdr[Proto.HdrInputSeq] = job.inputSeq;
            *(long*)(frameBase + Proto.HdrDrawnAtByte) = job.drawnAt;
            *(long*)(frameBase + Proto.HdrInputAtByte) = job.inputAt;
            float* fp = (float*)(frameBase + Proto.HdrPoseByte);
            if (job.pose != null) for (int i = 0; i < 7; i++) fp[i] = job.pose[i];
            *(int*)(frameBase + Proto.HdrPoseValidByte) = job.pose != null ? 1 : 0;
            Thread.MemoryBarrier();
            hdr[Proto.HdrSeq] = job.seq;
            publishedSeq = job.seq;
            FramesPublished++;
            frameEvent.Set();
        }
    }
}
