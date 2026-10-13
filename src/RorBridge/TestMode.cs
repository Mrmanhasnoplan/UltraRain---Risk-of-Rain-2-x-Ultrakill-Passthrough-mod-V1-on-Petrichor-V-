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
        int exceptions;
        readonly List<string> exceptionList = new List<string>();

        public void Begin(string name, string dir)
        {
            scenario = name;
            outDir = string.IsNullOrEmpty(dir) ? Path.Combine(Plugin.ProfileDir, "test-output") : dir;
            Directory.CreateDirectory(outDir);
            UkLink.OnMessage += OnMessage;
            Application.logMessageReceived += (msg, stack, type) =>
            {
                if (type != LogType.Exception) return;
                exceptions++;
                if (exceptionList.Count < 6 && !exceptionList.Contains(msg)) exceptionList.Add(msg + " @ " + (stack ?? "").Split('\n')[0].Trim());
            };
            if (scenario == "mpjoin") SecondPlayer.Apply();
            V1Net.Trace = scenario.StartsWith("mp");
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
                case "runloop": body = RunLoop(); break;
                case "whiplash": body = Whiplash(); break;
                case "conduction": body = Conduction(); break;
                case "coins": body = CoinConduction(); break;
                case "model": body = ModelTest(); break;
                case "oil": body = OilTest(); break;
                case "audio": body = AudioTest(); break;
                case "blast": body = BlastTest(); break;
                case "jumppad": body = JumpPadTest(); break;
                case "outofbounds": body = OutOfBoundsTest(); break;
                case "ping": body = PingTest(); break;
                case "items": body = ItemsHidden(); break;
                case "closeup": body = CloseUp(); break;
                case "newt": body = NewtTest(); break;
                case "worm": body = WormTest(); break;
                case "health": body = HealthTest(); break;
                case "maxhealth": body = MaxHealthItems(); break;
                case "sharpshooter": body = Sharpshooter(); break;
                case "coinshot": body = CoinsAgainstHitscan(); break;
                case "moonorbs": body = MoonOrbs(); break;
                case "lasthp": body = LastHp(); break;
                case "figure": body = FigureTest(); break;
                case "fx": body = FxTest(); break;
                case "mphost": body = MpHost(); break;
                case "mpjoin": body = MpJoin(); break;
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
            metrics["exceptions_logged"] = exceptions.ToString();
            if (exceptionList.Count > 0) metrics["exceptions"] = string.Join(" | ", exceptionList.ToArray());
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
            metrics["stage_meshes_read_from_gpu"] = st.unreadable + " (" + st.readBack + " straight from Direct3D)";
            metrics["stage_skipped"] = st.skipped + (st.skippedNames != null ? ": " + st.skippedNames : "") + (st.problem != null ? "; " + st.problem : "");
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
        CharacterBody SpawnAhead(string masterName, float metres, bool ai, float side = 0f)
        {
            var feet = V1Drive.ToRor(V1Drive.Latest.feet);
            var ahead = feet + Quaternion.Euler(0f, V1Drive.Latest.yaw, 0f) * new Vector3(side, 0f, metres);
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
            if (!ai && spawned != null)
            {
                // switching the AI component off does not stop every monster walking: no speed does
                spawned.baseMoveSpeed = 0f;
                spawned.baseAcceleration = 0f;
                // nor does it stop every monster swinging at what stands in front of it (a Beetle Guard's swing is half
                // of V1's health, and took the tests' parries and V1's life at random): its own hits do nothing
                spawned.baseDamage = 0f;
                spawned.levelDamage = 0f;
                spawned.MarkAllStatsDirty();
            }
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
            // the Feedbacker: ULTRAKILL remembers the arm last used, and the Knuckleblaster does not parry
            UkLink.Send("TESTARM 0");
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

        // ------------------------------------------------------------ phase 6

        IEnumerator Interact()
        {
            V1Drive.Script = () => new V1Drive.Frame { interact = true };
            yield return new WaitForSecondsRealtime(0.25f);
            V1Drive.Script = () => default;
        }

        /// <summary>Phase 6: the run still works as V1. RoR2's HUD makes room, items are picked up and count, chests and
        /// the teleporter answer RoR2's interact key, the next stage is solid again with V1 as it was, V1's death ends
        /// the run, and a new run starts V1 whole.</summary>
        IEnumerator RunLoop()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var user = LocalUserManager.GetFirstLocalUser();
            var v1 = user.cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);

            // 1. RoR2's HUD makes room for ULTRAKILL's
            var hud = user.cameraRigController != null ? user.cameraRigController.hud : null;
            Check("RoR2's health bar and skill icons are hidden, its crosshair off", hud != null && hud.healthBar != null && !hud.healthBar.gameObject.activeSelf && v1.hideCrosshair);

            // 2. an item dropped at V1's feet is picked up
            var syringe = RoR2Content.Items.Syringe;
            int had = v1.inventory.GetItemCount(syringe);
            PickupDropletController.CreatePickupDroplet(PickupCatalog.FindPickupIndex(syringe.itemIndex), v1.corePosition + Vector3.up * 2.5f, Vector3.zero);
            float t = Now;
            while (v1.inventory.GetItemCount(syringe) == had && Now - t < 10f) yield return null;
            metrics["pickup_seconds"] = F(Now - t);
            Check("V1 picked up an item lying at its feet", v1.inventory.GetItemCount(syringe) == had + 1);

            // 3. items count: more damage on the body is more damage from ULTRAKILL's weapons
            float unit0 = Monsters.UnitDamage(v1);
            v1.inventory.GiveItem(RoR2Content.Items.BoostDamage, 10);
            yield return new WaitForSecondsRealtime(1f);
            float unit1 = Monsters.UnitDamage(v1);
            Check("a damage item raises what a shot does", unit1 > unit0 * 1.8f, F(unit0) + " -> " + F(unit1) + " per ULTRAKILL damage unit");

            // 4. a chest, opened with RoR2's interact key
            v1.master.GiveMoney(500);
            var card = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<InteractableSpawnCard>("RoR2/Base/Chest1/iscChest1.asset").WaitForCompletion();
            var feet = V1Drive.ToRor(V1Drive.Latest.feet);
            var spot = feet + Quaternion.Euler(0f, V1Drive.Latest.yaw, 0f) * Vector3.forward * 2.5f;
            if (Physics.Raycast(spot + Vector3.up * 20f, Vector3.down, out var ground, 60f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) spot = ground.point;
            var chest = card != null ? DirectorCore.instance.TrySpawnObject(new DirectorSpawnRequest(card, new DirectorPlacementRule { placementMode = DirectorPlacementRule.PlacementMode.Direct, position = spot }, new Xoroshiro128Plus(1))) : null;
            if (Check("spawned a chest in front of V1", chest != null))
            {
                yield return new WaitForSecondsRealtime(1f);
                var buy = chest.GetComponent<PurchaseInteraction>();
                uint money0 = v1.master.money;
                for (int attempt = 0; attempt < 3 && buy.available; attempt++)
                {
                    yield return AimAt(() => chest.transform.position + Vector3.up * 0.6f);
                    yield return Interact();
                    yield return new WaitForSecondsRealtime(0.8f);
                }
                Check("the interact key opened the chest and paid for it", !buy.available && v1.master.money < money0, "money " + money0 + " -> " + v1.master.money);
                yield return SnapRor("ror2-chest");
            }

            // 5. the teleporter
            var tp = TeleporterInteraction.instance;
            if (Check("the stage has a teleporter", tp != null))
            {
                Vector3 beside = tp.transform.position + new Vector3(2.2f, 1.5f, 0f);
                V1Drive.Teleport(beside, 270f);
                yield return new WaitForSecondsRealtime(2f);
                for (int attempt = 0; attempt < 3 && tp.isIdle; attempt++)
                {
                    yield return AimAt(() => tp.transform.position + Vector3.up * 0.8f);
                    yield return Interact();
                    yield return new WaitForSecondsRealtime(1f);
                }
                Check("the interact key started the teleporter", !tp.isIdle, tp.activationState.ToString());
                t = Now;
                while (!tp.isCharging && Now - t < 8f) yield return null;
                // it only charges while a player stands in its zone
                yield return new WaitForSecondsRealtime(5f);
                metrics["teleporter_charge_after_5s"] = F(tp.chargeFraction);
                Check("the teleporter charges with V1 standing by it", tp.isCharging && tp.chargeFraction > 0.01f, tp.activationState + ", " + F(tp.chargeFraction * 100f) + " %");
                yield return SnapRor("ror2-teleporter");
            }

            // 6. the next stage: solid again, V1 as it was
            yield return WaitVulnerable();
            UkLink.Send(Proto.Hurt + " 30");
            yield return new WaitForSecondsRealtime(1f);
            int hpBefore = V1Drive.UkHp;
            string loadedBefore = V1Drive.StageLoaded;
            Run.instance.AdvanceStage(SceneCatalog.GetSceneDefFromSceneName("blackbeach"));
            t = Now;
            while ((SceneManager.GetActiveScene().name != "blackbeach" || user.cachedBody == null || user.cachedBody == v1 || !V1Drive.UkLanded || V1Drive.StageLoaded == null || ReferenceEquals(V1Drive.StageLoaded, loadedBefore)) && Now - t < 90f) yield return null;
            metrics["stage_change_seconds"] = F(Now - t);
            if (!Check("V1 arrived on the next stage", SceneManager.GetActiveScene().name == "blackbeach" && user.cachedBody != null && user.cachedBody != v1 && V1Drive.UkLanded, SceneManager.GetActiveScene().name + ", stage " + (V1Drive.StageLoaded ?? "not loaded"))) yield break;
            v1 = user.cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(3f);
            metrics["stage2"] = V1Drive.Stage.objects + " colliders, " + V1Drive.Stage.triangles + " triangles, " + V1Drive.Stage.unreadable + " meshes read from the GPU, " + V1Drive.Stage.skipped + " skipped; ULTRAKILL: " + V1Drive.StageLoaded;
            Check("the new stage's geometry is in ULTRAKILL", V1Drive.Stage.objects > 10 && V1Drive.StageLoaded != null && V1Drive.StageLoaded.EndsWith(" ok"), metrics["stage2"]);
            Check("V1 is standing on the new stage", V1Drive.Latest.grounded);
            yield return StandingCheck("on the new stage");
            Check("V1 kept its health across the stage change", Mathf.Abs(V1Drive.UkHp - hpBefore) <= 12 && V1Drive.UkHp < 100, "ULTRAKILL HP " + hpBefore + " -> " + V1Drive.UkHp);
            Check("items came along", v1.inventory.GetItemCount(syringe) == had + 1);
            yield return SnapRor("ror2-stage2");

            // 7. V1 dies in ULTRAKILL: the run ends in Risk of Rain 2
            yield return WaitVulnerable();
            int exceptionsBefore = exceptions;
            UkLink.Send(Proto.Hurt + " 999");
            t = Now;
            while ((!V1Drive.UkDead || Run.instance == null || !Run.instance.isGameOverServer) && Now - t < 15f) yield return null;
            Check("V1's death in ULTRAKILL ended the run", V1Drive.UkDead && Run.instance != null && Run.instance.isGameOverServer, "ULTRAKILL dead " + V1Drive.UkDead + ", after " + F(Now - t) + " s");
            yield return new WaitForSecondsRealtime(4f);
            yield return SnapRor("ror2-game-over");
            Check("the game-over screen came up without errors", exceptions == exceptionsBefore, (exceptions - exceptionsBefore) + " exceptions: " + string.Join(" | ", exceptionList.ToArray()));

            // 8. a new run: V1 whole again
            yield return BackToTitle();
            yield return ToLobby();
            yield return Pick(V1Survivor.Def);
            yield return StartRun();
            if (failed) yield break;
            t = Now;
            while ((!V1Drive.UkLanded || V1Drive.UkDead || !V1Drive.HavePose) && Now - t < 30f) yield return null;
            yield return new WaitForSecondsRealtime(3f);
            Check("a new run starts V1 alive at full health", V1Drive.UkLanded && !V1Drive.UkDead && V1Drive.UkHp == 100, "ULTRAKILL HP " + V1Drive.UkHp + ", dead " + V1Drive.UkDead);
            Check("and standing on the stage", V1Drive.Latest.grounded);
            yield return SnapRor("ror2-new-run");
            yield return SnapUk("uk");
            yield return BackToTitle();
        }

        /// <summary>The Whiplash: a light monster is dragged to V1 (in Risk of Rain 2 too); a heavy one stays where it is and
        /// V1 flies to it; the hook's own damage arrives.</summary>
        IEnumerator Whiplash()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            Func<Vector3> eye = () => V1Drive.ToRor(V1Drive.Latest.eye);

            // 1. light: the monster comes to V1
            var lem = SpawnAhead("LemurianMaster", 14f, false);
            if (!Check("spawned a Lemurian 14 m away", lem != null)) yield break;
            yield return new WaitForSecondsRealtime(2.5f);
            Check("a Lemurian counts as light", Monsters.Light(lem), "mass " + F(lem.characterMotor != null ? lem.characterMotor.mass : -1f));
            float far = Vector3.Distance(lem.corePosition, eye()), near = far;
            Vector3 v1Start = V1Drive.ToRor(V1Drive.Latest.feet);
            int hits0 = Monsters.HitsApplied, pulls0 = Monsters.PullsApplied;
            string hookHit = "";
            float t = Now;
            for (int attempt = 0; attempt < 3 && Monsters.PullsApplied == pulls0; attempt++)
            {
                yield return AimAt(() => lem.corePosition);
                V1Drive.Script = () => new V1Drive.Frame { keys = new[] { KeyCode.R } };
                yield return new WaitForSecondsRealtime(0.35f);
                yield return UkState();
                metrics["hook_state_light_" + attempt] = StateValue(stateReply, "hook") ?? "?";
                // the pull starts when the key is let go
                V1Drive.Script = () => default;
                t = Now;
                while (Now - t < 2.5f && lem != null && lem.healthComponent.alive)
                {
                    near = Mathf.Min(near, Vector3.Distance(lem.corePosition, eye()));
                    if (Monsters.HitsApplied > hits0 && hookHit.Length == 0) hookHit = Monsters.LastHit;
                    yield return null;
                }
                V1Drive.Script = () => default;
                yield return new WaitForSecondsRealtime(0.8f);
            }
            float v1Moved = Vector3.Distance(V1Drive.ToRor(V1Drive.Latest.feet), v1Start);
            metrics["light_pull"] = F(far) + " m -> " + F(near) + " m, " + (Monsters.PullsApplied - pulls0) + " pull updates, V1 moved " + F(v1Moved) + " m";
            metrics["hook_hit"] = hookHit;
            Check("the hook's own hit arrived", hookHit.Contains("hook"), hookHit);
            Check("the Lemurian was dragged to V1 in Risk of Rain 2", Monsters.PullsApplied > pulls0 && near < 4.5f && near < far - 6f, metrics["light_pull"]);
            Check("V1 stayed where it was", v1Moved < 3f, F(v1Moved) + " m");
            yield return SnapRor("ror2-after-light-pull");
            // let go of at speed: what became of it
            yield return new WaitForSecondsRealtime(2.5f);
            metrics["light_after_release"] = lem == null ? "gone" : (lem.healthComponent.alive ? "alive, " : "DEAD, ") + "health " + F(lem.healthComponent.combinedHealth) + " of " + F(lem.healthComponent.fullCombinedHealth) + ", " + F(Vector3.Distance(lem.corePosition, eye())) + " m from V1";
            if (lem != null && lem.healthComponent.alive) lem.healthComponent.Suicide();
            yield return new WaitForSecondsRealtime(1f);

            // 2. heavy: V1 goes to the monster
            var guard = SpawnAhead("BeetleGuardMaster", 16f, false);
            if (!Check("spawned a Beetle Guard 16 m away", guard != null)) yield break;
            yield return new WaitForSecondsRealtime(2.5f);
            Check("a Beetle Guard counts as heavy", !Monsters.Light(guard), "mass " + F(guard.characterMotor != null ? guard.characterMotor.mass : -1f));
            Vector3 guardStart = guard.corePosition;
            far = Vector3.Distance(guard.corePosition, eye());
            near = far;
            pulls0 = Monsters.PullsApplied;
            for (int attempt = 0; attempt < 3 && near > far - 6f; attempt++)
            {
                yield return AimAt(() => guard.corePosition);
                V1Drive.Script = () => new V1Drive.Frame { keys = new[] { KeyCode.R } };
                yield return new WaitForSecondsRealtime(0.35f);
                V1Drive.Script = () => default;
                t = Now;
                while (Now - t < 2.5f)
                {
                    near = Mathf.Min(near, Vector3.Distance(guard.corePosition, eye()));
                    yield return null;
                }
                V1Drive.Script = () => default;
                yield return new WaitForSecondsRealtime(0.8f);
            }
            float guardMoved = Vector3.Distance(guard.corePosition, guardStart);
            metrics["heavy_pull"] = F(far) + " m -> " + F(near) + " m, the Beetle Guard moved " + F(guardMoved) + " m";
            Check("V1 flew to the Beetle Guard", near < far - 6f && near < 6f, metrics["heavy_pull"]);
            Check("the Beetle Guard was not dragged", guardMoved < 2f && Monsters.PullsApplied == pulls0, F(guardMoved) + " m");
            yield return SnapRor("ror2-after-heavy-pull");
            yield return SnapUk("uk");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }

        bool WasHit(CharacterBody b, params string[] hitters)
        {
            string id = b.GetInstanceID().ToString();
            foreach (var h in Monsters.HitLog)
            {
                var a = h.Split(' ');
                if (a[0] == id && Array.IndexOf(hitters, a[1]) >= 0) return true;
            }
            return false;
        }

        string HitsOn(CharacterBody b)
        {
            string id = b.GetInstanceID().ToString();
            var seen = new List<string>();
            foreach (var h in Monsters.HitLog)
            {
                var a = h.Split(' ');
                if (a[0] == id && !seen.Contains(a[1])) seen.Add(a[1]);
            }
            return string.Join(",", seen.ToArray());
        }

        /// <summary>Conduction: electricity into a monster with nails in it arcs to the monsters near it (electric
        /// railcannon, then the Jumpstart's cable), and what the stage has for water is reported.</summary>
        IEnumerator Conduction()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            int waterColliders = 0;
            foreach (var col in FindObjectsOfType<Collider>()) if (col.gameObject.layer == LayerIndex.water.intVal) waterColliders++;
            metrics["stage_water_colliders"] = waterColliders.ToString();

            var a = SpawnAhead("BeetleGuardMaster", 9f, false);
            var b = SpawnAhead("BeetleGuardMaster", 10f, false, 6f);
            if (!Check("spawned two Beetle Guards 6 m apart", a != null && b != null)) yield break;
            // tough enough to take the whole test
            foreach (var g in new[] { a, b }) { g.baseMaxHealth = 100000f; g.MarkAllStatsDirty(); }
            yield return new WaitForSecondsRealtime(2.5f);
            foreach (var g in new[] { a, b }) g.healthComponent.Networkhealth = g.healthComponent.fullHealth;

            // 1. the Jumpstart: nails into the first one, its cable on it
            float t = Now;
            for (int i = 0; i < 3; i++)
            {
                yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha3 } });
                yield return new WaitForSecondsRealtime(0.9f);
            }
            yield return AimAt(() => a.corePosition);
            Monsters.HitLog.Clear();
            yield return Tap(new V1Drive.Frame { buttons = 2 }, 0.2f);
            // nails while the cable charges
            V1Drive.Script = () => new V1Drive.Frame { buttons = 1 };
            t = Now;
            while (!WasHit(a, "zapper") && Now - t < 12f) yield return null;
            V1Drive.Script = () => default;
            float t2 = Now;
            while (!WasHit(b, "zap", "aftershock") && Now - t2 < 4f) yield return null;
            metrics["jumpstart_hits_first"] = HitsOn(a);
            metrics["jumpstart_hits_second"] = HitsOn(b);
            Check("the Jumpstart's cable shocked the first one", WasHit(a, "zapper"), "first was hit by: " + HitsOn(a));
            Check("and that arced to the second", WasHit(b, "zap", "aftershock"), "second was hit by: " + HitsOn(b));
            // 2. fresh nails (a shock spends the ones it runs through), then the electric railcannon into the nailed one
            yield return new WaitForSecondsRealtime(3f);
            yield return AimAt(() => a.corePosition);
            yield return Hold(1.5f, new V1Drive.Frame { buttons = 1 });
            yield return new WaitForSecondsRealtime(0.3f);
            yield return UkState();
            metrics["nails_stuck"] = StateValue(stateReply, "nails") ?? "?";
            Check("nails stick in a monster", metrics["nails_stuck"] != "0" && metrics["nails_stuck"] != "?", metrics["nails_stuck"] + " nails in it");
            yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha4 } });
            yield return new WaitForSecondsRealtime(1.5f);
            yield return AimAt(() => a.corePosition);
            Monsters.HitLog.Clear();
            yield return UkState();
            int conductions0 = 0;
            int.TryParse(StateValue(stateReply, "conductions"), out conductions0);
            yield return Tap(new V1Drive.Frame { buttons = 1 });
            t = Now;
            while (!WasHit(b, "zap", "aftershock") && Now - t < 5f) yield return null;
            yield return new WaitForSecondsRealtime(1f);
            yield return UkState();
            metrics["rail_hits_first"] = HitsOn(a);
            metrics["rail_hits_second"] = HitsOn(b);
            Check("the railcannon hit the nailed one", WasHit(a, "railcannon"), HitsOn(a));
            Check("its charge arced to the second one", WasHit(b, "zap", "aftershock"), "second was hit by: " + HitsOn(b) + "; ULTRAKILL conductions " + conductions0 + " -> " + StateValue(stateReply, "conductions"));
            yield return SnapRor("ror2-after-rail");

            // 3. water: two fresh monsters with no nails in them, both standing in the stage's water
            a.healthComponent.Suicide();
            b.healthComponent.Suicide();
            yield return new WaitForSecondsRealtime(1f);
            var c = SpawnAhead("BeetleGuardMaster", 9f, false);
            var d = SpawnAhead("BeetleGuardMaster", 10f, false, 6f);
            if (Check("spawned two more Beetle Guards", c != null && d != null))
            {
                foreach (var g in new[] { c, d }) { g.baseMaxHealth = 100000f; g.MarkAllStatsDirty(); }
                yield return new WaitForSecondsRealtime(2.5f);
                // the railcannon needs its charge back
                yield return new WaitForSecondsRealtime(14f);
                Monsters.ForceMonstersWet = true;
                yield return new WaitForSecondsRealtime(0.5f);
                yield return AimAt(() => c.corePosition);
                Monsters.HitLog.Clear();
                yield return Tap(new V1Drive.Frame { buttons = 1 });
                t = Now;
                while (!WasHit(d, "zap", "aftershock") && Now - t < 5f) yield return null;
                metrics["water_hits_first"] = HitsOn(c);
                metrics["water_hits_second"] = HitsOn(d);
                Check("the railcannon hit the one in the water", WasHit(c, "railcannon"), HitsOn(c));
                Check("the water carried the charge to the other", WasHit(d, "zap", "aftershock"), "second was hit by: " + HitsOn(d));
                Monsters.ForceMonstersWet = false;
            }
            yield return SnapUk("uk");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }
        /// <summary>Nails into a monster, whichever nailgun variant happens to be out (the saw launcher sticks none).</summary>
        IEnumerator NailIt(CharacterBody target)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha3 } });
                yield return new WaitForSecondsRealtime(1f);
                yield return AimAt(() => target.corePosition);
                yield return Hold(1.2f, new V1Drive.Frame { buttons = 1 });
                yield return new WaitForSecondsRealtime(0.3f);
                yield return UkState();
                if ((StateValue(stateReply, "nails") ?? "0") != "0") yield break;
            }
        }

        /// <summary>Coins conduct: two monsters too far apart for a shock to jump between them; with one of ULTRAKILL's
        /// coins hanging between them it does.</summary>
        IEnumerator CoinConduction()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);

            // 24 m apart (ULTRAKILL's shock reaches 30 of its units, 15.6 m), V1 in the middle
            var a = SpawnAhead("BeetleGuardMaster", 12f, false);
            var b = SpawnAhead("BeetleGuardMaster", -12f, false);
            if (!Check("spawned two Beetle Guards 24 m apart", a != null && b != null)) yield break;
            foreach (var g in new[] { a, b }) { g.baseMaxHealth = 100000f; g.MarkAllStatsDirty(); }
            yield return new WaitForSecondsRealtime(2.5f);
            metrics["apart_m"] = F(Vector3.Distance(a.corePosition, b.corePosition));
            Check("they are out of a shock's direct reach", Vector3.Distance(a.corePosition, b.corePosition) * V1Drive.K > 31f, F(Vector3.Distance(a.corePosition, b.corePosition) * V1Drive.K) + " ULTRAKILL units");

            // 1. without a coin: the shock stays on the first
            yield return NailIt(a);
            yield return UkState();
            Check("nails are in the first", (StateValue(stateReply, "nails") ?? "0") != "0", StateValue(stateReply, "nails") + " nails");
            yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha4 } });
            yield return new WaitForSecondsRealtime(1.5f);
            yield return AimAt(() => a.corePosition);
            Monsters.HitLog.Clear();
            yield return Tap(new V1Drive.Frame { buttons = 1 });
            yield return new WaitForSecondsRealtime(3f);
            metrics["no_coin_first"] = HitsOn(a);
            metrics["no_coin_second"] = HitsOn(b);
            Check("no coin: the first is shocked", WasHit(a, "railcannon"), HitsOn(a));
            Check("no coin: the second, too far away, is not", !WasHit(b, "zap", "aftershock"), "second was hit by: " + HitsOn(b));

            // 2. with a coin hanging between them
            yield return NailIt(a);
            yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha4 } });
            // the railcannon's charge
            yield return new WaitForSecondsRealtime(14f);
            yield return AimAt(() => a.corePosition);
            var mid = V1Drive.ToUk((a.corePosition + b.corePosition) * 0.5f + Vector3.up * 3f);
            UkLink.Send(Proto.TestCoin + " " + F(mid.x) + " " + F(mid.y) + " " + F(mid.z));
            yield return new WaitForSecondsRealtime(0.3f);
            yield return UkState();
            metrics["coins_in_ultrakill"] = StateValue(stateReply, "coins") ?? "?";
            Check("a coin hangs between them", (StateValue(stateReply, "coins") ?? "0") != "0", StateValue(stateReply, "coins") + " things in ULTRAKILL that carry a shock");
            Monsters.HitLog.Clear();
            yield return Tap(new V1Drive.Frame { buttons = 1 });
            float t = Now;
            while (!WasHit(b, "zap", "aftershock") && Now - t < 5f) yield return null;
            metrics["coin_first"] = HitsOn(a);
            metrics["coin_second"] = HitsOn(b);
            Check("with the coin: the first is shocked", WasHit(a, "railcannon"), HitsOn(a));
            Check("with the coin: the shock reaches the second through it", WasHit(b, "zap", "aftershock"), "second was hit by: " + HitsOn(b));
            yield return SnapUk("uk");
            yield return BackToTitle();
        }

        /// <summary>Character select shows V1's own model, read out of the running ULTRAKILL (or kept from last time).</summary>
        IEnumerator ModelTest()
        {
            yield return ToTitle();
            if (failed) yield break;
            metrics["model_from_cache_at_start"] = (V1Model.Parts > 0 && V1Model.FromCache).ToString();
            yield return ToLobby();
            if (failed) yield break;
            yield return Pick(V1Survivor.Def);
            yield return new WaitForSecondsRealtime(1.5f);
            yield return SnapRor("ror2-lobby-before");
            float t = Now;
            while ((V1Model.Parts == 0 || V1Model.FromCache) && V1Model.Problem == null && UkLauncher.Problem == null && Now - t < 150f) yield return null;
            metrics["model_seconds_after_pick"] = F(Now - t);
            metrics["model"] = V1Model.Parts + " parts, " + V1Model.Vertices + " vertices, " + V1Model.Textures + " textures";
            if (!Check("ULTRAKILL handed over V1's model", V1Model.Parts > 0 && !V1Model.FromCache, V1Model.Problem ?? metrics["model"])) yield break;
            yield return new WaitForSecondsRealtime(1.5f);
            Transform shown = null;
            foreach (var tr in FindObjectsOfType<Transform>()) if (tr.name == "V1 model" && tr.gameObject.activeInHierarchy) shown = tr;
            if (Check("the display in character select carries it", shown != null))
            {
                var b = new Bounds(shown.position, Vector3.zero);
                int renderers = 0;
                foreach (var r in shown.GetComponentsInChildren<Renderer>()) { b.Encapsulate(r.bounds); renderers++; }
                var placeholder = shown.parent.Find("Model");
                metrics["model_bounds"] = b.size.ToString();
                Check("it stands about as tall as a survivor", b.size.y > 1.6f && b.size.y < 2.6f, F(b.size.y) + " m, " + renderers + " renderers");
                Check("the placeholder is gone", placeholder == null || !placeholder.gameObject.activeInHierarchy);
            }
            yield return SnapRor("ror2-lobby-v1");
            Check("a copy is kept for next time", File.Exists(Path.Combine(Plugin.ProfileDir, "cache", "v1model.bin")));
            yield return BackToTitle();
        }

        /// <summary>A monster ULTRAKILL has soaked in gasoline is shown soaked in Risk of Rain 2.</summary>
        IEnumerator OilTest()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            var guard = SpawnAhead("BeetleGuardMaster", 6f, false);
            if (!Check("spawned a Beetle Guard", guard != null)) yield break;
            yield return new WaitForSecondsRealtime(2.5f);
            yield return AimAt(() => guard.corePosition);
            yield return SnapRor("ror2-clean");
            var model = guard.modelLocator.modelTransform.GetComponent<CharacterModel>();
            UkLink.Send("TESTOIL " + guard.GetInstanceID());
            float t = Now;
            while (Monsters.Oiled == 0 && Now - t < 5f) yield return null;
            yield return new WaitForSecondsRealtime(0.5f);
            Check("ULTRAKILL reported the gasoline and the monster got its overlay", Monsters.Oiled == 1, Monsters.Oiled + " oiled monsters after " + F(Now - t) + " s");
            yield return SnapRor("ror2-oiled");
            yield return BackToTitle();
        }

        /// <summary>ULTRAKILL's sound is on: its volumes are the player's, its mixer is not faded out, and firing plays sounds.</summary>
        IEnumerator AudioTest()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            yield return UkState();
            string volumes = StateValue(stateReply, "volumes") ?? "?", audio = StateValue(stateReply, "audio") ?? "?";
            metrics["uk_volumes"] = volumes;
            metrics["uk_audio_idle"] = audio;
            Check("ULTRAKILL took its volumes from the real settings", volumes.StartsWith("all") && !volumes.Contains("/sfx0/"), volumes);
            var m = System.Text.RegularExpressions.Regex.Match(audio, @"mixer(-?[0-9.]+)dB");
            float db = m.Success ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -999f;
            Check("its sound-effects mixer is not faded out", db > -60f, audio);
            Check("its listener is on", audio.Contains("listener") && !audio.Contains("listener0/") && !audio.Contains("PAUSED") && audio.Contains("listeners1"), audio);
            V1Drive.Script = () => new V1Drive.Frame { buttons = 1 };
            yield return new WaitForSecondsRealtime(0.25f);
            yield return UkState();
            V1Drive.Script = () => default;
            string firing = StateValue(stateReply, "audio") ?? "?";
            metrics["uk_audio_firing"] = firing;
            var p = System.Text.RegularExpressions.Regex.Match(firing, @"playing(\d+)");
            Check("firing plays sounds in ULTRAKILL", p.Success && int.Parse(p.Groups[1].Value) > 0, firing);
            yield return BackToTitle();
        }

        /// <summary>A blast that hurts nothing still throws the monsters it reaches, light ones far and heavy ones less.</summary>
        IEnumerator BlastTest()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            foreach (var kind in new[] { "LemurianMaster", "BeetleGuardMaster" })
            {
                var m = SpawnAhead(kind, 10f, false);
                if (!Check("spawned " + kind, m != null)) yield break;
                yield return new WaitForSecondsRealtime(2.5f);
                Vector3 start = m.corePosition;
                float hp0 = m.healthComponent.combinedHealth, top = start.y, far = 0f;
                int knocks0 = Monsters.KnocksApplied;
                // on the ground just this side of it
                var at = V1Drive.ToUk(m.footPosition + (V1Drive.ToRor(V1Drive.Latest.feet) - m.footPosition).normalized * 1.5f);
                UkLink.Send("TESTBLAST " + F(at.x) + " " + F(at.y) + " " + F(at.z));
                float t = Now;
                while (Now - t < 2.5f && m != null)
                {
                    top = Mathf.Max(top, m.corePosition.y);
                    far = Mathf.Max(far, Vector3.Distance(new Vector3(m.corePosition.x, 0f, m.corePosition.z), new Vector3(start.x, 0f, start.z)));
                    yield return null;
                }
                string what = kind.Replace("Master", "") + " (" + F(m.characterMotor != null ? m.characterMotor.mass : 0f) + " kg): up " + F(top - start.y) + " m, along " + F(far) + " m, health " + F(hp0) + " -> " + F(m.healthComponent.combinedHealth);
                metrics["blast_" + kind] = what;
                Check("the blast reached the " + kind.Replace("Master", ""), Monsters.KnocksApplied > knocks0, what);
                Check("it was thrown", top - start.y > 0.5f || far > 1.5f, what);
                Check("it was not hurt", m.healthComponent.combinedHealth >= hp0 - 0.5f, what);
                m.healthComponent.Suicide();
                yield return new WaitForSecondsRealtime(1f);
            }
            yield return BackToTitle();
        }

        /// <summary>A Risk of Rain 2 jump pad throws V1, and V1 comes down about where the pad aims.</summary>
        IEnumerator JumpPadTest()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            var pads = FindObjectsOfType<JumpVolume>();
            metrics["jump_pads_on_stage"] = pads.Length.ToString();
            if (!Check("the stage has jump pads", pads.Length > 0, SceneManager.GetActiveScene().name + " has " + pads.Length)) yield break;
            var pad = pads[0];
            int thrown0 = JumpPads.Thrown;
            Vector3 from = pad.transform.position;
            V1Drive.Teleport(from + Vector3.up * 0.5f, 0f);
            float t = Now;
            while (JumpPads.Thrown == thrown0 && Now - t < 4f) yield return null;
            if (!Check("the pad fired for V1", JumpPads.Thrown > thrown0, pad.name)) yield break;
            Vector3 v = JumpPads.LastVelocity, start = V1Drive.ToRor(V1Drive.Latest.feet);
            float g = Physics.gravity.y, top = start.y, flight = 0f;
            t = Now;
            yield return new WaitForSecondsRealtime(0.3f);
            // until V1 lands (or the pad's own flight time and a bit)
            while (Now - t < Mathf.Max(8f, pad.time + 3f))
            {
                var p = V1Drive.ToRor(V1Drive.Latest.feet);
                top = Mathf.Max(top, p.y);
                if (V1Drive.Latest.grounded && Now - t > 0.6f) break;
                yield return null;
            }
            flight = Now - t;
            Vector3 landed = V1Drive.ToRor(V1Drive.Latest.feet);
            // where RoR2's own survivor would be at its highest, and how far along
            float apex = v.y > 0f ? v.y * v.y / (2f * -g) : 0f;
            float along = new Vector2(landed.x - start.x, landed.z - start.z).magnitude;
            float padAlong = pad.targetElevationTransform != null ? new Vector2(pad.targetElevationTransform.position.x - from.x, pad.targetElevationTransform.position.z - from.z).magnitude : new Vector2(v.x, v.z).magnitude * pad.time;
            metrics["jump_pad"] = pad.name + " velocity " + v + " time " + F(pad.time);
            metrics["jump_result"] = "rose " + F(top - start.y) + " m (RoR2 apex " + F(apex) + " m), travelled " + F(along) + " m (pad aims " + F(padAlong) + " m), " + F(flight) + " s in the air";
            Check("V1 was thrown into the air", top - start.y > Mathf.Max(2f, apex * 0.5f), metrics["jump_result"]);
            Check("V1 rose about as high as the pad throws a survivor", apex < 1f || Mathf.Abs(top - start.y - apex) < Mathf.Max(3f, apex * 0.3f), metrics["jump_result"]);
            Check("V1 came down about as far along as the pad aims", Mathf.Abs(along - padAlong) < Mathf.Max(6f, padAlong * 0.35f), metrics["jump_result"]);
            yield return SnapRor("ror2-after-jump");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>V1 out of the stage's bounds is put back on the stage by Risk of Rain 2, like any survivor.</summary>
        IEnumerator OutOfBoundsTest()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            MapZone zone = null;
            int zones = 0;
            foreach (var z in FindObjectsOfType<MapZone>())
            {
                if (z.zoneType != MapZone.ZoneType.OutOfBounds) continue;
                zones++;
                var c = z.GetComponent<Collider>();
                if (c == null || !c.bounds.Contains(v1.corePosition)) continue;
                if (z.triggerType == MapZone.TriggerType.TriggerExit && (zone == null || c.bounds.size.sqrMagnitude > zone.GetComponent<Collider>().bounds.size.sqrMagnitude)) zone = z;
            }
            metrics["out_of_bounds_zones"] = zones.ToString();
            if (!Check("the stage has a play area V1 stands in", zone != null, zones + " out-of-bounds zones")) yield break;
            var area = zone.GetComponent<Collider>().bounds;
            int hp0 = V1Drive.UkHp, teleports0 = V1Drive.RorTeleports;
            Vector3 start = V1Drive.ToRor(V1Drive.Latest.feet);
            // straight down through the bottom of the play area
            Vector3 outside = new Vector3(start.x, area.min.y - 25f, start.z);
            V1Drive.Teleport(outside, 0f);
            float t = Now;
            while (V1Drive.RorTeleports == teleports0 && Now - t < 8f) yield return null;
            metrics["seconds_until_put_back"] = F(Now - t);
            if (!Check("Risk of Rain 2 put V1 back", V1Drive.RorTeleports > teleports0, "after " + F(Now - t) + " s")) yield break;
            yield return new WaitForSecondsRealtime(3f);
            Vector3 now = V1Drive.ToRor(V1Drive.Latest.feet);
            bool ground = Physics.Raycast(now + Vector3.up * 2f, Vector3.down, out var hit, 30f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
            metrics["put_back_at"] = now + ", " + (ground ? F(now.y - hit.point.y) + " m above the ground" : "no ground below") + ", " + V1Drive.RorTeleports + " teleports in all";
            Check("V1 is inside the play area again", area.Contains(now + Vector3.up), metrics["put_back_at"]);
            Check("V1 is standing on the stage", V1Drive.Latest.grounded && ground && Mathf.Abs(now.y - hit.point.y) < 0.5f, metrics["put_back_at"]);
            Check("it happened once, not over and over", V1Drive.RorTeleports - teleports0 <= 2, (V1Drive.RorTeleports - teleports0) + " teleports");
            Check("RoR2's body is with V1", Vector3.Distance(v1.footPosition, now) < 0.5f, F(Vector3.Distance(v1.footPosition, now)) + " m apart");
            Check("V1 was not hurt and the run goes on", V1Drive.UkHp >= hp0 - 1 && Run.instance != null && !Run.instance.isGameOverServer && v1.healthComponent.alive, "ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp);
            yield return SnapRor("ror2-put-back");
            yield return BackToTitle();
        }

        /// <summary>Risk of Rain 2's ping works for V1: on a monster, on the ground, once per press.</summary>
        IEnumerator PingTest()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var user = LocalUserManager.GetFirstLocalUser();
            var v1 = user.cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            var pinger = user.cachedMasterController != null ? user.cachedMasterController.pingerController : null;
            if (!Check("the player has a pinger", pinger != null)) yield break;
            // the real binds, read once the way a real frame reads them
            InputCapture.Read(user);
            metrics["kept_from_ultrakill"] = InputCapture.KeptInfo();

            // 1. a monster
            var guard = SpawnAhead("BeetleGuardMaster", 8f, false);
            if (!Check("spawned a Beetle Guard", guard != null)) yield break;
            yield return new WaitForSecondsRealtime(2.5f);
            yield return AimAt(() => guard.corePosition);
            int pings0 = V1Drive.PingsSent;
            // held for half a second: still one ping
            yield return Tap(new V1Drive.Frame { ping = true }, 0.5f);
            yield return new WaitForSecondsRealtime(0.5f);
            var target = pinger.currentPing.targetGameObject;
            Check("one press is one ping, however long it is held", V1Drive.PingsSent == pings0 + 1, (V1Drive.PingsSent - pings0) + " pings");
            Check("the monster V1 looks at is pinged", pinger.currentPing.active && target == guard.gameObject, pinger.currentPing.active ? "target " + (target != null ? target.name : "none") + " at " + pinger.currentPing.origin : "no ping");
            yield return SnapRor("ror2-ping-monster");
            guard.healthComponent.Suicide();
            yield return new WaitForSecondsRealtime(1.5f);

            // 2. the ground
            Vector3 feet = V1Drive.ToRor(V1Drive.Latest.feet);
            Vector3 spot = feet + Quaternion.Euler(0f, V1Drive.Latest.yaw, 0f) * Vector3.forward * 6f;
            if (Physics.Raycast(spot + Vector3.up * 20f, Vector3.down, out var ground, 60f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) spot = ground.point;
            yield return AimAt(() => spot);
            pings0 = V1Drive.PingsSent;
            yield return Tap(new V1Drive.Frame { ping = true });
            yield return new WaitForSecondsRealtime(0.5f);
            float off = Vector3.Distance(pinger.currentPing.origin, spot);
            metrics["ground_ping"] = pinger.currentPing.origin + ", " + F(off) + " m from where V1 aimed";
            Check("the ground V1 looks at is pinged", V1Drive.PingsSent == pings0 + 1 && pinger.currentPing.active && pinger.currentPing.targetGameObject == null && off < 1.5f, metrics["ground_ping"]);
            yield return SnapRor("ror2-ping-ground");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>Items picked up as V1 are not drawn on its hidden body (the view is from inside it).</summary>
        IEnumerator ItemsHidden()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            var model = v1.modelLocator != null ? v1.modelLocator.modelTransform : null;
            if (!Check("V1's body has a model", model != null)) yield break;
            Func<int> drawn = () =>
            {
                int n = 0;
                foreach (var r in model.GetComponentsInChildren<Renderer>()) if (r.enabled) n++;
                return n;
            };
            Check("nothing of the body is drawn before any item", drawn() == 0, drawn() + " renderers on");
            int hidden0 = V1Drive.HiddenParts;
            foreach (var item in new[] { RoR2Content.Items.Syringe, RoR2Content.Items.Bear, RoR2Content.Items.Hoof, RoR2Content.Items.CritGlasses, RoR2Content.Items.Mushroom, RoR2Content.Items.Behemoth, RoR2Content.Items.IceRing, RoR2Content.Items.FireRing })
                v1.inventory.GiveItem(item, 1);
            yield return new WaitForSecondsRealtime(1.5f);
            int displays = model.GetComponentsInChildren<ItemDisplay>().Length;
            metrics["item_displays_on_body"] = displays.ToString();
            metrics["renderers_switched_off_for_items"] = (V1Drive.HiddenParts - hidden0).ToString();
            metrics["renderers_still_on"] = drawn().ToString();
            Check("the items have displays on the body", displays >= 4, displays + " displays");
            Check("none of them is drawn", drawn() == 0, drawn() + " renderers on, " + (V1Drive.HiddenParts - hidden0) + " switched off by the bridge");
            // looking down at where the body is
            Vector3 feet = V1Drive.ToRor(V1Drive.Latest.feet);
            Vector3 down = feet + Quaternion.Euler(0f, V1Drive.Latest.yaw, 0f) * Vector3.forward * 0.4f;
            yield return AimAt(() => down);
            yield return SnapRor("ror2-looking-down");

            // every item in the game at once: whatever any of them puts on the body
            int given = 0;
            string lastGiven = "none";
            foreach (var def in ItemCatalog.allItemDefs)
            {
                // not the lunar ones: they turn the run inside out, which is not what is being looked at
                if (def == null || def.hidden || def.tier == ItemTier.NoTier || def.tier == ItemTier.Lunar) continue;
                if (v1 == null || model == null) { metrics["body_gone_after"] = lastGiven; break; }
                try { v1.inventory.GiveItem(def, 1); given++; lastGiven = def.name; }
                catch (Exception e) { metrics["could_not_give_" + def.name] = e.GetType().Name; }
            }
            if (!Check("the body took the items", v1 != null && model != null, "last item given: " + lastGiven)) yield break;
            yield return new WaitForSecondsRealtime(4f);
            int followers = 0;
            foreach (var f in model.GetComponentsInChildren<ItemFollower>()) if (f.followerInstance != null) followers++;
            metrics["all_items_given"] = given.ToString();
            metrics["all_items_displays"] = model.GetComponentsInChildren<ItemDisplay>().Length.ToString();
            metrics["all_items_followers_alive"] = followers.ToString();
            metrics["all_items_switched_off_by_bridge"] = V1Drive.HiddenPartNames();
            Check("with every item: nothing on the body is drawn", drawn() == 0 && followers == 0, drawn() + " renderers on, " + followers + " followers floating, " + model.GetComponentsInChildren<ItemDisplay>().Length + " displays");
            // and nothing round it: the bubbles and glows Risk of Rain 2 hangs on a body, and what items attach to it
            int bubbles = 0, attachedOn = 0, attachments = 0;
            foreach (var fx in FindObjectsOfType<TemporaryVisualEffect>()) if (fx.healthComponent == v1.healthComponent) bubbles++;
            var attached = new List<NetworkedBodyAttachment>();
            NetworkedBodyAttachment.FindBodyAttachments(v1, attached);
            var attachedNames = new List<string>();
            foreach (var a in attached)
            {
                if (a == null) continue;
                attachments++;
                attachedNames.Add(a.name.Replace("(Clone)", ""));
                foreach (var r in a.GetComponentsInChildren<Renderer>()) if (r.enabled) attachedOn++;
            }
            int onBody = 0;
            foreach (var r in v1.GetComponentsInChildren<Renderer>()) if (r.enabled) onBody++;
            metrics["effects_round_the_body"] = bubbles + " buff and item visuals on V1 (" + NoBodyEffects.Refused + " refused so far), " + attachments + " things attached (" + string.Join(", ", attachedNames.ToArray()) + ") with " + attachedOn + " renderers on, " + onBody + " renderers on under the body itself";
            Check("the patch that keeps buff and item visuals off V1 is on", NoBodyEffects.Problem == null && NoBodyEffects.Patched > 0, NoBodyEffects.Problem ?? NoBodyEffects.Patched + " methods");
            Check("with every item: no bubble, glow or attached effect is drawn round V1", bubbles == 0 && attachedOn == 0 && onBody == 0 && NoBodyEffects.Refused > 0, metrics["effects_round_the_body"]);
            yield return SnapRor("ror2-all-items-down");
            Vector3 ahead = V1Drive.ToRor(V1Drive.Latest.eye) + Quaternion.Euler(0f, V1Drive.Latest.yaw, 0f) * Vector3.forward * 10f;
            yield return AimAt(() => ahead);
            yield return SnapRor("ror2-all-items-ahead");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>Monsters and props stay solid when V1's view is right up against them (RoR2 fades them out for its own
        /// third-person camera).</summary>
        IEnumerator CloseUp()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var user = LocalUserManager.GetFirstLocalUser();
            var v1 = user.cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            var rig = user.cameraRigController;
            Check("RoR2's close-up fading is off for V1's view", rig != null && !rig.enableFading);

            // 1. a monster, close enough that RoR2 would have faded it right out
            var guard = SpawnAhead("BeetleGuardMaster", 2.4f, false);
            if (!Check("spawned a Beetle Guard up close", guard != null)) yield break;
            yield return new WaitForSecondsRealtime(2.5f);
            yield return AimAt(() => guard.corePosition);
            yield return new WaitForSecondsRealtime(0.5f);
            var model = guard.modelLocator.modelTransform.GetComponent<CharacterModel>();
            Vector3 eye = V1Drive.ToRor(V1Drive.Latest.eye);
            float nearest = 999f;
            foreach (var hb in guard.hurtBoxGroup.hurtBoxes) nearest = Mathf.Min(nearest, Vector3.Distance(hb.transform.position, eye) - Util.SphereVolumeToRadius(hb.volume));
            float fade = HarmonyLib.Traverse.Create(model).Field("fade").GetValue<float>();
            metrics["monster"] = "nearest hurt box " + F(nearest) + " m from the eye (RoR2 fades from " + F(rig.fadeEndDistance) + " m down to nothing at " + F(rig.fadeStartDistance) + " m), fade " + F(fade);
            Check("the monster is inside RoR2's fade distance", nearest < rig.fadeEndDistance, metrics["monster"]);
            Check("and is drawn solid", fade > 0.99f && model.visibility == VisibilityLevel.Visible, metrics["monster"]);
            yield return SnapRor("ror2-monster-close");
            guard.healthComponent.Suicide();
            yield return new WaitForSecondsRealtime(1.5f);

            // 2. a chest
            var card = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<InteractableSpawnCard>("RoR2/Base/Chest1/iscChest1.asset").WaitForCompletion();
            var feet = V1Drive.ToRor(V1Drive.Latest.feet);
            var spot = feet + Quaternion.Euler(0f, V1Drive.Latest.yaw, 0f) * Vector3.forward * 1.6f;
            if (Physics.Raycast(spot + Vector3.up * 20f, Vector3.down, out var ground, 60f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) spot = ground.point;
            var chest = card != null ? DirectorCore.instance.TrySpawnObject(new DirectorSpawnRequest(card, new DirectorPlacementRule { placementMode = DirectorPlacementRule.PlacementMode.Direct, position = spot }, new Xoroshiro128Plus(1))) : null;
            if (Check("spawned a chest in front of V1", chest != null))
            {
                yield return new WaitForSecondsRealtime(1f);
                yield return AimAt(() => chest.transform.position + Vector3.up * 0.6f);
                yield return new WaitForSecondsRealtime(0.5f);
                var dither = chest.GetComponentInChildren<DitherModel>();
                eye = V1Drive.ToRor(V1Drive.Latest.eye);
                float gap = dither != null && dither.bounds != null ? Vector3.Distance(dither.bounds.ClosestPointOnBounds(eye), eye) : -1f;
                metrics["chest"] = dither != null ? F(gap) + " m from the eye, fade " + F(dither.fade) : "the chest has no fading of its own";
                if (dither != null)
                {
                    Check("the chest is inside RoR2's fade distance", gap < rig.fadeEndDistance, metrics["chest"]);
                    Check("and is drawn solid", dither.fade > 0.99f, metrics["chest"]);
                }
                yield return SnapRor("ror2-chest-close");
            }
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>The Newt in the Bazaar can be hit by V1 (run with --v1bridge-stage bazaar).</summary>
        IEnumerator NewtTest()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            yield return new WaitForSecondsRealtime(2f);
            metrics["stage"] = V1Drive.Stage.objects + " colliders, " + V1Drive.Stage.unreadable + " meshes from the graphics card (" + V1Drive.Stage.readBack + " straight from Direct3D), skipped " + V1Drive.Stage.skipped + (V1Drive.Stage.problem != null ? ", " + V1Drive.Stage.problem : "");
            Check("V1 is standing in the Bazaar", V1Drive.Latest.grounded, metrics["stage"]);
            CharacterBody newt = null;
            var others = new List<string>();
            // he is put in a moment after the Bazaar opens
            float waited = Now;
            while (newt == null && Now - waited < 25f)
            {
                others.Clear();
                foreach (var b in CharacterBody.readOnlyInstancesList)
                {
                    if (b == null || b == v1) continue;
                    others.Add(b.name.Replace("(Clone)", "") + ":" + (b.teamComponent != null ? b.teamComponent.teamIndex.ToString() : "no team"));
                    if (Monsters.IsNewt(b)) newt = b;
                }
                if (newt == null) yield return new WaitForSecondsRealtime(0.5f);
            }
            metrics["newt_seconds"] = F(Now - waited);
            var keeper = BazaarController.instance != null ? BazaarController.instance.shopkeeper : null;
            metrics["bazaar_shopkeeper_object"] = BazaarController.instance == null ? "no BazaarController" : keeper == null ? "none" : keeper.name + ", active " + keeper.activeInHierarchy + ", body " + (keeper.GetComponent<CharacterBody>() != null);
            metrics["newt_body_index"] = ((int)BodyCatalog.FindBodyIndex("ShopkeeperBody")).ToString();
            metrics["bodies_in_the_bazaar"] = string.Join(", ", others.ToArray());
            if (!Check("the Newt is here", newt != null, metrics["bodies_in_the_bazaar"])) yield break;
            float mass = newt.characterMotor != null ? newt.characterMotor.mass : newt.rigidbody != null ? newt.rigidbody.mass : -1f;
            metrics["newt"] = "team " + newt.teamComponent.teamIndex + ", health " + F(newt.healthComponent.combinedHealth) + ", " + F(Vector3.Distance(newt.corePosition, v1.corePosition)) + " m away, mass " + F(mass) + ", light " + Monsters.Light(newt);
            yield return UkState();
            Check("ULTRAKILL has a stand-in for him", Monsters.Listed >= 1 && StateValue(stateReply, "proxies") == Monsters.Listed.ToString(), Monsters.Listed + " listed, ULTRAKILL has " + StateValue(stateReply, "proxies"));
            // from a few metres in front of him, wherever the Bazaar put V1 down
            Vector3 stand = newt.corePosition + (v1.corePosition - newt.corePosition).normalized * 9f;
            if (Physics.Raycast(stand + Vector3.up * 10f, Vector3.down, out var ground, 40f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) stand = ground.point;
            V1Drive.Teleport(stand + Vector3.up * 0.3f, 0f);
            yield return new WaitForSecondsRealtime(1.5f);
            float hp0 = newt.healthComponent.combinedHealth;
            int hits0 = Monsters.HitsApplied;
            Monsters.HitLog.Clear();
            for (int attempt = 0; attempt < 3 && Monsters.HitsApplied == hits0; attempt++)
            {
                yield return AimAt(() => newt.corePosition);
                yield return Tap(new V1Drive.Frame { buttons = 1 });
                float t = Now;
                while (Monsters.HitsApplied == hits0 && Now - t < 1.5f) yield return null;
            }
            yield return SnapRor("ror2-newt");
            yield return SnapUk("uk");
            metrics["newt_hit"] = Monsters.LastHit + "; health " + F(hp0) + " -> " + F(newt != null ? newt.healthComponent.combinedHealth : 0f);
            Check("V1's shot landed on the Newt", Monsters.HitsApplied > hits0 && WasHit(newt, "revolver", "shotgun", "shotgunzone", "nail", "railcannon", "explosion", "punch"), metrics["newt_hit"] + "; hit by " + HitsOn(newt));
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>A Magma Worm is hit where Risk of Rain 2 has its hurt boxes: every segment, not one box at its head.</summary>
        IEnumerator WormTest()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            var worm = SpawnAhead("MagmaWormMaster", 30f, false);
            if (!Check("spawned a Magma Worm", worm != null)) yield break;
            worm.baseMaxHealth = 1000000f;
            worm.MarkAllStatsDirty();
            Check("it is one of the segmented kind", Monsters.Segmented(worm), BodyCatalog.GetBodyName(worm.bodyIndex));
            float t = Now;
            int ukParts = 0;
            while (Now - t < 20f)
            {
                yield return UkState();
                int.TryParse(StateValue(stateReply, "parts"), out ukParts);
                if (Monsters.PartsListed >= 5 && ukParts >= 5) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            int boxes = worm.hurtBoxGroup != null ? worm.hurtBoxGroup.hurtBoxes.Length : 0;
            metrics["worm_hurt_boxes"] = boxes + " on the monster, " + Monsters.PartsListed + " listed, ULTRAKILL has " + ukParts;
            Check("ULTRAKILL has a box for each of its hurt boxes", Monsters.PartsListed >= 5 && Mathf.Abs(ukParts - Monsters.PartsListed) <= 1, metrics["worm_hurt_boxes"]);
            // the one box it used to have: its radius round its core
            float oldReach = Mathf.Max(worm.radius, 0.5f) * 1.15f * 1.75f;
            metrics["worm_old_box_reach_m"] = F(oldReach);
            float best = 0f, length = 0f;
            int landed = 0, shots = 0;
            for (int attempt = 0; attempt < 14 && (landed < 3 || best < oldReach + 3f); attempt++)
            {
                // the segment in sight that is furthest from its head
                Vector3 eye = V1Drive.ToRor(V1Drive.Latest.eye);
                HurtBox pick = null;
                float far = 0f;
                foreach (var hb in worm.hurtBoxGroup.hurtBoxes)
                {
                    if (hb == null || hb.collider == null || !hb.collider.enabled) continue;
                    float d = Vector3.Distance(hb.collider.bounds.center, worm.corePosition);
                    length = Mathf.Max(length, d);
                    if (d <= far || Physics.Linecast(eye, hb.collider.bounds.center, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) continue;
                    far = d;
                    pick = hb;
                }
                if (pick == null) { yield return new WaitForSecondsRealtime(0.7f); continue; }
                yield return AimAt(() => pick != null ? pick.collider.bounds.center : worm.corePosition);
                int hits0 = Monsters.HitsApplied;
                yield return Tap(new V1Drive.Frame { buttons = 1 });
                shots++;
                t = Now;
                while (Monsters.HitsApplied == hits0 && Now - t < 0.8f) yield return null;
                if (Monsters.HitsApplied > hits0 && Monsters.LastHitOn == worm)
                {
                    landed++;
                    best = Mathf.Max(best, Vector3.Distance(Monsters.LastHitAt, worm.corePosition));
                }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            metrics["worm_shots"] = landed + " of " + shots + " landed; furthest from its core " + F(best) + " m (segments reach " + F(length) + " m from it; the old box reached " + F(oldReach) + " m)";
            Check("shots at its body land", landed >= 1, metrics["worm_shots"]);
            Check("and land where the old single box never reached", best > oldReach + 3f, metrics["worm_shots"]);
            yield return SnapRor("ror2-worm");
            yield return SnapUk("uk");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>V1's Risk of Rain 2 health is the configured number, follows the setting live, and does not regenerate
        /// by itself.</summary>
        IEnumerator HealthTest()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            float configured = Plugin.V1Health.Value;
            metrics["configured_health"] = F(configured);
            metrics["body"] = "level " + F(v1.level) + ", max health " + F(v1.maxHealth) + ", regen " + F(v1.regen) + " (base " + F(v1.baseRegen) + ", per level " + F(v1.levelRegen) + ")";
            Check("V1's health in Risk of Rain 2 is the configured number at level 1", Mathf.Abs(v1.maxHealth - configured) < 0.5f && Mathf.Abs(v1.level - 1f) < 0.01f, metrics["body"]);
            Check("the default is 90", Mathf.Abs((float)Plugin.V1Health.DefaultValue - 90f) < 0.01f, Plugin.V1Health.DefaultValue.ToString());
            Check("V1 has no regeneration of its own", Mathf.Abs(v1.regen) < 0.001f, metrics["body"]);

            // hurt, then left alone: nothing comes back
            yield return WaitVulnerable();
            UkLink.Send(Proto.Hurt + " 40");
            yield return new WaitForSecondsRealtime(1.5f);
            int hp0 = V1Drive.UkHp;
            float healed0 = HealsReachV1.Sent;
            yield return new WaitForSecondsRealtime(8f);
            metrics["left_alone_8s"] = "ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp + ", Risk of Rain 2 healing forwarded " + F(HealsReachV1.Sent - healed0);
            Check("hurt and left alone for 8 s, V1 does not heal", hp0 < 100 && V1Drive.UkHp == hp0 && HealsReachV1.Sent - healed0 < 0.01f, metrics["left_alone_8s"]);
            Check("the body shows the same share of its health", Mathf.Abs(v1.healthComponent.health / v1.healthComponent.fullHealth * 100f - hp0) < 2f, F(v1.healthComponent.health) + " of " + F(v1.healthComponent.fullHealth));

            // the setting, changed in the middle of a run (and put back: it is the player's own config file)
            float changed = -1f;
            int hpAfter = -1;
            try
            {
                Plugin.V1Health.Value = 200f;
                yield return new WaitForSecondsRealtime(1.5f);
                changed = v1.maxHealth;
                hpAfter = V1Drive.UkHp;
            }
            finally { Plugin.V1Health.Value = configured; }
            yield return new WaitForSecondsRealtime(1.5f);
            metrics["setting_changed"] = "200 gave max health " + F(changed) + " with ULTRAKILL HP " + hpAfter + "; back to " + F(configured) + " gave " + F(v1.maxHealth);
            Check("changing the setting changes the body's health at once", Mathf.Abs(changed - 200f) < 0.5f && Mathf.Abs(v1.maxHealth - configured) < 0.5f, metrics["setting_changed"]);
            Check("and leaves V1's ULTRAKILL HP alone", hpAfter == hp0 && V1Drive.UkHp == hp0, metrics["setting_changed"]);
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>One plain Risk of Rain 2 hit on V1 from nobody, and what it cost on both sides.</summary>
        IEnumerator PlainHit(CharacterBody v1, float damage, string key)
        {
            yield return WaitVulnerable();
            var hc = v1.healthComponent;
            int hp0 = V1Drive.UkHp, hurts0 = V1HealthLink.HurtsSent;
            float shield0 = hc.shield, max0 = hc.fullHealth;
            hc.TakeDamage(new DamageInfo { damage = damage, position = v1.corePosition });
            float sent = V1HealthLink.HurtsSent > hurts0 ? V1HealthLink.LastHurt : 0f;
            yield return new WaitForSecondsRealtime(1.2f);
            lastCost = hp0 - V1Drive.UkHp;
            metrics[key] = F(damage) + " damage on " + F(max0) + " health + " + F(shield0) + " shield: HURT " + F(sent) + ", ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp + (V1Drive.UkDead ? " DEAD" : "") + ", shield -> " + F(hc.shield) + ", body health " + F(hc.health);
        }
        int lastCost;

        IEnumerator Whole()
        {
            // made whole by hand: the shield with it
            V1Drive.ShieldSyncUntil = Time.time + 1.2f;
            UkLink.Send(Proto.Respawn);
            yield return new WaitForSecondsRealtime(1.5f);
        }

        /// <summary>Healing as ULTRAKILL gives it (blood, a parry), which is not Risk of Rain 2's to see.</summary>
        IEnumerator UkHeal(int hp, string key)
        {
            int hp0 = V1Drive.UkHp;
            UkLink.Send(Proto.Heal + " " + hp);
            yield return new WaitForSecondsRealtime(1.2f);
            var hc = LocalUserManager.GetFirstLocalUser().cachedBody.healthComponent;
            metrics[key] = "healing " + hp + " in ULTRAKILL: HP " + hp0 + " -> " + V1Drive.UkHp + " (limit " + V1Drive.HpCap + "), body health " + F(hc.health) + ", shield " + F(hc.shield);
        }

        /// <summary>Out of danger, Risk of Rain 2 fills the shield, and V1's HP with it.</summary>
        IEnumerator ShieldBack(string key)
        {
            int hp0 = V1Drive.UkHp;
            float t = Now;
            while (V1Drive.UkHp < 100 && Now - t < 20f) yield return null;
            yield return new WaitForSecondsRealtime(0.5f);
            var hc = LocalUserManager.GetFirstLocalUser().cachedBody.healthComponent;
            metrics[key] = "left alone: HP " + hp0 + " -> " + V1Drive.UkHp + " after " + F(Now - t) + " s, shield " + F(hc.shield) + " of " + F(hc.fullShield);
        }

        /// <summary>What lowers or reshapes V1's Risk of Rain 2 health: Shaped Glass (half the health) and Transcendence
        /// (all of it turned into shield).</summary>
        IEnumerator MaxHealthItems()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            var hc = v1.healthComponent;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            float dmg = v1.maxHealth * 0.2f;
            // whatever the stage did to V1 before it went quiet is not part of this
            metrics["hp_at_start"] = V1Drive.UkHp + " (limit " + V1Drive.HpCap + ", " + V1HealthLink.HurtsSent + " hits from the stage so far)";
            yield return Whole();

            // 1. as V1 comes
            yield return PlainHit(v1, dmg, "plain_hit");
            int plain = lastCost;
            Check("a hit of a fifth of V1's health costs about 20 HP", Mathf.Abs(plain - 20) <= 2, metrics["plain_hit"]);

            // 2. Shaped Glass
            int hp0 = V1Drive.UkHp;
            v1.inventory.GiveItem(RoR2Content.Items.LunarDagger, 1);
            yield return new WaitForSecondsRealtime(1.5f);
            metrics["glass_pickup"] = "max health " + F(hc.fullHealth) + ", ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp;
            Check("Shaped Glass halves the body's health and leaves V1's HP where it was", Mathf.Abs(hc.fullHealth - dmg * 2.5f) < 1f && V1Drive.UkHp == hp0, metrics["glass_pickup"]);
            yield return Whole();
            yield return PlainHit(v1, dmg, "glass_hit");
            Check("the same hit now costs V1 twice as much", Mathf.Abs(lastCost - 40) <= 3, metrics["glass_hit"]);
            hp0 = V1Drive.UkHp;
            v1.inventory.RemoveItem(RoR2Content.Items.LunarDagger.itemIndex, 1);
            yield return new WaitForSecondsRealtime(1.5f);
            metrics["glass_removed"] = "max health " + F(hc.fullHealth) + ", ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp;
            Check("losing Shaped Glass does not heal V1", V1Drive.UkHp == hp0, metrics["glass_removed"]);

            // 2b. levels: more health on the body, none for V1
            float max0 = hc.fullHealth, level0 = v1.level;
            TeamManager.instance.GiveTeamExperience(TeamIndex.Player, 2000);
            yield return new WaitForSecondsRealtime(2f);
            metrics["levels"] = "level " + F(level0) + " -> " + F(v1.level) + ", max health " + F(max0) + " -> " + F(hc.fullHealth) + ", ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp;
            Check("levelling up raises the body's health and does not heal V1", v1.level > level0 && hc.fullHealth > max0 + 1f && V1Drive.UkHp == hp0, metrics["levels"]);
            dmg = hc.fullHealth * 0.2f;

            // 2c. a Personal Shield Generator: a layer on top of health that only Risk of Rain 2 fills
            yield return Whole();
            hp0 = V1Drive.UkHp;
            v1.inventory.GiveItem(RoR2Content.Items.PersonalShield, 1);
            yield return new WaitForSecondsRealtime(1.5f);
            float poolPsg = hc.fullHealth + hc.fullShield;
            int healthOnly = Mathf.CeilToInt(hc.fullHealth / poolPsg * 100f - 0.001f);
            metrics["shield_pickup"] = "health " + F(hc.fullHealth) + " + shield " + F(hc.fullShield) + ", ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp;
            Check("a Personal Shield Generator leaves V1's HP where it was", hc.fullShield > 1f && V1Drive.UkHp == hp0, metrics["shield_pickup"]);
            yield return PlainHit(v1, dmg, "shield_hit");
            Check("a hit costs its share of health and shield together, the shield going first", Mathf.Abs(lastCost - dmg / poolPsg * 100f) <= 3f && hc.shield < 0.5f, metrics["shield_hit"] + "; a share of the whole would be " + F(dmg / poolPsg * 100f));
            yield return UkHeal(60, "shield_ultrakill_heal");
            Check("ULTRAKILL's healing fills health and stops short of the shield", V1Drive.UkHp >= healthOnly - 1 && V1Drive.UkHp <= healthOnly && hc.shield < poolPsg / 100f && Mathf.Abs(hc.health - hc.fullHealth) < hc.fullHealth * 0.02f, metrics["shield_ultrakill_heal"] + "; full health without shield is " + healthOnly + " HP (ULTRAKILL counts whole HP: up to one of them, " + F(poolPsg / 100f) + " shield, comes with it)");
            yield return ShieldBack("shield_recharge");
            Check("left alone, Risk of Rain 2 fills the shield and V1 is at 100", V1Drive.UkHp == 100 && hc.shield > hc.fullShield - 0.5f, metrics["shield_recharge"]);
            v1.inventory.RemoveItem(RoR2Content.Items.PersonalShield.itemIndex, 1);
            yield return new WaitForSecondsRealtime(1f);

            // 3. Transcendence
            yield return Whole();
            hp0 = V1Drive.UkHp;
            v1.inventory.GiveItem(RoR2Content.Items.ShieldOnly, 1);
            yield return new WaitForSecondsRealtime(1.5f);
            metrics["transcendence_pickup"] = "health " + F(hc.fullHealth) + ", shield " + F(hc.fullShield) + " (now " + F(hc.shield) + "), ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp;
            Check("Transcendence leaves V1's HP where it was", V1Drive.UkHp == hp0, metrics["transcendence_pickup"]);
            float pool = hc.fullHealth + hc.fullShield;
            yield return PlainHit(v1, dmg, "transcendence_hit");
            int first = lastCost;
            Check("a hit on a transcendent V1 costs its share of health and shield together", Mathf.Abs(first - dmg / pool * 100f) <= 3f, metrics["transcendence_hit"] + "; a share of the whole would be " + F(dmg / pool * 100f));
            int afterHit = V1Drive.UkHp;
            yield return UkHeal(50, "transcendence_ultrakill_heal");
            Check("ULTRAKILL's healing does nothing for a transcendent V1's shield", V1Drive.UkHp <= afterHit + 1, metrics["transcendence_ultrakill_heal"]);
            yield return ShieldBack("transcendence_recharge");
            Check("left alone, the shield comes back and V1 is at 100", V1Drive.UkHp == 100 && hc.shield > hc.fullShield - 1f, metrics["transcendence_recharge"]);
            // one that goes through most of it
            yield return Whole();
            float big = pool * 0.8f;
            yield return PlainHit(v1, big, "transcendence_big_hit");
            Check("a hit of four fifths of the whole costs about 80 HP and does not kill", !V1Drive.UkDead && Mathf.Abs(lastCost - 80) <= 4, metrics["transcendence_big_hit"]);
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && hc.alive);
            yield return BackToTitle();
        }

        /// <summary>The Sharpshooter's ricochet shot through a Risk of Rain 2 projectile blows it up, as it does ULTRAKILL's.</summary>
        IEnumerator Sharpshooter()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            var owner = SpawnAhead("BeetleGuardMaster", 34f, false);
            if (!Check("spawned a Beetle Guard to own the fireballs", owner != null)) yield break;
            owner.baseMaxHealth = 100000f;
            owner.MarkAllStatsDirty();
            yield return new WaitForSecondsRealtime(2.5f);
            var fireball = LegacyResourcesAPI.Load<GameObject>("Prefabs/Projectiles/Fireball");
            if (!Check("fireball prefab found", fireball != null)) yield break;

            // the revolver, turned to its Sharpshooter
            string weapon = "?";
            for (int i = 0; i < 5; i++)
            {
                yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha1 } });
                yield return new WaitForSecondsRealtime(1.1f);
                yield return UkState();
                weapon = StateValue(stateReply, "weapon") ?? "?";
                metrics["weapon_after_" + (i + 1) + "_presses"] = weapon;
                if (weapon.Contains("/revolver2")) break;
            }
            if (!Check("V1 has the Sharpshooter out", weapon.Contains("/revolver2"), weapon)) yield break;

            int blown0 = Parry.ShotsBlown, hurts0 = V1HealthLink.HurtsSent, hp0 = V1Drive.UkHp;
            float ownerHp0 = owner.healthComponent.combinedHealth;
            int ukBlown = 0, tries = 0;
            for (; tries < 6 && Parry.ShotsBlown == blown0; tries++)
            {
                yield return WaitVulnerable();
                yield return AimAt(() => owner.corePosition);
                // the shot is wound up with the alternate fire held, and goes when it is let go
                V1Drive.Script = () => new V1Drive.Frame { buttons = 2 };
                yield return new WaitForSecondsRealtime(1.3f);
                Vector3 eye = V1Drive.ToRor(V1Drive.Latest.eye);
                Vector3 from = eye + (owner.corePosition - eye).normalized * 26f;
                RoR2.Projectile.ProjectileManager.instance.FireProjectile(fireball, from, Quaternion.LookRotation(eye - from), owner.gameObject, 10f, 0f, false, DamageColorIndex.Default, null, 14f);
                // a moment for its copy to be in ULTRAKILL, on the line the shot will take
                yield return new WaitForSecondsRealtime(0.12f);
                V1Drive.Script = () => default;
                float t = Now;
                while (Now - t < 2.5f && Parry.ShotsBlown == blown0) yield return null;
                yield return new WaitForSecondsRealtime(1.2f);
            }
            yield return UkState();
            int.TryParse(StateValue(stateReply, "blown"), out ukBlown);
            metrics["sharpshooter"] = (Parry.ShotsBlown - blown0) + " blown up after " + tries + " shots (ULTRAKILL counts " + ukBlown + "); V1 HP " + hp0 + " -> " + V1Drive.UkHp + ", " + (V1HealthLink.HurtsSent - hurts0) + " hits on V1; the owner 34 m off lost " + F(ownerHp0 - owner.healthComponent.combinedHealth) + "; last hit " + Monsters.LastHit;
            Check("a Sharpshooter shot through a fireball blew it up", Parry.ShotsBlown > blown0 && ukBlown >= 1, metrics["sharpshooter"]);
            yield return SnapRor("ror2-after-shot");
            yield return SnapUk("uk");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>One hitscan shot from a monster, the way Risk of Rain 2's monsters fire them, straight at V1's head.</summary>
        void MonsterShot(CharacterBody from, CharacterBody v1, float damage)
        {
            Vector3 eye = V1Drive.ToRor(V1Drive.Latest.eye), origin = from.corePosition + Vector3.up * 1.5f;
            new BulletAttack
            {
                owner = from.gameObject, weapon = from.gameObject, origin = origin, aimVector = (eye - origin).normalized, damage = damage,
                maxDistance = 200f, radius = 0.1f, bulletCount = 1, minSpread = 0f, maxSpread = 0f, procCoefficient = 1f, force = 0f,
                falloffModel = BulletAttack.FalloffModel.None, hitMask = LayerIndex.CommonMasks.bullet, stopperMask = LayerIndex.CommonMasks.bullet
            }.Fire();
        }

        /// <summary>A monster's hitscan shot that meets one of V1's coins is the coin's: it never reaches V1 as the shot it
        /// was, and ULTRAKILL does with the coin what it does when its own enemies shoot one.</summary>
        IEnumerator CoinsAgainstHitscan()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            Check("the bullet patches are on", Coins.Problem == null, Coins.Problem ?? "");
            var guard = SpawnAhead("BeetleGuardMaster", 20f, false);
            if (!Check("spawned a Beetle Guard to do the shooting", guard != null)) yield break;
            guard.baseMaxHealth = 100000f;
            guard.MarkAllStatsDirty();
            yield return new WaitForSecondsRealtime(2.5f);
            yield return AimAt(() => guard.corePosition);
            float dmg = v1.maxHealth * 0.2f;

            // 1. no coin: the shot is V1's to take
            yield return WaitVulnerable();
            int hurts0 = V1HealthLink.HurtsSent, hp0 = V1Drive.UkHp;
            MonsterShot(guard, v1, dmg);
            yield return new WaitForSecondsRealtime(1.2f);
            metrics["shot_without_coin"] = "HURT " + (V1HealthLink.HurtsSent > hurts0 ? F(V1HealthLink.LastHurt) : "none") + ", ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp;
            Check("with no coin in the way the shot hits V1", V1HealthLink.HurtsSent > hurts0 && V1Drive.UkHp < hp0, metrics["shot_without_coin"]);

            // 2. a coin on the line
            yield return Whole();
            yield return WaitVulnerable();
            Vector3 eye = V1Drive.ToRor(V1Drive.Latest.eye), origin = guard.corePosition + Vector3.up * 1.5f;
            var at = V1Drive.ToUk(eye + (origin - eye).normalized * 6f + Vector3.down * 0.15f);
            UkLink.Send(Proto.TestCoin + " " + F(at.x) + " " + F(at.y) + " " + F(at.z));
            float t = Now;
            while (Coins.Listed == 0 && Now - t < 3f) yield return null;
            metrics["coins_listed"] = Coins.Listed.ToString();
            if (!Check("Risk of Rain 2 knows where the coin is", Coins.Listed >= 1)) yield break;
            hurts0 = V1HealthLink.HurtsSent;
            hp0 = V1Drive.UkHp;
            int taken0 = Coins.Intercepted, hitsBefore = Monsters.HitsApplied;
            float guardHp0 = guard.healthComponent.combinedHealth;
            Monsters.HitLog.Clear();
            MonsterShot(guard, v1, dmg);
            yield return new WaitForSecondsRealtime(0.25f);
            int hpSoon = V1Drive.UkHp;
            yield return UkState();
            string hits = StateValue(stateReply, "coinhits") ?? "?";
            Check("the coin took the shot", Coins.Intercepted == taken0 + 1 && hits == "1", (Coins.Intercepted - taken0) + " taken here, ULTRAKILL counts " + hits);
            Check("the shot itself never reached V1", V1HealthLink.HurtsSent == hurts0 && hpSoon == hp0, "HURT sent " + (V1HealthLink.HurtsSent - hurts0) + ", ULTRAKILL HP " + hp0 + " -> " + hpSoon + " a quarter of a second on");
            // then the coin sends it on, as it does a shot of V1's: into the nearest monster (here, the one that fired)
            yield return new WaitForSecondsRealtime(2f);
            metrics["after_the_coin"] = "the Beetle Guard was hit by: " + HitsOn(guard) + " (" + Monsters.LastHit + "), its health " + F(guardHp0) + " -> " + F(guard.healthComponent.combinedHealth) + "; ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp + " two seconds on, coins left " + Coins.Listed;
            Check("the coin sent the shot into the nearest monster", Monsters.HitsApplied > hitsBefore && Monsters.LastHitOn == guard && guard.healthComponent.combinedHealth < guardHp0, metrics["after_the_coin"]);
            Check("V1 was not hurt by any of it", V1Drive.UkHp == hp0 && V1HealthLink.HurtsSent == hurts0, metrics["after_the_coin"]);
            Check("the coin is spent", Coins.Listed == 0, metrics["after_the_coin"]);
            yield return SnapUk("uk");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>The orbs that appear round Mithrix's arena when he is beaten take V1 where they take any survivor: down
        /// to the way back to the ship (run with --v1bridge-stage moon2).</summary>
        IEnumerator MoonOrbs()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            // the escape's own objects, switched on as beating Mithrix switches them on
            Transform holder = null;
            foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (tr.gameObject.scene.name != null && tr.name == "EscapeSequenceObjects") holder = tr;
            }
            if (!Check("Commencement has its escape objects", holder != null)) yield break;
            holder.gameObject.SetActive(true);
            yield return new WaitForSecondsRealtime(3f);
            MapZone orb = null;
            int orbs = 0;
            foreach (var z in holder.GetComponentsInChildren<MapZone>())
            {
                orbs++;
                if (orb == null && z.explicitDestination != null && z.isActiveAndEnabled) orb = z;
            }
            metrics["orbs"] = orbs + " exit orbs";
            if (!Check("there is an exit orb with somewhere to go", orb != null, metrics["orbs"])) yield break;
            var col = orb.GetComponent<Collider>();
            Vector3 dest = orb.explicitDestination.position;
            metrics["orb"] = orb.name + " at " + orb.transform.position + ", " + (col != null ? col.GetType().Name + " " + F(col.bounds.size.x) + " m across, enabled " + col.enabled : "no collider") + ", layer " + LayerMask.LayerToName(orb.gameObject.layer) + ", takes players to " + dest;
            int teleports0 = V1Drive.RorTeleports, hp0 = V1Drive.UkHp;
            float t = Now;
            // into it, again and again as it circles the arena, the way a player jumps at it
            while (V1Drive.RorTeleports == teleports0 && Now - t < 12f)
            {
                V1Drive.Teleport(orb.transform.position - Vector3.up * 1f, 0f);
                yield return new WaitForSecondsRealtime(0.25f);
            }
            metrics["orb_seconds"] = F(Now - t);
            if (!Check("the orb took V1", V1Drive.RorTeleports > teleports0, metrics["orb"])) yield break;
            yield return new WaitForSecondsRealtime(3f);
            Vector3 now = V1Drive.ToRor(V1Drive.Latest.feet);
            bool ground = Physics.Raycast(now + Vector3.up * 2f, Vector3.down, out var hit, 60f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
            metrics["arrived"] = "V1 at " + now + ", " + F(Vector3.Distance(now, dest)) + " m from where the orb sends players, " + (ground ? F(now.y - hit.point.y) + " m above the ground" : "no ground below") + ", body " + F(Vector3.Distance(v1.footPosition, now)) + " m from V1, " + (V1Drive.RorTeleports - teleports0) + " teleports";
            Check("V1 is where the orb sends players", Vector3.Distance(now, dest) < 25f, metrics["arrived"]);
            Check("standing on the ground there, body and all", V1Drive.Latest.grounded && ground && Vector3.Distance(v1.footPosition, now) < 0.5f, metrics["arrived"]);
            Check("unhurt, the run going on", V1Drive.UkHp >= hp0 - 1 && Run.instance != null && !Run.instance.isGameOverServer && v1.healthComponent.alive, "ULTRAKILL HP " + hp0 + " -> " + V1Drive.UkHp);
            yield return SnapRor("ror2-after-orb");
            yield return BackToTitle();
        }
        /// <summary>A V1 on its last HP dies to a Risk of Rain 2 hit: the hit counts in full even where RoR2, which may not
        /// kill the body itself, stops the body's health at 1.</summary>
        IEnumerator LastHp()
        {
            V1Drive.Script = () => default;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            yield return Whole();
            // down to a few HP, by a hit bigger than what is left: it has to cost all of its worth
            yield return WaitVulnerable();
            UkLink.Send(Proto.Hurt + " 90");
            yield return new WaitForSecondsRealtime(1.5f);
            metrics["start"] = "ULTRAKILL HP " + V1Drive.UkHp + ", body " + F(v1.healthComponent.health) + " of " + F(v1.healthComponent.fullHealth);
            if (!Check("V1 is down to 10 HP", V1Drive.UkHp == 10, metrics["start"])) yield break;
            // 6 HP worth: leaves 4
            yield return PlainHit(v1, v1.maxHealth * 0.06f, "hit_leaving_some");
            Check("a hit smaller than what is left costs what it is worth", V1Drive.UkHp >= 3 && V1Drive.UkHp <= 5 && !V1Drive.UkDead, metrics["hit_leaving_some"]);
            // down to exactly 1
            yield return WaitVulnerable();
            UkLink.Send(Proto.Hurt + " " + (V1Drive.UkHp - 1));
            yield return new WaitForSecondsRealtime(1.5f);
            Check("V1 is on its last HP", V1Drive.UkHp == 1 && !V1Drive.UkDead, "ULTRAKILL HP " + V1Drive.UkHp + ", body " + F(v1.healthComponent.health));
            yield return WaitVulnerable();
            int hurts0 = V1HealthLink.HurtsSent;
            v1.healthComponent.TakeDamage(new DamageInfo { damage = v1.maxHealth * 0.2f, position = v1.corePosition });
            float t = Now;
            while ((!V1Drive.UkDead || Run.instance == null || !Run.instance.isGameOverServer) && Now - t < 8f) yield return null;
            metrics["last_hit"] = "HURT " + (V1HealthLink.HurtsSent > hurts0 ? F(V1HealthLink.LastHurt) : "none") + ", ULTRAKILL HP " + V1Drive.UkHp + ", dead " + V1Drive.UkDead + ", run over " + (Run.instance != null && Run.instance.isGameOverServer) + " after " + F(Now - t) + " s";
            Check("a hit on a V1 with 1 HP is sent on in full", V1HealthLink.HurtsSent > hurts0 && V1HealthLink.LastHurt > 15f, metrics["last_hit"]);
            Check("and kills it: the run ends", V1Drive.UkDead && Run.instance != null && Run.instance.isGameOverServer, metrics["last_hit"]);
            yield return new WaitForSecondsRealtime(3f);
            yield return BackToTitle();
        }

        /// <summary>What another player sees where V1 is: the mod's own figure in place of the Commando, standing on the
        /// ground and facing as the body faces; and the player inside V1 sees none of it. (One game stands for both: a
        /// V1 body that is nobody's, looked at as Commando, then the player's own. A second player was not there.)</summary>
        IEnumerator FigureTest()
        {
            V1Drive.Script = () => default;
            yield return ToTitle();
            if (failed) yield break;
            Check("the patch that keeps the Commando's meshes off V1 is on", V1Figure.Problem == null, V1Figure.Problem ?? "");
            var commando = SurvivorCatalog.FindSurvivorDef("Commando");
            if (!Check("Commando found", commando != null)) yield break;
            yield return ToLobby();
            if (failed) yield break;
            yield return Pick(commando);
            yield return StartRun();
            if (failed) yield break;
            var user = LocalUserManager.GetFirstLocalUser();
            var me = user.cachedBody;
            QuietStage(me);
            yield return new WaitForSecondsRealtime(3f);
            var sceneCam = user.cameraRigController != null ? user.cameraRigController.sceneCam : null;
            if (!Check("RoR2 scene camera found", sceneCam != null)) yield break;
            var cam = sceneCam.transform;
            // on the ground a third of the way across the picture: beside the drop pod the Commando is still sitting in
            Vector3 spot = me.footPosition + cam.right * -4f;
            if (Physics.Raycast(sceneCam.ViewportPointToRay(new Vector3(0.3f, 0.5f, 0f)), out var ground, 200f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) spot = ground.point;
            Vector3 flat = Vector3.ProjectOnPlane(spot - cam.position, Vector3.up).normalized;
            var go = Instantiate(V1Survivor.BodyPrefab, spot + Vector3.up * 1.2f, Quaternion.identity);
            var other = go.GetComponent<CharacterBody>();
            go.GetComponent<TeamComponent>().teamIndex = TeamIndex.Player;
            UnityEngine.Networking.NetworkServer.Spawn(go);
            yield return new WaitForSecondsRealtime(1f);
            float facing = Quaternion.LookRotation(-flat).eulerAngles.y;
            if (other != null && other.characterDirection != null) other.characterDirection.yaw = facing;
            // its skin is put on a moment after it appears, and Risk of Rain 2 sets its renderers again then
            yield return new WaitForSecondsRealtime(4f);
            if (!Check("a V1 body that is not the player's stands in the stage", other != null && other.healthComponent != null && other.healthComponent.alive)) yield break;
            var figure = V1Figure.On(other);
            if (!Check("it carries the figure, shown", figure != null && figure.activeInHierarchy, figure == null ? "no figure" : "active " + figure.activeInHierarchy)) yield break;
            int parts = 0;
            var bounds = new Bounds(figure.transform.position, Vector3.zero);
            foreach (var r in figure.GetComponentsInChildren<Renderer>()) if (r.enabled) { parts++; bounds.Encapsulate(r.bounds); }
            var model = other.modelLocator.modelTransform.GetComponent<CharacterModel>();
            int commandoOn = 0, commandoAll = 0;
            foreach (var info in model.baseRendererInfos) { if (info.renderer == null) continue; commandoAll++; if (info.renderer.enabled) commandoOn++; }
            float turnedTo = Vector3.Angle(Vector3.ProjectOnPlane(figure.transform.forward, Vector3.up), -flat);
            metrics["figure"] = parts + " parts drawn, " + F(bounds.size.y) + " m tall, its feet " + F(bounds.min.y - other.footPosition.y) + " m above the body's, " + F(turnedTo) + " degrees off the way the body was turned; Commando's own renderers on: " + commandoOn + " of " + commandoAll;
            Check("the figure is all there", parts >= 30, metrics["figure"]);
            Check("none of the Commando's own meshes is drawn", commandoAll > 0 && commandoOn == 0, metrics["figure"]);
            Check("it stands a survivor's height, feet on the ground", bounds.size.y > 1.6f && bounds.size.y < 2.3f && Mathf.Abs(bounds.min.y - other.footPosition.y) < 0.3f, metrics["figure"]);
            Check("it faces the way the body faces", turnedTo < 15f, metrics["figure"]);
            yield return SnapRor("ror2-v1-seen-by-another");

            // carried along the ground, as Risk of Rain 2 carries a player's body about: its limbs swing; put down, they stop
            var motion = figure.GetComponent<V1FigureMotion>();
            if (Check("the figure has its motion", motion != null))
            {
                float stood = motion.Swing;
                // across the picture, away from the drop pod, turned the way it goes
                Vector3 along = -Vector3.Cross(Vector3.up, flat).normalized;
                if (other.characterDirection != null) other.characterDirection.yaw = Quaternion.LookRotation(along).eulerAngles.y;
                float maxSwing = 0f, maxLegs = 0f, t0 = Now;
                bool snapped = false;
                while (Now - t0 < 2.2f && other != null)
                {
                    Vector3 next = other.transform.position + along * 6f * Time.deltaTime;
                    if (Physics.Raycast(next + Vector3.up * 3f, Vector3.down, out var under, 8f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) next.y = under.point.y + (other.transform.position.y - other.footPosition.y);
                    if (other.characterMotor != null) other.characterMotor.Motor.SetPosition(next);
                    else other.transform.position = next;
                    maxSwing = Mathf.Max(maxSwing, motion.Swing);
                    maxLegs = Mathf.Max(maxLegs, motion.LegSpread);
                    if (!snapped && Now - t0 > 1.2f)
                    {
                        snapped = true;
                        ScreenCapture.CaptureScreenshot(Path.Combine(outDir, "ror2-v1-running.png"));
                    }
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(1.5f);
                metrics["figure_motion"] = "standing: swing " + F(stood) + "; carried at 6 m/s: swing up to " + F(maxSwing) + ", legs up to " + F(maxLegs) + " degrees apart; put down again: swing " + F(motion.Swing) + ", falling pose " + F(motion.Air);
                Check("standing still, its limbs are still", stood < 0.05f, metrics["figure_motion"]);
                Check("carried along the ground, its legs swing", maxSwing > 0.5f && maxLegs > 25f, metrics["figure_motion"]);
                Check("put down, it stops and stands", motion.Swing < 0.05f && motion.Air < 0.05f, metrics["figure_motion"]);
                // a punch: its left arm goes out
                var armL = figure.transform.Find("body/armL");
                motion.Punch(false);
                yield return new WaitForSecondsRealtime(0.15f);
                float armOut = armL != null ? Quaternion.Angle(armL.localRotation, Quaternion.identity) : 0f;
                ScreenCapture.CaptureScreenshot(Path.Combine(outDir, "ror2-v1-punching.png"));
                yield return new WaitForSecondsRealtime(0.8f);
                float armBack = armL != null ? Quaternion.Angle(armL.localRotation, Quaternion.identity) : 0f;
                metrics["figure_punch"] = "left arm " + F(armOut) + " degrees out mid-punch, " + F(armBack) + " after";
                Check("told of a punch, it throws its left arm out and brings it back", armOut > 50f && armBack < 10f, metrics["figure_punch"]);
            }
            yield return BackToTitle();
            if (failed) yield break;

            // the player inside V1
            yield return ToLobby();
            if (failed) yield break;
            yield return Pick(V1Survivor.Def);
            float t = Now;
            while (!V1Drive.UkReady && UkLink.Problem == null && UkLauncher.Problem == null && Now - t < 150f) yield return null;
            if (!Check("ULTRAKILL reached the Sandbox and stripped it", V1Drive.UkReady, V1Drive.UkReady ? "" : UkLink.Problem ?? UkLauncher.Problem ?? "last seen in " + V1Drive.UkLoading)) yield break;
            yield return StartRun();
            if (failed) yield break;
            t = Now;
            while (!V1Drive.UkLanded && Now - t < 30f) yield return null;
            if (!Check("V1 landed in ULTRAKILL", V1Drive.UkLanded, V1Drive.LandedInfo)) yield break;
            yield return new WaitForSecondsRealtime(4f);
            var mine = user.cachedBody;
            var own = V1Figure.On(mine);
            int drawn = 0;
            foreach (var r in mine.modelLocator.modelTransform.GetComponentsInChildren<Renderer>()) if (r.enabled) drawn++;
            Check("the player inside V1 is not shown the figure", own != null && !own.activeInHierarchy && drawn == 0, own == null ? "no figure" : "active " + own.activeInHierarchy + ", " + drawn + " renderers on under the model");
            yield return Hold(0.5f, new V1Drive.Frame { dx = 12f });
            yield return new WaitForSecondsRealtime(0.5f);
            float sent = mine.characterDirection != null ? mine.characterDirection.yaw : float.NaN;
            metrics["facing_sent"] = "the body's facing " + F(sent) + ", V1's view " + F(V1Drive.Latest.yaw);
            Check("the facing the other players are sent is V1's", mine.characterDirection != null && Mathf.Abs(Mathf.DeltaAngle(sent, V1Drive.Latest.yaw)) < 3f, metrics["facing_sent"]);
            yield return SnapRor("ror2-as-v1");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && mine != null && mine.healthComponent.alive);
            yield return BackToTitle();
        }

        /// <summary>What V1's weapons do is reported by ULTRAKILL as plain facts and drawn from them: a line for a hitscan
        /// shot, a ball for a blast, a light for each thing in the air. (One game: it is made to ask for the facts
        /// with nobody else there, and to draw them itself, which the V1 player's game otherwise never does.)</summary>
        IEnumerator FxTest()
        {
            V1Drive.Script = () => default;
            V1Fx.Force = true;
            V1Fx.ShowOwn = true;
            yield return V1Run();
            if (failed) yield break;
            var v1 = LocalUserManager.GetFirstLocalUser().cachedBody;
            QuietStage(v1);
            yield return new WaitForSecondsRealtime(2f);
            Func<float, float, Vector3> ahead = (metres, up) => V1Drive.ToRor(V1Drive.Latest.eye) + Quaternion.Euler(0f, V1Drive.Latest.yaw, 0f) * Vector3.forward * metres + Vector3.up * up;
            IEnumerator Until(Func<bool> done, float seconds, string shot)
            {
                float t0 = Now;
                while (!done() && Now - t0 < seconds) yield return null;
                if (done()) ScreenCapture.CaptureScreenshot(Path.Combine(outDir, shot + ".png"));
                yield return new WaitForSecondsRealtime(0.6f);
            }

            // what it costs: ULTRAKILL's frame and the time it spends saying its effects, with the saying off and then on
            V1Fx.Force = false;
            yield return new WaitForSecondsRealtime(4f);
            yield return UkState();
            metrics["cost_fx_off"] = "ULTRAKILL frame " + (StateValue(stateReply, "framems") ?? "?") + " ms, effects " + (StateValue(stateReply, "fxms") ?? "?") + " ms; RoR2 " + F(1f / Time.smoothDeltaTime) + " fps";
            V1Fx.Force = true;
            yield return new WaitForSecondsRealtime(4f);
            yield return UkState();
            metrics["cost_fx_on_idle"] = "ULTRAKILL frame " + (StateValue(stateReply, "framems") ?? "?") + " ms, effects " + (StateValue(stateReply, "fxms") ?? "?") + " ms; RoR2 " + F(1f / Time.smoothDeltaTime) + " fps";

            // 1. a revolver shot at nothing
            yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha1 } });
            yield return new WaitForSecondsRealtime(1.2f);
            Vector3 sky = ahead(30f, 4f);
            yield return AimAt(() => sky);
            int beams0 = V1Fx.BeamsDrawn;
            yield return Tap(new V1Drive.Frame { buttons = 1 });
            yield return Until(() => V1Fx.BeamsDrawn > beams0, 2.5f, "ror2-fx-shot");
            Check("a revolver shot at nothing is drawn as a line", V1Fx.BeamsDrawn > beams0, (V1Fx.BeamsDrawn - beams0) + " lines, " + V1Fx.LinesFromUk + " facts from ULTRAKILL");

            // 2. a blast on the ground ahead
            Vector3 spot = ahead(10f, 0f);
            if (Physics.Raycast(spot + Vector3.up * 20f, Vector3.down, out var ground, 60f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) spot = ground.point;
            yield return AimAt(() => spot);
            int blasts0 = V1Fx.BlastsDrawn;
            var at = V1Drive.ToUk(spot + Vector3.up * 0.5f);
            UkLink.Send("TESTBLAST " + F(at.x) + " " + F(at.y) + " " + F(at.z));
            yield return Until(() => V1Fx.BlastsDrawn > blasts0, 2.5f, "ror2-fx-blast");
            Check("an explosion is drawn as a ball of light", V1Fx.BlastsDrawn > blasts0, (V1Fx.BlastsDrawn - blasts0) + " drawn");

            // 3. a coin in the air
            var coinAt = V1Drive.ToUk(ahead(5f, 0.3f));
            UkLink.Send(Proto.TestCoin + " " + F(coinAt.x) + " " + F(coinAt.y) + " " + F(coinAt.z));
            yield return Until(() => V1Fx.KindsSeen.Contains("c"), 2.5f, "ror2-fx-coin");
            Check("a coin in the air has a light", V1Fx.KindsSeen.Contains("c"), "kinds seen: " + V1Fx.KindsSeen);

            // 4. the Whiplash thrown at nothing: its hook and its line
            yield return AimAt(() => sky);
            V1Drive.Script = () => new V1Drive.Frame { keys = new[] { KeyCode.R } };
            yield return Until(() => V1Fx.KindsSeen.Contains("h"), 2f, "ror2-fx-hook");
            V1Drive.Script = () => default;
            Check("the Whiplash's hook has a light and a line", V1Fx.KindsSeen.Contains("h"), "kinds seen: " + V1Fx.KindsSeen);
            yield return new WaitForSecondsRealtime(1.5f);

            // 4a. the shotgun's pellets, and nails: fast small things, drawn as streaks
            yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha2 } });
            yield return new WaitForSecondsRealtime(1.3f);
            yield return AimAt(() => sky);
            for (int attempt = 0; attempt < 3 && !V1Fx.KindsSeen.Contains("p"); attempt++)
            {
                yield return Tap(new V1Drive.Frame { buttons = 1 });
                yield return Until(() => V1Fx.KindsSeen.Contains("p"), 1.5f, "ror2-fx-pellets");
            }
            yield return UkState();
            metrics["weapon_for_the_pellets"] = StateValue(stateReply, "weapon") ?? "?";
            Check("a shotgun blast's pellets are drawn", V1Fx.KindsSeen.Contains("p"), "kinds seen: " + V1Fx.KindsSeen + "; weapon " + metrics["weapon_for_the_pellets"] + "; up to " + V1Fx.MarkersPeak + " lights at once");
            yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha3 } });
            yield return new WaitForSecondsRealtime(1.3f);
            yield return AimAt(() => sky);
            V1Drive.Script = () => new V1Drive.Frame { buttons = 1 };
            yield return Until(() => V1Fx.KindsSeen.Contains("n") || V1Fx.KindsSeen.Contains("s"), 2.5f, "ror2-fx-nails");
            yield return new WaitForSecondsRealtime(1.5f);
            // what it costs with the air full of nails
            yield return UkState();
            V1Drive.Script = () => default;
            metrics["weapon_for_the_nails"] = StateValue(stateReply, "weapon") ?? "?";
            metrics["cost_fx_on_firing"] = "ULTRAKILL frame " + (StateValue(stateReply, "framems") ?? "?") + " ms, effects " + (StateValue(stateReply, "fxms") ?? "?") + " ms; RoR2 " + F(1f / Time.smoothDeltaTime) + " fps; " + V1Fx.MarkersPeak + " lights at most";
            Check("nails (or saws) in flight are drawn", V1Fx.KindsSeen.Contains("n") || V1Fx.KindsSeen.Contains("s"), "kinds seen: " + V1Fx.KindsSeen + "; weapon " + metrics["weapon_for_the_nails"]);
            float fxMs = float.TryParse(StateValue(stateReply, "fxms"), System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : 999f;
            Check("saying its effects costs ULTRAKILL under a millisecond a frame, even then", fxMs < 1f && (StateValue(stateReply, "fxhooks") ?? "") == "ok", metrics["cost_fx_on_firing"] + "; hooks " + (StateValue(stateReply, "fxhooks") ?? "?"));
            yield return new WaitForSecondsRealtime(1.5f);

            // 4b. a punch at the air: said to the other players, whose figure of V1 swings its arm
            UkLink.Send("TESTARM 0");
            yield return new WaitForSecondsRealtime(1.2f);
            int punches0 = V1Fx.PunchesShown;
            for (int attempt = 0; attempt < 3 && V1Fx.PunchesShown == punches0; attempt++)
            {
                yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.F } }, 0.2f);
                yield return Until(() => V1Fx.PunchesShown > punches0, 1.5f, "ror2-fx-punch");
            }
            Check("a punch at nothing is passed on", V1Fx.PunchesShown > punches0, (V1Fx.PunchesShown - punches0) + " punches");

            // 4c. where V1 looks is what Risk of Rain 2 is given as this player's view, for whoever watches through it
            var fxUser = LocalUserManager.GetFirstLocalUser().currentNetworkUser;
            var angles = fxUser != null ? fxUser.GetComponent<RoR2.Networking.NetworkedViewAngles>() : null;
            yield return Hold(0.4f, new V1Drive.Frame { dx = 14f, dy = 5f });
            yield return new WaitForSecondsRealtime(0.4f);
            yield return null;
            // (what goes out to the other games: the game's own camera may write over the angles themselves in between)
            var sent = angles != null ? HarmonyLib.Traverse.Create(angles).Field("networkDesiredViewAngles").GetValue<PitchYawPair>() : default;
            metrics["view_angles"] = angles == null ? "none" : "sent: pitch " + F(sent.pitch) + ", yaw " + F(sent.yaw) + "; V1 looks: pitch " + F(Mathf.DeltaAngle(0f, V1Drive.Latest.pitch)) + ", yaw " + F(V1Drive.Latest.yaw);
            Check("the view angles this player sends are V1's", ViewAnglesAreV1s.Problem == null && angles != null && Mathf.Abs(Mathf.DeltaAngle(sent.yaw, V1Drive.Latest.yaw)) < 3f && Mathf.Abs(Mathf.DeltaAngle(sent.pitch, V1Drive.Latest.pitch)) < 3f, metrics["view_angles"] + (ViewAnglesAreV1s.Problem != null ? "; " + ViewAnglesAreV1s.Problem : ""));

            // 5. a rocket (slot 5), flying
            yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha5 } });
            yield return new WaitForSecondsRealtime(1.5f);
            yield return AimAt(() => sky);
            yield return Tap(new V1Drive.Frame { buttons = 1 });
            yield return Until(() => V1Fx.KindsSeen.Contains("g"), 3f, "ror2-fx-rocket");
            yield return UkState();
            metrics["weapon_for_the_rocket"] = StateValue(stateReply, "weapon") ?? "?";
            Check("a rocket in flight has a light", V1Fx.KindsSeen.Contains("g"), "kinds seen: " + V1Fx.KindsSeen + "; weapon " + metrics["weapon_for_the_rocket"]);

            yield return new WaitForSecondsRealtime(2f);
            yield return UkState();
            metrics["fx"] = V1Fx.LinesFromUk + " facts from ULTRAKILL (it counts beams/blasts/in the air now: " + (StateValue(stateReply, "fx") ?? "?") + "); drawn: " + V1Fx.BeamsDrawn + " shots, " + V1Fx.BlastsDrawn + " blasts, up to " + V1Fx.MarkersPeak + " lights at once, kinds " + V1Fx.KindsSeen;
            Check("drawing them raised no problem", V1Fx.Problem == null, V1Fx.Problem ?? "");
            Check("the run is still going and V1's body is alive", Run.instance != null && !Run.instance.isGameOverServer && v1 != null && v1.healthComponent.alive);
            V1Fx.Force = V1Fx.ShowOwn = false;
            // not left for the next test with the rocket launcher out
            yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha1 } });
            yield return new WaitForSecondsRealtime(1.2f);
            yield return BackToTitle();
        }

        // ------------------------------------------------------------ two games on one PC (tools\Run-MpTest.ps1)
        // mphost hosts as Commando, mpjoin joins it as V1. They keep step through marker files in a folder both are
        // given (--v1bridge-mp). Both are the same Steam user; a real second player was not there.

        public const string ArgMp = "--v1bridge-mp";
        string MpFile(string name) => Path.Combine(Plugin.Arg(ArgMp) ?? outDir, name);
        void MpMark(string name, string text = "") => File.WriteAllText(MpFile(name), text);

        IEnumerator MpWait(string name, float seconds, string what)
        {
            float t = Now;
            while (!File.Exists(MpFile(name)) && Now - t < seconds) yield return new WaitForSecondsRealtime(0.25f);
            Check(what, File.Exists(MpFile(name)), "waited " + F(Now - t) + " s for " + name);
        }

        /// <summary>The host's side of a V1 that joined: it stands in the host's game as the figure, its shots hurt the
        /// host's monsters and its kills pay, the host's damage reaches its ULTRAKILL, and its death is its body's.</summary>
        IEnumerator MpHost()
        {
            yield return ToTitle();
            if (failed) yield break;
            Check("the messages between a host and a joining V1 are set up", V1Net.Problem == null, V1Net.Problem ?? "");
            // the profile remembers the survivor last picked, and a remembered V1 would start an ULTRAKILL for the host
            var hostUser = LocalUserManager.GetFirstLocalUser();
            if (hostUser != null && hostUser.userProfile != null) hostUser.userProfile.SetSurvivorPreference(SurvivorCatalog.FindSurvivorDef("Commando"));
            RoR2.Console.instance.SubmitCmd(null, "transition_command \"gamemode ClassicRun; host 1;\"");
            float t = Now;
            while ((PreGameController.instance == null || LocalUserManager.GetFirstLocalUser()?.currentNetworkUser == null) && Now - t < 90f) yield return null;
            if (!Check("hosting, in character select", PreGameController.instance != null && UnityEngine.Networking.NetworkServer.active)) yield break;
            yield return new WaitForSecondsRealtime(2f);
            MpMark("host-ready");
            t = Now;
            while (NetworkUser.readOnlyInstancesList.Count < 2 && Now - t < 300f) yield return new WaitForSecondsRealtime(0.25f);
            metrics["join_seconds"] = F(Now - t);
            if (!Check("a second player joined", NetworkUser.readOnlyInstancesList.Count >= 2, NetworkUser.readOnlyInstancesList.Count + " players")) yield break;
            yield return Pick(SurvivorCatalog.FindSurvivorDef("Commando"));
            yield return MpWait("join-ready", 300f, "the joining player picked V1 and their ULTRAKILL is ready");
            if (failed) yield break;
            NetworkUser guest = null;
            foreach (var u in NetworkUser.readOnlyInstancesList) if (u != null && !u.isLocalPlayer) guest = u;
            Check("the host sees the joining player's pick", guest != null && guest.bodyIndexPreference == V1Survivor.BodyIndex, guest != null ? BodyCatalog.GetBodyName(guest.bodyIndexPreference) : "no guest");
            yield return StartRun();
            if (failed) yield break;
            var user = LocalUserManager.GetFirstLocalUser();
            var me = user.cachedBody;
            QuietStage(me);
            // how each of the test's monsters dies, for the log
            GlobalEventManager.onCharacterDeathGlobal += report =>
            {
                if (report.victimBody == null) return;
                Plugin.Log.LogInfo("[test] death: " + report.victimBody.name + " at " + report.victimBody.corePosition + ", by " + (report.attackerBody != null ? report.attackerBody.name : "nobody") + ", " + F(report.damageDealt) + " damage, type " + (report.damageInfo != null ? report.damageInfo.damageType.damageType.ToString() : "?") + ", inflictor " + (report.damageInfo != null && report.damageInfo.inflictor != null ? report.damageInfo.inflictor.name : "none"));
            };
            CharacterBody v1 = null;
            t = Now;
            while (v1 == null && Now - t < 60f)
            {
                v1 = guest != null ? guest.GetCurrentBody() : null;
                yield return null;
            }
            if (!Check("the joining player's V1 body is in the host's game", v1 != null && v1.bodyIndex == V1Survivor.BodyIndex && v1 != me)) yield break;
            var link = v1.GetComponent<V1HealthLink>();
            yield return MpWait("join-landed", 150f, "the joining V1 landed in its ULTRAKILL");
            if (failed) yield break;
            t = Now;
            while (link.Remote == null && Now - t < 15f) yield return null;
            Check("the host knows that V1 is run by its player's ULTRAKILL", link.Remote != null);
            yield return new WaitForSecondsRealtime(2f);
            var figure = V1Figure.On(v1);
            Check("the host sees the figure where the joining V1 is", figure != null && figure.activeInHierarchy);
            Vector3 seenAt = v1.footPosition;
            metrics["v1_seen_at"] = seenAt.ToString();

            // 1. a monster for it to shoot, 9 m in front of it
            Vector3 ahead = v1.footPosition + Quaternion.Euler(0f, v1.characterDirection != null ? v1.characterDirection.yaw : 0f, 0f) * Vector3.forward * 9f;
            if (Physics.Raycast(ahead + Vector3.up * 30f, Vector3.down, out var hit, 100f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) ahead = hit.point;
            var master = new MasterSummon { masterPrefab = MasterCatalog.FindMasterPrefab("BeetleGuardMaster"), position = ahead + Vector3.up * 0.5f, rotation = Quaternion.identity, teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform();
            var guard = master != null ? master.GetBody() : null;
            if (!Check("spawned a Beetle Guard in front of the joining V1", guard != null)) yield break;
            testMasters.Add(master);
            foreach (var ai in master.GetComponents<RoR2.CharacterAI.BaseAI>()) ai.enabled = false;
            guard.baseMoveSpeed = 0f;
            guard.baseAcceleration = 0f;
            // with its AI off it still swings at what stands in front of it: its swings do nothing
            guard.baseDamage = 0f;
            guard.levelDamage = 0f;
            guard.MarkAllStatsDirty();
            var rewards = guard.GetComponent<DeathRewards>();
            if (rewards != null) { rewards.goldReward = 12; rewards.expReward = 20; }
            yield return new WaitForSecondsRealtime(2f);
            float guardHp0 = guard.healthComponent.combinedHealth;
            int hits0 = V1Net.HostHits;
            uint gold0 = guest.master != null ? guest.master.money : 0;
            MpMark("target-up");
            t = Now;
            while (V1Net.HostHits == hits0 && Now - t < 60f) yield return null;
            yield return new WaitForSecondsRealtime(0.5f);
            metrics["first_hit_from_the_joining_v1"] = V1Net.LastHostHit + "; the Beetle Guard " + F(guardHp0) + " -> " + F(guard != null ? guard.healthComponent.combinedHealth : 0f);
            Check("the joining V1's shot hurt the monster in the host's game", V1Net.HostHits > hits0 && (guard == null || guard.healthComponent.combinedHealth < guardHp0), metrics["first_hit_from_the_joining_v1"]);
            // until it is dead: the kill is the joining player's (and a picture of one of its shots as the host is shown it)
            t = Now;
            int beamsSeen = V1Fx.BeamsDrawn;
            bool beamSnapped = false;
            while (guard != null && guard.healthComponent.alive && Now - t < 90f)
            {
                if (!beamSnapped && V1Fx.BeamsDrawn > beamsSeen)
                {
                    beamSnapped = true;
                    ScreenCapture.CaptureScreenshot(Path.Combine(outDir, "ror2-host-sees-a-shot.png"));
                }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(1f);
            metrics["kill"] = (V1Net.HostHits - hits0) + " hits; the joining player's gold " + gold0 + " -> " + (guest.master != null ? guest.master.money : 0);
            Check("the joining V1 killed it, and was paid for it", (guard == null || !guard.healthComponent.alive) && guest.master != null && guest.master.money > gold0, metrics["kill"]);
            MpMark("target-dead");

            // 2. Risk of Rain 2's damage on it is its ULTRAKILL's to take
            yield return new WaitForSecondsRealtime(2.5f);
            int hp0 = link.RemoteHp, hurts0 = V1HealthLink.HurtsSent;
            v1.healthComponent.TakeDamage(new DamageInfo { damage = v1.healthComponent.fullHealth * 0.3f, position = v1.corePosition });
            t = Now;
            while (link.RemoteHp == hp0 && Now - t < 5f) yield return null;
            yield return new WaitForSecondsRealtime(0.5f);
            float share = v1.healthComponent.health / v1.healthComponent.fullHealth * 100f;
            metrics["hit_on_the_joining_v1"] = "30% of its health: HURT " + F(V1HealthLink.LastHurt) + ", its ULTRAKILL HP " + hp0 + " -> " + link.RemoteHp + ", its body " + F(v1.healthComponent.health) + " of " + F(v1.healthComponent.fullHealth);
            Check("a hit on the joining V1 went to its ULTRAKILL, and cost it its share", V1HealthLink.HurtsSent > hurts0 && Mathf.Abs(hp0 - link.RemoteHp - 30) <= 3, metrics["hit_on_the_joining_v1"]);
            Check("its body in the host's game shows its ULTRAKILL HP", Mathf.Abs(share - link.RemoteHp) < 3f && v1.healthComponent.alive, metrics["hit_on_the_joining_v1"]);
            // and healing
            hp0 = link.RemoteHp;
            v1.healthComponent.Heal(v1.healthComponent.fullHealth * 0.1f, default, true);
            t = Now;
            while (link.RemoteHp == hp0 && Now - t < 5f) yield return null;
            metrics["heal_on_the_joining_v1"] = "10% of its health: its ULTRAKILL HP " + hp0 + " -> " + link.RemoteHp;
            Check("Risk of Rain 2's healing reached the joining V1", Mathf.Abs(link.RemoteHp - hp0 - 10) <= 2, metrics["heal_on_the_joining_v1"]);
            yield return SnapRor("ror2-host-sees-v1");
            MpMark("host-hit-done");

            // 2b. parrying: a monster in punching reach of the joining V1 (harmless but for the hits the test deals for it)
            Vector3 near = v1.footPosition + Quaternion.Euler(0f, v1.characterDirection != null ? v1.characterDirection.yaw : 0f, 0f) * Vector3.forward * 2.5f;
            if (Physics.Raycast(near + Vector3.up * 30f, Vector3.down, out var nearHit, 100f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) near = nearHit.point;
            var master2 = new MasterSummon { masterPrefab = MasterCatalog.FindMasterPrefab("BeetleGuardMaster"), position = near + Vector3.up * 0.5f, rotation = Quaternion.identity, teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform();
            var guard2 = master2 != null ? master2.GetBody() : null;
            if (Check("spawned a Beetle Guard in punching reach of the joining V1", guard2 != null))
            {
                testMasters.Add(master2);
                foreach (var ai in master2.GetComponents<RoR2.CharacterAI.BaseAI>()) ai.enabled = false;
                guard2.baseMoveSpeed = 0f;
                guard2.baseAcceleration = 0f;
                guard2.baseDamage = 0f;
                guard2.levelDamage = 0f;
                guard2.baseMaxHealth = 100000f;
                guard2.MarkAllStatsDirty();
                yield return new WaitForSecondsRealtime(2f);
                guard2.healthComponent.Networkhealth = guard2.healthComponent.fullHealth;
                float unit = Monsters.UnitDamage(v1);
                MpMark("punch-target-up");
                // melee, parried: the joining V1's punch arrives, and the monster's hit on it comes right behind
                int parries0 = Parry.MeleeParries;
                hurts0 = V1HealthLink.HurtsSent;
                hp0 = link.RemoteHp;
                float g2 = 0f;
                t = Now;
                while (Parry.MeleeParries == parries0 && Now - t < 25f)
                {
                    int seen = V1Net.HostHits;
                    float t1 = Now;
                    while (V1Net.HostHits == seen && Now - t1 < 5f) yield return null;
                    if (V1Net.HostHits == seen || !V1Net.LastHostHit.Contains("punch")) continue;
                    g2 = guard2.healthComponent.combinedHealth;
                    v1.healthComponent.TakeDamage(new DamageInfo { attacker = guard2.gameObject, inflictor = guard2.gameObject, damage = v1.healthComponent.fullHealth * 0.25f, position = v1.corePosition });
                    yield return new WaitForSecondsRealtime(0.8f);
                }
                metrics["melee_parry"] = (Parry.MeleeParries - parries0) + " parries, " + (V1HealthLink.HurtsSent - hurts0) + " HURT sent, the joining V1's HP " + hp0 + " -> " + link.RemoteHp + ", the monster lost " + F(g2 - guard2.healthComponent.combinedHealth) + " (a return is " + F(Parry.ParryUnits * unit) + ")";
                Check("the joining V1's punch meeting a monster's hit is a parry", Parry.MeleeParries > parries0, metrics["melee_parry"]);
                Check("the parried hit never reached it", V1HealthLink.HurtsSent == hurts0 && link.RemoteHp >= hp0, metrics["melee_parry"]);
                Check("the monster took the return", g2 - guard2.healthComponent.combinedHealth >= Parry.ParryUnits * unit * 0.9f, metrics["melee_parry"]);
                MpMark("parry-done");

                // melee, not parried: held for the window, then it lands
                yield return new WaitForSecondsRealtime(2.5f);
                hurts0 = V1HealthLink.HurtsSent;
                hp0 = link.RemoteHp;
                v1.healthComponent.TakeDamage(new DamageInfo { attacker = guard2.gameObject, inflictor = guard2.gameObject, damage = v1.healthComponent.fullHealth * 0.1f, position = v1.corePosition });
                Check("an unparried melee hit on the joining V1 is held for the parry window", V1HealthLink.HurtsSent == hurts0);
                t = Now;
                while (link.RemoteHp == hp0 && Now - t < 3f) yield return null;
                metrics["melee_unparried"] = "HP " + hp0 + " -> " + link.RemoteHp + " after " + F(Now - t) + " s";
                Check("then lands", V1HealthLink.HurtsSent > hurts0 && link.RemoteHp < hp0, metrics["melee_unparried"]);

                // a projectile: the joining game lists it to its ULTRAKILL, and a catch there removes it here
                var fireball = LegacyResourcesAPI.Load<GameObject>("Prefabs/Projectiles/Fireball");
                if (Check("fireball prefab found", fireball != null))
                {
                    yield return new WaitForSecondsRealtime(1f);
                    int caught0 = Parry.ShotParries;
                    hurts0 = V1HealthLink.HurtsSent;
                    g2 = guard2.healthComponent.combinedHealth;
                    Vector3 target = v1.corePosition, from = target + (guard2.corePosition - target).normalized * 30f + Vector3.up * 2f;
                    RoR2.Projectile.ProjectileManager.instance.FireProjectile(fireball, from, Quaternion.LookRotation(target - from), guard2.gameObject, 10f, 0f, false, DamageColorIndex.Default, null, 6f);
                    MpMark("fireball-up");
                    t = Now;
                    while (Parry.ShotParries == caught0 && Now - t < 8f) yield return null;
                    yield return new WaitForSecondsRealtime(0.6f);
                    int flying = 0;
                    foreach (var p in InstanceTracker.GetInstancesList<RoR2.Projectile.ProjectileController>()) if (p != null) flying++;
                    metrics["projectile_catch"] = (Parry.ShotParries - caught0) + " caught after " + F(Now - t) + " s, " + flying + " still flying, " + (V1HealthLink.HurtsSent - hurts0) + " HURT sent, its owner lost " + F(g2 - guard2.healthComponent.combinedHealth);
                    Check("a catch by the joining V1's ULTRAKILL removed the fireball in the host's game", Parry.ShotParries > caught0 && flying == 0, metrics["projectile_catch"]);
                    Check("it never hit V1, and its owner took the return", V1HealthLink.HurtsSent == hurts0 && g2 - guard2.healthComponent.combinedHealth >= Parry.ParryUnits * unit * 0.9f, metrics["projectile_catch"]);
                }
                if (guard2 != null && guard2.healthComponent.alive) guard2.healthComponent.Suicide();
            }
            MpMark("parry-all-done");

            // 2c. what moves a monster, and the coins: all the host's to carry out for a joining V1
            CharacterBody MpMonster(string masterName, float metres)
            {
                Vector3 at = v1.footPosition + Quaternion.Euler(0f, v1.characterDirection != null ? v1.characterDirection.yaw : 0f, 0f) * Vector3.forward * metres;
                if (Physics.Raycast(at + Vector3.up * 30f, Vector3.down, out var h, 100f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) at = h.point;
                var m = new MasterSummon { masterPrefab = MasterCatalog.FindMasterPrefab(masterName), position = at + Vector3.up * 0.5f, rotation = Quaternion.identity, teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform();
                var b = m != null ? m.GetBody() : null;
                if (b == null) return null;
                testMasters.Add(m);
                foreach (var ai in m.GetComponents<RoR2.CharacterAI.BaseAI>()) ai.enabled = false;
                b.baseMoveSpeed = 0f;
                b.baseAcceleration = 0f;
                b.baseDamage = 0f;
                b.levelDamage = 0f;
                b.baseMaxHealth = 100000f;
                b.MarkAllStatsDirty();
                return b;
            }
            yield return new WaitForSecondsRealtime(1.5f);
            var lem = MpMonster("LemurianMaster", 14f);
            if (Check("spawned a Lemurian 14 m in front of the joining V1", lem != null))
            {
                yield return new WaitForSecondsRealtime(2f);
                lem.healthComponent.Networkhealth = lem.healthComponent.fullHealth;
                Check("a Lemurian counts as light", Monsters.Light(lem));
                float d0 = Vector3.Distance(lem.corePosition, v1.corePosition), nearest = d0;
                int pulls0 = V1Net.HostPulls;
                MpMark("pull-target-up");
                t = Now;
                while (Now - t < 35f && nearest > 4.5f)
                {
                    nearest = Mathf.Min(nearest, Vector3.Distance(lem.corePosition, v1.corePosition));
                    yield return null;
                }
                metrics["whiplash"] = F(d0) + " m -> " + F(nearest) + " m, " + (V1Net.HostPulls - pulls0) + " pull updates from the joining V1";
                Check("the joining V1's Whiplash dragged the monster to it, in the host's game", V1Net.HostPulls > pulls0 && nearest < 4.5f && nearest < d0 - 6f, metrics["whiplash"]);
                MpMark("pull-done");

                // a blast of the joining V1's ULTRAKILL that hurts nothing still throws it
                yield return new WaitForSecondsRealtime(2.5f);
                Check("the dragged monster is alive and still by V1 once it is let go", lem != null && lem.healthComponent.alive && Vector3.Distance(lem.corePosition, v1.corePosition) < 8f, lem == null ? "gone" : (lem.healthComponent.alive ? "alive" : "DEAD") + ", " + F(Vector3.Distance(lem.corePosition, v1.corePosition)) + " m from V1");
                if (lem == null || !lem.healthComponent.alive)
                {
                    metrics["whiplash_note"] = "the dragged Lemurian died after it was let go";
                    lem = MpMonster("LemurianMaster", 6f);
                    yield return new WaitForSecondsRealtime(2f);
                    if (lem != null) lem.healthComponent.Networkhealth = lem.healthComponent.fullHealth;
                }
                Vector3 start = lem.corePosition;
                float lemHp = lem.healthComponent.combinedHealth, top = start.y, along = 0f;
                int knocks0 = V1Net.HostKnocks;
                MpMark("blast-ready");
                t = Now;
                while (V1Net.HostKnocks == knocks0 && Now - t < 15f) yield return null;
                t = Now;
                while (Now - t < 2.5f && lem != null)
                {
                    top = Mathf.Max(top, lem.corePosition.y);
                    along = Mathf.Max(along, Vector3.Distance(new Vector3(lem.corePosition.x, 0f, lem.corePosition.z), new Vector3(start.x, 0f, start.z)));
                    yield return null;
                }
                metrics["blast"] = (V1Net.HostKnocks - knocks0) + " blasts told to the host; the Lemurian went up " + F(top - start.y) + " m, along " + F(along) + " m, health " + F(lemHp) + " -> " + F(lem.healthComponent.combinedHealth);
                Check("a blast of the joining V1's threw the monster in the host's game", V1Net.HostKnocks > knocks0 && (top - start.y > 0.5f || along > 1.5f), metrics["blast"]);
                Check("and did not hurt it", lem.healthComponent.combinedHealth >= lemHp - 0.5f, metrics["blast"]);
                lem.healthComponent.Suicide();
            }
            MpMark("blast-done");

            // a coin of the joining V1's in the way of a monster's hitscan shot
            yield return new WaitForSecondsRealtime(1.5f);
            var shooter = MpMonster("BeetleGuardMaster", 16f);
            if (Check("spawned a Beetle Guard to do the shooting", shooter != null && Coins.Problem == null, Coins.Problem ?? ""))
            {
                yield return new WaitForSecondsRealtime(2f);
                shooter.healthComponent.Networkhealth = shooter.healthComponent.fullHealth;
                MpMark("shooter-up");
                yield return MpWait("coin-up", 40f, "the joining V1 hung a coin in the air");
                yield return new WaitForSecondsRealtime(0.3f);
                metrics["coins_known_to_the_host"] = link.RemoteCoins.Count.ToString();
                if (Check("the host knows where the joining V1's coin hangs", link.RemoteCoins.Count >= 1))
                {
                    // along the line the joining game gave, through the coin where it hangs now (it drifts)
                    var d = File.ReadAllText(MpFile("coin-up")).Split(' ');
                    Vector3 dir = new Vector3(float.Parse(d[0], CultureInfo.InvariantCulture), float.Parse(d[1], CultureInfo.InvariantCulture), float.Parse(d[2], CultureInfo.InvariantCulture)).normalized;
                    Vector3 coinAt = link.RemoteCoins.coins[0].at, origin = coinAt + dir * 12f;
                    int taken0 = Coins.Intercepted;
                    hurts0 = V1HealthLink.HurtsSent;
                    hits0 = V1Net.HostHits;
                    hp0 = link.RemoteHp;
                    float shooterHp = shooter.healthComponent.combinedHealth;
                    new BulletAttack
                    {
                        owner = shooter.gameObject, weapon = shooter.gameObject, origin = origin, aimVector = -dir, damage = v1.maxHealth * 0.2f,
                        maxDistance = 200f, radius = 0.1f, bulletCount = 1, minSpread = 0f, maxSpread = 0f, procCoefficient = 1f, force = 0f,
                        falloffModel = BulletAttack.FalloffModel.None, hitMask = LayerIndex.CommonMasks.bullet, stopperMask = LayerIndex.CommonMasks.bullet
                    }.Fire();
                    yield return new WaitForSecondsRealtime(0.3f);
                    Check("the joining V1's coin took the monster's shot", Coins.Intercepted == taken0 + 1 && V1HealthLink.HurtsSent == hurts0, (Coins.Intercepted - taken0) + " taken, " + (V1HealthLink.HurtsSent - hurts0) + " HURT sent");
                    yield return new WaitForSecondsRealtime(2.5f);
                    metrics["after_the_coin"] = (V1Net.HostHits - hits0) + " hits came back from the joining V1 (" + V1Net.LastHostHit + "); the shooter " + F(shooterHp) + " -> " + F(shooter.healthComponent.combinedHealth) + "; the joining V1's HP " + hp0 + " -> " + link.RemoteHp;
                    Check("and sent it on into a monster, through the host", V1Net.HostHits > hits0 && shooter.healthComponent.combinedHealth < shooterHp, metrics["after_the_coin"]);
                    Check("the joining V1 was not hurt by any of it", link.RemoteHp >= hp0 && V1HealthLink.HurtsSent == hurts0, metrics["after_the_coin"]);
                }
                shooter.healthComponent.Suicide();
            }
            MpMark("coins-done");

            // 3. it moves: its body in the host's game goes with it
            yield return MpWait("join-walked", 30f, "the joining V1 walked");
            yield return new WaitForSecondsRealtime(1f);
            float moved = v1 != null ? Vector3.Distance(v1.footPosition, seenAt) : 0f;
            metrics["v1_moved_m"] = F(moved);
            Check("its body in the host's game went with it", moved > 3f, F(moved) + " m");

            // 3b. where it looks reaches the host, and a dead host watches through its eyes
            var angles = guest.GetComponent<RoR2.Networking.NetworkedViewAngles>();
            MpMark("turn-please");
            yield return MpWait("join-turned", 30f, "the joining V1 turned");
            yield return new WaitForSecondsRealtime(0.8f);
            float said = File.Exists(MpFile("join-turned")) ? float.Parse(File.ReadAllText(MpFile("join-turned")), CultureInfo.InvariantCulture) : 0f;
            metrics["view_angles_on_the_host"] = angles == null ? "none" : "the joining V1 looks along yaw " + F(said) + "; the host has " + F(angles.viewAngles.yaw);
            Check("the host is sent where the joining V1 looks", angles != null && Mathf.Abs(Mathf.DeltaAngle(angles.viewAngles.yaw, said)) < 10f, metrics["view_angles_on_the_host"]);
            Check("the host was shown the joining V1's punches on its figure", V1Fx.PunchesShown >= 1 && figure != null && figure.GetComponent<V1FigureMotion>() != null && figure.GetComponent<V1FigureMotion>().Punches >= 1, V1Fx.PunchesShown + " punches told");
            me.healthComponent.Suicide();
            yield return new WaitForSecondsRealtime(5f);
            var rig = user.cameraRigController;
            Check("the dead host's camera is on the joining V1", rig != null && rig.targetBody == v1, rig == null ? "no camera" : "watching " + (rig.targetBody != null ? rig.targetBody.name : "nothing"));
            MpMark("turn-again");
            yield return MpWait("join-turned-again", 30f, "the joining V1 turned again");
            yield return new WaitForSecondsRealtime(1.5f);
            said = File.Exists(MpFile("join-turned-again")) ? float.Parse(File.ReadAllText(MpFile("join-turned-again")), CultureInfo.InvariantCulture) : 0f;
            float camYaw = rig != null && rig.sceneCam != null ? rig.sceneCam.transform.eulerAngles.y : 0f;
            metrics["spectating"] = "the joining V1 looks along yaw " + F(said) + "; the dead host's camera looks along " + F(camYaw);
            Check("the spectating camera looks where the joining V1 looks", Mathf.Abs(Mathf.DeltaAngle(camYaw, said)) < 20f, metrics["spectating"]);
            yield return SnapRor("ror2-host-spectating-v1");

            // 4. its death in ULTRAKILL is its body's death here
            MpMark("host-ready-for-death");
            t = Now;
            while (v1 != null && v1.healthComponent.alive && Now - t < 30f) yield return null;
            Check("the joining V1's death in its ULTRAKILL killed its body in the host's game", v1 == null || !v1.healthComponent.alive, "after " + F(Now - t) + " s");
            Check("the host's game is still up", Run.instance != null);
            // what its weapons did was drawn here all along
            metrics["v1_fx_drawn_by_the_host"] = V1Fx.BeamsDrawn + " shots, " + V1Fx.BlastsDrawn + " blasts, up to " + V1Fx.MarkersPeak + " things in the air at once (kinds: " + V1Fx.KindsSeen + ")" + (V1Fx.Problem != null ? "; " + V1Fx.Problem : "");
            Check("the host was shown the joining V1's shots, its blast, its coin and its hook", V1Fx.BeamsDrawn >= 3 && V1Fx.BlastsDrawn >= 1 && V1Fx.KindsSeen.Contains("c") && V1Fx.KindsSeen.Contains("h") && V1Fx.Problem == null, metrics["v1_fx_drawn_by_the_host"]);
            metrics["messages"] = V1Net.SentToJoined + " lines to the joining V1, " + V1Net.HostHits + " hits from it";
            yield return new WaitForSecondsRealtime(2f);
            MpMark("host-done");
            yield return new WaitForSecondsRealtime(4f);
        }

        /// <summary>The joining side: V1 in a game someone else hosts.</summary>
        IEnumerator MpJoin()
        {
            V1Drive.Script = () => default;
            yield return ToTitle();
            if (failed) yield break;
            yield return MpWait("host-ready", 400f, "the host is up");
            if (failed) yield break;
            Check("this game goes in as a second player (tests only)", SecondPlayer.Problem == null, SecondPlayer.Problem ?? "");
            var joinUser = LocalUserManager.GetFirstLocalUser();
            if (joinUser != null && joinUser.userProfile != null) joinUser.userProfile.SetSurvivorPreference(SurvivorCatalog.FindSurvivorDef("Commando"));
            yield return new WaitForSecondsRealtime(3f);
            RoR2.Console.instance.SubmitCmd(null, "connect localhost:7777");
            float t = Now;
            while ((PreGameController.instance == null || LocalUserManager.GetFirstLocalUser()?.currentNetworkUser == null) && Now - t < 120f) yield return null;
            metrics["connect_seconds"] = F(Now - t);
            if (!Check("joined the host's character select", PreGameController.instance != null && LocalUserManager.GetFirstLocalUser()?.currentNetworkUser != null && !UnityEngine.Networking.NetworkServer.active, "scene " + SceneManager.GetActiveScene().name + ", hosting " + UnityEngine.Networking.NetworkServer.active)) yield break;
            yield return new WaitForSecondsRealtime(2f);
            yield return Pick(V1Survivor.Def);
            t = Now;
            while (!V1Drive.UkReady && UkLink.Problem == null && UkLauncher.Problem == null && Now - t < 150f) yield return null;
            if (!Check("ULTRAKILL reached the Sandbox and stripped it", V1Drive.UkReady, V1Drive.UkReady ? "" : UkLink.Problem ?? UkLauncher.Problem ?? "last seen in " + V1Drive.UkLoading)) yield break;
            MpMark("join-ready");
            var user = LocalUserManager.GetFirstLocalUser();
            t = Now;
            while ((Run.instance == null || user.cachedBody == null) && Now - t < 300f) yield return null;
            if (!Check("the host started the run and this player has a body", Run.instance != null && user.cachedBody != null, "scene " + SceneManager.GetActiveScene().name)) yield break;
            var v1 = user.cachedBody;
            Check("the body is V1's, in someone else's game", BodyCatalog.GetBodyName(v1.bodyIndex) == V1Survivor.BodyName && !UnityEngine.Networking.NetworkServer.active);
            t = Now;
            while (!V1Drive.UkLanded && Now - t < 40f) yield return null;
            if (!Check("V1 landed in ULTRAKILL", V1Drive.UkLanded, V1Drive.LandedInfo + "; stage " + (V1Drive.StageLoaded ?? "not loaded"))) yield break;
            t = Now;
            while (Frames.Uploaded < 30 && Now - t < 30f) yield return null;
            Check("ULTRAKILL frames are arriving", Frames.Uploaded >= 30, Frames.Uploaded + " frames");
            yield return new WaitForSecondsRealtime(3f);
            Check("V1 is standing on the stage", V1Drive.Latest.grounded, "feet " + V1Drive.ToRor(V1Drive.Latest.feet) + ", stage " + V1Drive.Stage.objects + " colliders");
            yield return StandingCheck("in the host's game");
            // the revolver: ULTRAKILL remembers the weapon last out, whatever the test before this one left
            yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.Alpha1 } });
            yield return new WaitForSecondsRealtime(1.2f);
            MpMark("join-landed");

            // 1. the monster the host put in front of V1
            yield return MpWait("target-up", 120f, "the host put a monster in front of V1");
            if (failed) yield break;
            CharacterBody guard = null;
            t = Now;
            while (guard == null && Now - t < 10f)
            {
                foreach (var b in CharacterBody.readOnlyInstancesList) if (b != null && b != v1 && b.teamComponent != null && b.teamComponent.teamIndex == TeamIndex.Monster && b.healthComponent != null && b.healthComponent.alive) guard = b;
                yield return null;
            }
            if (!Check("the joining game has the monster too", guard != null && Monsters.Listed >= 1, Monsters.Listed + " listed to ULTRAKILL")) yield break;
            float guardHp0 = guard.healthComponent.combinedHealth;
            int shots = 0, hitsSent0 = Monsters.HitsApplied;
            while (guard != null && guard.healthComponent.alive && shots < 40 && !File.Exists(MpFile("target-dead")))
            {
                yield return AimAt(() => guard != null ? guard.corePosition : Vector3.zero);
                if (guard == null || !guard.healthComponent.alive) break;
                yield return Tap(new V1Drive.Frame { buttons = 1 });
                shots++;
                if (shots == 2) metrics["monster_health_seen_by_the_joining_game"] = F(guardHp0) + " -> " + F(guard.healthComponent.combinedHealth) + " after 2 shots";
                yield return new WaitForSecondsRealtime(0.55f);
            }
            metrics["shots"] = shots + " shots, " + (Monsters.HitsApplied - hitsSent0) + " hits sent to the host (" + Monsters.LastHit + ")";
            Check("V1's shots were sent to the host", Monsters.HitsApplied > hitsSent0, metrics["shots"]);
            yield return MpWait("target-dead", 30f, "the host saw the monster die to V1");
            yield return new WaitForSecondsRealtime(1f);
            metrics["kills_told_by_the_host"] = V1Net.JoinedKills.ToString();
            Check("the host told V1's ULTRAKILL about the kill", V1Net.JoinedKills >= 1 && Monsters.KillsByV1 >= 1, V1Net.JoinedKills + " told, " + Monsters.KillsByV1 + " passed to ULTRAKILL");
            yield return SnapRor("ror2-join-after-kill");

            // 2. the host's hit and heal
            int hpBefore = V1Drive.UkHp;
            yield return MpWait("host-hit-done", 40f, "the host hit and healed V1");
            yield return new WaitForSecondsRealtime(0.5f);
            metrics["hp_after_the_hosts_hit_and_heal"] = hpBefore + " -> " + V1Drive.UkHp;
            Check("the host's hit and heal reached V1's ULTRAKILL health", V1Drive.UkHp < hpBefore - 10 && V1Drive.UkHp > hpBefore - 30, metrics["hp_after_the_hosts_hit_and_heal"]);

            // 2b. parrying. The Feedbacker: ULTRAKILL remembers the arm last used, and the Knuckleblaster does not parry
            yield return MpWait("punch-target-up", 40f, "the host put a monster in punching reach");
            UkLink.Send("TESTARM 0");
            yield return new WaitForSecondsRealtime(1.5f);
            CharacterBody guard2 = null;
            foreach (var b in CharacterBody.readOnlyInstancesList) if (b != null && b != v1 && b.teamComponent != null && b.teamComponent.teamIndex == TeamIndex.Monster && b.healthComponent != null && b.healthComponent.alive) guard2 = b;
            if (Check("the joining game has that monster too", guard2 != null))
            {
                int told0 = V1Net.JoinedParries;
                float t2 = Now;
                while (!File.Exists(MpFile("parry-done")) && Now - t2 < 40f)
                {
                    yield return AimAt(() => guard2 != null ? guard2.corePosition : Vector3.zero);
                    yield return Tap(new V1Drive.Frame { keys = new[] { KeyCode.F } }, 0.25f);
                    yield return new WaitForSecondsRealtime(0.7f);
                }
                yield return new WaitForSecondsRealtime(0.5f);
                yield return UkState();
                metrics["parries"] = (V1Net.JoinedParries - told0) + " told by the host; ULTRAKILL counts " + (StateValue(stateReply, "parries") ?? "?");
                Check("the host told V1's ULTRAKILL of the parry, and it played one", V1Net.JoinedParries > told0 && (StateValue(stateReply, "parries") ?? "0") != "0", metrics["parries"]);
            }
            // the fireball: listed to ULTRAKILL here; then the catch, said to the host as ULTRAKILL's fist would have it said
            yield return MpWait("fireball-up", 40f, "the host fired a fireball at V1");
            float t3 = Now;
            int listed = 0, ukShots = 0, shotId = 0;
            while (Now - t3 < 4f && shotId == 0)
            {
                listed = Mathf.Max(listed, Parry.ShotsListed);
                shotId = Parry.AnyShot();
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.25f);
            yield return UkState();
            int.TryParse(StateValue(stateReply, "shots"), out ukShots);
            metrics["fireball"] = Mathf.Max(listed, Parry.ShotsListed) + " listed here, ULTRAKILL had " + ukShots;
            Check("the fireball flies through the joining V1's ULTRAKILL too", shotId != 0 && ukShots >= 1, metrics["fireball"]);
            int hpAtCatch = V1Drive.UkHp;
            if (shotId != 0) Parry.OnMessage(Proto.Parry, "p " + shotId + " 0 0 1");
            yield return MpWait("parry-all-done", 20f, "the host finished with the parries");
            yield return new WaitForSecondsRealtime(0.5f);
            Check("the caught fireball did not hurt V1", V1Drive.UkHp >= hpAtCatch, "ULTRAKILL HP " + hpAtCatch + " -> " + V1Drive.UkHp);

            // 2c. the Whiplash on a light monster, a blast beside it, a coin against a shot
            CharacterBody MpFind()
            {
                CharacterBody found = null;
                foreach (var b in CharacterBody.readOnlyInstancesList) if (b != null && b != v1 && b.teamComponent != null && b.teamComponent.teamIndex == TeamIndex.Monster && b.healthComponent != null && b.healthComponent.alive) found = b;
                return found;
            }
            yield return MpWait("pull-target-up", 40f, "the host put a light monster 14 m off");
            yield return new WaitForSecondsRealtime(0.5f);
            var lem = MpFind();
            if (Check("the joining game has it too", lem != null))
            {
                for (int attempt = 0; attempt < 5 && !File.Exists(MpFile("pull-done")); attempt++)
                {
                    yield return AimAt(() => lem != null ? lem.corePosition : Vector3.zero);
                    V1Drive.Script = () => new V1Drive.Frame { keys = new[] { KeyCode.R } };
                    yield return new WaitForSecondsRealtime(0.35f);
                    // the pull starts when the key is let go
                    V1Drive.Script = () => default;
                    float tp = Now;
                    while (Now - tp < 3f && !File.Exists(MpFile("pull-done"))) yield return null;
                    yield return new WaitForSecondsRealtime(0.8f);
                }
                yield return MpWait("pull-done", 20f, "the host saw the monster dragged to V1");
                metrics["whiplash_seen_here"] = lem != null ? F(Vector3.Distance(lem.corePosition, v1.corePosition)) + " m from V1 in the joining game" : "gone";
                yield return MpWait("blast-ready", 30f, "the host is watching for a blast");
                lem = MpFind() ?? lem;
                if (lem != null)
                {
                    var at = V1Drive.ToUk(lem.footPosition + (V1Drive.ToRor(V1Drive.Latest.feet) - lem.footPosition).normalized * 1.5f);
                    UkLink.Send("TESTBLAST " + F(at.x) + " " + F(at.y) + " " + F(at.z));
                }
            }
            yield return MpWait("blast-done", 40f, "the host finished with the blast");
            yield return MpWait("shooter-up", 40f, "the host put up a monster to shoot at V1");
            yield return new WaitForSecondsRealtime(0.8f);
            var shooter = MpFind();
            if (Check("the joining game has the shooter", shooter != null))
            {
                Vector3 eye = V1Drive.ToRor(V1Drive.Latest.eye), dir = (shooter.corePosition + Vector3.up * 4f - eye).normalized;
                var coinAt = V1Drive.ToUk(eye + dir * 6f);
                UkLink.Send(Proto.TestCoin + " " + F(coinAt.x) + " " + F(coinAt.y) + " " + F(coinAt.z));
                float tc = Now;
                while (Coins.Listed == 0 && Now - tc < 3f) yield return null;
                Check("V1's coin is listed in the joining game", Coins.Listed >= 1);
                yield return new WaitForSecondsRealtime(0.3f);
                int hpCoin = V1Drive.UkHp;
                MpMark("coin-up", F(dir.x) + " " + F(dir.y) + " " + F(dir.z));
                yield return MpWait("coins-done", 40f, "the host fired at the coin");
                yield return UkState();
                metrics["coin"] = "ULTRAKILL counts " + (StateValue(stateReply, "coinhits") ?? "?") + " shots taken by coins; V1's HP " + hpCoin + " -> " + V1Drive.UkHp;
                Check("the host told V1's ULTRAKILL its coin took a shot", (StateValue(stateReply, "coinhits") ?? "0") != "0" && V1Drive.UkHp >= hpCoin, metrics["coin"]);
            }
            else yield return MpWait("coins-done", 60f, "the host finished with the coin");

            // 3. a walk
            Vector3 p0 = V1Drive.ToRor(V1Drive.Latest.feet);
            yield return Hold(1.5f, new V1Drive.Frame { keys = new[] { KeyCode.S } });
            yield return new WaitForSecondsRealtime(0.5f);
            Check("V1 walks in someone else's game", Vector3.Distance(V1Drive.ToRor(V1Drive.Latest.feet), p0) > 3f, F(Vector3.Distance(V1Drive.ToRor(V1Drive.Latest.feet), p0)) + " m");
            yield return SnapUk("uk");
            MpMark("join-walked");

            // 3b. turning, twice, for the host to follow: first alive, then dead and watching through V1
            yield return MpWait("turn-please", 60f, "the host asked V1 to turn");
            yield return Hold(0.4f, new V1Drive.Frame { dx = 14f });
            yield return new WaitForSecondsRealtime(0.4f);
            MpMark("join-turned", F(V1Drive.Latest.yaw));
            yield return MpWait("turn-again", 60f, "the host asked V1 to turn again");
            yield return Hold(0.5f, new V1Drive.Frame { dx = -22f });
            yield return new WaitForSecondsRealtime(0.4f);
            MpMark("join-turned-again", F(V1Drive.Latest.yaw));

            // 4. death
            yield return MpWait("host-ready-for-death", 60f, "the host is watching for V1's death");
            yield return WaitVulnerable();
            UkLink.Send(Proto.Hurt + " 999");
            t = Now;
            while (!V1Drive.UkDead && Now - t < 10f) yield return null;
            Check("V1 died in ULTRAKILL", V1Drive.UkDead);
            t = Now;
            while (user.cachedBody != null && user.cachedBody.healthComponent.alive && Now - t < 10f) yield return null;
            Check("and its body died in the joining game too (the host's doing)", user.cachedBody == null || !user.cachedBody.healthComponent.alive, "after " + F(Now - t) + " s");
            metrics["v1_fx"] = V1Fx.LinesFromUk + " facts from ULTRAKILL passed to the host; drawn in V1's own game: " + V1Fx.BeamsDrawn + " shots, " + V1Fx.BlastsDrawn + " blasts";
            Check("V1's game passed on what its weapons did, and drew none of it itself", V1Fx.LinesFromUk > 3 && V1Fx.BeamsDrawn == 0 && V1Fx.BlastsDrawn == 0, metrics["v1_fx"]);
            metrics["messages"] = V1Net.SentToHost + " lines to the host";
            yield return MpWait("host-done", 30f, "the host finished");
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
    /// <summary>Tests only (the mpjoin scenario): two games on one PC are the same Steam user, and a host turns away a
    /// second connection from an account it already has. The joining test game says it is somebody else. Never
    /// applied in normal play.</summary>
    static class SecondPlayer
    {
        static readonly RoR2.PlatformID id = (RoR2.PlatformID)Activator.CreateInstance(typeof(RoR2.PlatformID), 76561190000000001UL);
        public static string Problem;

        public static void Apply()
        {
            try
            {
                var h = new HarmonyLib.Harmony("dev.v1bridge.ror.test");
                var t = typeof(RoR2.Networking.NetworkManagerSystemSteam);
                h.Patch(HarmonyLib.AccessTools.Method(t, "PlatformAuth"), postfix: new HarmonyLib.HarmonyMethod(typeof(SecondPlayer), nameof(Auth)));
                h.Patch(HarmonyLib.AccessTools.Method(t, "CreateClientAddPlayerMessage"), postfix: new HarmonyLib.HarmonyMethod(typeof(SecondPlayer), nameof(Add)));
            }
            catch (Exception e) { Problem = e.Message; }
        }

        static void Auth(ref RoR2.Networking.ClientAuthData authData)
        {
            authData.platformId = id;
            authData.authTicket = new byte[0];
        }

        static void Add(object __result)
        {
            if (__result == null) return;
            HarmonyLib.Traverse.Create(__result).Field("id").SetValue(id);
            HarmonyLib.Traverse.Create(__result).Field("steamAuthTicketData").SetValue(new byte[0]);
        }
    }
}
