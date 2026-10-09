using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RoR2;
using UnityEngine;
using UnityEngine.UI;

namespace V1Bridge.Ror
{
    /// <summary>A bridged run, from Risk of Rain 2's side: tells ULTRAKILL where V1 stands, feeds it the keys and the
    /// mouse, shows its picture under RoR2's HUD, looks at the stage from the view each ULTRAKILL frame was drawn
    /// from, and keeps RoR2's body where V1 is.</summary>
    internal static class V1Drive
    {
        /// <summary>ULTRAKILL units per Risk of Rain 2 metre: V1's 3.5-unit capsule for a survivor's 1.82 m.</summary>
        public const float K = 3.5f / 1.82f;

        public static bool UkReady, UkLanded;
        public static string UkLoading = "";
        public static string LandedInfo = "";
        /// <summary>Where ULTRAKILL's origin is in the stage.</summary>
        public static Vector3 Origin;
        public static Pose Latest;
        public static bool HavePose;
        public static int UkHp = 100;
        public static bool UkDead;

        public struct Pose
        {
            public Vector3 feet, eye, velocity;
            public float yaw, pitch, roll, fov;
            public bool grounded, sliding;
        }

        public static StageGeometry.Result Stage;
        /// <summary>GEODONE's numbers: colliders, triangles, milliseconds, ok or what went wrong.</summary>
        public static string StageLoaded;
        static string pendingTp;
        static int geoSerial;
        static bool engaged, idleSent = true;
        static CharacterBody body;
        static CameraRigController rig;
        static Provider provider;
        static PlayerCharacterMasterController quiet;
        /// <summary>RoR2's own handling of the player's keys is skipped for this controller while V1 is driven by ULTRAKILL.
        /// (Switching the component off instead takes the player out of the game's list of the living: instant defeat.)</summary>
        public static bool Silenced(PlayerCharacterMasterController c) => engaged && c == quiet;
        static RawImage overlay;
        static readonly List<Behaviour> disabled = new List<Behaviour>();
        static CharacterModel hiddenModel;
        static int inputSeq;

