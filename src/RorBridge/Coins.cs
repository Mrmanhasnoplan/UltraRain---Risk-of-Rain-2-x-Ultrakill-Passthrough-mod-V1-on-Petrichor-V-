using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace V1Bridge.Ror
{
    /// <summary>V1's coins and Risk of Rain 2's hitscan. In ULTRAKILL an enemy's hitscan shot that meets one of V1's coins
    /// is taken by the coin, which then does ULTRAKILL's own thing with it. Risk of Rain 2's monsters shoot with
    /// BulletAttack: ULTRAKILL says where the coins hang (COINS), and a monster's bullet whose line runs into one before
    /// it reaches the stage or V1 stops there, and ULTRAKILL is told which coin it was (COINHIT).</summary>
    /// <summary>Risk of Rain 2 hangs a visual on a body for many of its buffs and items (a bubble round it for a shield or
    /// armor, a glow, a ring). On V1 they are seen from the inside, and by the other players on a body that is not
    /// drawn as one: V1's body never gets them. The buffs and items themselves are untouched.</summary>
    internal static class NoBodyEffects
    {
        public static string Problem;
        public static int Patched, Refused;

        public static void Apply(Harmony h)
        {
            try
            {
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(CharacterBody)))
                {
                    if (m.Name != "UpdateSingleTemporaryVisualEffect") continue;
                    h.Patch(m, prefix: new HarmonyMethod(typeof(NoBodyEffects), nameof(Before)));
                    Patched++;
                }
                if (Patched == 0) Problem = "CharacterBody.UpdateSingleTemporaryVisualEffect not found";
            }
            catch (Exception e) { Problem = e.Message; }
            if (Problem != null) Plugin.Log.LogError("no effects on V1's body: " + Problem);
        }

        static void Before(CharacterBody __instance, ref bool active)
        {
            if (!active || __instance.bodyIndex != V1Survivor.BodyIndex || V1Survivor.BodyIndex == BodyIndex.None) return;
            active = false;
            Refused++;
        }
    }

    internal static class Coins
    {
        struct Coin { public int id; public Vector3 at; }
        static readonly List<Coin> coins = new List<Coin>();
        static float listedAt = -10f;
        /// <summary>How near a bullet's line has to pass (metres, on top of the bullet's own radius): about the reach
        /// ULTRAKILL gives a coin against its own enemies' shots.</summary>
        public const float Reach = 0.75f;
        public static int Listed => Time.time - listedAt < 0.5f ? coins.Count : 0;
        public static int Intercepted;
        public static string Problem;

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        public static void OnMessage(string cmd, string rest)
        {
            if (cmd == "DISCONNECTED") { coins.Clear(); return; }
            if (cmd != Proto.Coins) return;
            coins.Clear();
            listedAt = Time.time;
            foreach (var entry in rest.Split(';'))
            {
                if (entry.Length == 0) continue;
                var a = entry.Split(',');
                if (a.Length < 4) continue;
                coins.Add(new Coin { id = int.Parse(a[0]), at = V1Drive.ToRor(new Vector3(F(a[1]), F(a[2]), F(a[3]))) });
            }
        }

        /// <summary>Applied by hand, each on its own: a Risk of Rain 2 update that renames these must not take the rest of
        /// the plugin's patches with it.</summary>
        public static void Apply(Harmony h)
        {
            try
            {
                var cast = AccessTools.Method(typeof(BulletAttack), "BulletHitsFromRaycast");
                var single = AccessTools.Method(typeof(BulletAttack), "FireSingle");
                if (cast == null || single == null) { Problem = "BulletAttack." + (cast == null ? "BulletHitsFromRaycast" : "FireSingle") + " not found"; }
                else
                {
                    h.Patch(cast, prefix: new HarmonyMethod(typeof(Coins), nameof(BeforeCast)));
                    h.Patch(single, postfix: new HarmonyMethod(typeof(Coins), nameof(AfterSingle)));
                }
            }
            catch (Exception e) { Problem = e.Message; }
            if (Problem != null) Plugin.Log.LogError("coins against hitscan: " + Problem);
        }

        static BulletAttack shortened;
        static float fullDistance;

        /// <summary>One bullet is about to be cast along <paramref name="ray"/>: if a coin is in its way, that is as far as it goes.</summary>
        static void BeforeCast(BulletAttack __instance, Ray ray)
        {
            try
            {
                if (coins.Count == 0 || Time.time - listedAt > 0.5f) return;
                var v1 = V1Drive.Body;
                if (v1 == null || __instance.owner == null || __instance.owner == v1.gameObject) return;
                // a monster's shot: not V1's side's
                var team = __instance.owner.GetComponent<TeamComponent>();
                if (team == null || v1.teamComponent == null || team.teamIndex == v1.teamComponent.teamIndex) return;
                Vector3 from = ray.origin, dir = ray.direction.normalized;
                float max = __instance.maxDistance, reach = Mathf.Max(0f, __instance.radius) + Reach;
                int best = -1;
                float bestAlong = max;
                for (int i = 0; i < coins.Count; i++)
                {
                    float along = Vector3.Dot(coins[i].at - from, dir);
                    if (along <= 0.1f || along >= bestAlong) continue;
                    if ((from + dir * along - coins[i].at).sqrMagnitude > reach * reach) continue;
                    best = i;
                    bestAlong = along;
                }
                if (best < 0) return;
                // V1 standing between the gun and the coin is hit first
                float v1Along = Vector3.Dot(v1.corePosition - from, dir);
                if (v1Along > 0f && v1Along < bestAlong && (from + dir * v1Along - v1.corePosition).magnitude < Mathf.Max(v1.radius, 0.5f) + __instance.radius) return;
                // and so is the stage
                if (Physics.Raycast(from, dir, bestAlong, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) return;
                int id = coins[best].id;
                // a coin takes one shot
                coins.RemoveAt(best);
                shortened = __instance;
                fullDistance = max;
                __instance.maxDistance = bestAlong;
                Intercepted++;
                UkLink.Send(Proto.CoinHit + " " + id);
            }
            catch (Exception e) { Problem = e.Message; }
        }

        /// <summary>The bullet is done: the attack has its own reach back for the bullets after it.</summary>
        static void AfterSingle(BulletAttack __instance)
        {
            if (!ReferenceEquals(shortened, __instance)) return;
            __instance.maxDistance = fullDistance;
            shortened = null;
        }
    }
}
