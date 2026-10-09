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
        string probeReply;

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
            if (cmd == Proto.Probed) probeReply = rest;
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
                case "geometry": body = Geometry(); break;
                case "combat": body = Combat(); break;
                case "parry": body = ParryTest(); break;
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

        // ------------------------------------------------------------ phase 4

        /// <summary>Phase 4: the stage's colliders exist in ULTRAKILL where RoR2 has them, and V1 stands and walks on the
        /// stage's ground.</summary>
        IEnumerator Geometry()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var user = LocalUserManager.GetFirstLocalUser();
            var st = V1Drive.Stage;
            metrics["stage_colliders"] = st.objects.ToString();
            metrics["stage_triangles"] = st.triangles.ToString();
            metrics["stage_meshes_read_from_gpu"] = st.unreadable.ToString();
            metrics["stage_skipped"] = st.skipped.ToString();
            metrics["stage_export_ms"] = F(st.ms);
            metrics["stage_bounds"] = st.bounds.min + " .. " + st.bounds.max;
            metrics["stage_loaded"] = V1Drive.StageLoaded ?? "no reply";
            Check("stage exported", st.objects > 10 && st.triangles > 5000 && st.problem == null, st.objects + " colliders, " + st.triangles + " triangles" + (st.problem != null ? ", " + st.problem : ""));
            var done = (V1Drive.StageLoaded ?? "").Split(' ');
            Check("ULTRAKILL built every collider", done.Length >= 4 && done[0] == st.objects.ToString() && done[1] == st.triangles.ToString() && done[3] == "ok", V1Drive.StageLoaded ?? "no GEODONE");

            // 1. the same ground in both games: straight down from the same points, a grid round the start
            int mask = LayerIndex.world.mask;
            int sampled = 0, same = 0, missing = 0;
            float worst = 0f;
            for (int gx = -4; gx <= 4; gx++)
            {
                for (int gz = -4; gz <= 4; gz++)
                {
                    var from = V1Drive.Origin + new Vector3(gx * 12f, 80f, gz * 12f);
                    if (!Physics.Raycast(from, Vector3.down, out var hit, 400f, mask, QueryTriggerInteraction.Ignore)) continue;
                    var uk = V1Drive.ToUk(from);
                    probeReply = null;
                    UkLink.Send(Proto.Probe + " " + F(uk.x) + " " + F(uk.y) + " " + F(uk.z));
                    float t = Now;
                    while (probeReply == null && Now - t < 5f) yield return null;
                    sampled++;
                    if (probeReply == null || probeReply.StartsWith("none")) { missing++; continue; }
                    float ukY = float.Parse(probeReply.Split(' ')[0], CultureInfo.InvariantCulture);
                    float diff = Mathf.Abs(V1Drive.ToRor(new Vector3(uk.x, ukY, uk.z)).y - hit.point.y);
                    worst = Mathf.Max(worst, diff);
                    if (diff < 0.1f) same++;
                }
            }
            metrics["ground_points_sampled"] = sampled.ToString();
            metrics["ground_points_matching"] = same.ToString();
            metrics["ground_worst_difference_m"] = F(worst);
            Check("the ground is at the same height in both games", sampled >= 20 && same >= sampled * 0.95f, same + " of " + sampled + " points within 10 cm, " + missing + " with nothing under them in ULTRAKILL, worst " + F(worst) + " m");

            // 2. V1 stands on it
            yield return new WaitForSecondsRealtime(1.5f);
            Check("V1 is standing on the ground", V1Drive.Latest.grounded, "feet " + V1Drive.ToRor(V1Drive.Latest.feet));
            yield return StandingCheck("at the start");
            yield return SnapRor("ror2-start");

            // 3. and walks over it: the ground rises and falls under V1, V1 stays on it
            Vector3 p0 = V1Drive.ToRor(V1Drive.Latest.feet);
            float minY = p0.y, maxY = p0.y;
            V1Drive.Script = () => new V1Drive.Frame { keys = new[] { KeyCode.W } };
            float w0 = Now;
            while (Now - w0 < 4f)
            {
                float y = V1Drive.ToRor(V1Drive.Latest.feet).y;
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
                yield return null;
            }
            V1Drive.Script = () => default;
            yield return new WaitForSecondsRealtime(1f);
            Vector3 p1 = V1Drive.ToRor(V1Drive.Latest.feet);
            float flat = new Vector2(p1.x - p0.x, p1.z - p0.z).magnitude;
            metrics["walked_m"] = F(flat);
            metrics["walk_height_range_m"] = F(maxY - minY);
            Check("V1 walked across the stage", flat > 8f, F(flat) + " m, height changing by " + F(maxY - minY) + " m on the way");
            Check("V1 is still on the ground after walking", V1Drive.Latest.grounded);
            yield return StandingCheck("after walking");
            Check("V1 did not fall out of the stage", p1.y > st.bounds.min.y - 5f, "feet at " + F(p1.y) + ", stage bottom " + F(st.bounds.min.y));
            yield return SnapRor("ror2-after-walk");
            yield return SnapUk("uk");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && user.cachedBody != null && user.cachedBody.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>V1's feet against RoR2's own ground under them.</summary>
        IEnumerator StandingCheck(string when)
        {
            var feet = V1Drive.ToRor(V1Drive.Latest.feet);
            bool hit = Physics.Raycast(feet + Vector3.up * 2f, Vector3.down, out var h, 50f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
            float gap = hit ? feet.y - h.point.y : 999f;
            Check("V1's feet are on RoR2's ground " + when, hit && Mathf.Abs(gap) < 0.25f, hit ? F(gap) + " m above " + h.collider.name : "no RoR2 ground below V1");
            var body = LocalUserManager.GetFirstLocalUser().cachedBody;
            Check("RoR2's body is at V1's feet " + when, body != null && Vector3.Distance(body.footPosition, feet) < 0.3f, body != null ? F(Vector3.Distance(body.footPosition, feet)) + " m" : "no body");
            yield break;
        }

        // ------------------------------------------------------------ phase 5

        readonly HashSet<CharacterMaster> testMasters = new HashSet<CharacterMaster>();

        /// <summary>A monster on the ground <paramref name="metres"/> ahead of V1, standing still unless its AI is left on.</summary>
        CharacterBody SpawnAhead(string masterName, float metres, bool ai)
        {
            var feet = V1Drive.ToRor(V1Drive.Latest.feet);
            var ahead = feet + Quaternion.Euler(0f, V1Drive.Latest.yaw, 0f) * Vector3.forward * metres;
            if (Physics.Raycast(ahead + Vector3.up * 30f, Vector3.down, out var hit, 100f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) ahead = hit.point;
            var master = new MasterSummon
            {
                masterPrefab = MasterCatalog.FindMasterPrefab(masterName), position = ahead + Vector3.up * 0.5f, rotation = Quaternion.identity,
                teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true
            }.Perform();
            if (master == null) return null;
            testMasters.Add(master);
            if (!ai) foreach (var a in master.GetComponents<RoR2.CharacterAI.BaseAI>()) a.enabled = false;
            var spawned = master.GetBody();
            // what the director gives a monster it spawns
            var rewards = spawned != null ? spawned.GetComponent<DeathRewards>() : null;
            if (rewards != null) { rewards.goldReward = 12; rewards.expReward = 20; }
            return spawned;
        }

        /// <summary>Turn V1 to look straight at a point in the stage.</summary>
        IEnumerator AimAt(Func<Vector3> target)
        {
            for (int i = 0; i < 2; i++)
            {
                Vector3 dir = V1Drive.ToUk(target()) - V1Drive.Latest.eye;
                float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, pitch = -Mathf.Asin(dir.normalized.y) * Mathf.Rad2Deg;
                var f = V1Drive.Latest.feet;
                UkLink.Send(Proto.Tp + " " + F(f.x) + " " + F(f.y) + " " + F(f.z) + " " + F(yaw) + " " + F(pitch));
                yield return new WaitForSecondsRealtime(0.4f);
            }
        }

        IEnumerator Tap(V1Drive.Frame input, float seconds = 0.12f)
        {
            V1Drive.Script = () => input;
            yield return new WaitForSecondsRealtime(seconds);
            V1Drive.Script = () => default;
        }

        IEnumerator UkState()
        {
            stateReply = null;
            UkLink.Send(Proto.State);
            float t = Now;
            while (stateReply == null && Now - t < 5f) yield return null;
        }

        static string StateValue(string state, string key)
        {
            foreach (var kv in (state ?? "").Split(' ')) if (kv.StartsWith(key + "=")) return kv.Substring(key.Length + 1);
            return null;
        }

        /// <summary>Phase 5: monsters are ULTRAKILL targets; V1's shots and punches hurt and kill them through Risk of
        /// Rain 2 (gold, experience); RoR2's damage and healing reach V1's ULTRAKILL health, and a monster's own attack
        /// hurts V1.</summary>
        IEnumerator Combat()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var user = LocalUserManager.GetFirstLocalUser();
            var v1 = user.cachedBody;
            // who hurts V1 during the test, for the record
            var attackers = new List<string>();
            GlobalEventManager.onServerDamageDealt += report =>
            {
                if (report.victimBody != v1) return;
                string who = (report.attackerBody != null ? report.attackerBody.name : report.damageInfo != null && report.damageInfo.inflictor != null ? report.damageInfo.inflictor.name : "nobody") + " " + F(report.damageDealt);
                if (attackers.Count < 12) attackers.Add(who);
            };
            // a quiet stage: only the monsters the test brings
            foreach (var d in FindObjectsOfType<CombatDirector>()) d.enabled = false;
            CharacterBody.onBodyStartGlobal += b =>
            {
                // anything the stage still sends is removed as it arrives
                RoR2Application.onNextUpdate += () =>
                {
                    if (b != null && b != v1 && b.teamComponent != null && b.teamComponent.teamIndex != TeamIndex.Player && !testMasters.Contains(b.master) && b.healthComponent != null) b.healthComponent.Suicide();
                };
            };
            foreach (var b in new List<CharacterBody>(CharacterBody.readOnlyInstancesList))
            {
                if (b != null && b != v1 && b.teamComponent != null && b.teamComponent.teamIndex == TeamIndex.Monster && b.healthComponent != null) b.healthComponent.Suicide();
            }
            yield return new WaitForSecondsRealtime(1.5f);
            float unit = Monsters.UnitDamage(v1);
            metrics["unit_damage"] = F(unit);

            // 1. a monster is a target in ULTRAKILL
            var lem = SpawnAhead("LemurianMaster", 9f, false);
            if (!Check("spawned a Lemurian", lem != null)) yield break;
            yield return new WaitForSecondsRealtime(2f);
            yield return UkState();
            Check("ULTRAKILL has a stand-in for every monster", Monsters.Listed >= 1 && StateValue(stateReply, "proxies") == Monsters.Listed.ToString(),
                Monsters.Listed + " monsters, ULTRAKILL has " + StateValue(stateReply, "proxies"));

            // 2. shooting it
            yield return AimAt(() => lem.corePosition);
            yield return SnapRor("ror2-aiming");
            float hp0 = lem.healthComponent.combinedHealth;
            int hits0 = Monsters.HitsApplied;
            float t = Now;
            for (int attempt = 0; attempt < 3 && Monsters.HitsApplied == hits0; attempt++)
            {
                yield return AimAt(() => lem.corePosition);
                yield return Tap(new V1Drive.Frame { buttons = 1 });
                t = Now;
                while (Monsters.HitsApplied == hits0 && Now - t < 1.5f) yield return null;
            }
            float took = hp0 - lem.healthComponent.combinedHealth;
            metrics["first_shot"] = Monsters.LastHit;
            metrics["shot_to_damage_seconds"] = F(Now - t);
            Check("a revolver shot hurt the Lemurian in Risk of Rain 2", Monsters.HitsApplied > hits0 && took > unit * 0.5f, Monsters.LastHit + " (of " + F(hp0) + " health)");

            // 3. killing it: Risk of Rain 2's kill, with its rewards
            uint gold0 = v1.master.money;
            ulong xp0 = TeamManager.instance.GetTeamExperience(TeamIndex.Player);
            int kills0 = Monsters.KillsByV1, shots = 1;
            while (lem != null && lem.healthComponent.alive && shots < 14)
            {
                yield return AimAt(() => lem != null ? lem.corePosition : Vector3.zero);
                if (lem == null || !lem.healthComponent.alive) break;
                yield return Tap(new V1Drive.Frame { buttons = 1 });
                shots++;
                yield return new WaitForSecondsRealtime(0.6f);
            }
            yield return new WaitForSecondsRealtime(1.5f);
            metrics["shots_to_kill_lemurian"] = shots.ToString();
            Check("V1 killed the Lemurian", (lem == null || !lem.healthComponent.alive) && Monsters.KillsByV1 > kills0, shots + " shots");
            Check("the kill paid gold and experience", v1.master.money > gold0 && TeamManager.instance.GetTeamExperience(TeamIndex.Player) > xp0,
                "gold " + gold0 + " -> " + v1.master.money + ", experience " + xp0 + " -> " + TeamManager.instance.GetTeamExperience(TeamIndex.Player));
            yield return UkState();
            Check("the dead monster's stand-in is gone", StateValue(stateReply, "proxies") == Monsters.Listed.ToString(), "ULTRAKILL has " + StateValue(stateReply, "proxies") + ", monsters " + Monsters.Listed);

            // 4. punching one
            var lem2 = SpawnAhead("LemurianMaster", 1.5f, false);
            if (!Check("spawned a second Lemurian up close", lem2 != null)) yield break;
            yield return new WaitForSecondsRealtime(2f);
            yield return AimAt(() => lem2.corePosition);
            hits0 = Monsters.HitsApplied;
            metrics["punch_distance_m"] = F(Vector3.Distance(V1Drive.ToRor(V1Drive.Latest.eye), lem2.corePosition));
            int punches = 0;
            for (; punches < 4 && Monsters.HitsApplied == hits0; punches++)
            {
                yield return AimAt(() => lem2.corePosition);
                yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.F } }, 0.2f);
                t = Now;
                while (Monsters.HitsApplied == hits0 && Now - t < 1.5f) yield return null;
            }
            metrics["punches_thrown"] = punches.ToString();
            metrics["punch"] = Monsters.LastHit;
            Check("a punch hurt it", Monsters.HitsApplied > hits0 && Monsters.LastHit.Contains("punch"), Monsters.LastHit);
            yield return SnapRor("ror2-after-punch");
            yield return SnapUk("uk");

            // 5. Risk of Rain 2's damage is ULTRAKILL's to take
            yield return new WaitForSecondsRealtime(1f);
            // not while V1 is still invincible from an earlier hit: ULTRAKILL would, rightly, ignore this one
            for (int i = 0; i < 10; i++)
            {
                yield return UkState();
                if (StateValue(stateReply, "layer") != "15") break;
                yield return new WaitForSecondsRealtime(0.4f);
            }
            metrics["uk_layer_before_hit"] = StateValue(stateReply, "layer") ?? "?";
            int ukHp0 = V1Drive.UkHp, hurts0 = V1HealthLink.HurtsSent;
            float dealt = v1.healthComponent.fullHealth * 0.3f;
            v1.healthComponent.TakeDamage(new DamageInfo { attacker = lem2.gameObject, inflictor = lem2.gameObject, damage = dealt, position = v1.corePosition });
            // the hit is worked out at once; what it cost is already on its way to ULTRAKILL
            float sentShare = V1HealthLink.LastHurt;
            metrics["hurt_sent"] = F(sentShare);
            t = Now;
            while (V1Drive.UkHp == ukHp0 && Now - t < 3f) yield return null;
            yield return new WaitForSecondsRealtime(0.3f);
            Check("a hit of 30% of the body's health cost V1 the share Risk of Rain 2 let through", V1HealthLink.HurtsSent > hurts0 && sentShare > 5f && Mathf.Abs(ukHp0 - V1Drive.UkHp - sentShare) <= 2f,
                "RoR2 let " + F(sentShare) + " % of full health through (armor " + F(v1.armor) + "), ULTRAKILL HP " + ukHp0 + " -> " + V1Drive.UkHp);
            float share = v1.healthComponent.health / v1.healthComponent.fullHealth * 100f;
            Check("the body's health shows ULTRAKILL's HP", Mathf.Abs(share - Mathf.Min(100, V1Drive.UkHp)) < 2f, F(v1.healthComponent.health) + " of " + F(v1.healthComponent.fullHealth) + " = " + F(share) + " %, ULTRAKILL " + V1Drive.UkHp);

            // 6. and its healing reaches V1
            ukHp0 = V1Drive.UkHp;
            v1.healthComponent.Heal(v1.healthComponent.fullHealth * 0.1f, default, true);
            t = Now;
            while (V1Drive.UkHp == ukHp0 && Now - t < 3f) yield return null;
            Check("healing 10% of the body's health gave V1 about 10 HP", Mathf.Abs(V1Drive.UkHp - (ukHp0 + 10)) <= 2, "ULTRAKILL HP " + ukHp0 + " -> " + V1Drive.UkHp);

            // 7. a monster's own attack
            if (lem2 != null && lem2.healthComponent.alive) lem2.healthComponent.Suicide();
            var lem3 = SpawnAhead("LemurianMaster", 5f, true);
            Check("spawned a Lemurian with its AI on", lem3 != null);
            hurts0 = V1HealthLink.HurtsSent;
            ukHp0 = V1Drive.UkHp;
            t = Now;
            while (V1HealthLink.HurtsSent == hurts0 && Now - t < 40f)
            {
                // regeneration keeps raising it while V1 waits: what counts is the HP just before the hit
                ukHp0 = V1Drive.UkHp;
                yield return null;
            }
            metrics["seconds_until_attacked"] = F(Now - t);
            yield return new WaitForSecondsRealtime(0.5f);
            Check("the Lemurian's own attack hurt V1", V1HealthLink.HurtsSent > hurts0 && V1Drive.UkHp < ukHp0, "ULTRAKILL HP " + ukHp0 + " -> " + V1Drive.UkHp + ", " + F(V1HealthLink.LastHurt) + " sent");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && user.cachedBody != null && user.cachedBody.healthComponent.alive);
            metrics["hits_applied"] = Monsters.HitsApplied.ToString();
            metrics["v1_was_hurt_by"] = string.Join(", ", attackers.ToArray());
            metrics["heal_sent_total"] = F(HealsReachV1.Sent);
            yield return BackToTitle();
        }

        /// <summary>Only the monsters the test brings: the directors stop, and whatever else arrives is removed.</summary>
        void QuietStage(CharacterBody v1)
        {
            foreach (var d in FindObjectsOfType<CombatDirector>()) d.enabled = false;
            CharacterBody.onBodyStartGlobal += b =>
            {
                RoR2Application.onNextUpdate += () =>
                {
                    if (b != null && b != v1 && b.teamComponent != null && b.teamComponent.teamIndex != TeamIndex.Player && !testMasters.Contains(b.master) && b.healthComponent != null) b.healthComponent.Suicide();
                };
            };
            foreach (var b in new List<CharacterBody>(CharacterBody.readOnlyInstancesList))
            {
                if (b != null && b != v1 && b.teamComponent != null && b.teamComponent.teamIndex != TeamIndex.Player && b.healthComponent != null) b.healthComponent.Suicide();
            }
        }

        IEnumerator WaitVulnerable()
        {
            for (int i = 0; i < 12; i++)
            {
                yield return UkState();
                if (StateValue(stateReply, "layer") != "15") break;
                yield return new WaitForSecondsRealtime(0.4f);
            }
        }

        /// <summary>Parry: a punch that meets a monster's melee hit cancels it and returns it; an unparried hit lands after
        /// the parry window; a projectile caught by the fist never arrives and its owner pays.</summary>
        IEnumerator ParryTest()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            var hurtBy = new List<string>();
            GlobalEventManager.onServerDamageDealt += report =>
            {
                if (report.victimBody == v1 && hurtBy.Count < 10) hurtBy.Add((report.attackerBody != null ? report.attackerBody.name : "nobody") + " " + F(report.damageDealt) + " @" + F(Now));
            };
            yield return new WaitForSecondsRealtime(2f);
            float unit = Monsters.UnitDamage(v1);

            // 1. melee, parried: the fist lands, the monster's hit comes right behind it
            var lem = SpawnAhead("BeetleGuardMaster", 2.5f, false);
            if (!Check("spawned a Beetle Guard up close", lem != null)) yield break;
            yield return new WaitForSecondsRealtime(2.5f);
            yield return WaitVulnerable();
            int parries0 = Parry.MeleeParries, hurts0 = V1HealthLink.HurtsSent, hp0 = V1Drive.UkHp;
            float lemHp0 = lem.healthComponent.combinedHealth;
            float t = Now;
            for (int attempt = 0; attempt < 5 && Parry.MeleeParries == parries0; attempt++)
            {
                yield return AimAt(() => lem.corePosition);
                int hits0 = Monsters.HitsApplied;
                V1Drive.Script = () => new V1Drive.Frame { keys = new[] { KeyCode.F } };
                t = Now;
                while (Monsters.HitsApplied == hits0 && Now - t < 1.5f) yield return null;
                V1Drive.Script = () => default;
                if (Monsters.HitsApplied == hits0) continue;
                // the punch has landed: the monster's swing arrives within the parry's reach in time
                v1.healthComponent.TakeDamage(new DamageInfo { attacker = lem.gameObject, inflictor = lem.gameObject, damage = v1.healthComponent.fullHealth * 0.25f, position = v1.corePosition });
                yield return new WaitForSecondsRealtime(0.6f);
            }
            yield return UkState();
            metrics["melee_parry_returned"] = F(lemHp0 - (lem != null ? Mathf.Max(0f, lem.healthComponent.combinedHealth) : 0f));
            Check("a punch meeting a melee hit is a parry", Parry.MeleeParries > parries0, "ULTRAKILL counts " + StateValue(stateReply, "parries") + " parries");
            Check("the parried hit never reached V1", V1HealthLink.HurtsSent == hurts0 && V1Drive.UkHp >= hp0, "ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp + ", " + (V1HealthLink.HurtsSent - hurts0) + " HURT sent (last " + F(V1HealthLink.LastHurt) + "), parries " + (Parry.MeleeParries - parries0) + ", hurt by: " + string.Join(", ", hurtBy.ToArray()));
            Check("the monster took the return", lem == null || lemHp0 - lem.healthComponent.combinedHealth >= Parry.ParryUnits * unit * 0.9f, F(lemHp0 - (lem != null ? lem.healthComponent.combinedHealth : 0f)) + " of " + F(lemHp0));
            yield return SnapRor("ror2-after-parry");

            // 2. melee, not parried: it lands once the window has passed
            if (lem == null || !lem.healthComponent.alive)
            {
                lem = SpawnAhead("BeetleGuardMaster", 2.5f, false);
                yield return new WaitForSecondsRealtime(2.5f);
            }
            yield return new WaitForSecondsRealtime(1f);
            yield return WaitVulnerable();
            hurts0 = V1HealthLink.HurtsSent;
            hp0 = V1Drive.UkHp;
            v1.healthComponent.TakeDamage(new DamageInfo { attacker = lem.gameObject, inflictor = lem.gameObject, damage = v1.healthComponent.fullHealth * 0.2f, position = v1.corePosition });
            Check("an unparried melee hit is held for the parry window", V1HealthLink.HurtsSent == hurts0);
            t = Now;
            while (V1Drive.UkHp == hp0 && Now - t < 2f) yield return null;
            metrics["melee_hit_delay_seconds"] = F(Now - t);
            Check("then lands", V1HealthLink.HurtsSent > hurts0 && V1Drive.UkHp < hp0, "ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp + " after " + F(Now - t) + " s");
            if (lem != null && lem.healthComponent.alive) lem.healthComponent.Suicide();

            // 3. a projectile, caught
            var owner = SpawnAhead("LemurianMaster", 16f, false);
            if (!Check("spawned a Lemurian to own the fireball", owner != null)) yield break;
            yield return new WaitForSecondsRealtime(2f);
            var fireball = LegacyResourcesAPI.Load<GameObject>("Prefabs/Projectiles/Fireball");
            if (!Check("fireball prefab found", fireball != null)) yield break;
            int shot0 = Parry.ShotParries, listedMax = 0, ukShotsMax = 0;
            float ownerHp0 = owner.healthComponent.combinedHealth;
            for (int attempt = 0; attempt < 4 && Parry.ShotParries == shot0; attempt++)
            {
                yield return WaitVulnerable();
                yield return AimAt(() => owner.corePosition);
                Vector3 eye = V1Drive.ToRor(V1Drive.Latest.eye);
                Vector3 from = eye + (owner.corePosition - eye).normalized * 12f;
                hp0 = V1Drive.UkHp;
                RoR2.Projectile.ProjectileManager.instance.FireProjectile(fireball, from, Quaternion.LookRotation(eye - from), owner.gameObject, 10f, 0f, false, DamageColorIndex.Default, null, 8f);
                t = Now;
                bool punched = false;
                while (Now - t < 3f && Parry.ShotParries == shot0)
                {
                    listedMax = Mathf.Max(listedMax, Parry.ShotsListed);
                    float nearest = 999f;
                    foreach (var p in InstanceTracker.GetInstancesList<RoR2.Projectile.ProjectileController>()) if (p != null) nearest = Mathf.Min(nearest, Vector3.Distance(p.transform.position, eye));
                    if (!punched && nearest < 3.5f)
                    {
                        punched = true;
                        V1Drive.Script = () => new V1Drive.Frame { keys = new[] { KeyCode.F } };
                    }
                    if (!punched && nearest < 9f && ukShotsMax == 0)
                    {
                        yield return UkState();
                        int.TryParse(StateValue(stateReply, "shots"), out ukShotsMax);
                    }
                    yield return null;
                }
                V1Drive.Script = () => default;
                yield return new WaitForSecondsRealtime(1f);
            }
            metrics["projectiles_listed"] = listedMax.ToString();
            Check("the fireball flew through ULTRAKILL too", listedMax >= 1 && ukShotsMax >= 1, listedMax + " listed, ULTRAKILL had " + ukShotsMax);
            yield return UkState();
            Check("V1's fist caught a fireball", Parry.ShotParries > shot0, "ULTRAKILL counts " + StateValue(stateReply, "parries") + " parries in all");
            Check("its owner took the return", owner == null || ownerHp0 - owner.healthComponent.combinedHealth >= unit, F(ownerHp0 - (owner != null ? owner.healthComponent.combinedHealth : 0f)) + " of " + F(ownerHp0));
            yield return SnapUk("uk");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
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
