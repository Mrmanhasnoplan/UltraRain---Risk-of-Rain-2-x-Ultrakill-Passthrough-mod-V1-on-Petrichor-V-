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
                KillsByV1++;
                UkLink.Send(Proto.Kill + " " + report.victimBody.GetInstanceID());
            };
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
                  .Append(S(w * V1Drive.K)).Append(',').Append(S(h * V1Drive.K)).Append(',').Append(S(b.healthComponent.combinedHealth / unit)).Append(',').Append(Light(b) ? 1 : 0).Append(',').Append(InWater(b.corePosition - Vector3.up * (h * 0.5f), true) ? 1 : 0).Append(';');
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
            if (cmd == Proto.Knock) { Knocked(rest.Split(' ')); return; }
            if (cmd == Proto.Pull || cmd == Proto.PullEnd) { Pulled(rest.Split(' '), cmd == Proto.PullEnd); return; }
            if (cmd != Proto.Damage) return;
            var v1 = V1Drive.Body;
            var a = rest.Split(' ');
            if (v1 == null || a.Length < 8 || !byId.TryGetValue(int.Parse(a[0]), out var victim) || victim == null || victim.healthComponent == null || !victim.healthComponent.alive) return;
            // a punch that meets this monster's own hit is a parry: Parry deals the return
            if (a[4] == "punch" && Parry.Punched(int.Parse(a[0]), victim, v1))
            {
                HitsApplied++;
                LastHit = "parry";
                return;
            }
            if (HitLog.Count > 400) HitLog.RemoveRange(0, 200);
            HitLog.Add(a[0] + " " + a[4] + " " + a[1]);
            float mult = F(a[1]);
            bool explosion = a[3] == "1";
            var info = new DamageInfo
            {
                attacker = v1.gameObject,
                inflictor = v1.gameObject,
                damage = mult * UnitDamage(v1),
                crit = v1.RollCrit(),
                position = V1Drive.ToRor(new Vector3(F(a[5]), F(a[6]), F(a[7]))),
                force = Vector3.zero,
                // weak hits (a nail, a pellet) proc less, like RoR2's own rapid weapons
                procCoefficient = Mathf.Clamp(mult, 0.1f, 1f),
                damageColorIndex = DamageColorIndex.Default,
                damageType = explosion ? DamageType.AOE : DamageType.Generic
            };
            LastHitAt = info.position;
            LastHitOn = victim;
            float before = victim.healthComponent.combinedHealth;
            victim.healthComponent.TakeDamage(info);
            if (!info.rejected && GlobalEventManager.instance != null)
            {
                GlobalEventManager.instance.OnHitEnemy(info, victim.gameObject);
                GlobalEventManager.instance.OnHitAll(info, victim.gameObject);
            }
            HitsApplied++;
            LastHit = "x" + S(mult) + " " + a[4] + (a[2] == "1" ? " head" : "") + (info.crit ? " crit" : "") + ": " + S(info.damage) + " sent, " + S(before - Mathf.Max(0f, victim.healthComponent.combinedHealth)) + " taken";
        }
    }

    /// <summary>On V1's body: what RoR2's damage does to its health is ULTRAKILL's to decide. A hit can never kill on
    /// RoR2's side; the health it took is handed to ULTRAKILL and put back, and ULTRAKILL's answer (its HP) is what the
    /// body's health then shows. It must be on the prefab: the health component finds its receivers when it wakes.</summary>
    public class V1HealthLink : MonoBehaviour, IOnIncomingDamageServerReceiver, IOnTakeDamageServerReceiver
    {
        HealthComponent hc;
        float before;
        DamageInfo incoming;
        public static int HurtsSent;
        public static float LastHurt;

        void Awake() => hc = GetComponent<HealthComponent>();

        bool Linked => V1Drive.Linked(hc) && !V1Drive.Dying;

        public void OnIncomingDamageServer(DamageInfo info)
        {
            if (!Linked) return;
            before = hc.health;
            incoming = info;
            info.damageType |= DamageType.NonLethal;
        }

        public void OnTakeDamageServer(DamageReport report)
        {
            if (!Linked) return;
            float lost = before - hc.health;
            if (lost <= 0f) return;
            hc.Networkhealth = before;
            LastHurt = lost / hc.fullHealth * 100f * Plugin.IncomingDamage.Value;
            // a monster's swing waits a moment for V1's fist; everything else lands at once
            if (incoming != null && Parry.Hold(incoming, hc.body, LastHurt)) return;
            Send(LastHurt);
        }

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
            if (__result <= 0f || Muted || !V1Drive.Linked(__instance)) return;
            float x = __result / __instance.fullHealth * 100f;
            Sent += x;
            UkLink.Send(Proto.Heal + " " + x.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }
}
