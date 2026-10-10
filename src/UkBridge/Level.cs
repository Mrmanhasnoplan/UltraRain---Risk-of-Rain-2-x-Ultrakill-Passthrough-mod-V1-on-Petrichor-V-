using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace V1Bridge.Uk
{
    // Getting V1 alone into an empty world. The approach (the Sandbox, stripped; switching V1 on by hand) is
    // Ultracraft's (MIT, github.com/alfr0762/ultracraft), adapted.
    public partial class Bridge
    {
        const string Sandbox = "uk_construct";
        bool levelPrepared, v1Landed, sawLevelStart;
        string lastScene = "";
        float nextLoadingNote;
        GameObject worldRoot;

        public bool LevelReady => levelPrepared;

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        static string S(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);

        void UpdateLevel()
        {
            var scene = SceneHelper.CurrentScene ?? "";
            if (scene != lastScene)
            {
                lastScene = scene;
                levelPrepared = false;
                v1Landed = false;
                sawLevelStart = false;
                worldRoot = null;
                stageRoot = null;
                stageMeshes.Clear();
                proxies.Clear();
                shots.Clear();
                goreZone = null;
                Plugin.Log.LogInfo("scene: " + scene);
            }
            // the menus are skipped: V1 is needed in the Sandbox
            if ((scene == "Main Menu" || scene == "Intro" || scene == "Tutorial") && Time.unscaledTime > 3f && SceneHelper.PendingScene != Sandbox)
            {
                SceneHelper.LoadScene(Sandbox);
            }
            if (!levelPrepared && Net.Connected && Time.unscaledTime > nextLoadingNote)
            {
                nextLoadingNote = Time.unscaledTime + 2f;
                Net.Send(Proto.Loading + " " + scene.Replace(' ', '_'));
            }
            // Never touch MonoSingleton<T>.Instance before the level is ready: a lookup that finds nothing uses up the
            // singleton's one search (Ultracraft's finding).
            if (scene == Sandbox && !levelPrepared && Time.timeSinceLevelLoad > 1f && LevelStarted()) PrepareLevel();
        }

        /// <summary>The Sandbox has really started: loading and menu states are gone and its start sequence (V1 dropping
        /// into the pit) has run.</summary>
        bool LevelStarted()
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || gsm.IsStateActive("main-menu") || gsm.IsStateActive("pause")) return false;
            if (gsm.IsStateActive("pit-falling")) sawLevelStart = true;
            if (!sawLevelStart && Time.timeSinceLevelLoad < 8f) return false;
            return FindObjectOfType<NewMovement>() != null;
        }

        /// <summary>Strip the Sandbox down to V1: every renderer, collider, terrain and light that isn't part of the
        /// player rig, the HUD or the gore pools goes, and the camera clears to transparent black, so what is left of
        /// ULTRAKILL's picture is only what belongs on top of Risk of Rain 2's.</summary>
        void PrepareLevel()
        {
            var nm = FindObjectOfType<NewMovement>();
            nm.MakeCurrent();
            var cc = nm.cc != null ? nm.cc : nm.GetComponentInChildren<CameraController>(true);
            if (cc != null)
            {
                cc.MakeCurrent();
                if (cc.nm == null || cc.player == null)
                {
                    cc.nm = nm;
                    cc.player = nm.gameObject;
                }
            }
            var keep = new HashSet<Transform> { nm.transform.root };
            if (cc != null) keep.Add(cc.transform.root);
            var bsm = MonoSingleton<BloodsplatterManager>.Instance;
            var gore = bsm != null ? bsm.transform : null;
            int hidden = 0;
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (keep.Contains(root.transform))
                {
                    // V1's root is the spawn room: keep V1, strip the room
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (!r.transform.IsChildOf(nm.transform)) { r.enabled = false; hidden++; }
                    foreach (var c in root.GetComponentsInChildren<Collider>(true)) if (!c.transform.IsChildOf(nm.transform)) { c.enabled = false; hidden++; }
                    foreach (var l in root.GetComponentsInChildren<Light>(true)) if (!l.transform.IsChildOf(nm.transform)) l.enabled = false;
                    continue;
                }
                if (root.GetComponentInChildren<Canvas>(true) != null && root.GetComponentInChildren<Renderer>(true) == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (gore != null && r.transform.IsChildOf(gore)) continue;
                    r.enabled = false;
                    hidden++;
                }
                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (gore != null && c.transform.IsChildOf(gore)) continue;
                    c.enabled = false;
                    hidden++;
                }
                foreach (var t in root.GetComponentsInChildren<Terrain>(true)) t.enabled = false;
            }
            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            if (cc != null)
            {
                cc.cam.clearFlags = CameraClearFlags.SolidColor;
                cc.cam.backgroundColor = new Color(0, 0, 0, 0);
            }
            foreach (var cam in Camera.allCameras) if (cam.useOcclusionCulling) cam.useOcclusionCulling = false;

            worldRoot = new GameObject("V1Bridge world");
            // held still until Risk of Rain 2 says where V1 stands
            nm.rb.isKinematic = true;
            levelPrepared = true;
            Net.Send(Proto.Ready);
            Plugin.Log.LogInfo("level prepared, hid " + hidden + " renderers/colliders");
        }

        /// <summary>TP x y z yaw pitch: V1's feet. The first one switches V1 on.</summary>
        void Teleport(string rest)
        {
            if (!levelPrepared) return;
            var a = rest.Split(' ');
            var nm = MonoSingleton<NewMovement>.Instance;
            if (nm == null) return;
            var p = new Vector3(F(a[0]), F(a[1]), F(a[2])) + Vector3.up * 1.5f;
            nm.transform.position = p;
            nm.rb.position = p;
            nm.rb.isKinematic = false;
            nm.rb.velocity = Vector3.zero;
            if (!v1Landed) LandV1(nm);
            // said again for every placement: after a stage change Risk of Rain 2 waits to hear V1 is down
            else Net.Send(Proto.Landed + " again");
            var cc = MonoSingleton<CameraController>.Instance;
            if (cc != null && a.Length >= 5)
            {
                cc.rotationY = F(a[3]);
                cc.rotationX = -F(a[4]);
                cc.ApplyRotations();
            }
        }

        /// <summary>What the Sandbox's pit does when V1 lands in it: movement, camera, weapons, fists and HUD on.</summary>
        void LandV1(NewMovement nm)
        {
            v1Landed = true;
            GameStateManager.Instance.PopState("pit-falling");
            // each on its own: a weapon that fails to build must not cost V1 its arms (the Whiplash is one of them)
            try { MonoSingleton<GunSetter>.Instance?.ResetWeapons(); }
            catch (Exception e) { Plugin.Log.LogWarning("weapons: " + e.Message); }
            try { MonoSingleton<FistControl>.Instance?.ResetFists(); }
            catch (Exception e) { Plugin.Log.LogWarning("arms: " + e.Message); }
            try { nm.ActivatePlayer(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ActivatePlayer: " + e.Message);
                nm.activated = true;
            }
            if (nm.cc != null)
            {
                nm.cc.activated = true;
                nm.cc.enabled = true;
            }
            var relay = FindObjectOfType<PlayerActivatorRelay>();
            if (relay != null)
            {
                try
                {
                    relay.ResetIndex();
                    relay.Activate();
                }
                catch (Exception e) { Plugin.Log.LogWarning("HUD relay: " + e.Message); }
            }
            var gc = MonoSingleton<GunControl>.Instance;
            string slots = gc != null ? string.Join("/", gc.slots.ConvertAll(s => s.Count.ToString()).ToArray()) : "?";
            var hookArm = MonoSingleton<HookArm>.Instance;
            slots += " whiplash=" + (hookArm != null && hookArm.equipped ? 1 : 0);
            var fists = MonoSingleton<FistControl>.Instance;
            bool panel = false;
            if (fists != null && fists.fistPanels != null) foreach (var fp in fists.fistPanels) panel |= fp != null && fp.activeSelf;
            slots += " fistpanel=" + (panel ? 1 : 0);
            Plugin.Log.LogInfo("V1 landed: camera " + (nm.cc != null && nm.cc.isActiveAndEnabled ? "on" : "OFF") + ", HUD relay " + (relay != null ? "on" : "missing") + ", weapons per slot " + slots);
            Net.Send(Proto.Landed + " weapons=" + slots);
        }

        int lastHp = int.MinValue;
        bool lastDead;

        void SendPose()
        {
            if (!levelPrepared || !v1Landed) return;
            var nm = MonoSingleton<NewMovement>.Instance;
            var cc = MonoSingleton<CameraController>.Instance;
            if (nm == null || cc == null || cc.cam == null) return;
            var feet = nm.transform.position + Vector3.down * 1.5f;
            var eye = cc.cam.transform.position;
            var e = cc.cam.transform.eulerAngles;
            var v = nm.rb.velocity;
            Net.Send(Proto.Pose + " " + S(feet.x) + " " + S(feet.y) + " " + S(feet.z) + " " + S(eye.x) + " " + S(eye.y) + " " + S(eye.z)
                     + " " + S(e.y) + " " + S(e.x) + " " + S(e.z) + " " + S(cc.cam.fieldOfView) + " " + S(v.x) + " " + S(v.y) + " " + S(v.z)
                     + " " + (nm.gc != null && nm.gc.onGround ? 1 : 0) + " " + (nm.sliding ? 1 : 0));
            if (nm.hp != lastHp || nm.dead != lastDead)
            {
                lastHp = nm.hp;
                lastDead = nm.dead;
                Net.Send(Proto.Hp + " " + nm.hp + " " + (nm.dead ? 1 : 0));
            }
        }
    }

    // ---------------------------------------------------------------- Harmony: what the bridge's save looks like

    /// <summary>The sandboxed save is new, and a new save is sent through the intro and the tutorial. Only what is read
    /// changes.</summary>
    [HarmonyPatch(typeof(GameProgressSaver))]
    static class SkipFirstRun
    {
        [HarmonyPrefix, HarmonyPatch(nameof(GameProgressSaver.GetTutorial))]
        static bool Tutorial(ref bool __result) { __result = true; return false; }

        [HarmonyPrefix, HarmonyPatch(nameof(GameProgressSaver.GetIntro))]
        static bool Intro(ref bool __result) { __result = true; return false; }
    }

    /// <summary>The Sandbox runs with cheats on (it is how the bridge runs ULTRAKILL); its "CHEATS ENABLED" box has no
    /// place over Risk of Rain 2.</summary>
    [HarmonyPatch(typeof(ULTRAKILL.Cheats.HideCheatsStatus), nameof(ULTRAKILL.Cheats.HideCheatsStatus.HideStatus), MethodType.Getter)]
    static class NoCheatsBox
    {
        static void Postfix(ref bool __result) { __result = true; }
    }

    /// <summary>Every weapon and arm from the start of a run: no shop.</summary>
    [HarmonyPatch(typeof(GameProgressSaver), nameof(GameProgressSaver.CheckGear))]
    static class AllGear
    {
        // the gold arm is in the code but not in the game: owning it makes the arm set-up throw half way, before the
        // HUD's fist panel is switched on
        static bool Prefix(string gear, ref int __result) { __result = gear == "arm3" ? 0 : 1; return false; }
    }

    /// <summary>A weapon variant the game has no model for yet is skipped, not allowed to stop the rest of the loadout.</summary>
    [HarmonyPatch(typeof(GunSetter), "CheckWeapon")]
    static class OnlyRealWeapons
    {
        static bool Prefix(GameObject[] prefabs) => prefabs != null && prefabs.Length > 0 && prefabs[0] != null;
    }
}
