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
    /// Scenarios: handshake.</summary>
    public class TestMode : MonoBehaviour
    {
        public const string ArgName = "--v1bridge-test";
        public const string ArgOut = "--v1bridge-out";

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
            StartCoroutine(Run());
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

        IEnumerator Run()
        {
            float started = Time.realtimeSinceStartup;
            IEnumerator body;
            switch (scenario)
            {
                case "handshake": body = Handshake(); break;
                default:
                    Check("known scenario", false, scenario);
                    body = null;
                    break;
            }
            // a scenario that hangs is a failure, not a game left running
            const float limit = 420f;
            while (body != null)
            {
                bool more;
                try { more = body.MoveNext(); }
                catch (Exception e)
                {
                    Check("no exception", false, e.ToString());
                    break;
                }
                if (!more) break;
                if (Time.realtimeSinceStartup - started > limit)
                {
                    Check("finished within " + limit + " s", false);
                    break;
                }
                yield return body.Current;
            }
            metrics["seconds"] = F(Time.realtimeSinceStartup - started);
            Write();
            UkLauncher.Close();
            yield return new WaitForSecondsRealtime(0.5f);
            Application.Quit();
        }

        /// <summary>Phase 1: both plugins load, the saves are sandboxed, ULTRAKILL starts, the two shake hands, the link
        /// is quick, both can save a screenshot, and ULTRAKILL goes when told.</summary>
        IEnumerator Handshake()
        {
            // 1. Risk of Rain 2 reaches its main menu on the sandboxed saves
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
