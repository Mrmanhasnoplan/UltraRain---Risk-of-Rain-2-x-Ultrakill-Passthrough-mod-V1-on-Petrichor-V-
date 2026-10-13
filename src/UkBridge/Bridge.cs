using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace V1Bridge.Uk
{
    public partial class Bridge : MonoBehaviour
    {
        public static Bridge I;
        public int TestCoins;
        int parentPid;
        float nextPark, nextParentCheck;
        bool quitting;

        void Awake()
        {
            I = this;
            int port = int.TryParse(Plugin.Arg(Proto.UkArgPort), out var p) ? p : Proto.DefaultPort;
            int.TryParse(Plugin.Arg(Proto.UkArgParent), out parentPid);
            try { Net.Start(port); }
            catch (Exception e) { Plugin.Log.LogError("can't listen on " + port + ": " + e.Message); }
            try { MapFrames(); StartCoroutine(Capture()); }
            catch (Exception e) { Plugin.Log.LogError("frame file: " + e); }
            Win32.IgnoreTimerThrottling();
            Plugin.Log.LogInfo("listening on 127.0.0.1:" + port + (parentPid > 0 ? ", closing with process " + parentPid : ""));
        }

        void Update()
        {
            while (Net.Inbox.TryDequeue(out var line))
            {
                try { Handle(line); }
                catch (Exception e) { Plugin.Log.LogWarning("handle '" + line + "': " + e); }
            }
            UpdateLevel();
            UpdateModelExport();
            UpdateOil();
            SendCoins();
            SendFx();
            InjectInput();
            SendPose();
            // out of sight from the first frame: off-screen, never minimised (a minimised Unity window stops drawing)
            if (Time.unscaledTime > nextPark)
            {
                nextPark = Time.unscaledTime + (Net.Connected ? 2f : 0.25f);
                if (Screen.fullScreenMode != FullScreenMode.Windowed) Screen.SetResolution(wantW, wantH, FullScreenMode.Windowed);
                else Win32.ParkWindow();
            }
            // ULTRAKILL closes with Risk of Rain 2, however RoR2 went (a crash sends no QUIT)
            if (parentPid > 0 && Time.unscaledTime > nextParentCheck)
            {
                nextParentCheck = Time.unscaledTime + 2f;
                if (!Alive(parentPid)) Quit("Risk of Rain 2 (process " + parentPid + ") is gone");
            }
            Net.Flush();
        }

        static bool Alive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        void Quit(string why)
        {
            if (quitting) return;
            quitting = true;
            Plugin.Log.LogInfo("quitting: " + why);
            Application.Quit();
        }

        void Handle(string line)
        {
            int sp = line.IndexOf(' ');
            string cmd = sp < 0 ? line : line.Substring(0, sp);
            string rest = sp < 0 ? "" : line.Substring(sp + 1);
            switch (cmd)
            {
                case "CONNECTED":
                    Plugin.Log.LogInfo("Risk of Rain 2 connected");
                    modelSent = false;
                    break;
                case "DISCONNECTED":
                    Plugin.Log.LogInfo("Risk of Rain 2 disconnected");
                    // let go of everything, or V1 keeps running on the last keys held
                    ReleaseInput();
                    ClearProxies();
                    ClearShots();
                    idle = true;
                    hpCap = 999;
                    fxWanted = false;
                    break;
                case Proto.Size:
                {
                    var a = rest.Split(' ');
                    int w = int.Parse(a[0]), h = int.Parse(a[1]);
                    if ((long)w * h <= Proto.FrameMaxPixels && w >= 320 && h >= 180) { wantW = w; wantH = h; }
                    if (a.Length > 2) fpsCap = Mathf.Clamp(int.Parse(a[2]), 20, 1000);
                    break;
                }
                case Proto.Tp:
                    Teleport(rest);
                    break;
                case Proto.Geo:
                    LoadStage(rest);
                    break;
                case Proto.GeoClear:
                    ClearStage();
                    break;
                case Proto.Probe:
                    Net.Send(Proto.Probed + " " + Probe(rest));
                    break;
                case Proto.Ents:
                    ApplyEntities(rest);
                    break;
                case Proto.Parts:
                    ApplyParts(rest);
                    break;
                case Proto.Projs:
                    ApplyProjectiles(rest);
                    break;
                case Proto.Parried:
                    MeleeParried(rest);
                    break;
                case "TESTBLAST":
                {
                    // (tests) one of ULTRAKILL's own explosions that hurts nothing, as a rocket leaves when it lands by an enemy
                    var a = rest.Split(' ');
                    var drm = MonoSingleton<DefaultReferenceManager>.Instance;
                    if (drm == null || drm.explosion == null) break;
                    var go = Instantiate(drm.explosion, new Vector3(F(a[0]), F(a[1]), F(a[2])), Quaternion.identity);
                    foreach (var ex in go.GetComponentsInChildren<Explosion>()) { ex.damage = 0; ex.enemyDamageMultiplier = 1f; ex.harmless = false; ex.canHit = AffectedSubjects.EnemiesOnly; }
                    break;
                }
                case Proto.CoinHit:
                    CoinHit(rest);
                    break;
                case Proto.FxWanted:
                    SetFx(rest);
                    break;
                case "TESTARM":
                {
                    // (tests) the arm V1 punches with: 0 is the Feedbacker, the one that parries
                    var fc = MonoSingleton<FistControl>.Instance;
                    if (fc != null && int.TryParse(rest.Trim(), out int arm)) fc.ArmChange(arm);
                    break;
                }
                case "TESTOIL":
                {
                    // (tests) gasoline on that monster, as the Firestarter leaves it
                    if (int.TryParse(rest.Trim(), out int oid) && proxies.TryGetValue(oid, out var op) && op != null && op.eid != null) op.eid.AddFlammable(1f);
                    break;
                }
                case Proto.TestCoin:
                {
                    // a real coin from the revolver's own prefab, drifting slowly upward (a coin that stops moving is removed)
                    var a = rest.Split(' ');
                    Revolver rev = null;
                    foreach (var r in Resources.FindObjectsOfTypeAll<Revolver>()) if (r.coin != null) { rev = r; break; }
                    if (rev == null) { Plugin.Log.LogWarning("TESTCOIN: no revolver with coins"); break; }
                    var coin = Instantiate(rev.coin, new Vector3(F(a[0]), F(a[1]), F(a[2])), Quaternion.identity);
                    var crb = coin.GetComponent<Rigidbody>();
                    if (crb != null) { crb.useGravity = false; crb.velocity = Vector3.up * 2f; }
                    TestCoins++;
                    Plugin.Log.LogInfo("TESTCOIN at " + coin.transform.position + ", zappable " + (coin.GetComponent<Zappable>() != null));
                    break;
                }
                case Proto.Launch:
                    Launch(rest);
                    break;
                case Proto.Wet:
                    SetWet(rest);
                    break;
                case Proto.Hurt:
                    Hurt(rest);
                    break;
                case Proto.Cap:
                    if (!int.TryParse(rest.Trim(), out hpCap)) hpCap = 999;
                    break;
                case Proto.Heal:
                    Heal(rest);
                    break;
                case Proto.Kill:
                    Killed(rest);
                    break;
                case Proto.Respawn:
                    RespawnV1();
                    break;
                case Proto.Input:
                    OnInput(rest);
                    break;
                case Proto.Idle:
                    idle = rest.Trim() == "1";
                    if (idle) { ReleaseInput(); ClearProxies(); ClearShots(); }
                    break;
                case Proto.Hello:
                {
                    var a = rest.Split(' ');
                    bool match = a.Length > 0 && a[0] == Proto.Protocol.ToString();
                    if (!match) Plugin.Log.LogError("protocol mismatch: theirs '" + rest + "', ours " + Proto.Protocol);
                    Net.Send(Proto.Hello + " " + Proto.Protocol + " ukbridge " + Proto.Version + " unity=" + Application.unityVersion
                             + " pid=" + Process.GetCurrentProcess().Id + " guards=" + SaveGuard.Summary + " " + StateLine());
                    break;
                }
                case Proto.State:
                    Net.Send(Proto.State + " " + StateLine());
                    break;
                case Proto.Snap:
                    StartCoroutine(Snap(rest));
                    break;
                case Proto.Quit:
                    Quit("asked by Risk of Rain 2");
                    break;
                default:
                    Plugin.Log.LogWarning("unknown message: " + line);
                    break;
            }
        }

        /// <summary>The weapon V1 has out: its name and, for a revolver, which variant (2: the Sharpshooter).</summary>
        static string WeaponInfo()
        {
            try
            {
                var gc = MonoSingleton<GunControl>.Instance;
                if (gc == null || gc.currentWeapon == null) return "none";
                var rev = gc.currentWeapon.GetComponent<Revolver>();
                return gc.currentWeapon.name.Replace("(Clone)", "").Replace(' ', '_') + (rev != null ? "/revolver" + rev.gunVariation + (rev.altVersion ? "alt" : "") : "");
            }
            catch (Exception e) { return "err:" + e.Message.Replace(' ', '_'); }
        }

        string PulledInfo()
        {
            foreach (var p in proxies.Values)
            {
                if (p == null || !p.pulled) continue;
                return S(p.transform.position.x) + "," + S(p.transform.position.y) + "," + S(p.transform.position.z) + "/v" + S(p.rootRb.velocity.magnitude) + "/" + (p.rootRb.isKinematic ? "kin" : "dyn");
            }
            return "none";
        }

        int NailCount()
        {
            int n = 0;
            foreach (var p in proxies.Values) if (p != null && p.eid != null) n += p.eid.nailsAmount;
            return n;
        }

        static string HookInfo()
        {
            try
            {
                var h = MonoSingleton<HookArm>.Instance;
                if (h == null) return "none";
                return (h.equipped ? "eq" : "uneq") + "/" + h.state + "/" + (HarmonyLib.Traverse.Create(h).Field("caughtEid").GetValue() != null ? "enemy" : "-") + "/" + (HarmonyLib.Traverse.Create(h).Field("lightTarget").GetValue<bool>() ? "light" : "heavy");
            }
            catch (Exception e) { return "err:" + e.Message.Replace(' ', '_'); }
        }

        string StateLine()
        {
            return "scene=" + (SceneHelper.CurrentScene ?? "none").Replace(' ', '_') + " ready=" + (levelPrepared ? 1 : 0) + " landed=" + (v1Landed ? 1 : 0) + " frames=" + FramesPublished + " volumes=" + VolumeInfo + " audio=" + (levelPrepared ? AudioInfo() : "-") + " layer=" + (levelPrepared && MonoSingleton<NewMovement>.Instance != null ? MonoSingleton<NewMovement>.Instance.gameObject.layer : -1) + " proxies=" + proxies.Count + " parts=" + PartsShown +" dmg=" + DamageReports + " hook=" + HookInfo() + " pulled=" + PulledInfo() + " coins=" + (MonoSingleton<ObjectTracker>.Instance != null ? MonoSingleton<ObjectTracker>.Instance.zappablesList.Count : -1) + " nails=" + NailCount() + " conductions=" + Conductions + " launches=" + Launches + " knocks=" + Knocks + " pulls=" + Pulls + " shots=" + shots.Count + " blown=" + ShotsBlown + " coinhits=" + CoinHits + " fx=" + FxBeams + "/" + FxBlasts + "/" + FxFlying +" weapon=" + WeaponInfo() + " parries=" + Parries + " res=" + Screen.width + "x" + Screen.height
                   + " t=" + Time.unscaledTime.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " blocked=" + SaveGuard.Blocked
                   + " saves=" + GameProgressSaver.BaseSavePath.Replace(' ', '?');
        }

        /// <summary>What ULTRAKILL just drew, as a PNG (the window being parked off-screen doesn't matter).</summary>
        IEnumerator Snap(string path)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                Texture2D tex;
                if (finalRT != null && hookedCam != null)
                {
                    // once the Virtual Camera draws into our texture the window itself no longer changes
                    var prev = RenderTexture.active;
                    RenderTexture.active = finalRT;
                    tex = new Texture2D(finalRT.width, finalRT.height, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, finalRT.width, finalRT.height), 0, 0);
                    RenderTexture.active = prev;
                }
                else tex = ScreenCapture.CaptureScreenshotAsTexture();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Destroy(tex);
                Net.Send(Proto.Snapped + " " + path);
            }
            catch (Exception e) { Net.Send(Proto.SnapFail + " " + e.Message); }
        }
    }

    static class Win32
    {
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr process, int cls, ref PowerThrottling info, int size);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        delegate bool EnumProc(IntPtr h, IntPtr l);

        [StructLayout(LayoutKind.Sequential)]
        struct PowerThrottling { public uint Version, ControlMask, StateMask; }

        /// <summary>Windows 11 coarsens timers for processes without a visible window; opt out so the parked game keeps
        /// its frame rate.</summary>
        public static void IgnoreTimerThrottling()
        {
            try
            {
                var p = new PowerThrottling { Version = 1, ControlMask = 0x4 | 0x1, StateMask = 0 };
                SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref p, Marshal.SizeOf(p));
            }
            catch { }
        }

        public static void ParkWindow()
        {
            uint me = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (pid == me && IsWindowVisible(h))
                {
                    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
                    SetWindowPos(h, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
                return true;
            }, IntPtr.Zero);
        }
    }
}
