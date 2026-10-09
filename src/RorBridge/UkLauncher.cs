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
            if (UkLink.Connected) return true;
            if (Running() != null) return Fail("ULTRAKILL is already running without the bridge: close it first");
            string steam = SteamValue("SteamExe");
            if (steam == null || !File.Exists(steam)) return Fail("Steam not found");
            string game = FindGame(SteamValue("SteamPath"));
            if (game == null) return Fail("ULTRAKILL not found in any Steam library");
            string preloader = Path.Combine(UkProfile, "BepInEx", "core", "BepInEx.Preloader.dll");
            if (!File.Exists(preloader)) return Fail("no BepInEx profile for ULTRAKILL at " + UkProfile + " (run tools\\Setup-Profiles.ps1)");
            if (!File.Exists(Path.Combine(UkProfile, "BepInEx", "plugins", "UkBridge", "UkBridge.dll"))) return Fail("UkBridge.dll is not in " + UkProfile);
            int doorstop = DoorstopVersion(game);
            if (doorstop == 0) return Fail("ULTRAKILL's folder has no Doorstop (winhttp.dll + doorstop_config.ini)");
            string switches = doorstop >= 4
                ? "--doorstop-enabled true --doorstop-target-assembly \"" + preloader + "\""
                : "--doorstop-enable true --doorstop-target \"" + preloader + "\"";
            string args = "-applaunch " + AppId + " " + switches + " " + Proto.UkArgActive + " " + Proto.UkArgPort + " " + Plugin.Port.Value
                          + " " + Proto.UkArgParent + " " + Process.GetCurrentProcess().Id + " -screen-fullscreen 0 -screen-width 1280 -screen-height 720";
            try
            {
                Process.Start(new ProcessStartInfo(steam, args) { UseShellExecute = false });
                Started = true;
                Plugin.Log.LogInfo("starting ULTRAKILL (Doorstop " + doorstop + "): " + steam + " " + args);
                return true;
            }
            catch (Exception e) { return Fail("couldn't start ULTRAKILL: " + e.Message); }
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

        /// <summary>3 or 4 from the config Doorstop left next to ULTRAKILL.exe (the two take different switches), 0: none.</summary>
        static int DoorstopVersion(string game)
        {
            string ini = Path.Combine(game, "doorstop_config.ini");
            if (!File.Exists(Path.Combine(game, "winhttp.dll")) || !File.Exists(ini)) return 0;
            string text = File.ReadAllText(ini);
            return text.Contains("[UnityDoorstop]") ? 3 : 4;
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
