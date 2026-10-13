using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RoR2;
using RoR2.Projectile;
using UnityEngine;
using UnityEngine.Networking;

namespace V1Bridge.Ror
{
    /// <summary>The hits on one V1 that are waiting to see whether its fist parries them, on the game that works out
    /// damage (the host). One for the host's own V1, one for each joining player's V1 (V1HealthLink.RemoteParry).</summary>
    internal sealed class ParryState
    {
        struct Held { public int attacker; public float share, due; public bool shot; public CharacterBody owner; }
        readonly List<Held> held = new List<Held>();
        readonly Dictionary<int, float> punchedAt = new Dictionary<int, float>();
        readonly Action<float> hurt;
        readonly Action<CharacterBody> parried;
        /// <summary>Added to every window: a joining player's punch takes a moment to reach the host, and they see the
        /// monster's swing a moment after the host has worked it out.</summary>
        readonly float slack;

        /// <param name="hurt">An unparried hit lands: that share of V1's 100, for its ULTRAKILL.</param>
        /// <param name="parried">V1's punch met that monster's hit: its ULTRAKILL plays the parry.</param>
        public ParryState(Action<float> hurt, Action<CharacterBody> parried, float slack = 0f)
        {
            this.hurt = hurt;
            this.parried = parried;
            this.slack = slack;
        }

        public void Reset()
        {
            held.Clear();
            punchedAt.Clear();
        }

        /// <summary>RoR2 took <paramref name="share"/> (of 100) off V1. True: it is held for a parry and will be sent,
        /// or cancelled, later.</summary>
        public bool Hold(DamageInfo info, CharacterBody v1, float share)
        {
            var attacker = info.attacker != null ? info.attacker.GetComponent<CharacterBody>() : null;
            if (attacker == null || info.dotIndex != DotController.DotIndex.None) return false;
            if (info.inflictor != null && info.inflictor != info.attacker)
            {
                // a projectile: held briefly in case the fist is catching its copy in ULTRAKILL right now
                if (info.inflictor.GetComponent<ProjectileController>() == null) return false;
                held.Add(new Held { attacker = attacker.GetInstanceID(), share = share, due = Time.time + Parry.ShotWindow + slack, shot = true, owner = attacker });
                return true;
            }
            // a monster's own body swinging at V1
            if (Vector3.Distance(attacker.corePosition, v1.corePosition) > Parry.MeleeReach + attacker.radius) return false;
            int id = attacker.GetInstanceID();
            if (punchedAt.TryGetValue(id, out float at) && Time.time - at <= Parry.EarlyPunch + slack)
            {
                Parried(attacker, v1);
                return true;
            }
            held.Add(new Held { attacker = id, share = share, due = Time.time + Parry.Window + slack });
            return true;
        }

        /// <summary>V1's fist landed on that monster. True: it met a held hit, a parry.</summary>
        public bool Punched(CharacterBody attacker, CharacterBody v1)
        {
            int id = attacker.GetInstanceID();
            punchedAt[id] = Time.time;
            bool any = false;
            for (int i = held.Count - 1; i >= 0; i--)
            {
                if (held[i].attacker != id || held[i].shot) continue;
                held.RemoveAt(i);
                any = true;
            }
            if (any) Parried(attacker, v1);
            return any;
        }

        void Parried(CharacterBody attacker, CharacterBody v1)
        {
            Parry.MeleeParries++;
            parried(attacker);
            Parry.Return(attacker, v1);
        }

        /// <summary>Held hits that nobody parried land.</summary>
        public void Flush()
        {
            for (int i = held.Count - 1; i >= 0; i--)
            {
                if (Time.time < held[i].due) continue;
                float share = held[i].share;
                held.RemoveAt(i);
                hurt(share);
            }
        }

        /// <summary>The fist caught a projectile that has already landed here: the hit it left waiting is the one caught.</summary>
        public bool CaughtLanded(CharacterBody v1)
        {
            for (int i = held.Count - 1; i >= 0; i--)
            {
                if (!held[i].shot) continue;
                var from = held[i].owner;
                held.RemoveAt(i);
                Parry.ShotParries++;
                Parry.Return(from, v1);
                return true;
            }
            return false;
        }

        /// <summary>ULTRAKILL blew up a projectile that had just landed here: the hit it left waiting goes with it.</summary>
        public void BlownLanded()
        {
            for (int i = held.Count - 1; i >= 0; i--)
            {
                if (!held[i].shot) continue;
                held.RemoveAt(i);
                break;
            }
        }
    }

    /// <summary>Parrying Risk of Rain 2's attacks.
    /// Projectiles: every hostile one in flight is listed to ULTRAKILL, which decides whether V1's fist caught it; a
    /// caught one is removed (by the host) and its owner takes the return.
    /// Melee: a hit from a monster within reach is held for a moment before it becomes ULTRAKILL damage; a punch on
    /// that monster in the window (or just before the hit) cancels it and is a parry.
    /// For a V1 that joined someone else's game the listing is its own game's, and the rest is the host's (V1Net).</summary>
    internal static class Parry
    {
        /// <summary>How long a melee hit waits for a punch, and how long before a hit a punch still counts.</summary>
        public const float Window = 0.2f, EarlyPunch = 0.25f;
        /// <summary>Monsters this close (metres) are hitting V1 with their bodies, not their shots.</summary>
        public const float MeleeReach = 7f;
        /// <summary>ULTRAKILL damage units a parry returns.</summary>
        public const float ParryUnits = 5f;
        /// <summary>How long a projectile's hit waits: its copy in ULTRAKILL trails it by a frame or two, so the fist may
        /// catch it there just after it has landed here.</summary>
        public const float ShotWindow = 0.12f;
        /// <summary>What a joining player's windows are longer by (ParryState).</summary>
        public const float JoinedSlack = 0.15f;

