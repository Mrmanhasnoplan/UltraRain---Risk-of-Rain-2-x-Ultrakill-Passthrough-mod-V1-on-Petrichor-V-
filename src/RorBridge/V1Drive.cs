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
        static bool rigFaded = true;
        static Provider provider;
        static PlayerCharacterMasterController quiet;
        /// <summary>RoR2's own handling of the player's keys is skipped for this controller while V1 is driven by ULTRAKILL.
        /// (Switching the component off instead takes the player out of the game's list of the living: instant defeat.)</summary>
        public static bool Silenced(PlayerCharacterMasterController c) => engaged && c == quiet;
        static RawImage overlay;
        static readonly List<Behaviour> disabled = new List<Behaviour>();
        static CharacterModel hiddenModel;
        static readonly List<Renderer> hiddenParts = new List<Renderer>(), partScratch = new List<Renderer>();
        static float nextPartSweep;
        /// <summary>Renderers on V1's hidden body switched off by the sweep (item displays, mostly), for the tests.</summary>
        public static int HiddenParts => hiddenParts.Count;

        /// <summary>For the tests: what the sweep switched off, as "item display/renderer".</summary>
        public static string HiddenPartNames()
        {
            var names = new List<string>();
            foreach (var r in hiddenParts)
            {
                if (r == null) continue;
                var display = r.GetComponentInParent<ItemDisplay>();
                string n = (display != null ? display.name.Replace("(Clone)", "") + "/" : "") + r.name + ":" + r.GetType().Name;
                if (!names.Contains(n)) names.Add(n);
            }
            return string.Join(", ", names.ToArray());
        }
        static int inputSeq;

        /// <summary>V1's RoR2 body while ULTRAKILL is driving it.</summary>
        public static CharacterBody Body => engaged && UkLanded ? body : null;
        /// <summary>This health component is V1's and ULTRAKILL's HP is its truth right now.</summary>
        public static bool Linked(HealthComponent hc) => engaged && UkLanded && body != null && hc != null && hc == body.healthComponent;
        static bool deathSent, releasedSent;
        static Run lastRun;
        static readonly List<GameObject> hiddenHud = new List<GameObject>();
        /// <summary>True while the body is being killed because V1 died in ULTRAKILL: that one hit may be lethal.</summary>
        public static bool Dying;

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
                    if (rest != "again") LandedInfo = rest;
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
            if (b == null)
            {
                // between stages, or dead: V1 lets go of everything
                if (!releasedSent && UkLanded)
                {
                    UkLink.Send(Proto.Input + " " + (++inputSeq) + " " + Frames.NowMicros() + " 0 0 0 0");
                    releasedSent = true;
                }
                return;
            }
            releasedSent = false;
            if (!engaged || b != body)
            {
                if (!UkReady) return;
                Engage(user, b);
            }
            if (!UkLanded) return;
            SendInput(user);
            Monsters.Tick(b);
            Parry.Tick(b);
            Frames.Update();
            ShowOverlay(user);
            HideBodyParts();
        }

        /// <summary>Nothing on V1's hidden body is drawn: the view is ULTRAKILL's, from inside it. RoR2 only tells an item's
        /// display to hide when the model's visibility changes, so one picked up while the model is already hidden stays
        /// in sight, and particles, trails and the like on a display are never told at all. Whatever is drawing under
        /// the model is switched off here, and back on when the bridge lets go of the body.</summary>
        static void HideBodyParts()
        {
            if (hiddenModel == null || Time.time < nextPartSweep) return;
            nextPartSweep = Time.time + 0.1f;
            hiddenModel.GetComponentsInChildren(false, partScratch);
            SwitchOff();
            if (body == null) return;
            // and what hangs on the body itself, or is attached to it over the network (an item's aura, its tethers,
            // its range ring): effects of V1's own items, seen from inside them
            body.GetComponentsInChildren(false, partScratch);
            SwitchOff();
            attachScratch.Clear();
            NetworkedBodyAttachment.FindBodyAttachments(body, attachScratch);
            foreach (var a in attachScratch)
            {
                if (a == null) continue;
                a.GetComponentsInChildren(false, partScratch);
                SwitchOff();
                foreach (var l in a.GetComponentsInChildren<Light>()) if (l.enabled) l.enabled = false;
            }
        }

        static readonly List<NetworkedBodyAttachment> attachScratch = new List<NetworkedBodyAttachment>();

        static void SwitchOff()
        {
            foreach (var r in partScratch)
            {
                if (!r.enabled) continue;
                r.enabled = false;
                hiddenParts.Add(r);
            }
            partScratch.Clear();
        }

        /// <summary>A V1 run has a body and ULTRAKILL is waiting: ULTRAKILL's world is laid over the stage with its origin
        /// at the body's feet, and V1 is put down there.</summary>
        static void Engage(LocalUser user, CharacterBody b)
        {
            if (engaged) Disengage();
            body = b;
            engaged = true;
            Origin = b.footPosition;
            SendSize();
            float yaw = b.inputBank != null ? Quaternion.LookRotation(b.inputBank.aimDirection).eulerAngles.y : 0f;
            deathSent = false;
            if (UkDead || Run.instance != lastRun)
            {
                // a new run, or back from the dead (a revive): V1 whole again. A new stage keeps V1 as it was.
                lastRun = Run.instance;
                UkHp = 100;
                UkDead = false;
                UkLink.Send(Proto.Respawn);
            }
            b.hideCrosshair = true;
            shieldReady = false;
            capSent = -1;
            Monsters.Hook();
            Parry.Reset();
            Stage = StageGeometry.Export(Origin, K, ++geoSerial % 2);
            StageLoaded = null;
            Plugin.Log.LogInfo("stage exported: " + Stage.objects + " colliders, " + Stage.triangles + " triangles (" + Stage.unreadable + " meshes read from the GPU, " + Stage.readBack + " of them straight from Direct3D, " + Stage.skipped
                               + " skipped) in " + Stage.ms.ToString("0") + " ms" + (Stage.skippedNames != null ? ", skipped: " + Stage.skippedNames : "") + (Stage.problem != null ? ", problem: " + Stage.problem : ""));
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
            Plugin.Log.LogInfo("engaged: origin " + Origin);
        }

        static void Disengage()
        {
            engaged = false;
            quiet = null;
            foreach (var c in disabled) if (c != null) c.enabled = true;
            disabled.Clear();
            foreach (var go in hiddenHud) if (go != null) go.SetActive(true);
            hiddenHud.Clear();
            if (hiddenModel != null) hiddenModel.invisibilityCount--;
            hiddenModel = null;
            foreach (var r in hiddenParts) if (r != null) r.enabled = true;
            hiddenParts.Clear();
            nextPartSweep = 0f;
            if (rig != null && rig.hasOverride) rig.SetOverrideCam(null, 0f);
            if (rig != null) rig.enableFading = rigFaded;
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
            /// <summary>Risk of Rain 2's own actions, which stay RoR2's: their keys are not passed to ULTRAKILL.</summary>
            public bool interact, equipment, ping;
        }
        public static int InputsSent, PingsSent;
        static bool interactHeld, equipmentHeld, pingWas;

        /// <summary>Risk of Rain 2's ping, at what V1 is looking at: the silenced controller's own pinger, aimed along the
        /// view of the ULTRAKILL frame on screen.</summary>
        static void Ping()
        {
            var pinger = quiet != null ? quiet.pingerController : null;
            if (pinger == null || body == null) return;
            var f = Frames.Shown;
            Ray ray;
            if (Frames.Uploaded > 0 && f.poseValid) ray = new Ray(ToRor(f.eye), Quaternion.Euler(f.pitch, f.yaw, 0f) * Vector3.forward);
            else if (HavePose) ray = new Ray(ToRor(Latest.eye), Quaternion.Euler(Latest.pitch, Latest.yaw, 0f) * Vector3.forward);
            else return;
            pinger.AttemptPing(ray, body.gameObject);
            PingsSent++;
        }

        /// <summary>Physics step: interact and equipment reach the body the way RoR2's own controller would press them.</summary>
        public static void FixedTick()
        {
            if (!engaged || !UkLanded || body == null || body.inputBank == null) return;
            body.inputBank.interact.PushState(interactHeld);
            body.inputBank.activateEquipment.PushState(equipmentHeld);
            interactHeld = equipmentHeld = false;
        }

        /// <summary>What ULTRAKILL renders at: the configured height, this window's shape, the configured frame cap.</summary>
        public static void SendSize()
        {
            if (!UkLink.Ready) return;
            int h = Mathf.Clamp(Plugin.UkHeight.Value, 360, 1440);
            int w = Mathf.RoundToInt(h * (Screen.width / (float)Screen.height) / 2f) * 2;
            UkLink.Send(Proto.Size + " " + w + " " + h + " " + Plugin.UkFps.Value);
        }

        public static int RorTeleports;
        static Vector3 holdAt;
        static float holdUntil = -1f;

        /// <summary>Risk of Rain 2 teleported V1's body (fell out of bounds, a zone that puts players back, anything that
        /// uses its teleport helper): V1 goes there in ULTRAKILL, standing still, facing as it was. Until ULTRAKILL's
        /// V1 has arrived the body stays where RoR2 put it, or it would be dragged back out and teleported again.</summary>
        public static void OnRorTeleport(CharacterBody b, Vector3 feet)
        {
            if (!engaged || b != body || !UkLanded) return;
            RorTeleports++;
            holdAt = feet;
            holdUntil = Time.time + 1.5f;
            Teleport(feet + Vector3.up * 0.2f, HavePose ? Latest.yaw : 0f, HavePose ? Latest.pitch : 0f);
            Plugin.Log.LogInfo("Risk of Rain 2 teleported V1 to " + feet);
        }

        /// <summary>Put V1 down somewhere in the stage (RoR2 coordinates), looking along yaw.</summary>
        public static void Teleport(Vector3 feet, float yaw, float pitch = 0f)
        {
            var u = ToUk(feet);
            UkLink.Send(Proto.Tp + " " + S(u.x) + " " + S(u.y) + " " + S(u.z) + " " + S(yaw) + " " + S(pitch));
        }
        static readonly StringBuilder sb = new StringBuilder(128);

        static void SendInput(LocalUser user)
        {
            Frame f;
            if (Script != null) f = Script();
            else if (PauseManager.isPaused || Cursor.lockState != CursorLockMode.Locked || !Application.isFocused) f = default;
            else
            {
                f = InputCapture.Read(user);
                f.dx *= Plugin.MouseScale.Value;
                f.dy *= Plugin.MouseScale.Value;
            }
            // latched until the next physics step, where RoR2 reads its buttons
            interactHeld |= f.interact;
            equipmentHeld |= f.equipment;
            // one ping per press: holding the key does not ping again (a second ping on the same thing takes it off)
            if (f.ping && !pingWas) Ping();
            pingWas = f.ping;
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
                // RoR2 fades monsters and props out as its third-person camera closes in on them, so they do not block
                // the view of the survivor. V1's view is first person: what is close stays solid.
                rigFaded = rig.enableFading;
                rig.enableFading = false;
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
            if (hiddenHud.Count == 0 && rig.hud != null && Plugin.HideRorHud.Value)
            {
                // ULTRAKILL's HUD shows V1's health and weapons; RoR2's bar and skill icons would sit on top of it
                void Hide(Component c) { if (c != null && c.gameObject.activeSelf) { c.gameObject.SetActive(false); hiddenHud.Add(c.gameObject); } }
                Hide(rig.hud.healthBar);
                if (rig.hud.skillIcons != null) foreach (var s in rig.hud.skillIcons) Hide(s);
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
            if (holdUntil > 0f)
            {
                // RoR2 has just put the body somewhere: wait for V1 to turn up there
                if (Time.time < holdUntil && Vector3.Distance(feet, holdAt) > 4f) return;
                holdUntil = -1f;
            }
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
            // what Risk of Rain 2 sends the other players as the body's facing: they see V1 turned the way it looks
            if (body.characterDirection != null) body.characterDirection.yaw = Latest.yaw;
            // ULTRAKILL's HP is V1's health: the body shows the same share of its own (overheal has no place to go)
            var hc = body.healthComponent;
            if (hc != null && hc.alive)
            {
                if (UkDead)
                {
                    if (!deathSent)
                    {
                        deathSent = true;
                        Plugin.Log.LogInfo("V1 died in ULTRAKILL: the body dies in Risk of Rain 2");
                        Dying = true;
                        try { hc.Suicide(); }
                        finally { Dying = false; }
                    }
                }
                else MirrorHealth(hc);
            }
            if (body.inputBank != null) body.inputBank.aimDirection = Quaternion.Euler(Latest.pitch, Latest.yaw, 0f) * Vector3.forward;
        }

        // ------------------------------------------------------------ health and shield

        static float shieldPts, shieldWrote, shieldFull, shieldCredit, shieldCreditAt;
        static int shieldHp, capSent = -1;
        static bool shieldReady;
        /// <summary>For the tests: until then the shield is simply kept at ULTRAKILL's HP share (V1 was made whole by hand).</summary>
        public static float ShieldSyncUntil;
        /// <summary>The shield part of V1's health, in Risk of Rain 2 points, and the HP limit last sent to ULTRAKILL.</summary>
        public static float ShieldPoints => shieldPts;
        public static int HpCap => capSent;

        /// <summary>ULTRAKILL's HP (0-100) is V1's health and shield together, as a share of the two (V1HealthLink.Pool).
        /// Which part of what is left is shield is kept here: damage comes off the shield first, as in Risk of Rain 2;
        /// ULTRAKILL's healing (blood, parries) and Risk of Rain 2's healing fill health only; the shield fills only
        /// the way Risk of Rain 2 fills it, out of danger for a while, and that is passed on to ULTRAKILL as it
        /// happens. So that ULTRAKILL's healing cannot fill the shield's share, ULTRAKILL is told the most HP V1 may
        /// have (CAP): full health plus the shield as it stands.</summary>
        static void MirrorHealth(HealthComponent hc)
        {
            float pool = V1HealthLink.Pool(hc), fs = hc.fullShield, share = Mathf.Clamp01(UkHp / 100f);
            if (!shieldReady || fs <= 0f || Time.time < ShieldSyncUntil)
            {
                shieldPts = fs * share;
                shieldFull = fs;
                shieldCredit = 0f;
                shieldReady = true;
            }
            else
            {
                if (Mathf.Abs(fs - shieldFull) > 0.001f)
                {
                    // a bigger or smaller shield (an item, a level): what is left of it keeps its share
                    shieldPts = shieldFull > 0f ? shieldPts * fs / shieldFull : fs * share;
                    shieldFull = fs;
                }
                else
                {
                    // Risk of Rain 2 has been filling the shield since it was last written: V1 gains that in ULTRAKILL
                    float grown = hc.shield - shieldWrote;
                    if (grown > 0.0005f)
                    {
                        shieldCredit += grown;
                        shieldCreditAt = Time.time;
                        UkLink.Send(Proto.Heal + " " + (grown / pool * 100f).ToString("0.####", CultureInfo.InvariantCulture));
                    }
                }
                float change = (UkHp - shieldHp) / 100f * pool;
                if (change < 0f) shieldPts += change;
                else if (change > 0f)
                {
                    float toShield = Mathf.Min(change, shieldCredit);
                    shieldCredit -= toShield;
                    shieldPts += toShield;
                }
                if (Time.time - shieldCreditAt > 1.5f) shieldCredit = 0f;
            }
            shieldHp = UkHp;
            float left = share * pool;
            shieldPts = Mathf.Clamp(shieldPts, 0f, Mathf.Min(fs, left));
            // ULTRAKILL's HP comes in whole points: the last fraction of one goes to the shield, not past full health
            if (left - shieldPts > hc.fullHealth) shieldPts = Mathf.Min(fs, left - hc.fullHealth);
            shieldCredit = Mathf.Clamp(shieldCredit, 0f, fs - shieldPts);
            hc.Networkhealth = Mathf.Clamp(left - shieldPts, 1f, hc.fullHealth);
            if (fs > 0f || hc.shield > 0f) hc.Networkshield = shieldPts;
            shieldWrote = shieldPts;
            float most = hc.fullHealth + shieldPts + shieldCredit;
            int cap = fs > 0f && most < pool - 0.01f ? Mathf.Clamp(Mathf.CeilToInt(most / pool * 100f - 0.001f), 1, 100) : 999;
            if (cap != capSent)
            {
                capSent = cap;
                UkLink.Send(Proto.Cap + " " + cap);
            }
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

    /// <summary>Risk of Rain 2's jump pads throw V1. The pad sets the velocity of the body's motor, which is switched off
    /// while ULTRAKILL moves V1; the same throw is handed to ULTRAKILL instead.</summary>
    [HarmonyLib.HarmonyPatch(typeof(JumpVolume), nameof(JumpVolume.OnTriggerStay))]
    static class JumpPads
    {
        public static int Thrown;
        public static Vector3 LastVelocity;
        static float lastAt = -10f;

        static void Postfix(JumpVolume __instance, Collider other)
        {
            var v1 = V1Drive.Body;
            if (v1 == null || other == null || other.gameObject != v1.gameObject) return;
            // the pad fires every physics step V1 is in it: one throw
            if (Time.time - lastAt < 0.4f) return;
            lastAt = Time.time;
            Vector3 v = __instance.alignJumpVelocityToTransform ? __instance.transform.up * __instance.jumpVelocity.magnitude : __instance.jumpVelocity;
            LastVelocity = v;
            Thrown++;
            Vector3 u = v * V1Drive.K;
            UkLink.Send(Proto.Launch + " " + u.x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " " + u.y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                        + " " + u.z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " " + (Physics.gravity.magnitude * V1Drive.K).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Whatever teleports V1's body in Risk of Rain 2 (out of bounds first of all) moves V1 in ULTRAKILL.</summary>
    [HarmonyLib.HarmonyPatch(typeof(TeleportHelper), nameof(TeleportHelper.TeleportBody), new[] { typeof(TeleportHelper.TeleportBodyArgs) })]
    static class RorTeleports
    {
        static void Postfix(TeleportHelper.TeleportBodyArgs bodyArgs)
        {
            var b = bodyArgs.body;
            if (b != null) V1Drive.OnRorTeleport(b, bodyArgs.targetPosition);
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

        static readonly HashSet<KeyCode> kept = new HashSet<KeyCode>();
        static float keptAt = -10f;
        /// <summary>Mouse buttons bound to Risk of Rain 2's own actions, as bits in <see cref="V1Drive.Frame.buttons"/>.</summary>
        static int keptButtons;
        static readonly int[] RorActions = { RewiredConsts.Action.Interact, RewiredConsts.Action.Equipment, RewiredConsts.Action.Ping };

        /// <summary>For the tests and the log: what is held back from ULTRAKILL.</summary>
        public static string KeptInfo()
        {
            var names = new List<string>();
            foreach (var k in kept) names.Add(k.ToString());
            for (int i = 0; i < 5; i++) if ((keptButtons & (1 << i)) != 0) names.Add("mouse" + i);
            return names.Count > 0 ? string.Join(",", names.ToArray()) : "nothing";
        }

        public static V1Drive.Frame Read(LocalUser user)
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
            var player = user != null ? user.inputPlayer : null;
            if (player != null)
            {
                f.interact = player.GetButton(RewiredConsts.Action.Interact);
                f.equipment = player.GetButton(RewiredConsts.Action.Equipment);
                f.ping = player.GetButton(RewiredConsts.Action.Ping);
                // the keys and mouse buttons those are bound to are RoR2's alone (looked up now and then: binds can change in the menu)
                if (Time.unscaledTime - keptAt > 2f)
                {
                    keptAt = Time.unscaledTime;
                    kept.Clear();
                    keptButtons = 0;
                    foreach (int action in RorActions)
                    {
                        foreach (var map in player.controllers.maps.ElementMapsWithAction(Rewired.ControllerType.Keyboard, action, true)) if (map.keyCode != KeyCode.None) kept.Add(map.keyCode);
                        if (m == null) continue;
                        foreach (var map in player.controllers.maps.ElementMapsWithAction(Rewired.ControllerType.Mouse, action, true))
                        {
                            if (map.elementType != Rewired.ControllerElementType.Button) continue;
                            int button = m.GetButtonIndexById(map.elementIdentifierId);
                            if (button >= 0 && button < 5) keptButtons |= 1 << button;
                        }
                    }
                }
            }
            f.buttons &= ~keptButtons;
            keys.Clear();
            var kb = c.Keyboard;
            if (kb != null)
            {
                foreach (var p in kb.PollForAllKeys())
                {
                    // Escape stays Risk of Rain 2's (its pause menu)
                    if (p.keyboardKey != KeyCode.None && p.keyboardKey != KeyCode.Escape && !kept.Contains(p.keyboardKey)) keys.Add(p.keyboardKey);
                }
            }
            f.keys = keys.ToArray();
            return f;
        }
    }
}
