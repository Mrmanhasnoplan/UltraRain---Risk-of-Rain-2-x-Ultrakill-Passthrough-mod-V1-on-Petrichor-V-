using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;

namespace V1Bridge.Ror
{
    /// <summary>Starts ULTRAKILL through Steam with its own BepInEx profile (picked with Doorstop's command-line
    /// switches, so nothing in ULTRAKILL's folder changes) and closes the one it started.</summary>
    internal static class UkLauncher
    {
        const string AppId = "1229490";
        public static bool Started { get; private set; }
        public static string Problem { get; private set; }

        public static string UkProfile
        {
            get
            {
                var cfg = Plugin.UkProfileDir.Value;
                return string.IsNullOrWhiteSpace(cfg) ? Path.GetFullPath(Path.Combine(Plugin.ProfileDir, "..", "uk")) : cfg;
            }
        }

        public static Process Running()
        {
            foreach (var p in Process.GetProcessesByName("ULTRAKILL")) return p;
            return null;
        }

        /// <summary>False with <see cref="Problem"/> set when ULTRAKILL can't be started as the bridge needs it.</summary>
        public static bool Launch()
        {
            Problem = null;
            UkLink.Wanted = true;
            if (UkLink.Connected) return true;
            if (Running() != null) return Fail("ULTRAKILL is already running without the bridge: close it first");
            string steam = SteamValue("SteamExe");
            if (steam == null || !File.Exists(steam)) return Fail("Steam not found");
            string game = FindGame(SteamValue("SteamPath"));
            if (game == null) return Fail("ULTRAKILL not found in any Steam library");
            string preloader = Path.Combine(UkProfile, "BepInEx", "core", "BepInEx.Preloader.dll");
            if (!File.Exists(preloader)) return Fail("no BepInEx profile for ULTRAKILL at " + UkProfile + " (run tools\\Setup-Profiles.ps1)");
            if (!File.Exists(Path.Combine(UkProfile, "BepInEx", "plugins", "UkBridge", "UkBridge.dll"))) return Fail("UkBridge.dll is not in " + UkProfile);
            // Doorstop 3 does not load on ULTRAKILL's Unity 2022: the game would start unmodded, on the real save
            int doorstop = DoorstopVersion(game);
            if (doorstop < 4) return Fail("ULTRAKILL needs Doorstop 4 next to ULTRAKILL.exe (found " + (doorstop == 0 ? "none" : "version " + doorstop) + "): run tools\\Install-UkDoorstop.ps1");
            string switches = "--doorstop-enabled true --doorstop-target-assembly \"" + preloader + "\"";
            string gameArgs = switches + " " + Proto.UkArgActive + " " + Proto.UkArgPort + " " + Plugin.Port.Value
                              + " " + Proto.UkArgParent + " " + Process.GetCurrentProcess().Id + " -screen-fullscreen 0 -screen-width 1280 -screen-height 720";
            // Straight from its exe, told which Steam app it is, not through "steam -applaunch": Steam's launcher may
            // stop to ask something first (cloud saves out of step: "Play anyway?"), and a question waiting behind
            // Risk of Rain 2 means an ULTRAKILL that never comes. The bridge's ULTRAKILL plays on its own copies of the
            // saves, so there is nothing of Steam's cloud for it to wait on. Through Steam only if that fails.
            string how;
            ours = null;
            try
            {
                var direct = new ProcessStartInfo(Path.Combine(game, "ULTRAKILL.exe"), gameArgs) { UseShellExecute = false, WorkingDirectory = game };
                // Doorstop marks the process it has loaded into with environment variables of its own, and a child
                // inherits them: ULTRAKILL's Doorstop then takes itself for already loaded and does nothing, and
                // ULTRAKILL runs without UkBridge (it did, once, for the 25 s the watchdog allows). They are not
                // this game's to pass on.
                var inherited = new List<string>();
                foreach (System.Collections.DictionaryEntry v in direct.EnvironmentVariables)
                {
                    if (((string)v.Key).StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase)) inherited.Add((string)v.Key);
                }
                foreach (string name in inherited) direct.EnvironmentVariables.Remove(name);
                Plugin.Log.LogInfo("not handed on to ULTRAKILL: " + (inherited.Count > 0 ? string.Join(", ", inherited.ToArray()) : "no Doorstop variables found"));
                direct.EnvironmentVariables["SteamAppId"] = AppId;
                direct.EnvironmentVariables["SteamGameId"] = AppId;
                ours = Process.Start(direct);
                how = "directly";
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("couldn't start ULTRAKILL from its exe (" + e.Message + "): asking Steam");
                try
                {
                    Process.Start(new ProcessStartInfo(steam, "-applaunch " + AppId + " " + gameArgs) { UseShellExecute = false });
                    how = "through Steam";
                }
                catch (Exception e2) { return Fail("couldn't start ULTRAKILL: " + e2.Message); }
            }
            Started = true;
            seenAt = -1f;
            startedAt = UnityEngine.Time.realtimeSinceStartup;
            nextLook = 0f;
            warned = false;
            Plugin.Log.LogInfo("starting ULTRAKILL " + how + " (Doorstop " + doorstop + "): " + gameArgs);
            return true;
        }

