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

        void ClearShots()
        {
            foreach (var go in shots.Values) if (go != null) Destroy(go);
            shots.Clear();
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
                if (!shots.TryGetValue(id, out var go) || go == null)
                {
                    go = new GameObject("ror_shot_" + id);
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
                    shots[id] = go;
                }
                go.transform.position = pos;
            }
            shotsGone.Clear();
            foreach (var kv in shots) if (!shotsSeen.Contains(kv.Key)) shotsGone.Add(kv.Key);
            foreach (var id in shotsGone)
            {
                if (shots[id] != null) Destroy(shots[id]);
                shots.Remove(id);
            }
        }

        void ShotParried(int id)
        {
            Parries++;
            var cc = MonoSingleton<CameraController>.Instance;
            var dir = cc != null ? cc.transform.forward : Vector3.forward;
            Net.Send(Proto.Parry + " p " + id + " " + S(dir.x) + " " + S(dir.y) + " " + S(dir.z));
            if (shots.TryGetValue(id, out var go) && go != null) Destroy(go);
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
}
