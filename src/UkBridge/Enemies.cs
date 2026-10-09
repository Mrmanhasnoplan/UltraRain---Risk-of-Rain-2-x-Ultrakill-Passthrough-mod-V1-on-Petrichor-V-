using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using ULTRAKILL.Enemy;
using ULTRAKILL.Portal;
using UnityEngine;
using UnityEngine.Rendering;

namespace V1Bridge.Uk
{
    /// <summary>A Risk of Rain 2 monster's stand-in: an EnemyIdentifier with a head and a body to hit, and its entry in
    /// ULTRAKILL's TargetTracker (what coins and auto-aim search). Built the way Ultracraft builds its mob stand-ins (MIT).</summary>
    public class RorProxy : MonoBehaviour, ITarget
    {
        public int id;
        public EnemyIdentifier eid;
        public GameObject head;
        public BoxCollider body, headBox, rootBox;
        public Rigidbody rb;
        public Transform center, occluder;
        public Vector3 velocity, lastPos;
        public float lastSeen, w, h;
        public readonly CancellationTokenSource life = new CancellationTokenSource();
        Vector3 cachedPos, cachedHead;

        public int Id => GetInstanceID();
        public TargetType Type => TargetType.ENEMY;
        // spelled out: net472 can't lean on the interface's default implementations
        bool ITarget.isPlayer => false;
        bool ITarget.isEnemy => true;
        public EnemyIdentifier EID => eid;
        public GameObject GameObject => gameObject;
        public Rigidbody Rigidbody => rb;
        public Transform Transform => transform;
        public Vector3 Position => cachedPos;
        public Vector3 HeadPosition => cachedHead;

        public void UpdateCachedTransformData()
        {
            cachedPos = center != null ? center.position : transform.position;
            cachedHead = head != null ? head.transform.position : cachedPos;
        }

        public void SetData(ref TargetData data)
        {
            data.position = cachedPos;
            data.realPosition = cachedPos;
            data.headPosition = cachedHead;
            data.realHeadPosition = cachedHead;
            data.rotation = transform.rotation;
            data.velocity = velocity;
        }

        void OnDestroy() => life.Cancel();
    }

    public partial class Bridge
    {
        readonly Dictionary<int, RorProxy> proxies = new Dictionary<int, RorProxy>();
        readonly HashSet<int> seen = new HashSet<int>();
        readonly List<int> gone = new List<int>();
        GoreZone goreZone;
        float healCarry;
        public int DamageReports;

        void ClearProxies()
        {
            foreach (var p in proxies.Values) RemoveProxy(p);
            proxies.Clear();
        }

        static void RemoveProxy(RorProxy p)
        {
            if (p == null) return;
            if (p.eid != null)
            {
                p.eid.dead = true;
                MonoSingleton<EnemyTracker>.Instance?.GetCurrentEnemies().Remove(p.eid);
            }
            Destroy(p.gameObject);
        }

        /// <summary>ENTS id,x,y,z,w,h,hp;...: every monster alive, feet position, size, and how many ULTRAKILL damage units
        /// it has left. Stand-ins not listed are gone.</summary>
        void ApplyEntities(string rest)
        {
            if (!levelPrepared) return;
            seen.Clear();
            foreach (var entry in rest.Split(';'))
            {
                if (entry.Length == 0) continue;
                var a = entry.Split(',');
                if (a.Length < 7) continue;
                int id = int.Parse(a[0]);
                seen.Add(id);
                var feet = new Vector3(F(a[1]), F(a[2]), F(a[3]));
                float w = F(a[4]), h = F(a[5]);
                if (!proxies.TryGetValue(id, out var p) || p == null)
                {
                    p = MakeProxy(id, w, h);
                    proxies[id] = p;
                    p.lastPos = feet;
                }
                else if (Mathf.Abs(p.w - w) > 0.05f || Mathf.Abs(p.h - h) > 0.05f) Resize(p, w, h);
                float dt = Time.time - p.lastSeen;
                if (p.lastSeen > 0f && dt > 0.0001f) p.velocity = (feet - p.lastPos) / dt;
                p.lastSeen = Time.time;
                p.lastPos = feet;
                p.transform.position = feet;
                if (p.eid != null) p.eid.health = F(a[6]);
            }
            gone.Clear();
            foreach (var kv in proxies) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var id in gone)
            {
                RemoveProxy(proxies[id]);
                proxies.Remove(id);
            }
        }

        static void Resize(RorProxy p, float w, float h)
        {
            p.w = w;
            p.h = h;
            float headH = Mathf.Min(h * 0.25f, w);
            p.body.size = new Vector3(w, h - headH, w);
            p.body.center = new Vector3(0, (h - headH) * 0.5f, 0);
            p.headBox.size = new Vector3(w, headH, w);
            p.head.transform.localPosition = new Vector3(0, h - headH * 0.5f, 0);
            p.center.localPosition = new Vector3(0, h * 0.5f, 0);
            p.rootBox.size = new Vector3(w, h, w);
            p.rootBox.center = new Vector3(0, h * 0.5f, 0);
            p.occluder.localPosition = new Vector3(0, h * 0.5f, 0);
            p.occluder.localScale = new Vector3(w * 0.8f, h * 0.95f, w * 0.8f);
        }