        /// <summary>Said in Risk of Rain 2's chat, where the player will see it.</summary>
        static void Tell(string text)
        {
            try { RoR2.Chat.AddMessage("<color=#ffcc33>V1 Bridge:</color> " + text); }
            catch { }
        }
        static bool Fail(string why)
        {
            Problem = why;
            Plugin.Log.LogError(why);
            return false;
        }

        /// <summary>Ask ULTRAKILL to quit; it also quits by itself once this process is gone.</summary>
        public static void Close()
        {
            if (!Started) return;
            Started = false;
            UkLink.Send(Proto.Quit);
            var p = Running();
            if (p == null) return;
            try
            {
                if (!p.WaitForExit(6000))
                {
                    Plugin.Log.LogInfo("ULTRAKILL didn't quit when asked: closing it");
                    p.Kill();
                }
            }
            catch { }
        }

        static Process ours;
        static float seenAt = -1f, startedAt, nextLook;
        static bool warned;
        const float PluginGrace = 25f;

        static bool Gone(Process p)
        {
            try { return p.HasExited; }
            catch { return true; }
        }

        /// <summary>An ULTRAKILL we started that hasn't connected soon after its process appeared is running without
        /// UkBridge (so without the save sandbox): it is closed at once. Looked at once a second, not every frame:
        /// asking Windows for its processes takes milliseconds, and while ULTRAKILL was not coming (Steam waiting on an
        /// answer) doing it every frame dragged Risk of Rain 2's frame rate down for as long as that lasted.</summary>
        public static void Tick()
        {
            if (!Started || UkLink.Connected || Problem != null) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextLook) return;
            nextLook = now + 1f;
            // (started from its exe, it is ours to watch; if that one went, a copy Steam started may have taken its place)
            if (ours != null && Gone(ours)) ours = null;
            var p = ours ?? Running();
            if (p == null)
            {
                if (!warned && now - startedAt > 40f)
                {
                    warned = true;
                    Plugin.Log.LogWarning("ULTRAKILL has not appeared 40 s after it was started");
                    Tell("ULTRAKILL has not started. If Steam is waiting on a question (for example about cloud saves), answer it: Play anyway. Then leave and re-enter character select.");
                }
                return;
            }
            if (seenAt < 0f) { seenAt = now; return; }
            if (now - seenAt < PluginGrace) return;
            try { p.Kill(); } catch { }
            Started = false;
            ours = null;
            Fail("ULTRAKILL started without UkBridge (no connection " + PluginGrace + " s after it appeared): closed it");
            Tell("ULTRAKILL started without the mod and was closed. Run Install.cmd again, then try once more.");
        }
        /// <summary>Major version of the Doorstop proxy next to ULTRAKILL.exe, 0: none.</summary>
        static int DoorstopVersion(string game)
        {
            string dll = Path.Combine(game, "winhttp.dll");
            if (!File.Exists(dll) || !File.Exists(Path.Combine(game, "doorstop_config.ini"))) return 0;
            try { return FileVersionInfo.GetVersionInfo(dll).FileMajorPart; }
            catch { return 0; }
        }

        static string SteamValue(string name)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("reg", "query HKCU\\Software\\Valve\\Steam /v " + name)
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var m = Regex.Match(o, name + @"\s+REG_SZ\s+(.+)");
                return m.Success ? m.Groups[1].Value.Trim().Replace('/', '\\') : null;
            }
            catch { return null; }
        }

        static string FindGame(string steam)
        {
            var libs = new List<string>();
            if (steam != null)
            {
                libs.Add(steam);
                try
                {
                    string vdf = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
                    foreach (Match m in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"")) libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { }
            }
            foreach (var lib in libs)
            {
                string apps = Path.Combine(lib, "steamapps");
                string dir = "ULTRAKILL";
                try
                {
                    var m = Regex.Match(File.ReadAllText(Path.Combine(apps, "appmanifest_" + AppId + ".acf")), "\"installdir\"\\s+\"([^\"]+)\"");
                    if (m.Success) dir = m.Groups[1].Value;
                }
                catch { continue; }
                string game = Path.Combine(apps, "common", dir);
                if (File.Exists(Path.Combine(game, "ULTRAKILL.exe"))) return game;
            }
            return null;
        }
    }
}
