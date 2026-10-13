using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace V1Bridge.Ror
{
    /// <summary>Risk of Rain 2's monsters as ULTRAKILL's targets, and damage between the two games.
    /// Out: ULTRAKILL reports each hit as a multiple of its own damage unit (1 = a revolver shot); here that becomes a
    /// real DamageInfo from V1's body, sized by the body's damage stat, so crits, on-hit items, kills, gold and
    /// experience are all Risk of Rain 2's own doing.
    /// In: RoR2 works a hit on V1 out in full (armor, shield, barrier, blocks); the health it would have cost goes to
    /// ULTRAKILL as a share of V1's 100, and ULTRAKILL's HP, the one truth, is mirrored back onto the RoR2 body.</summary>
    internal static class Monsters
    {
        static readonly Dictionary<int, CharacterBody> byId = new Dictionary<int, CharacterBody>();
        static readonly StringBuilder sb = new StringBuilder(1024);
        public static int Listed, HitsApplied, KillsByV1;
        /// <summary>The last hit V1 landed, for the tests.</summary>
        public static string LastHit = "";
        /// <summary>Where the last hit landed, in the stage, and on whom.</summary>
        public static Vector3 LastHitAt;
        public static CharacterBody LastHitOn;
        static bool hooked;

        static string S(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>RoR2 damage one ULTRAKILL damage unit does: the body's damage stat times the configured coefficient.</summary>
        public static float UnitDamage(CharacterBody v1) => Mathf.Max(0.01f, v1.damage * Plugin.DamageCoefficient.Value);

        public static void Hook()
        {
            if (hooked) return;
            hooked = true;
            GlobalEventManager.onCharacterDeathGlobal += report =>
            {
                var v1 = V1Drive.Body;
                if (v1 == null || report.victimBody == null || report.attackerBody != v1) return;
                Killed(report.victimBody);
            };
        }

        /// <summary>That monster died to this game's V1: ULTRAKILL counts the kill. (For a V1 that joined someone else's
        /// game the host says so: V1Net.)</summary>
        public static void Killed(CharacterBody victim)
        {
            KillsByV1++;
            UkLink.Send(Proto.Kill + " " + victim.GetInstanceID());
        }

        public static bool V1Wet;
        /// <summary>The hits V1 landed lately, oldest first: "id hitter multiplier".</summary>
        public static readonly List<string> HitLog = new List<string>();

        /// <summary>Feet in Risk of Rain 2's water: something on its Water layer at ankle height.</summary>
        /// <summary>For tests: every monster counts as standing in water.</summary>
        public static bool ForceMonstersWet;

        public static bool InWater(Vector3 feet, bool monster = false)
        {
            if (monster && ForceMonstersWet) return true;
            return Physics.CheckSphere(feet + Vector3.up * 0.25f, 0.3f, LayerIndex.water.mask, QueryTriggerInteraction.Collide);
        }

        static BodyIndex newt = BodyIndex.None;
        static bool newtLooked;

        /// <summary>The Bazaar's shopkeeper, who is on no monster team.</summary>
        public static bool IsNewt(CharacterBody b)
        {
            if (!newtLooked)
            {
                newtLooked = true;
                newt = BodyCatalog.FindBodyIndex("ShopkeeperBody");
            }
            return newt != BodyIndex.None && b.bodyIndex == newt;
        }

        /// <summary>Every frame of a bridged run: where every monster is.</summary>
        public static void Tick(CharacterBody v1)
        {
            sb.Length = 0;
            sb.Append(Proto.Ents).Append(' ');
            byId.Clear();
            heights.Clear();
            float unit = UnitDamage(v1);
            // only the host can move a monster: for a V1 that joined, every monster is one the Whiplash takes V1 to
            bool host = UnityEngine.Networking.NetworkServer.active;
            var team = v1.teamComponent != null ? v1.teamComponent.teamIndex : TeamIndex.Player;
            foreach (var b in CharacterBody.readOnlyInstancesList)
            {
                if (b == null || b == v1 || b.healthComponent == null || !b.healthComponent.alive || b.teamComponent == null) continue;
                var t = b.teamComponent.teamIndex;
                if (t == team) continue;
                // bystanders are left alone, all but the Newt: any survivor can pick a fight with him in the Bazaar
                if ((t == TeamIndex.None || t == TeamIndex.Neutral) && !IsNewt(b)) continue;
                float w, h;
                var cap = b.GetComponent<CapsuleCollider>();
                if (cap != null) { w = cap.radius * 2f; h = Mathf.Max(cap.height, w); }
                else { w = Mathf.Max(b.radius, 0.5f) * 2f; h = w; }
                // a little generous: RoR2's hurt boxes reach past the capsule the monster walks with
                w *= 1.15f;
                Vector3 feet = V1Drive.ToUk(b.corePosition - Vector3.up * (h * 0.5f));
                int id = b.GetInstanceID();
                byId[id] = b;
                sb.Append(id).Append(',').Append(S(feet.x)).Append(',').Append(S(feet.y)).Append(',').Append(S(feet.z)).Append(',')
                  .Append(S(w * V1Drive.K)).Append(',').Append(S(h * V1Drive.K)).Append(',').Append(S(b.healthComponent.combinedHealth / unit)).Append(',').Append(Light(b) && host ? 1 : 0).Append(',').Append(InWater(b.corePosition - Vector3.up * (h * 0.5f), true) ? 1 : 0).Append(';');
                heights[id] = h;
                if (Segmented(b)) segmentedNow.Add(b);
            }
            Listed = byId.Count;
            UkLink.Send(sb.ToString());
            foreach (var b in segmentedNow) SendParts(b);
            segmentedNow.Clear();
            bool wet = InWater(v1.footPosition);
            if (wet != V1Wet)
            {
                V1Wet = wet;
                UkLink.Send(Proto.Wet + " " + (wet ? 1 : 0));
            }
        }

        static readonly Dictionary<BodyIndex, bool> segmentedKinds = new Dictionary<BodyIndex, bool>();
        static readonly List<CharacterBody> segmentedNow = new List<CharacterBody>();
        /// <summary>Hurt boxes listed for the last segmented monster, for the tests.</summary>
        public static int PartsListed;

        /// <summary>A monster that is a chain of parts with no one shape (the worms): its capsule says nothing about where
        /// it can be hit, so ULTRAKILL is given its hurt boxes themselves.</summary>
        public static bool Segmented(CharacterBody b)
        {
            if (!segmentedKinds.TryGetValue(b.bodyIndex, out bool yes))
            {
                string name = BodyCatalog.GetBodyName(b.bodyIndex);
                yes = name == "MagmaWormBody" || name == "ElectricWormBody";
                segmentedKinds[b.bodyIndex] = yes;
            }
            return yes;
        }

        /// <summary>PARTS id cx,cy,cz,sx,sy,sz;...: where each of that monster's hurt boxes is right now, and how big.</summary>
        static void SendParts(CharacterBody b)
        {
            sb.Length = 0;
            sb.Append(Proto.Parts).Append(' ').Append(b.GetInstanceID()).Append(' ');
            int n = 0;
            var group = b.hurtBoxGroup;
            if (group != null && group.hurtBoxes != null)
            {
                foreach (var hb in group.hurtBoxes)
                {
                    if (hb == null || hb.collider == null || !hb.collider.enabled || !hb.gameObject.activeInHierarchy) continue;
                    var bounds = hb.collider.bounds;
                    Vector3 c = V1Drive.ToUk(bounds.center), s = bounds.size * V1Drive.K;
                    sb.Append(S(c.x)).Append(',').Append(S(c.y)).Append(',').Append(S(c.z)).Append(',').Append(S(s.x)).Append(',').Append(S(s.y)).Append(',').Append(S(s.z)).Append(';');
                    n++;
                }
            }
            PartsListed = n;
            UkLink.Send(sb.ToString());
        }

        static readonly Dictionary<int, float> heights = new Dictionary<int, float>();
        static readonly Dictionary<int, float> stunnedAt = new Dictionary<int, float>();
        public static int PullsApplied;
        /// <summary>Above this mass (kg) the Whiplash takes V1 to the monster instead of the monster to V1.</summary>
        public const float LightMass = 250f;

        /// <summary>The Whiplash pulls it to V1: not a boss or a champion, and not heavy.</summary>
        public static bool Light(CharacterBody b)
        {
            if (b.isChampion || b.isBoss) return false;
            float mass = b.characterMotor != null ? b.characterMotor.mass : b.rigidbody != null ? b.rigidbody.mass : 9999f;
            return mass <= LightMass;
        }

        /// <summary>PULL id x y z: the Whiplash has that monster; it goes where ULTRAKILL has dragged its stand-in, helpless.</summary>
        static void Pulled(string[] a, bool end)
        {
            if (a.Length < 4 || !byId.TryGetValue(int.Parse(a[0]), out var b) || b == null) return;
            var v = new Vector3(F(a[1]), F(a[2]), F(a[3]));
            if (end)
            {
                // let go: it keeps the speed it was dragged at
                if (b.characterMotor != null) b.characterMotor.velocity = v / V1Drive.K;
                else if (b.rigidbody != null) b.rigidbody.velocity = v / V1Drive.K;
                return;
            }
            heights.TryGetValue(int.Parse(a[0]), out float h);
            Vector3 delta = V1Drive.ToRor(v) + Vector3.up * (h * 0.5f) - b.corePosition;
            if (b.characterMotor != null)
            {
                b.characterMotor.velocity = Vector3.zero;
                b.characterMotor.Motor.SetPosition(b.characterMotor.Motor.TransientPosition + delta);
            }
            else
            {
                b.transform.position += delta;
                if (b.rigidbody != null) { b.rigidbody.position = b.transform.position; b.rigidbody.velocity = Vector3.zero; }
            }
            PullsApplied++;
            int id = b.GetInstanceID();
            if (!stunnedAt.TryGetValue(id, out float at) || Time.time - at > 0.25f)
            {
                stunnedAt[id] = Time.time;
                var hurt = b.GetComponent<SetStateOnHurt>();
                if (hurt != null && hurt.canBeStunned) hurt.SetStun(0.5f);
            }
        }

        public static int KnocksApplied;
        /// <summary>The push of a rocket's blast, as Risk of Rain 2 force: a 100 kg monster leaves at this many m/s; heavier
        /// ones in proportion less (RoR2 divides force by mass), so a boss barely shifts.</summary>
        public const float KnockSpeed = 16f;

        /// <summary>KNOCK id dx dy dz strength: an ULTRAKILL blast reached that monster. Risk of Rain 2 throws it, with
        /// its own physics: off the ground, no steering until it lands.</summary>
        static void Knocked(string[] a)
        {
            if (a.Length < 5 || !byId.TryGetValue(int.Parse(a[0]), out var b) || b == null || b.healthComponent == null || !b.healthComponent.alive) return;
            var dir = new Vector3(F(a[1]), F(a[2]), F(a[3]));
            float strength = Mathf.Clamp(F(a[4]), 0.1f, 3f);
            b.healthComponent.TakeDamageForce(dir * (KnockSpeed * 100f * strength), true, true);
            KnocksApplied++;
        }

        static readonly Dictionary<int, TemporaryOverlayInstance> oil = new Dictionary<int, TemporaryOverlayInstance>();
        public static int Oiled => oil.Count;

        /// <summary>OILED id amount: ULTRAKILL's gasoline is on that monster. Risk of Rain 2 draws the monster, so the
        /// soaking is shown here: its own dark goo overlay, for as long as ULTRAKILL says the fuel is there.</summary>
        static void SetOiled(string[] a)
        {
            if (a.Length < 2) return;
            int id = int.Parse(a[0]);
            bool on = a[1] != "0";
            if (oil.TryGetValue(id, out var had))
            {
                if (on) return;
                try { had.RemoveFromCharacterModel(); had.Destroy(); } catch { }
                oil.Remove(id);
                return;
            }
            if (!on || !byId.TryGetValue(id, out var b) || b == null || b.modelLocator == null || b.modelLocator.modelTransform == null) return;
            var model = b.modelLocator.modelTransform.GetComponent<CharacterModel>();
            var mat = CharacterModel.TankerAccelerantGoo != null ? CharacterModel.TankerAccelerantGoo : CharacterModel.clayGooMaterial;
            if (model == null || mat == null) return;
            var overlay = TemporaryOverlayManager.AddOverlay(model.gameObject);
            overlay.originalMaterial = mat;
            overlay.animateShaderAlpha = false;
            overlay.duration = 100000f;
            overlay.destroyComponentOnEnd = true;
            overlay.AddToCharacterModel(model);
            oil[id] = overlay;
        }

        public static void OnMessage(string cmd, string rest)
        {
            if (cmd == Proto.Oiled) { SetOiled(rest.Split(' ')); return; }
            // throwing and dragging a monster are the host's to do: not carried over yet for a V1 that joined
            bool host = UnityEngine.Networking.NetworkServer.active;
            if (cmd == Proto.Knock) { if (host) Knocked(rest.Split(' ')); return; }
            if (cmd == Proto.Pull || cmd == Proto.PullEnd) { if (host) Pulled(rest.Split(' '), cmd == Proto.PullEnd); return; }
            if (cmd != Proto.Damage) return;
            var v1 = V1Drive.Body;
            var a = rest.Split(' ');
            if (v1 == null || a.Length < 8 || !byId.TryGetValue(int.Parse(a[0]), out var victim) || victim == null || victim.healthComponent == null || !victim.healthComponent.alive) return;
            // a punch that meets this monster's own hit is a parry: Parry deals the return
            if (host && a[4] == "punch" && Parry.Punched(int.Parse(a[0]), victim, v1))
            {
                HitsApplied++;
                LastHit = "parry";
                return;
            }
            if (HitLog.Count > 400) HitLog.RemoveRange(0, 200);
            HitLog.Add(a[0] + " " + a[4] + " " + a[1]);
            float mult = F(a[1]);
            bool head = a[2] == "1", explosion = a[3] == "1";
            LastHitAt = V1Drive.ToRor(new Vector3(F(a[5]), F(a[6]), F(a[7])));
            LastHitOn = victim;
            HitsApplied++;
            if (host) LastHit = Hit(v1, victim, mult, head, explosion, a[4], LastHitAt);
            else
            {
                // V1 joined someone else's game: the hit is the host's to deal
                V1Net.ClientHit(victim, mult, head, explosion, a[4], LastHitAt);
                LastHit = "x" + S(mult) + " " + a[4] + (head ? " head" : "") + ": sent to the host";
            }
        }

        /// <summary>One hit of V1's on a monster, dealt (on the host) as real Risk of Rain 2 damage from that V1's body.</summary>
        public static string Hit(CharacterBody v1, CharacterBody victim, float mult, bool head, bool explosion, string hitter, Vector3 at)
        {
            var info = new DamageInfo
            {
                attacker = v1.gameObject,
                inflictor = v1.gameObject,
                damage = mult * UnitDamage(v1),
                crit = v1.RollCrit(),
                position = at,
                force = Vector3.zero,
                // weak hits (a nail, a pellet) proc less, like RoR2's own rapid weapons
                procCoefficient = Mathf.Clamp(mult, 0.1f, 1f),
                damageColorIndex = DamageColorIndex.Default,
                damageType = explosion ? DamageType.AOE : DamageType.Generic
            };
            float before = victim.healthComponent.combinedHealth;
            victim.healthComponent.TakeDamage(info);
            if (!info.rejected && GlobalEventManager.instance != null)
            {
                GlobalEventManager.instance.OnHitEnemy(info, victim.gameObject);
                GlobalEventManager.instance.OnHitAll(info, victim.gameObject);
            }
            return "x" + S(mult) + " " + hitter + (head ? " head" : "") + (info.crit ? " crit" : "") + ": " + S(info.damage) + " sent, " + S(before - Mathf.Max(0f, victim.healthComponent.combinedHealth)) + " taken";
        }
    }
    /// <summary>On V1's body: what RoR2's damage does to its health is ULTRAKILL's to decide. A hit can never kill on
    /// RoR2's side; the health it took is handed to ULTRAKILL and put back, and ULTRAKILL's answer (its HP) is what the
    /// body's health then shows. It must be on the prefab: the health component finds its receivers when it wakes.</summary>
    public class V1HealthLink : MonoBehaviour, IOnIncomingDamageServerReceiver, IOnTakeDamageServerReceiver
    {
        HealthComponent hc;
        float before, shieldBefore, barrierBefore;
        DamageInfo incoming;
        public static int HurtsSent;
        public static float LastHurt;

        // ---- a V1 whose player joined this game (this game is the host): its ULTRAKILL is on the player's PC, and
        // tells the host its HP; the host tells it what Risk of Rain 2 did to it (V1Net)
        /// <summary>The joining player this body's V1 belongs to, while their bridge has hold of it. Null: nobody's, or the host's own.</summary>
        public UnityEngine.Networking.NetworkConnection Remote { get; private set; }
        public int RemoteHp { get; private set; } = 100;
        public bool RemoteDead { get; private set; }
        bool remoteDying;
        readonly HealthMirror remoteMirror = new HealthMirror();
        /// <summary>Health components of the V1s joining players are driving, for the patches that have only that to go by.</summary>
        public static readonly Dictionary<HealthComponent, V1HealthLink> Remotes = new Dictionary<HealthComponent, V1HealthLink>();

        void Awake() => hc = GetComponent<HealthComponent>();

        void OnDestroy()
        {
            if (hc != null) Remotes.Remove(hc);
        }

        /// <summary>The joining player's bridge took hold of this body (or let go: null).</summary>
        public void SetRemote(UnityEngine.Networking.NetworkConnection conn)
        {
            Remote = conn;
            if (hc == null) return;
            if (conn != null)
            {
                Remotes[hc] = this;
                remoteMirror.Reset();
                RemoteDead = false;
            }
            else Remotes.Remove(hc);
        }

        public void SetRemoteHp(int hp, bool dead)
        {
            RemoteHp = hp;
            RemoteDead = dead;
        }

        /// <summary>The host, every frame: the joining V1's HP is this body's health, and its death this body's.</summary>
        void LateUpdate()
        {
            if (Remote == null || hc == null || !hc.alive || !UnityEngine.Networking.NetworkServer.active) return;
            if (!Remote.isConnected) { SetRemote(null); return; }
            if (RemoteDead)
            {
                Plugin.Log.LogInfo("a joining V1 died in its ULTRAKILL: its body dies here");
                remoteDying = true;
                try { hc.Suicide(); }
                finally { remoteDying = false; }
                return;
            }
            remoteMirror.Apply(hc, RemoteHp, line => V1Net.ToClient(Remote, line));
        }

        bool Linked => (V1Drive.Linked(hc) && !V1Drive.Dying) || (Remote != null && !remoteDying);
        public void OnIncomingDamageServer(DamageInfo info)
        {
            if (!Linked) return;
            before = hc.health;
            shieldBefore = hc.shield;
            barrierBefore = hc.barrier;
            incoming = info;
            info.damageType |= DamageType.NonLethal;
        }

        public void OnTakeDamageServer(DamageReport report)
        {
            if (!Linked) return;
            // health and shield are one pool to V1 (see Pool): what either lost is what the hit cost
            float lost = before + shieldBefore - hc.health - hc.shield;
            // The hit could not kill here (NonLethal), so RoR2 stopped the body's health at 1 and what the hit had left
            // over went unmeasured: a V1 whose share of the body's health is 1 or less (1 HP of ULTRAKILL's on a body of
            // under 100) lost nothing at all, and could not die. Where the health was stopped, the hit counts for what
            // RoR2 worked it out to be, less what the barrier took.
            if (hc.health <= 1.001f && report != null) lost = Mathf.Max(lost, report.damageDealt - Mathf.Max(0f, barrierBefore - hc.barrier));
            if (lost <= 0f) return;
            hc.Networkhealth = before;
            hc.Networkshield = shieldBefore;
            LastHurt = lost / Pool(hc) * 100f * Plugin.IncomingDamage.Value;
            if (Remote != null)
            {
                // a joining player's V1: their ULTRAKILL decides (no parry window yet: the hit lands at once)
                HurtsSent++;
                V1Net.ToClient(Remote, Proto.Hurt + " " + LastHurt.ToString("0.##", CultureInfo.InvariantCulture));
                return;
            }
            // a monster's swing waits a moment for V1's fist; everything else lands at once
            if (incoming != null && Parry.Hold(incoming, hc.body, LastHurt)) return;
            Send(LastHurt);
        }
        /// <summary>What ULTRAKILL's 100 HP stands for on the body: its health and its shield together. A shield of
        /// RoR2's own would soak hits up out of ULTRAKILL's sight (nothing of it shows in ULTRAKILL's HUD) and fill
        /// itself again; with Transcendence, which turns all health into shield, V1 would take nothing at all until the
        /// one hit that killed it. So a shield is more health to V1: every hit costs its share of the two.</summary>
        public static float Pool(HealthComponent hc) => Mathf.Max(1f, hc.fullHealth + hc.fullShield);

        public static void Send(float share)
        {
            HurtsSent++;
            UkLink.Send(Proto.Hurt + " " + share.ToString("0.##", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Risk of Rain 2's healing (regeneration, items, shrines) reaches V1 as ULTRAKILL health.</summary>
    [HarmonyPatch(typeof(HealthComponent), nameof(HealthComponent.Heal))]
    static class HealsReachV1
    {
        public static float Sent;
        /// <summary>Set while the bridge itself changes the body's health figures: that is not healing.</summary>
        public static bool Muted;

        static void Postfix(HealthComponent __instance, float __result)
        {
            if (__result <= 0f || Muted) return;
            V1HealthLink remote = null;
            if (!V1Drive.Linked(__instance) && (V1HealthLink.Remotes.Count == 0 || !V1HealthLink.Remotes.TryGetValue(__instance, out remote))) return;
            float x = __result / V1HealthLink.Pool(__instance) * 100f;
            Sent += x;
            string line = Proto.Heal + " " + x.ToString("0.###", CultureInfo.InvariantCulture);
            if (remote != null) V1Net.ToClient(remote.Remote, line);
            else UkLink.Send(line);
        }
    }
    /// <summary>Risk of Rain 2 tops a body up by whatever its maximum health grew (a level, an item, a curse lifted). For
    /// V1 the body's health is only a picture of ULTRAKILL's HP: a bigger maximum is not healing, and is not forwarded.</summary>
    [HarmonyPatch(typeof(CharacterBody), nameof(CharacterBody.RecalculateStats))]
    static class GrowthIsNotHealing
    {
        static void Prefix(CharacterBody __instance, out bool __state)
        {
            __state = HealsReachV1.Muted;
            var hc = __instance.healthComponent;
            if (V1Drive.Linked(hc) || (V1HealthLink.Remotes.Count > 0 && hc != null && V1HealthLink.Remotes.ContainsKey(hc))) HealsReachV1.Muted = true;
        }

        static void Finalizer(bool __state)
        {
            HealsReachV1.Muted = __state;
        }
    }
}