        /// <summary>The host's own V1.</summary>
        static readonly ParryState local = new ParryState(V1HealthLink.Send, attacker => UkLink.Send(Proto.Parried + " " + attacker.GetInstanceID()));
        static readonly Dictionary<int, ProjectileController> shots = new Dictionary<int, ProjectileController>();
        static readonly StringBuilder sb = new StringBuilder(512);
        public static int MeleeParries, ShotParries, ShotsListed, ShotsBlown;

        static string S(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        public static void Reset()
        {
            local.Reset();
            shots.Clear();
        }

        public static bool Hold(DamageInfo info, CharacterBody v1, float share) => local.Hold(info, v1, share);

        public static bool Punched(int id, CharacterBody attacker, CharacterBody v1) => local.Punched(attacker, v1);

        /// <summary>What a parry gives back to the monster, as real Risk of Rain 2 damage from that V1 (the host's to deal).</summary>
        internal static void Return(CharacterBody to, CharacterBody v1)
        {
            if (to == null || v1 == null || to.healthComponent == null || !to.healthComponent.alive) return;
            var info = new DamageInfo
            {
                attacker = v1.gameObject, inflictor = v1.gameObject, damage = ParryUnits * Monsters.UnitDamage(v1), crit = v1.RollCrit(),
                position = to.corePosition, procCoefficient = 1f, damageType = DamageType.Stun1s
            };
            to.healthComponent.TakeDamage(info);
            if (!info.rejected && GlobalEventManager.instance != null) GlobalEventManager.instance.OnHitEnemy(info, to.gameObject);
        }

        /// <summary>Every frame of a bridged run: held hits that nobody parried land (the host's V1), and the projectiles in
        /// flight are listed to this game's ULTRAKILL.</summary>
        public static void Tick(CharacterBody v1)
        {
            if (NetworkServer.active) local.Flush();
            sb.Length = 0;
            sb.Append(Proto.Projs).Append(' ');
            shots.Clear();
            var team = v1.teamComponent != null ? v1.teamComponent.teamIndex : TeamIndex.Player;
            foreach (var p in InstanceTracker.GetInstancesList<ProjectileController>())
            {
                if (p == null || p.teamFilter == null || p.teamFilter.teamIndex == team) continue;
                // only what could reach V1 soon
                if ((p.transform.position - v1.corePosition).sqrMagnitude > 80f * 80f) continue;
                int id = p.GetInstanceID();
                shots[id] = p;
                var u = V1Drive.ToUk(p.transform.position);
                sb.Append(id).Append(',').Append(S(u.x)).Append(',').Append(S(u.y)).Append(',').Append(S(u.z)).Append(",1;");
            }
            ShotsListed = shots.Count;
            UkLink.Send(sb.ToString());
        }

        /// <summary>For the tests: one of the projectiles listed to ULTRAKILL right now (0: none).</summary>
        public static int AnyShot()
        {
            foreach (var kv in shots) if (kv.Value != null) return kv.Key;
            return 0;
        }

        /// <summary>From this game's ULTRAKILL. PARRY p id: the fist caught that projectile; PARRY b id: a beam blew it up.</summary>
        public static void OnMessage(string cmd, string rest)
        {
            if (cmd != Proto.Parry) return;
            var a = rest.Split(' ');
            var v1 = V1Drive.Body;
            if (v1 == null || a.Length < 2 || (a[0] != "p" && a[0] != "b")) return;
            shots.TryGetValue(int.Parse(a[1]), out var p);
            if (!NetworkServer.active)
            {
                // V1 joined someone else's game: removing the projectile and the return are the host's
                var id = p != null ? p.GetComponent<NetworkIdentity>() : null;
                if (a[0] == "b") ShotsBlown++;
                else ShotParries++;
                V1Net.ClientParry(a[0], id != null ? id.netId.Value : 0u);
                return;
            }
            Caught(local, v1, a[0], p);
            if (p != null) shots.Remove(int.Parse(a[1]));
        }

        /// <summary>The host: that V1's ULTRAKILL caught (p) or blew up (b) that projectile; null: one that has just landed.</summary>
        public static void Caught(ParryState state, CharacterBody v1, string kind, ProjectileController p)
        {
            if (kind == "b")
            {
                // ULTRAKILL blew its copy up where it flew (the Sharpshooter's shot through it): the explosion and what
                // it does to the monsters round it are ULTRAKILL's; here the projectile is simply gone
                ShotsBlown++;
                if (p != null) UnityEngine.Object.Destroy(p.gameObject);
                else state.BlownLanded();
                return;
            }
            if (p == null)
            {
                state.CaughtLanded(v1);
                return;
            }
            ShotParries++;
            var owner = p.owner != null ? p.owner.GetComponent<CharacterBody>() : null;
            UnityEngine.Object.Destroy(p.gameObject);
            Return(owner, v1);
        }
    }
}
