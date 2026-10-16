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
        public static string Summary => (RealSave ? "real-save," : "") + (Ok ? "ok(" + applied + ")" : "MISSING:" + string.Join(",", missing.ToArray()));
        public static bool Active => Sandbox != null && ReferenceEquals(RoR2Application.cloudStorage, Sandbox);
        /// <summary>The owner's choice (UseRealRoR2Save): Risk of Rain 2 runs on its real user profiles, Steam cloud and
        /// achievements, as without the mod. Never in test mode. Read once at start-up: the profiles are loaded once.</summary>
        public static bool RealSave { get; private set; }
        public static string BackupDir;

        public static void Apply(Harmony h)
        {
            RealSave = Plugin.UseRealSave.Value && Plugin.Arg(TestMode.ArgName) == null;
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
            if (RealSave)
            {
                if (BackupDir == null) Backup(real);
                Plugin.Log.LogInfo("using the real Risk of Rain 2 save (UseRealRoR2Save); copy of it before this session in " + BackupDir);
                return;
            }
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

        /// <summary>Before a session on the real save: a copy of every user profile, in this profile's real-save-backups
        /// folder (the newest 10 are kept). Read only from the real storage.</summary>
        static void Backup(FileSystem real)
        {
            const string dir = "/UserProfiles";
            try
            {
                var root = Path.Combine(Plugin.ProfileDir, "real-save-backups");
                BackupDir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(BackupDir);
                int n = 0;
                if (real != null && real.DirectoryExists(dir))
                {
                    foreach (var p in real.EnumeratePaths(dir, "*", SearchOption.TopDirectoryOnly, SearchTarget.File))
                    {
                        using (var src = real.OpenFile(p, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var dst = File.Create(Path.Combine(BackupDir, p.GetName())))
                        {
                            src.CopyTo(dst);
                        }
                        n++;
                    }
                }
                var all = Directory.GetDirectories(root);
                Array.Sort(all);
                for (int i = 0; i < all.Length - 10; i++) Directory.Delete(all[i], true);
                Plugin.Log.LogInfo("backed up " + n + " real save files to " + BackupDir);
            }
            catch (Exception e) { Plugin.Log.LogWarning("backing up the real save: " + e.Message); }
        }

        static void ConfigToSandbox(ref FileSystem fileSystem)
        {
            if (RealSave) return;
            fileSystem = Sandbox;
        }

        static bool ReadOnlyOpen(UPath path, FileMode mode, FileAccess access)
        {
            if (RealSave || (mode == FileMode.Open && access == FileAccess.Read)) return true;
            Blocked++;
            Plugin.Log.LogWarning("blocked a write to Steam's storage: " + path);
            throw new IOException("V1Bridge: Steam cloud storage is read-only in this profile (" + path + ")");
        }

        static bool NoAchievement()
        {
            if (RealSave) return true;
            AchievementsSkipped++;
            return false;
        }

        static bool Skip(MethodBase __originalMethod)
        {
            if (RealSave) return true;
            Blocked++;
            Plugin.Log.LogInfo("skipped " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
            return false;
        }
    }
}
