using System;
using System.Collections.Generic;
using UnityEngine;

namespace V1Bridge.Uk
{
    // Parrying Risk of Rain 2. Its projectiles fly through ULTRAKILL as objects on ULTRAKILL's projectile layer with a
    // ParryReceiver, so the Feedbacker parries them with the real parry (flash, hitstop, heal); a melee hit that V1
    // punches away is parried on RoR2's word (PARRIED), with the same real parry.
    public partial class Bridge
    {
        readonly Dictionary<int, GameObject> shots = new Dictionary<int, GameObject>();
        readonly HashSet<int> shotsSeen = new HashSet<int>();
        readonly List<int> shotsGone = new List<int>();
        public int Parries;

        public int ShotsBlown;
        readonly Dictionary<int, float> blownAt = new Dictionary<int, float>();

        /// <summary>Removing a projectile's copy ourselves is not ULTRAKILL destroying it.</summary>
        static void Remove(GameObject go)
        {
            if (go == null) return;
            var mark = go.GetComponent<RorShot>();
            if (mark != null) mark.removed = true;
            Destroy(go);
        }

        void ClearShots()
        {
            foreach (var go in shots.Values) Remove(go);
            shots.Clear();
        }

        /// <summary>ULTRAKILL destroyed a projectile's copy itself: a beam that can hit projectiles (the Sharpshooter's)
        /// went through it and blew it up there, with ULTRAKILL's own explosion. Risk of Rain 2's projectile goes too.</summary>
        public void ShotBlown(int id)
        {
            if (!shots.Remove(id)) return;
            ShotsBlown++;
            // Risk of Rain 2 lists it for a frame or two more: it is not to be put back
            blownAt[id] = Time.time;
            Net.Send(Proto.Parry + " b " + id);
        }

        /// <summary>PROJS id,x,y,z,radius;...: every hostile projectile in flight.</summary>
        void ApplyProjectiles(string rest)
        {
            if (!levelPrepared) return;
            shotsSeen.Clear();
            foreach (var entry in rest.Split(';'))
            {
                if (entry.Length == 0) continue;
                var a = entry.Split(',');
                if (a.Length < 5) continue;
                int id = int.Parse(a[0]);
                shotsSeen.Add(id);
                var pos = new Vector3(F(a[1]), F(a[2]), F(a[3]));
                if (blownAt.TryGetValue(id, out float when))
                {
                    if (Time.time - when < 2f) continue;
                    blownAt.Remove(id);
                }
                if (!shots.TryGetValue(id, out var go) || go == null)
                {
                    go = new GameObject("ror_shot_" + id);
                    // built switched off: ULTRAKILL's Projectile must be known as ours before it wakes
                    go.SetActive(false);
                    go.layer = 14; // Projectile: what the fist looks for
                    if (worldRoot != null) go.transform.SetParent(worldRoot.transform, false);
                    var col = go.AddComponent<SphereCollider>();
                    col.isTrigger = true;
                    // generous, as ULTRAKILL's own projectiles are to a punch
                    col.radius = Mathf.Max(1.2f, F(a[4]));
                    var rb = go.AddComponent<Rigidbody>();
                    rb.isKinematic = true;
                    rb.useGravity = false;
                    var pr = go.AddComponent<ParryReceiver>();
                    pr.parryHeal = true;
                    pr.disappearOnParry = false;
                    pr.onParry = new UnityEngine.Events.UnityEvent();
                    int captured = id;
                    pr.onParry.AddListener(() => ShotParried(captured));
                    // To ULTRAKILL's weapons it is a projectile: a beam that can hit projectiles (the Sharpshooter's
                    // ricochet shot) finds a Projectile on this layer, sets off an explosion where it is and destroys
                    // it. The component is ULTRAKILL's own, kept from doing anything itself (StandInProjectile): the
                    // fist is to go on to the ParryReceiver, and explosions are to leave it be.
                    var pj = go.AddComponent<Projectile>();
                    pj.enabled = false;
                    pj.speed = 1f;
                    pj.unparryable = true;
                    pj.undeflectable = true;
                    pj.ignoreExplosions = true;
                    var drm = MonoSingleton<DefaultReferenceManager>.Instance;
                    pj.explosionEffect = drm != null ? drm.explosion : null;
                    StandInProjectile.Ours.Add(pj);
                    go.AddComponent<RorShot>().id = id;
                    go.SetActive(true);
                    shots[id] = go;
                }
                go.transform.position = pos;
            }
            shotsGone.Clear();
            foreach (var kv in shots) if (!shotsSeen.Contains(kv.Key)) shotsGone.Add(kv.Key);
            foreach (var id in shotsGone)
            {
                Remove(shots[id]);
                shots.Remove(id);
            }
        }

        void ShotParried(int id)
        {
            Parries++;
            var cc = MonoSingleton<CameraController>.Instance;
            var dir = cc != null ? cc.transform.forward : Vector3.forward;
            Net.Send(Proto.Parry + " p " + id + " " + S(dir.x) + " " + S(dir.y) + " " + S(dir.z));
            if (shots.TryGetValue(id, out var go)) Remove(go);
            shots.Remove(id);
        }

        /// <summary>PARRIED id: V1's punch met that monster's melee hit. The real parry: flash, hitstop, heal, style.</summary>
        void MeleeParried(string rest)
        {
            if (!levelPrepared || !int.TryParse(rest.Trim(), out int id)) return;
            proxies.TryGetValue(id, out var p);
            Parries++;
            try
            {
                var fc = MonoSingleton<FistControl>.Instance;
                if (fc != null && fc.currentPunch != null) fc.currentPunch.Parry(false, p != null ? p.eid : null);
                else MonoSingleton<TimeController>.Instance?.ParryFlash();
            }
            catch (Exception e) { Plugin.Log.LogWarning("parry: " + e.Message); }
        }
    }

    /// <summary>On a Risk of Rain 2 projectile's copy: tells the bridge when ULTRAKILL itself destroys it.</summary>
    public class RorShot : MonoBehaviour
    {
        public int id;
        /// <summary>The bridge is taking it away (the projectile is gone in Risk of Rain 2, or was parried).</summary>
        public bool removed;

        void OnDestroy()
        {
            if (removed || Bridge.I == null || !gameObject.scene.isLoaded) return;
            Bridge.I.ShotBlown(id);
        }
    }

    /// <summary>The Projectile on a copy is there for ULTRAKILL's weapons to find, not to fly, home or hit anything: it
    /// never wakes up, and touching something does nothing (Unity sends that even to a switched-off component).</summary>
    [HarmonyLib.HarmonyPatch(typeof(Projectile))]
    static class StandInProjectile
    {
        public static readonly HashSet<Projectile> Ours = new HashSet<Projectile>();

        static bool NotOurs(Projectile p)
        {
            if (Ours.Count > 256) Ours.RemoveWhere(x => x == null);
            return !Ours.Contains(p);
        }

        [HarmonyLib.HarmonyPrefix, HarmonyLib.HarmonyPatch("Awake")] static bool Awake(Projectile __instance) => NotOurs(__instance);
        [HarmonyLib.HarmonyPrefix, HarmonyLib.HarmonyPatch("OnTriggerEnter")] static bool OnTriggerEnter(Projectile __instance) => NotOurs(__instance);
    }
}