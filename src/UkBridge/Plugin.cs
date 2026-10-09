using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace V1Bridge.Uk
{
    [BepInPlugin("dev.v1bridge.uk", "UkBridge", Proto.Version)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        /// <summary>The folder holding this profile's BepInEx, sandboxed Saves and Preferences.</summary>
        internal static string ProfileDir;

        internal static string Arg(string name)
        {
            var a = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }

        private void Awake()
        {
            Log = Logger;
            ProfileDir = Path.GetDirectoryName(Paths.BepInExRootPath);
            // The save sandbox goes on whatever the launch looked like: this profile must never reach the real save.
            var harmony = new Harmony("dev.v1bridge.uk");
            SaveGuard.Apply(harmony);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), Proto.UkArgActive) < 0)
            {
                Log.LogInfo("UkBridge loaded without " + Proto.UkArgActive + ": idle");
                return;
            }
            Application.runInBackground = true;
            var go = new GameObject("UkBridge");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Bridge>();
            Log.LogInfo("UkBridge " + Proto.Version + " loaded, profile " + ProfileDir + ", guards " + SaveGuard.Summary);
        }
    }
}
