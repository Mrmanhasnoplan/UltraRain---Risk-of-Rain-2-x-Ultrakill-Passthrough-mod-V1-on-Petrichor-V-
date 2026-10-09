using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace V1Bridge.Uk
{
    /// <summary>Keeps this profile away from the player's real ULTRAKILL data: saves and preferences live in the
    /// profile folder (preferences start as a copy of the real ones, so binds and sensitivity carry over), and nothing
    /// is sent to the leaderboards or Steam stats. Each patch is applied on its own and counted, so an ULTRAKILL
    /// update that renames something shows up in the handshake instead of failing quietly.</summary>
    internal static class SaveGuard
    {
        public static string Saves, Prefs;
        static readonly List<string> missing = new List<string>();
        static int applied;
        /// <summary>Writes the guard turned away (a path outside the sandbox, or a leaderboard/stat call).</summary>
        public static int Blocked;

        public static bool Ok => missing.Count == 0;
        public static string Summary => Ok ? "ok(" + applied + ")" : "MISSING:" + string.Join(",", missing.ToArray());

        public static void Apply(Harmony harmony)
        {
            Saves = Path.Combine(Plugin.ProfileDir, "Saves");
            Prefs = Path.Combine(Plugin.ProfileDir, "Preferences");
            Directory.CreateDirectory(Saves);
            SeedPrefs();

            Getter(harmony, typeof(GameProgressSaver), "BaseSavePath", nameof(SavesPath));
            Getter(harmony, typeof(PrefsManager), "PrefsPath", nameof(PrefsPath));
            Prefix(harmony, typeof(GameProgressSaver), "WriteFile", nameof(OnlySandbox));
            foreach (var m in new[] { "SubmitCyberGrindScore", "SubmitLevelScore", "SubmitFishSize" }) Prefix(harmony, typeof(LeaderboardController), m, nameof(Skip));
            foreach (var m in new[] { "AddToStatInt", "UpdateTimeInSandbox", "UpdateWave" }) Prefix(harmony, typeof(SteamController), m, nameof(Skip));
        }

        /// <summary>The real preferences are only ever read: copied once into the sandbox.</summary>
        static void SeedPrefs()
        {
            if (Directory.Exists(Prefs)) return;
            Directory.CreateDirectory(Prefs);
            try
            {
                var real = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Preferences");
                if (!Directory.Exists(real)) return;
                foreach (var f in Directory.GetFiles(real, "*.json")) File.Copy(f, Path.Combine(Prefs, Path.GetFileName(f)), false);
            }
            catch (Exception e) { Plugin.Log.LogWarning("seeding preferences: " + e.Message); }
        }

        static MethodInfo Mine(string name) => typeof(SaveGuard).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        static void Getter(Harmony h, Type t, string prop, string prefix)
        {
            var g = AccessTools.PropertyGetter(t, prop);
            if (g == null) { missing.Add(t.Name + "." + prop); return; }
            Patch(h, g, prefix, t.Name + "." + prop);
        }

        static void Prefix(Harmony h, Type t, string method, string prefix)
        {
            var found = AccessTools.GetDeclaredMethods(t).FindAll(m => m.Name == method);
            if (found.Count == 0) { missing.Add(t.Name + "." + method); return; }
            foreach (var m in found) Patch(h, m, prefix, t.Name + "." + method);
        }

        static void Patch(Harmony h, MethodBase target, string prefix, string label)
        {
            try
            {
                h.Patch(target, prefix: new HarmonyMethod(Mine(prefix)));
                applied++;
            }
            catch (Exception e)
            {
                missing.Add(label);
                Plugin.Log.LogError("guard " + label + ": " + e.Message);
            }
        }

        static bool SavesPath(ref string __result) { __result = Saves; return false; }
        static bool PrefsPath(ref string __result) { __result = Prefs; return false; }

        static bool OnlySandbox(string path)
        {
            if (Inside(path, Saves) || Inside(path, Prefs)) return true;
            Blocked++;
            Plugin.Log.LogWarning("blocked a save write outside the sandbox: " + path);
            return false;
        }

        static bool Skip(MethodBase __originalMethod)
        {
            Blocked++;
            Plugin.Log.LogInfo("skipped " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
            return false;
        }

        static bool Inside(string path, string dir)
        {
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}
