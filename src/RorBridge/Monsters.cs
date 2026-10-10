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

        /// <summary>Every frame of a bridged run: where every monster is.</summary>
        public static void Tick(CharacterBody v1)
        {
            sb.Length = 0;
            sb.Append(Proto.Ents).Append(' ');
            byId.Clear();
            float unit = UnitDamage(v1);
            var team = v1.teamComponent != null ? v1.teamComponent.teamIndex : TeamIndex.Player;
            foreach (var b in CharacterBody.readOnlyInstancesList)
            {
                if (b == null || b == v1 || b.healthComponent == null || !b.healthComponent.alive || b.teamComponent == null) continue;
                var t = b.teamComponent.teamIndex;
                if (t == team || t == TeamIndex.None || t == TeamIndex.Neutral) continue;
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
                  .Append(S(w * V1Drive.K)).Append(',').Append(S(h * V1Drive.K)).Append(',').Append(S(b.healthComponent.combinedHealth / unit)).Append(';');
            }
            Listed = byId.Count;
            UkLink.Send(sb.ToString());
        }

        public static void OnMessage(string cmd, string rest)
        {
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
            LastHurt = lost / hc.fullHealth * 100f;
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

        static void Postfix(HealthComponent __instance, float __result)
        {
            if (__result <= 0f || !V1Drive.Linked(__instance)) return;
            float x = __result / __instance.fullHealth * 100f;
            Sent += x;
            UkLink.Send(Proto.Heal + " " + x.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }
}
