using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RoR2;
using Path = System.IO.Path;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace V1Bridge.Ror
{
    /// <summary>Unattended runs: <c>--v1bridge-test &lt;scenario&gt; --v1bridge-out &lt;folder&gt;</c> skips the menus, runs the
    /// scenario's checks, saves screenshots from both games and results.json to the folder, and quits both games.
    /// Scenarios: handshake, survivor. <c>--v1bridge-stage &lt;scene&gt;</c> picks the stage runs start on.</summary>
    public class TestMode : MonoBehaviour
    {
        public const string ArgName = "--v1bridge-test";
        public const string ArgOut = "--v1bridge-out";
        public const string ArgStage = "--v1bridge-stage";

        string scenario, outDir;
        readonly List<string> checks = new List<string>();
        readonly Dictionary<string, string> metrics = new Dictionary<string, string>();
        bool failed;
        string snapReply;
        string stateReply;

        public void Begin(string name, string dir)
        {
            scenario = name;
            outDir = string.IsNullOrEmpty(dir) ? Path.Combine(Plugin.ProfileDir, "test-output") : dir;
            Directory.CreateDirectory(outDir);
            UkLink.OnMessage += OnMessage;
            Plugin.Log.LogInfo("test mode: scenario '" + scenario + "', output " + outDir);
            StartCoroutine(RunScenario());
        }

        void OnMessage(string cmd, string rest)
        {
            if (cmd == Proto.Snapped || cmd == Proto.SnapFail) snapReply = cmd + " " + rest;
            if (cmd == Proto.State) stateReply = rest;
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        bool Check(string name, bool pass, string detail = "")
        {
            checks.Add("{\"name\":" + Q(name) + ",\"pass\":" + (pass ? "true" : "false") + ",\"detail\":" + Q(detail) + "}");
            if (!pass) failed = true;
            Plugin.Log.LogInfo("[test] " + (pass ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? ": " + detail : ""));
            return pass;
        }

        IEnumerator RunScenario()
        {
            float started = Time.realtimeSinceStartup;
            IEnumerator body;
            switch (scenario)
            {
                case "handshake": body = Handshake(); break;
                case "survivor": body = Survivor(); break;
                case "picture": body = Picture(); break;
                default:
                    Check("known scenario", false, scenario);
                    body = null;
                    break;
            }
            // a scenario that hangs is a failure, not a game left running
            const float limit = 480f;
            var stack = new Stack<IEnumerator>();
            if (body != null) stack.Push(body);
            while (stack.Count > 0)
            {
                bool more;
                try { more = stack.Peek().MoveNext(); }
                catch (Exception e)
                {
                    Check("no exception", false, e.ToString());
                    break;
                }
                if (!more) { stack.Pop(); continue; }
                if (Time.realtimeSinceStartup - started > limit)
                {
                    Check("finished within " + limit + " s", false);
                    break;
                }
                // a step that yields another sequence runs it to the end first
                if (stack.Peek().Current is IEnumerator inner) { stack.Push(inner); continue; }
                yield return stack.Peek().Current;
            }
            metrics["seconds"] = F(Time.realtimeSinceStartup - started);
            Write();
            UkLauncher.Close();
            yield return new WaitForSecondsRealtime(0.5f);
            Application.Quit();
        }

        /// <summary>Risk of Rain 2 reaches its main menu, signed in on the sandboxed saves.</summary>
        IEnumerator ToTitle()
        {
            float t0 = Time.realtimeSinceStartup, introSince = -1f;
            while (SceneManager.GetActiveScene().name != "title" && Time.realtimeSinceStartup - t0 < 240f)
            {
                // not before the game has finished loading: objects in the intro are waiting for that
                if (SceneManager.GetActiveScene().name == "intro" && RoR2Application.loadFinished)
                {
                    if (introSince < 0f) introSince = Time.realtimeSinceStartup;
                    else if (Time.realtimeSinceStartup - introSince > 1f && RoR2.Console.instance != null)
                    {
                        RoR2.Console.instance.SubmitCmd(null, "set_scene title");
                        introSince = float.MaxValue;
                    }
                }
                yield return null;
            }
            metrics["ror2_title_seconds"] = F(Time.realtimeSinceStartup);
            if (!Check("RoR2 reached the main menu", SceneManager.GetActiveScene().name == "title", "scene " + SceneManager.GetActiveScene().name)) yield break;
            Check("RoR2 save guards applied", SaveGuard.Ok, SaveGuard.Summary);
            // the profiles are read a moment after the menu appears
            t0 = Time.realtimeSinceStartup;
            while (!SaveGuard.Active && Time.realtimeSinceStartup - t0 < 30f) yield return null;
            Check("RoR2 saves are sandboxed", SaveGuard.Active, SaveGuard.SandboxDir + ", seeded " + SaveGuard.Seeded);
            metrics["ror2_unity"] = Application.unityVersion;
            metrics["ror2_profiles_loaded"] = PlatformSystems.saveSystem != null ? PlatformSystems.saveSystem.loadedUserProfiles.Count.ToString() : "?";
            metrics["ror2_is_modded"] = RoR2Application.isModded.ToString();
            yield return new WaitForSecondsRealtime(2f);
            var user = LocalUserManager.GetFirstLocalUser();
            Check("RoR2 signed in to a sandboxed profile", user != null && user.userProfile != null && ReferenceEquals(user.userProfile.fileSystem, SaveGuard.Sandbox),
                user?.userProfile != null ? user.userProfile.name + " (" + user.userProfile.fileName + ")" : "no user signed in");
        }

        /// <summary>Phase 1: both plugins load, the saves are sandboxed, ULTRAKILL starts, the two shake hands, the link
        /// is quick, both can save a screenshot, and ULTRAKILL goes when told.</summary>
        IEnumerator Handshake()
        {
            yield return ToTitle();
            if (failed) yield break;

            // 2. ULTRAKILL starts and shakes hands
            float launchAt = Time.realtimeSinceStartup;
            if (!Check("ULTRAKILL launch started", UkLauncher.Launch(), UkLauncher.Problem ?? "")) yield break;
            while (!UkLink.Ready && UkLink.Problem == null && UkLauncher.Problem == null && Time.realtimeSinceStartup - launchAt < 180f) yield return null;
            metrics["uk_handshake_seconds"] = F(Time.realtimeSinceStartup - launchAt);
            if (!Check("handshake", UkLink.Ready, UkLink.Ready ? "protocol " + Proto.Protocol : UkLink.Problem ?? UkLauncher.Problem ?? (UkLink.Connected ? "connected, no HELLO" : "never connected"))) yield break;
            foreach (var kv in UkLink.Peer) metrics["uk_" + kv.Key] = kv.Value;
            UkLink.Peer.TryGetValue("guards", out var guards);
            Check("ULTRAKILL save guards applied", guards != null && guards.StartsWith("ok"), guards ?? "none reported");
            UkLink.Peer.TryGetValue("saves", out var saves);
            Check("ULTRAKILL saves are sandboxed", saves != null && saves.StartsWith(UkLauncher.UkProfile, StringComparison.OrdinalIgnoreCase), saves ?? "none reported");
            Check("ULTRAKILL process is running", UkLauncher.Running() != null);

            // 3. the link is quick
            for (int i = 0; i < 100; i++)
            {
                UkLink.Ping();
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.5f);
            var pings = UkLink.PingSnapshot();
            double sum = 0, max = 0;
            foreach (var p in pings) { sum += p; if (p > max) max = p; }
            double avg = pings.Length > 0 ? sum / pings.Length : -1;
            metrics["ping_count"] = pings.Length.ToString();
            metrics["ping_avg_ms"] = F(avg);
            metrics["ping_max_ms"] = F(max);
            Check("100 pings answered", pings.Length == 100, pings.Length + " answered");
            Check("ping under 2 ms on average", avg >= 0 && avg < 2.0, F(avg) + " ms avg, " + F(max) + " ms max");

            // 4. ULTRAKILL has got somewhere, and both games can save what they show
            yield return new WaitForSecondsRealtime(8f);
            stateReply = null;
            UkLink.Send(Proto.State);
            float t = Time.realtimeSinceStartup;
            while (stateReply == null && Time.realtimeSinceStartup - t < 30f) yield return null;
            metrics["uk_state_reply_seconds"] = F(Time.realtimeSinceStartup - t);
            Check("ULTRAKILL answers STATE", stateReply != null, stateReply ?? "");
            string ukPng = Path.Combine(outDir, "uk.png"), rorPng = Path.Combine(outDir, "ror2.png");
            if (File.Exists(ukPng)) File.Delete(ukPng);
            if (File.Exists(rorPng)) File.Delete(rorPng);
            snapReply = null;
            UkLink.Send(Proto.Snap + " " + ukPng);
            ScreenCapture.CaptureScreenshot(rorPng);
            t = Time.realtimeSinceStartup;
            while ((snapReply == null || !File.Exists(rorPng)) && Time.realtimeSinceStartup - t < 10f) yield return null;
            Check("ULTRAKILL screenshot saved", snapReply != null && snapReply.StartsWith(Proto.Snapped) && File.Exists(ukPng), snapReply ?? "no reply");
            Check("RoR2 screenshot saved", File.Exists(rorPng));

            // 5. ULTRAKILL goes when told
            var uk = UkLauncher.Running();
            UkLink.Send(Proto.Quit);
            t = Time.realtimeSinceStartup;
            while (UkLauncher.Running() != null && Time.realtimeSinceStartup - t < 20f) yield return new WaitForSecondsRealtime(0.25f);
            metrics["uk_quit_seconds"] = F(Time.realtimeSinceStartup - t);
            Check("ULTRAKILL closed on QUIT", uk != null && UkLauncher.Running() == null);
            Check("nothing tried to write to Steam's storage", SaveGuard.Blocked == 0, SaveGuard.Blocked + " writes turned away");
            metrics["ror2_achievements_not_sent"] = SaveGuard.AchievementsSkipped.ToString();
        }

        // ------------------------------------------------------------ shared steps

        float Now => Time.realtimeSinceStartup;

        /// <summary>RoR2's picture to <paramref name="name"/>.png in the output folder.</summary>
        IEnumerator SnapRor(string name)
        {
            string png = Path.Combine(outDir, name + ".png");
            if (File.Exists(png)) File.Delete(png);
            ScreenCapture.CaptureScreenshot(png);
            float t = Now;
            while (!File.Exists(png) && Now - t < 10f) yield return null;
            Check("RoR2 screenshot " + name, File.Exists(png));
        }

        IEnumerator SnapUk(string name)
        {
            string png = Path.Combine(outDir, name + ".png");
            if (File.Exists(png)) File.Delete(png);
            snapReply = null;
            UkLink.Send(Proto.Snap + " " + png);
            float t = Now;
            while (snapReply == null && Now - t < 15f) yield return null;
            Check("ULTRAKILL screenshot " + name, snapReply != null && snapReply.StartsWith(Proto.Snapped) && File.Exists(png), snapReply ?? "no reply");
        }

        /// <summary>From the main menu into character select, hosting alone.</summary>
        IEnumerator ToLobby()
        {
            RoR2.Console.instance.SubmitCmd(null, "transition_command \"gamemode ClassicRun; host 0;\"");
            float t = Now;
            while ((PreGameController.instance == null || LocalUserManager.GetFirstLocalUser()?.currentNetworkUser == null) && Now - t < 90f) yield return null;
            Check("entered character select", PreGameController.instance != null && LocalUserManager.GetFirstLocalUser()?.currentNetworkUser != null,
                "scene " + SceneManager.GetActiveScene().name);
            yield return new WaitForSecondsRealtime(2f);
        }

        IEnumerator Pick(SurvivorDef def)
        {
            var net = LocalUserManager.GetFirstLocalUser().currentNetworkUser;
            net.SetSurvivorPreferenceClient(def);
            var want = SurvivorCatalog.GetBodyIndexFromSurvivorIndex(def.survivorIndex);
            float t = Now;
            while (net.bodyIndexPreference != want && Now - t < 10f) yield return null;
            Check("picked " + def.cachedName, net.bodyIndexPreference == want);
            yield return new WaitForSecondsRealtime(2f);
        }

        /// <summary>Out of character select into a run, on --v1bridge-stage if given, until the local body exists.</summary>
        IEnumerator StartRun()
        {
            string stage = Plugin.Arg(ArgStage);
            if (!string.IsNullOrEmpty(stage))
            {
                var cv = typeof(Run).GetField("cvRunSceneOverride", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) as RoR2.ConVar.BaseConVar;
                if (Check("stage override available", cv != null && SceneCatalog.GetSceneDefFromSceneName(stage) != null, stage)) cv.SetString(stage);
            }
            var user = LocalUserManager.GetFirstLocalUser();
            RoR2.Console.instance.SubmitCmd(user.currentNetworkUser, "pregame_start_run");
            float t = Now;
            while ((Run.instance == null || user.cachedBody == null) && Now - t < 120f) yield return null;
            metrics["run_start_seconds"] = F(Now - t);
            Check("run started with a body", Run.instance != null && user.cachedBody != null, "scene " + SceneManager.GetActiveScene().name);
            if (!string.IsNullOrEmpty(stage)) Check("on the chosen stage", SceneManager.GetActiveScene().name == stage, SceneManager.GetActiveScene().name);
            // out of the drop pod's fade-in
            yield return new WaitForSecondsRealtime(4f);
        }

        IEnumerator BackToTitle()
        {
            RoR2.Console.instance.SubmitCmd(null, "disconnect");
            float t = Now;
            while ((SceneManager.GetActiveScene().name != "title" || Run.instance != null) && Now - t < 60f) yield return null;
            Check("back at the main menu", SceneManager.GetActiveScene().name == "title" && Run.instance == null, SceneManager.GetActiveScene().name);
            yield return new WaitForSecondsRealtime(2f);
        }

        // ------------------------------------------------------------ phase 2

        /// <summary>Phase 2: V1 is a survivor like any other in character select; a run as someone else leaves the
        /// bridge (and ULTRAKILL) off; picking V1 starts ULTRAKILL and a run as V1 switches the bridge on.</summary>
        IEnumerator Survivor()
        {
            yield return ToTitle();
            if (failed) yield break;

            // 1. the catalog entry
            var def = V1Survivor.Def;
            if (!Check("V1 survivor registered", def != null && V1Survivor.Problem == null, V1Survivor.Problem ?? "")) yield break;
            if (!Check("V1 is in the survivor catalog", def.survivorIndex != SurvivorIndex.None && SurvivorCatalog.GetSurvivorDef(def.survivorIndex) == def,
                "index " + (int)def.survivorIndex + " of " + SurvivorCatalog.survivorCount)) yield break;
            var body = def.bodyPrefab.GetComponent<CharacterBody>();
            Check("V1 body is in the body catalog", body.bodyIndex != BodyIndex.None && BodyCatalog.GetBodyName(body.bodyIndex) == V1Survivor.BodyName, BodyCatalog.GetBodyName(body.bodyIndex));
            string name = Language.GetString(def.displayNameToken), desc = Language.GetString(def.descriptionToken);
            Check("name", name == "V1", name);
            Check("description", desc != def.descriptionToken && desc.Length > 80, desc.Length + " characters");
            Check("icon", body.portraitIcon != null);
            Check("character select model", def.displayPrefab != null && def.displayPrefab.GetComponentsInChildren<Renderer>(true).Length > 0);
            var loc = def.bodyPrefab.GetComponent<SkillLocator>();
            var names = new List<string>();
            bool skillsOk = true;
            foreach (var slot in new[] { loc.primary, loc.secondary, loc.utility, loc.special })
            {
                var sd = slot != null && slot.skillFamily != null && slot.skillFamily.variants.Length == 1 ? slot.skillFamily.variants[0].skillDef : null;
                string n = sd != null ? Language.GetString(sd.skillNameToken) : "?";
                names.Add(n);
                skillsOk &= sd != null && sd.icon != null && n != sd.skillNameToken && Language.GetString(sd.skillDescriptionToken) != sd.skillDescriptionToken;
            }
            Check("four skill entries with names, descriptions and icons", skillsOk, string.Join(", ", names.ToArray()));
            Check("the bridge is off in the menu", !Session.Active && !Session.Wanted && !UkLauncher.Started && UkLauncher.Running() == null);

            // 2. another survivor: plain Risk of Rain 2
            var commando = SurvivorCatalog.FindSurvivorDef("Commando");
            if (!Check("Commando found", commando != null)) yield break;
            yield return ToLobby();
            if (failed) yield break;
            yield return Pick(commando);
            yield return StartRun();
            if (failed) yield break;
            var user = LocalUserManager.GetFirstLocalUser();
            Check("as Commando: the body is Commando's", BodyCatalog.GetBodyName(user.cachedBody.bodyIndex) == "CommandoBody", BodyCatalog.GetBodyName(user.cachedBody.bodyIndex));
            Check("as Commando: bridge off, ULTRAKILL not started", !Session.Active && !Session.Wanted && !UkLauncher.Started && UkLauncher.Running() == null && !UkLink.Connected);
            yield return SnapRor("ror2-run-commando");
            yield return BackToTitle();
            if (failed) yield break;

            // 3. V1: ULTRAKILL starts from character select, the run is bridged
            yield return ToLobby();
            if (failed) yield break;
            float pickedAt = Now;
            yield return Pick(def);
            yield return new WaitForSecondsRealtime(1f);
            yield return SnapRor("ror2-lobby-v1");
            while (!UkLink.Ready && UkLink.Problem == null && UkLauncher.Problem == null && Now - pickedAt < 120f) yield return null;
            metrics["pick_to_handshake_seconds"] = F(Now - pickedAt);
            if (!Check("picking V1 started ULTRAKILL and it shook hands", UkLink.Ready, UkLink.Ready ? "" : UkLink.Problem ?? UkLauncher.Problem ?? "timed out")) yield break;
            UkLink.Peer.TryGetValue("guards", out var guards);
            Check("ULTRAKILL save guards applied", guards != null && guards.StartsWith("ok"), guards ?? "none reported");
            yield return StartRun();
            if (failed) yield break;
            Check("as V1: the body is V1's", user.cachedBody != null && BodyCatalog.GetBodyName(user.cachedBody.bodyIndex) == V1Survivor.BodyName,
                user.cachedBody != null ? BodyCatalog.GetBodyName(user.cachedBody.bodyIndex) : "no body");
            Check("as V1: bridge on", Session.Active && Session.Wanted && UkLink.Ready);
            metrics["v1_max_health"] = user.cachedBody != null ? F(user.cachedBody.maxHealth) : "?";
            metrics["v1_damage"] = user.cachedBody != null ? F(user.cachedBody.damage) : "?";
            yield return SnapRor("ror2-run-v1");
            yield return SnapUk("uk");

            // 4. leaving the run switches it off; ULTRAKILL stays up until Risk of Rain 2 closes
            yield return BackToTitle();
            Check("after the run: bridge off", !Session.Active && !Session.Wanted);
            Check("after the run: ULTRAKILL still up for the next one", UkLauncher.Running() != null && UkLink.Connected);
            Check("nothing tried to write to Steam's storage", SaveGuard.Blocked == 0, SaveGuard.Blocked + " writes turned away");
        }

        // ------------------------------------------------------------ phase 3

        /// <summary>Into a run as V1 with ULTRAKILL landed and its frames arriving.</summary>
        IEnumerator V1Run()
        {
            yield return ToTitle();
            if (failed) yield break;
            if (!Check("V1 survivor registered", V1Survivor.Def != null, V1Survivor.Problem ?? "")) yield break;
            yield return ToLobby();
            if (failed) yield break;
            yield return Pick(V1Survivor.Def);
            float t = Now;
            while (!V1Drive.UkReady && UkLink.Problem == null && UkLauncher.Problem == null && Now - t < 150f) yield return null;
            metrics["uk_ready_seconds"] = F(Now - t);
            if (!Check("ULTRAKILL reached the Sandbox and stripped it", V1Drive.UkReady, V1Drive.UkReady ? "" : UkLink.Problem ?? UkLauncher.Problem ?? "last seen in " + V1Drive.UkLoading)) yield break;
            yield return StartRun();
            if (failed) yield break;
            t = Now;
            while (!V1Drive.UkLanded && Now - t < 30f) yield return null;
            if (!Check("V1 landed in ULTRAKILL", V1Drive.UkLanded, V1Drive.LandedInfo)) yield break;
            t = Now;
            while (Frames.Uploaded < 30 && Now - t < 30f) yield return null;
            Check("ULTRAKILL frames are arriving", Frames.Uploaded >= 30, Frames.Uploaded + " frames, " + (Frames.Problem ?? "no problem reported"));
            // the weapon draw animation and HUD fade-in
            yield return new WaitForSecondsRealtime(3f);
        }

        IEnumerator Hold(float seconds, V1Drive.Frame input)
        {
            V1Drive.Script = () => input;
            yield return new WaitForSecondsRealtime(seconds);
            V1Drive.Script = () => default;
        }

        /// <summary>Phase 3: ULTRAKILL's picture is in Risk of Rain 2's window, RoR2 looks at the stage from ULTRAKILL's
        /// view, keys and mouse reach V1, and the delay is measured.</summary>
        IEnumerator Picture()
        {
            // scripted input from the start: nothing real reaches V1 during the test
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var user = LocalUserManager.GetFirstLocalUser();

            // 1. the picture
            int f0 = Frames.Uploaded, shown0 = Time.frameCount;
            float t = Now;
            yield return new WaitForSecondsRealtime(5f);
            float ukFps = (Frames.Uploaded - f0) / (Now - t), rorFps = (Time.frameCount - shown0) / (Now - t);
            metrics["uk_fps_shown"] = F(ukFps);
            metrics["ror2_fps"] = F(rorFps);
            metrics["frame_size"] = Frames.Shown.w + "x" + Frames.Shown.h;
            metrics["frame_coverage"] = F(Frames.Shown.coverage);
            Check("at least 30 ULTRAKILL frames a second shown", ukFps >= 30f, F(ukFps) + " fps (RoR2 " + F(rorFps) + ")");
            Check("ULTRAKILL's layer covers part of the picture, not all of it", Frames.Shown.coverage > 0.01f && Frames.Shown.coverage < 0.7f, F(Frames.Shown.coverage * 100) + " %");
            metrics["overlay"] = V1Drive.OverlayInfo();
            yield return SnapRor("ror2-composite");
            yield return SnapUk("uk");

            // 2. RoR2 sees the stage from ULTRAKILL's view, and its body is where V1 is
            var cam = user.cameraRigController != null ? user.cameraRigController.sceneCam : null;
            if (Check("RoR2 scene camera found", cam != null))
            {
                float off = Vector3.Distance(cam.transform.position, V1Drive.ToRor(Frames.Shown.eye));
                float ang = Quaternion.Angle(cam.transform.rotation, Quaternion.Euler(Frames.Shown.pitch, Frames.Shown.yaw, Frames.Shown.roll));
                Check("RoR2 camera is at the frame's view", off < 0.05f && ang < 0.5f && Mathf.Abs(cam.fieldOfView - Frames.Shown.fov) < 0.5f,
                    F(off) + " m, " + F(ang) + " deg off, fov " + F(cam.fieldOfView) + " vs " + F(Frames.Shown.fov));
            }
            float bodyOff = Vector3.Distance(user.cachedBody.footPosition, V1Drive.ToRor(V1Drive.Latest.feet));
            Check("RoR2 body stands where V1 stands", bodyOff < 0.5f, F(bodyOff) + " m apart");

            // 3. input: look, walk, jump
            float yaw0 = V1Drive.Latest.yaw;
            yield return Hold(0.5f, new V1Drive.Frame { dx = 12f });
            yield return new WaitForSecondsRealtime(0.3f);
            float turned = Mathf.DeltaAngle(yaw0, V1Drive.Latest.yaw);
            metrics["look_degrees"] = F(turned);
            Check("mouse right turns V1 right", turned > 3f, F(turned) + " degrees");
            float pitch0 = V1Drive.Latest.pitch;
            yield return Hold(0.3f, new V1Drive.Frame { dy = 6f });
            yield return new WaitForSecondsRealtime(0.3f);
            float lookedUp = Mathf.DeltaAngle(V1Drive.Latest.pitch, pitch0);
            Check("mouse up looks up", lookedUp > 1f, F(lookedUp) + " degrees");

            Vector3 p0 = V1Drive.Latest.feet;
            Vector3 fwd = Quaternion.Euler(0f, V1Drive.Latest.yaw, 0f) * Vector3.forward;
            yield return Hold(1.5f, new V1Drive.Frame { keys = new[] { KeyCode.W } });
            yield return new WaitForSecondsRealtime(0.5f);
            Vector3 moved = V1Drive.Latest.feet - p0;
            metrics["walk_units"] = F(moved.magnitude);
            Check("W walks V1 forward", Vector3.Dot(moved, fwd) > 5f, F(Vector3.Dot(moved, fwd)) + " units forward in 1.5 s (" + F(moved.magnitude / V1Drive.K) + " m)");
            Check("V1 stops when the key is let go", V1Drive.Latest.velocity.magnitude < 1f, F(V1Drive.Latest.velocity.magnitude) + " units/s");

            float y0 = V1Drive.Latest.feet.y, top = y0;
            V1Drive.Script = () => new V1Drive.Frame { keys = new[] { KeyCode.Space } };
            t = Now;
            while (Now - t < 1.2f)
            {
                if (Now - t > 0.15f) V1Drive.Script = () => default;
                top = Mathf.Max(top, V1Drive.Latest.feet.y);
                yield return null;
            }
            metrics["jump_units"] = F(top - y0);
            Check("Space jumps", top - y0 > 1f, F(top - y0) + " units up");
            yield return new WaitForSecondsRealtime(1f);
            Check("V1 is back on the ground", V1Drive.Latest.grounded);

            // 4. the delay, measured while the view turns
            Frames.FrameAge.Clear();
            Frames.InputAge.Clear();
            Frames.Merge.Clear();
            Frames.UploadMs.Clear();
            yield return Hold(4f, new V1Drive.Frame { dx = 3f });
            metrics["frame_age_ms_avg"] = F(Frames.FrameAge.Average);
            metrics["frame_age_ms_p95"] = F(Frames.FrameAge.P95);
            metrics["input_to_upload_ms_avg"] = F(Frames.InputAge.Average);
            metrics["input_to_upload_ms_p95"] = F(Frames.InputAge.P95);
            metrics["merge_ms_avg"] = F(Frames.Merge.Average);
            metrics["upload_ms_avg"] = F(Frames.UploadMs.Average);
            Check("frames are shown within 50 ms of being drawn, on average", Frames.FrameAge.Count > 30 && Frames.FrameAge.Average < 50, F(Frames.FrameAge.Average) + " ms avg, " + F(Frames.FrameAge.P95) + " ms p95");
            Check("input shows up in a frame within 80 ms, on average", Frames.InputAge.Count > 30 && Frames.InputAge.Average < 80, F(Frames.InputAge.Average) + " ms avg, " + F(Frames.InputAge.P95) + " ms p95");
            yield return SnapRor("ror2-composite-after");

            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && user.cachedBody != null && user.cachedBody.healthComponent.alive);
            metrics["v1_hp"] = V1Drive.UkHp.ToString();
            metrics["v1_weapons_per_slot"] = V1Drive.LandedInfo;
            yield return BackToTitle();
            Check("nothing tried to write to Steam's storage", SaveGuard.Blocked == 0, SaveGuard.Blocked + " writes turned away");
        }

        static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        void Write()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"scenario\": ").Append(Q(scenario)).Append(",\n  \"pass\": ").Append(failed || checks.Count == 0 ? "false" : "true");
            sb.Append(",\n  \"version\": ").Append(Q(Proto.Version)).Append(",\n  \"checks\": [\n    ").Append(string.Join(",\n    ", checks.ToArray())).Append("\n  ],\n  \"metrics\": {");
            bool first = true;
            foreach (var kv in metrics)
            {
                sb.Append(first ? "\n    " : ",\n    ").Append(Q(kv.Key)).Append(": ").Append(Q(kv.Value));
                first = false;
            }
            sb.Append("\n  }\n}\n");
            File.WriteAllText(Path.Combine(outDir, "results.json"), sb.ToString());
            Plugin.Log.LogInfo("[test] " + (failed ? "FAILED" : "passed") + ", results in " + outDir);
        }
    }
}
