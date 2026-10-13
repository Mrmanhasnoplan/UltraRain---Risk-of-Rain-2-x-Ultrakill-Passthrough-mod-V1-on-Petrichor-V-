using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace V1Bridge.Ror
{
    [BepInPlugin("dev.v1bridge.ror", "RorBridge", Proto.Version)]
    [BepInDependency(R2API.ContentManagement.R2APIContentManager.PluginGUID)]
    [BepInDependency(R2API.PrefabAPI.PluginGUID)]
    [BepInDependency(R2API.LanguageAPI.PluginGUID)]
    [BepInDependency(SettingsMenu.RiskOfOptionsGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        /// <summary>The folder holding this profile's BepInEx and sandboxed saves.</summary>
        internal static string ProfileDir;
        internal static ConfigEntry<string> UkProfileDir;
        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<int> UkHeight;
        internal static ConfigEntry<int> UkFps;
        internal static ConfigEntry<float> DamageCoefficient;
        internal static ConfigEntry<float> MouseScale;
        internal static ConfigEntry<float> IncomingDamage;
        internal static ConfigEntry<bool> HideRorHud;
        internal static ConfigEntry<float> V1Health;

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
            UkProfileDir = Config.Bind("ULTRAKILL", "ProfileDir", "", "The BepInEx profile ULTRAKILL is started with. Empty: the 'uk' folder next to this profile.");
            Port = Config.Bind("Link", "Port", Proto.DefaultPort, "Loopback port UkBridge listens on.");
            UkHeight = Config.Bind("Performance", "UltrakillHeight", 720, "Height ULTRAKILL renders at (its width follows this window's shape). Lower is faster.");
            DamageCoefficient = Config.Bind("Balance", "DamageCoefficient", 3f, "RoR2 damage of one ULTRAKILL damage unit (a revolver shot), as a multiple of V1's damage stat. 3 = 300%.");
            MouseScale = Config.Bind("Controls", "MouseScale", 2f, "Multiplies the mouse movement handed to ULTRAKILL, on top of ULTRAKILL's own sensitivity.");
            IncomingDamage = Config.Bind("Balance", "IncomingDamage", 1f, "Multiplies the damage Risk of Rain 2 does to V1.");
            HideRorHud = Config.Bind("HUD", "HideRoR2HealthAndSkills", true, "Hide Risk of Rain 2's health bar and skill icons while playing V1 (ULTRAKILL's HUD shows them).");
            V1Health = Config.Bind("Balance", "V1Health", 90f, "V1's base health in Risk of Rain 2, at level 1 (it grows 30% of this per level, as every survivor's does). V1's real health is ULTRAKILL's 100: a bigger number here makes each Risk of Rain 2 hit cost a smaller share of it.");
            UkFps = Config.Bind("Performance", "UltrakillFpsCap", 120, "ULTRAKILL's frame rate cap.");
            var harmony = new Harmony("dev.v1bridge.ror");
            SaveGuard.Apply(harmony);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Coins.Apply(harmony);
            NoBodyEffects.Apply(harmony);
            V1Figure.Apply(harmony);
            V1Net.Hook();
            ViewAnglesAreV1s.Apply(harmony);
            // what the game asks of mods: keeps modded players out of vanilla matchmaking
            RoR2.RoR2Application.isModded = true;

            V1Survivor.Register();
            V1Model.LoadCache();
            V1Health.SettingChanged += (s, e) => V1Survivor.ApplyHealth();
            UkHeight.SettingChanged += (s, e) => V1Drive.SendSize();
            UkFps.SettingChanged += (s, e) => V1Drive.SendSize();
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(SettingsMenu.RiskOfOptionsGuid))
            {
                try { SettingsMenu.Register(); }
                catch (Exception e) { Log.LogWarning("settings menu: " + e.Message); }
            }

            var go = new GameObject("RorBridge");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Bridge>();
            Log.LogInfo("RorBridge " + Proto.Version + " loaded, profile " + ProfileDir + ", guards " + SaveGuard.Summary);
        }
    }

    /// <summary>Owns the link and the ULTRAKILL process for the lifetime of this Risk of Rain 2.</summary>
    public class Bridge : MonoBehaviour
    {
        public static Bridge I;

        void Awake()
        {
            I = this;
            UkLink.OnMessage += V1Drive.OnMessage;
            UkLink.OnMessage += Monsters.OnMessage;
            UkLink.OnMessage += Parry.OnMessage;
            UkLink.OnMessage += Coins.OnMessage;
            UkLink.OnMessage += V1Fx.OnMessage;
            UkLink.OnMessage += V1Model.OnMessage;
            UkLink.Start(Plugin.Port.Value);
            string test = Plugin.Arg(TestMode.ArgName);
            if (test != null) gameObject.AddComponent<TestMode>().Begin(test, Plugin.Arg(TestMode.ArgOut));
        }

        void Update()
        {
            UkLink.Pump();
            Session.Tick();
            V1Drive.Tick();
            UkLauncher.Tick();
        }

        void FixedUpdate()
        {
            V1Drive.FixedTick();
        }

        void LateUpdate()
        {
            V1Drive.LateTick();
        }

        void OnApplicationQuit()
        {
            UkLauncher.Close();
        }
    }
}