        public static Vector3 ToRor(Vector3 uk) => Origin + uk / K;
        public static Vector3 ToUk(Vector3 ror) => (ror - Origin) * K;
        static string S(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        public static void OnMessage(string cmd, string rest)
        {
            switch (cmd)
            {
                case "DISCONNECTED":
                    UkReady = UkLanded = HavePose = false;
                    idleSent = true;
                    break;
                case Proto.Loading:
                    UkLoading = rest;
                    UkReady = UkLanded = false;
                    break;
                case Proto.Ready:
                    UkReady = true;
                    UkLanded = false;
                    Plugin.Log.LogInfo("ULTRAKILL is ready for V1");
                    break;
                case Proto.GeoDone:
                    StageLoaded = rest;
                    Plugin.Log.LogInfo("ULTRAKILL built the stage: " + rest);
                    // the ground is there: V1 can be put down
                    if (pendingTp != null) UkLink.Send(Proto.Tp + " " + pendingTp);
                    pendingTp = null;
                    break;
                case Proto.Landed:
                    UkLanded = true;
                    LandedInfo = rest;
                    Plugin.Log.LogInfo("V1 landed in ULTRAKILL: " + rest);
                    break;
                case Proto.Hp:
                {
                    var a = rest.Split(' ');
                    UkHp = int.Parse(a[0]);
                    UkDead = a.Length > 1 && a[1] == "1";
                    break;
                }
                case Proto.Pose:
                {
                    var a = rest.Split(' ');
                    if (a.Length < 15) break;
                    Latest = new Pose
                    {
                        feet = new Vector3(F(a[0]), F(a[1]), F(a[2])), eye = new Vector3(F(a[3]), F(a[4]), F(a[5])),
                        yaw = F(a[6]), pitch = F(a[7]), roll = F(a[8]), fov = F(a[9]),
                        velocity = new Vector3(F(a[10]), F(a[11]), F(a[12])), grounded = a[13] == "1", sliding = a[14] == "1"
                    };
                    HavePose = true;
                    break;
                }
            }
        }

        /// <summary>Every frame, early.</summary>
        public static void Tick()
        {
            if (!UkLink.Ready) { if (engaged) Disengage(); return; }
            if (!Session.Active)
            {
                if (engaged) Disengage();
                if (!idleSent) { UkLink.Send(Proto.Idle + " 1"); idleSent = true; }
                return;
            }
            if (idleSent)
            {
                UkLink.Send(Proto.Idle + " 0");
                idleSent = false;
            }
            var user = LocalUserManager.GetFirstLocalUser();
            var b = user != null ? user.cachedBody : null;
            if (b == null) return;
            if (!engaged || b != body)
            {
                if (!UkReady) return;
                Engage(user, b);
            }
            if (!UkLanded) return;
            SendInput(user);
            Frames.Update();
            ShowOverlay(user);
        }

        /// <summary>A V1 run has a body and ULTRAKILL is waiting: ULTRAKILL's world is laid over the stage with its origin
        /// at the body's feet, and V1 is put down there.</summary>
        static void Engage(LocalUser user, CharacterBody b)
        {
            if (engaged) Disengage();
            body = b;
            engaged = true;
            Origin = b.footPosition;
            int h = Mathf.Clamp(Plugin.UkHeight.Value, 360, 1440);
            int w = Mathf.RoundToInt(h * (Screen.width / (float)Screen.height) / 2f) * 2;
            UkLink.Send(Proto.Size + " " + w + " " + h + " " + Plugin.UkFps.Value);
            float yaw = b.inputBank != null ? Quaternion.LookRotation(b.inputBank.aimDirection).eulerAngles.y : 0f;
            Stage = StageGeometry.Export(Origin, K, ++geoSerial % 2);
            StageLoaded = null;
            Plugin.Log.LogInfo("stage exported: " + Stage.objects + " colliders, " + Stage.triangles + " triangles (" + Stage.unreadable + " meshes read from the GPU, " + Stage.skipped
                               + " skipped) in " + Stage.ms.ToString("0") + " ms" + (Stage.problem != null ? ", problem: " + Stage.problem : ""));
            pendingTp = "0 0 0 " + S(yaw) + " 0";
            UkLink.Send(Proto.Geo + " " + Stage.path);

            // RoR2 stops moving and aiming this body: V1's place and view are ULTRAKILL's
            disabled.Clear();
            void Off(Behaviour c) { if (c != null && c.enabled) { c.enabled = false; disabled.Add(c); } }
            quiet = user.cachedMasterController;
            if (b.characterMotor != null)
            {
                Off(b.characterMotor.Motor);
                Off(b.characterMotor);
            }
            var model = b.modelLocator != null && b.modelLocator.modelTransform != null ? b.modelLocator.modelTransform.GetComponent<CharacterModel>() : null;
            if (model != null)
            {
                model.invisibilityCount++;
                hiddenModel = model;
            }
            Frames.FrameAge.Clear();
            Frames.InputAge.Clear();
            Plugin.Log.LogInfo("engaged: origin " + Origin + ", ULTRAKILL at " + w + "x" + h);
        }

        static void Disengage()
        {
            engaged = false;
            quiet = null;
            foreach (var c in disabled) if (c != null) c.enabled = true;
            disabled.Clear();
            if (hiddenModel != null) hiddenModel.invisibilityCount--;
            hiddenModel = null;
            if (rig != null && rig.hasOverride) rig.SetOverrideCam(null, 0f);
            rig = null;
            if (overlay != null) UnityEngine.Object.Destroy(overlay.transform.parent.gameObject);
            overlay = null;
            body = null;
            UkLanded = false;
            Plugin.Log.LogInfo("disengaged");
        }

        // ------------------------------------------------------------ input

        /// <summary>For tests: what to send instead of the real keys and mouse.</summary>
        public static Func<Frame> Script;
        public struct Frame
        {
            public float dx, dy, wheel;
            public int buttons;
            public KeyCode[] keys;
        }
        public static int InputsSent;
        static readonly StringBuilder sb = new StringBuilder(128);

        static void SendInput(LocalUser user)
        {
            Frame f;
            if (Script != null) f = Script();
            else if (PauseManager.isPaused || Cursor.lockState != CursorLockMode.Locked || !Application.isFocused) f = default;
            else f = InputCapture.Read();
            sb.Length = 0;
            sb.Append(Proto.Input).Append(' ').Append(++inputSeq).Append(' ').Append(Frames.NowMicros()).Append(' ')
              .Append(S(f.dx)).Append(' ').Append(S(f.dy)).Append(' ').Append(S(f.wheel)).Append(' ').Append(f.buttons);
            if (f.keys != null) foreach (var k in f.keys) sb.Append(' ').Append((int)k);
            UkLink.Send(sb.ToString());
            InputsSent++;
        }

        // ------------------------------------------------------------ picture and view

        static void ShowOverlay(LocalUser user)
        {
            var r = user.cameraRigController;
            if (r == null || Frames.Texture == null) return;
            if (r != rig)
            {
                rig = r;
                // the rig tests its override as a UnityEngine.Object: it has to be a component
                if (provider == null) provider = Bridge.I.gameObject.AddComponent<Provider>();
                rig.SetOverrideCam(provider, 0f);
            }
            if (overlay == null && rig.uiCam != null)
            {
                // A canvas of our own on RoR2's UI camera, drawn before the HUD's (lower order, further away), so
                // ULTRAKILL's picture sits over the stage and under RoR2's HUD. (A child of the HUD's own canvas
                // gets switched off by the HUD.)
                var root = new GameObject("ULTRAKILL layer", typeof(RectTransform));
                root.layer = LayerIndex.ui.intVal;
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = rig.uiCam;
                var hudCanvas = rig.hud != null ? rig.hud.GetComponent<Canvas>() : null;
                canvas.planeDistance = (hudCanvas != null ? hudCanvas.planeDistance : 10f) + 1f;
                canvas.sortingOrder = (hudCanvas != null ? hudCanvas.sortingOrder : 0) - 10;
                var go = new GameObject("frame", typeof(RectTransform));
                go.layer = root.layer;
                var rt = (RectTransform)go.transform;
                rt.SetParent(root.transform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                overlay = go.AddComponent<RawImage>();
                overlay.raycastTarget = false;
            }
            if (overlay != null)
            {
                if (overlay.texture != Frames.Texture) overlay.texture = Frames.Texture;
                overlay.enabled = Frames.Fresh;
            }
        }

        /// <summary>For the tests and the log: where the ULTRAKILL layer sits in RoR2's HUD.</summary>
        public static string OverlayInfo()
        {
            if (overlay == null) return "no overlay (rig " + (rig != null) + ", hud " + (rig != null && rig.hud != null) + ")";
            var rt = overlay.rectTransform;
            var canvas = overlay.canvas;
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            return "canvas " + (canvas != null ? canvas.name + " " + canvas.renderMode + " order " + canvas.sortingOrder + " cam " + (canvas.worldCamera != null ? canvas.worldCamera.name : "none") : "NONE")
                   + ", parent " + rt.parent.name + ", rect " + rt.rect.size + ", corners " + corners[0] + ".." + corners[2] + ", enabled " + overlay.enabled + ", active " + overlay.gameObject.activeInHierarchy
                   + ", texture " + (overlay.texture != null ? overlay.texture.width + "x" + overlay.texture.height : "none") + ", layer " + overlay.gameObject.layer
                   + ", cull " + overlay.canvasRenderer.cull + ", alpha " + overlay.canvasRenderer.GetInheritedAlpha();
        }

        /// <summary>Late in the frame: the body goes where V1 is.</summary>
        public static void LateTick()
        {
            if (!engaged || !UkLanded || !HavePose || body == null) return;
            var feet = ToRor(Latest.feet);
            // fell out of the stage: back to where the run put V1 down
            if (Stage.objects > 0 && feet.y < Stage.bounds.min.y - 30f && pendingTp == null)
            {
                Plugin.Log.LogInfo("V1 fell out of the stage: back to the start");
                UkLink.Send(Proto.Tp + " 0 0.5 0 " + S(Latest.yaw) + " 0");
                return;
            }
            var t = body.transform;
            // the body's transform sits at its capsule's middle
            t.position = feet + (t.position - body.footPosition);
            t.rotation = Quaternion.Euler(0f, Latest.yaw, 0f);
            if (body.inputBank != null) body.inputBank.aimDirection = Quaternion.Euler(Latest.pitch, Latest.yaw, 0f) * Vector3.forward;
        }

        /// <summary>RoR2's camera, placed exactly where the ULTRAKILL frame on screen was drawn from, so the two pictures
        /// never slide apart.</summary>
        sealed class Provider : MonoBehaviour, ICameraStateProvider
        {
            public void GetCameraState(CameraRigController r, ref CameraState state)
            {
                var f = Frames.Shown;
                if (Frames.Uploaded == 0 || !f.poseValid) return;
                state.position = ToRor(f.eye);
                state.rotation = Quaternion.Euler(f.pitch, f.yaw, f.roll);
                state.fov = f.fov;
            }

            public bool IsUserLookAllowed(CameraRigController r) => false;
            public bool IsUserControlAllowed(CameraRigController r) => false;
            public bool IsHudAllowed(CameraRigController r) => true;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(PlayerCharacterMasterController))]
    static class QuietController
    {
        [HarmonyLib.HarmonyPrefix, HarmonyLib.HarmonyPatch("Update")]
        static bool Update(PlayerCharacterMasterController __instance) => !V1Drive.Silenced(__instance);

        [HarmonyLib.HarmonyPrefix, HarmonyLib.HarmonyPatch("FixedUpdate")]
        static bool FixedUpdate(PlayerCharacterMasterController __instance) => !V1Drive.Silenced(__instance);
    }

    /// <summary>The real keys and mouse, read through Rewired (what Risk of Rain 2 itself reads).</summary>
    internal static class InputCapture
    {
        static readonly List<KeyCode> keys = new List<KeyCode>(16);

        public static V1Drive.Frame Read()
        {
            var f = new V1Drive.Frame();
            var c = Rewired.ReInput.controllers;
            var m = c.Mouse;
            if (m != null)
            {
                f.dx = m.GetAxisRaw(0);
                f.dy = m.GetAxisRaw(1);
                f.wheel = m.GetAxisRaw(2);
                // left, right, middle, then the two side buttons (the Input System's order: forward, back)
                for (int i = 0; i < 5; i++) if (m.GetButton(i)) f.buttons |= 1 << i;
            }
            keys.Clear();
            var kb = c.Keyboard;
            if (kb != null)
            {
                foreach (var p in kb.PollForAllKeys())
                {
                    // Escape stays Risk of Rain 2's (its pause menu)
                    if (p.keyboardKey != KeyCode.None && p.keyboardKey != KeyCode.Escape) keys.Add(p.keyboardKey);
                }
            }
            f.keys = keys.ToArray();
            return f;
        }
    }
}