        RorProxy MakeProxy(int id, float w, float h)
        {
            var root = new GameObject("ror_" + id);
            root.SetActive(false);
            // tagged like a real enemy: every weapon only hurts colliders tagged Head/Body/Limb/EndLimb/Enemy on the
            // enemy layers
            root.tag = "Enemy";
            var proxy = root.AddComponent<RorProxy>();
            proxy.id = id;
            var body = new GameObject("body");
            body.transform.SetParent(root.transform, false);
            body.layer = 10;
            body.tag = "Body";
            proxy.body = body.AddComponent<BoxCollider>();
            var head = new GameObject("head");
            head.transform.SetParent(root.transform, false);
            head.layer = 10;
            head.tag = "Head";
            proxy.headBox = head.AddComponent<BoxCollider>();
            proxy.head = head;
            var center = new GameObject("center");
            center.transform.SetParent(root.transform, false);
            proxy.center = center.transform;
            var eid = root.AddComponent<EnemyIdentifier>();
            // a type none of ULTRAKILL's per-enemy damage exceptions cover
            eid.enemyType = EnemyType.Mannequin;
            eid.weakPoint = head;
            eid.health = 2f;
            eid.overrideCenter = center.transform;
            // what EnemyIdentifier.Awake (skipped for stand-ins) would set up: gasoline and fire need these lists
            eid.flammables = new List<Flammable>();
            eid.burners = new List<Flammable>();
            // explosions only hurt an enemy whose own object has a collider: a switched-off one counts without touching anything
            proxy.rootBox = root.AddComponent<BoxCollider>();
            proxy.rootBox.enabled = false;
            foreach (var go in new[] { body, head })
            {
                var eii = go.AddComponent<EnemyIdentifierIdentifier>();
                eii.eid = eid;
                // a kinematic body per part, like real enemies: a hit reports the rigidbody's transform, and weapons
                // check that transform's layer and tag
                var lrb = go.AddComponent<Rigidbody>();
                lrb.isKinematic = true;
                lrb.useGravity = false;
            }
            proxy.rb = body.GetComponent<Rigidbody>();
            proxy.eid = eid;
            // RoR2 draws the monster; here it only hides ULTRAKILL's effects behind it (a slim box, depth only)
            var occ = new GameObject("occluder");
            occ.layer = 8;
            occ.transform.SetParent(root.transform, false);
            occ.AddComponent<MeshFilter>().sharedMesh = CubeMesh();
            var omr = occ.AddComponent<MeshRenderer>();
            omr.sharedMaterial = OccluderMaterial();
            omr.shadowCastingMode = ShadowCastingMode.Off;
            omr.receiveShadows = false;
            omr.lightProbeUsage = LightProbeUsage.Off;
            omr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            proxy.occluder = occ.transform;
            Resize(proxy, w, h);
            if (worldRoot != null) root.transform.SetParent(worldRoot.transform, false);
            root.SetActive(true);
            proxy.UpdateCachedTransformData();
            MonoSingleton<EnemyTracker>.Instance?.AddEnemy(eid);
            // coins and auto-aim look enemies up in the TargetTracker, not EnemyTracker
            try
            {
                var pm = MonoSingleton<PortalManagerV2>.Instance;
                if (pm != null && pm.TargetTracker != null) pm.TargetTracker.RegisterTarget(proxy, proxy.life.Token);
            }
            catch (Exception e) { Plugin.Log.LogWarning("target tracker: " + e.Message); }
            return proxy;
        }

        static Mesh cubeMesh;

