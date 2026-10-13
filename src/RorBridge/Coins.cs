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

    /// <summary>One V1's coins in the air that a shot can still take, in Risk of Rain 2's coordinates, as last listed.</summary>
    internal sealed class CoinList
    {
        public struct Coin { public int id; public Vector3 at; }
        public readonly List<Coin> coins = new List<Coin>();
        float listedAt = -10f;
        public bool Fresh => coins.Count > 0 && Time.time - listedAt <= 0.5f;
        public int Count => Time.time - listedAt < 0.5f ? coins.Count : 0;

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>id,x,y,z;...: <paramref name="place"/> turns the listed position into Risk of Rain 2's (null: it is already).</summary>
        public void Read(string rest, Func<Vector3, Vector3> place)
        {
            coins.Clear();
            listedAt = Time.time;
            foreach (var entry in rest.Split(';'))
            {
                if (entry.Length == 0) continue;
                var a = entry.Split(',');
                if (a.Length < 4) continue;
                var at = new Vector3(F(a[1]), F(a[2]), F(a[3]));
                coins.Add(new Coin { id = int.Parse(a[0]), at = place != null ? place(at) : at });
            }
        }
    }

    internal static class Coins
    {
        /// <summary>The coins of this game's V1.</summary>
        static readonly CoinList own = new CoinList();
        /// <summary>How near a bullet's line has to pass (metres, on top of the bullet's own radius): about the reach
        /// ULTRAKILL gives a coin against its own enemies' shots.</summary>
        public const float Reach = 0.75f;
        public static int Listed => own.Count;
        public static int Intercepted;
        public static string Problem;
        static bool toldHost;

        static string S(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        public static void OnMessage(string cmd, string rest)
        {
            if (cmd == "DISCONNECTED") { own.coins.Clear(); return; }
            if (cmd != Proto.Coins) return;
            own.Read(rest, V1Drive.ToRor);
            if (UnityEngine.Networking.NetworkServer.active) return;
            // V1 joined someone else's game: the monsters' shots are worked out by the host, which has to know where
            // the coins hang (every time while there are any, and once more when the last is gone)
            if (own.coins.Count == 0 && !toldHost) return;
            toldHost = own.coins.Count > 0;
            var sb = new System.Text.StringBuilder(Proto.Coins).Append(' ');
            foreach (var c in own.coins) sb.Append(c.id).Append(',').Append(S(c.at.x)).Append(',').Append(S(c.at.y)).Append(',').Append(S(c.at.z)).Append(';');
            V1Net.ClientCoins(sb.ToString(), own.coins.Count == 0);
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

        /// <summary>One bullet is about to be cast along <paramref name="ray"/> (on the host, where monsters shoot): if a
        /// coin of a V1's is in its way, that is as far as it goes. The host's own V1 first, then each joining one.</summary>
        static void BeforeCast(BulletAttack __instance, Ray ray)
        {
            try
            {
                if (__instance.owner == null) return;
                var v1 = V1Drive.Body;
                if (v1 != null && own.Fresh && Take(__instance, ray, v1, own, id => UkLink.Send(Proto.CoinHit + " " + id))) return;
                if (V1HealthLink.Remotes.Count == 0) return;
                foreach (var link in V1HealthLink.Remotes.Values)
                {
                    if (link == null || link.Remote == null || !link.RemoteCoins.Fresh || link.Body == null) continue;
                    var conn = link.Remote;
                    if (Take(__instance, ray, link.Body, link.RemoteCoins, id => V1Net.ToClient(conn, Proto.CoinHit + " " + id))) return;
                }
            }
            catch (Exception e) { Problem = e.Message; }
        }

        /// <summary>True: the nearest of that V1's coins on the bullet's line took it, and that V1's ULTRAKILL was told which.</summary>
        static bool Take(BulletAttack bullet, Ray ray, CharacterBody v1, CoinList list, Action<int> tell)
        {
            if (bullet.owner == v1.gameObject) return false;
            // a monster's shot: not V1's side's
            var team = bullet.owner.GetComponent<TeamComponent>();
            if (team == null || v1.teamComponent == null || team.teamIndex == v1.teamComponent.teamIndex) return false;
            var coins = list.coins;
            Vector3 from = ray.origin, dir = ray.direction.normalized;
            float max = bullet.maxDistance, reach = Mathf.Max(0f, bullet.radius) + Reach;
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
            if (best < 0) return false;
            // V1 standing between the gun and the coin is hit first
            float v1Along = Vector3.Dot(v1.corePosition - from, dir);
            if (v1Along > 0f && v1Along < bestAlong && (from + dir * v1Along - v1.corePosition).magnitude < Mathf.Max(v1.radius, 0.5f) + bullet.radius) return false;
            // and so is the stage
            if (Physics.Raycast(from, dir, bestAlong, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) return false;
            int id = coins[best].id;
            // a coin takes one shot
            coins.RemoveAt(best);
            shortened = bullet;
            fullDistance = max;
            bullet.maxDistance = bestAlong;
            Intercepted++;
            tell(id);
            return true;
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