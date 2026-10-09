using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using RoR2;
using Path = System.IO.Path;
using Zio;
using Zio.FileSystems;

namespace V1Bridge.Ror
{
    /// <summary>Keeps this profile away from the player's real Risk of Rain 2 data. User profiles and settings live in
    /// the profile folder (seeded once with a copy of the real ones, which are only ever read), Steam's cloud storage
    /// refuses writes, and no Steam achievement is triggered. Each patch is applied on its own and counted.</summary>
    internal static class SaveGuard
    {
        public static string SandboxDir;
        public static FileSystem Sandbox;
        static readonly List<string> missing = new List<string>();
        static int applied;
        /// <summary>Writes turned away from Steam's storage.</summary>
        public static int Blocked;
        /// <summary>Steam achievements not sent (the game re-sends the profile's own on every start).</summary>
        public static int AchievementsSkipped;
        public static int Seeded;

        public static bool Ok => missing.Count == 0;
        public static string Summary => Ok ? "ok(" + applied + ")" : "MISSING:" + string.Join(",", missing.ToArray());
        public static bool Active => Sandbox != null && ReferenceEquals(RoR2Application.cloudStorage, Sandbox);

        public static void Apply(Harmony h)
        {
            SandboxDir = Path.Combine(Plugin.ProfileDir, "saves");
            Directory.CreateDirectory(SandboxDir);
            var physical = new PhysicalFileSystem();
            Sandbox = new SubFileSystem(physical, physical.ConvertPathFromInternal(SandboxDir));

            Patch(h, typeof(SaveSystem), "LoadUserProfiles", nameof(UseSandbox));
            Patch(h, typeof(RoR2.Console), "WriteConfigFile", nameof(ConfigToSandbox));
            Patch(h, typeof(SteamworksRemoteStorageFileSystem), "OpenFileImpl", nameof(ReadOnlyOpen));
            foreach (var m in new[] { "DeleteFileImpl", "MoveFileImpl", "CopyFileImpl", "ReplaceFileImpl" }) Patch(h, typeof(SteamworksRemoteStorageFileSystem), m, nameof(Skip));
            Patch(h, typeof(AchievementSystemSteam), "AddAchievement", nameof(NoAchievement));
        }

        static void Patch(Harmony h, Type t, string method, string prefix)
        {
            var found = AccessTools.GetDeclaredMethods(t).FindAll(m => m.Name == method);
            if (found.Count == 0) { missing.Add(t.Name + "." + method); return; }
            foreach (var m in found)
            {
                try
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(SaveGuard).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)));
                    applied++;
                }
                catch (Exception e)
                {
                    missing.Add(t.Name + "." + method);
                    Plugin.Log.LogError("guard " + t.Name + "." + method + ": " + e.Message);
                }
            }
        }

        /// <summary>Before the profiles are read: the sandbox takes the cloud storage's place for good.</summary>
        static void UseSandbox()
        {
            var real = RoR2Application.cloudStorage;
            if (ReferenceEquals(real, Sandbox)) return;
            Seed(real);
            RoR2Application.cloudStorage = Sandbox;
            Plugin.Log.LogInfo("saves sandboxed in " + SandboxDir + " (seeded " + Seeded + " files)");
        }

        static void Seed(FileSystem real)
        {
            const string dir = "/UserProfiles";
            try
            {
                if (Sandbox.DirectoryExists(dir)) return;
                Sandbox.CreateDirectory(dir);
                if (real == null || !real.DirectoryExists(dir)) return;
                foreach (var p in real.EnumeratePaths(dir, "*", SearchOption.TopDirectoryOnly, SearchTarget.File))
                {
                    using (var src = real.OpenFile(p, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var dst = Sandbox.OpenFile(p, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        src.CopyTo(dst);
                    }
                    Seeded++;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("seeding the save sandbox: " + e); }
        }

        static void ConfigToSandbox(ref FileSystem fileSystem)
        {
            fileSystem = Sandbox;
        }

        static bool ReadOnlyOpen(UPath path, FileMode mode, FileAccess access)
        {
            if (mode == FileMode.Open && access == FileAccess.Read) return true;
            Blocked++;
            Plugin.Log.LogWarning("blocked a write to Steam's storage: " + path);
            throw new IOException("V1Bridge: Steam cloud storage is read-only in this profile (" + path + ")");
        }

        static bool NoAchievement()
        {
            AchievementsSkipped++;
            return false;
        }

        static bool Skip(MethodBase __originalMethod)
        {
            Blocked++;
            Plugin.Log.LogInfo("skipped " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
            return false;
        }
    }
}