        static Mesh CubeMesh()
        {
            if (cubeMesh != null) return cubeMesh;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);
            return cubeMesh;
        }

        /// <summary>DMG id multiplier head explosion hitter x y z: V1 hit a monster's stand-in.</summary>
        public void ReportDamage(RorProxy p, float multiplier, bool head, bool explosion, string hitter, Vector3 at)
        {
            DamageReports++;
            Net.Send(Proto.Damage + " " + p.id + " " + S(multiplier) + " " + (head ? 1 : 0) + " " + (explosion ? 1 : 0) + " " + (string.IsNullOrEmpty(hitter) ? "none" : hitter.Replace(' ', '_'))
                     + " " + S(at.x) + " " + S(at.y) + " " + S(at.z));
        }

        /// <summary>A splatter of ULTRAKILL blood where a monster was hit: sound, stains, and V1's healing when close, as
        /// for ULTRAKILL's own enemies. hp &lt; 0 keeps the splatter's own amount.</summary>
        public void SpawnGore(EnemyIdentifier eid, GoreType type, Vector3 at, bool fromExplosion, int hp)
        {
            try
            {
                var bsm = MonoSingleton<BloodsplatterManager>.Instance;
                if (goreZone == null && levelPrepared) goreZone = new GameObject("V1Bridge gore zone").AddComponent<GoreZone>();
                if (bsm == null || goreZone == null) return;
                var gore = bsm.GetGore(type, eid, fromExplosion);
                if (gore == null) return;
                gore.transform.position = at;
                gore.transform.SetParent(goreZone.goreZone, true);
                var bs = gore.GetComponent<Bloodsplatter>();
                if (bs != null)
                {
                    if (hp >= 0) bs.hpAmount = hp;
                    bs.GetReady();
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("gore: " + e.Message); }
        }

        /// <summary>HURT n: Risk of Rain 2 hurt V1 by n of ULTRAKILL's 100. ULTRAKILL decides what that does (a dash's
        /// invincibility, hard damage, death).</summary>
        void Hurt(string rest)
        {
            var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
            if (nm == null || nm.dead) return;
            int before = nm.hp;
            int layer = nm.gameObject.layer;
            nm.GetHurt(Mathf.Max(1, Mathf.RoundToInt(F(rest))), true);
            if (nm.hp == before) Plugin.Log.LogInfo("HURT " + rest + " changed nothing (V1 on layer " + layer + (layer == 15 ? ", invincible" : "") + ", hp " + before + ")");
        }

        /// <summary>HEAL x: Risk of Rain 2 healed V1 by x of ULTRAKILL's 100 (fractions add up).</summary>
        void Heal(string rest)
        {
            var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
            if (nm == null || nm.dead) return;
            healCarry += F(rest);
            int n = Mathf.FloorToInt(healCarry);
            if (n < 1) return;
            healCarry -= n;
            nm.GetHealth(n, true);
        }

        /// <summary>KILL id: the monster died to V1: a kill on the style meter.</summary>
        void Killed(string rest)
        {
            if (!levelPrepared || !int.TryParse(rest.Trim(), out int id)) return;
            proxies.TryGetValue(id, out var p);
            MonoSingleton<StyleHUD>.Instance?.AddPoints(50, "ultrakill.kill", null, p != null ? p.eid : null);
            if (p != null && p.center != null) SpawnGore(p.eid, GoreType.Head, p.center.position, false, -1);
        }

        void RespawnV1()
        {
            var nm = levelPrepared ? MonoSingleton<NewMovement>.Instance : null;
            if (nm == null) return;
            if (nm.dead) nm.Respawn();
            nm.ResetHardDamage();
            nm.FullHeal(true);
        }
    }

    // ---------------------------------------------------------------- Harmony: stand-ins are not ULTRAKILL enemies

    [HarmonyPatch(typeof(EnemyIdentifier))]
    static class ProxyLifecycle
    {
        static bool NotProxy(EnemyIdentifier e) => e.GetComponent<RorProxy>() == null;

        [HarmonyPrefix, HarmonyPatch("Awake")] static bool Awake(EnemyIdentifier __instance) => NotProxy(__instance);
        [HarmonyPrefix, HarmonyPatch("Start")] static bool Start(EnemyIdentifier __instance) => NotProxy(__instance);
        [HarmonyPrefix, HarmonyPatch("Update")] static bool Update(EnemyIdentifier __instance) => NotProxy(__instance);
        [HarmonyPrefix, HarmonyPatch("OnEnable")] static bool OnEnable(EnemyIdentifier __instance) => NotProxy(__instance);
        [HarmonyPrefix, HarmonyPatch("OnDisable")] static bool OnDisable(EnemyIdentifier __instance) => NotProxy(__instance);
    }

    /// <summary>A hit on a stand-in is Risk of Rain 2's to resolve: ULTRAKILL says how hard and where, and bleeds.</summary>
    [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.DeliverDamage))]
    static class ProxyDamage
    {
        static bool Prefix(EnemyIdentifier __instance, GameObject target, Vector3 hitPoint, float multiplier, float critMultiplier, bool fromExplosion)
        {
            var p = __instance.GetComponent<RorProxy>();
            if (p == null) return true;
            var b = Bridge.I;
            if (b == null) return false;
            bool head = target != null && target == p.head;
            float dmg = multiplier;
            if (head) dmg *= critMultiplier > 0f ? 1f + critMultiplier : 2f;
            // a touch that does nothing isn't a hit
            if (dmg < 0.001f) return false;
            var at = hitPoint != Vector3.zero ? hitPoint : target != null && target != __instance.gameObject ? target.transform.position : p.center.position;
            b.ReportDamage(p, dmg, head, fromExplosion, __instance.hitter, at);
            // ULTRAKILL's own blood where it was hit: splatters, stains, and V1's healing when close (the amounts
            // ULTRAKILL gives: 3 per pellet or blast, 1 per nail)
            var type = head ? GoreType.Head : dmg >= 1f || fromExplosion ? GoreType.Body : GoreType.Small;
            int hp = __instance.hitter == "nail" ? 1 : __instance.hitter == "shotgun" || __instance.hitter == "shotgunzone" || fromExplosion ? 3 : -1;
            b.SpawnGore(__instance, type, at, fromExplosion, hp);
            return false;
        }
    }
}
