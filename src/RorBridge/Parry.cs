using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RoR2;
using RoR2.Projectile;
using UnityEngine;

namespace V1Bridge.Ror
{
    /// <summary>Parrying Risk of Rain 2's attacks.
    /// Projectiles: every hostile one in flight is listed to ULTRAKILL, which decides whether V1's fist caught it; a
    /// caught one is removed here and its owner takes the return.
    /// Melee: a hit from a monster within reach is held for a moment before it becomes ULTRAKILL damage; a punch on
    /// that monster in the window (or just before the hit) cancels it and is a parry.</summary>
    internal static class Parry
    {
        /// <summary>How long a melee hit waits for a punch, and how long before a hit a punch still counts.</summary>
        public const float Window = 0.2f, EarlyPunch = 0.25f;
        /// <summary>Monsters this close (metres) are hitting V1 with their bodies, not their shots.</summary>
        public const float MeleeReach = 7f;
        /// <summary>ULTRAKILL damage units a parry returns.</summary>
        public const float ParryUnits = 5f;

        struct Held { public int attacker; public float share, due; public bool shot; public CharacterBody owner; }
        /// <summary>How long a projectile's hit waits: its copy in ULTRAKILL trails it by a frame or two, so the fist may
        /// catch it there just after it has landed here.</summary>
        public const float ShotWindow = 0.12f;
        static readonly List<Held> held = new List<Held>();
        static readonly Dictionary<int, float> punchedAt = new Dictionary<int, float>();
        static readonly Dictionary<int, ProjectileController> shots = new Dictionary<int, ProjectileController>();
        static readonly StringBuilder sb = new StringBuilder(512);
        public static int MeleeParries, ShotParries, ShotsListed, ShotsBlown;

        static string S(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        public static void Reset()
        {
            held.Clear();
            punchedAt.Clear();
            shots.Clear();
        }

        /// <summary>RoR2 took <paramref name="share"/> (of 100) off V1. True: it is held for a parry and will be sent,
        /// or cancelled, later.</summary>
        public static bool Hold(DamageInfo info, CharacterBody v1, float share)
        {
            var attacker = info.attacker != null ? info.attacker.GetComponent<CharacterBody>() : null;
            if (attacker == null || info.dotIndex != DotController.DotIndex.None) return false;
            if (info.inflictor != null && info.inflictor != info.attacker)
            {
                // a projectile: held briefly in case the fist is catching its copy in ULTRAKILL right now
                if (info.inflictor.GetComponent<ProjectileController>() == null) return false;
                held.Add(new Held { attacker = attacker.GetInstanceID(), share = share, due = Time.time + ShotWindow, shot = true, owner = attacker });
                return true;
            }
            // a monster's own body swinging at V1
            if (Vector3.Distance(attacker.corePosition, v1.corePosition) > MeleeReach + attacker.radius) return false;
            int id = attacker.GetInstanceID();
            if (punchedAt.TryGetValue(id, out float at) && Time.time - at <= EarlyPunch)
            {
                Parried(id, attacker, v1);
                return true;
            }
            held.Add(new Held { attacker = id, share = share, due = Time.time + Window });
            return true;
        }

        /// <summary>V1's fist landed on that monster. True: it met a held hit, a parry.</summary>
        public static bool Punched(int id, CharacterBody attacker, CharacterBody v1)
        {
            punchedAt[id] = Time.time;
            bool any = false;
            for (int i = held.Count - 1; i >= 0; i--)
            {
                if (held[i].attacker != id || held[i].shot) continue;
                held.RemoveAt(i);
                any = true;
            }
            if (any) Parried(id, attacker, v1);
            return any;
        }

        static void Parried(int id, CharacterBody attacker, CharacterBody v1)
        {
            MeleeParries++;
            UkLink.Send(Proto.Parried + " " + id);
            Return(attacker, v1);
        }

        static void Return(CharacterBody to, CharacterBody v1)
        {
            if (to == null || to.healthComponent == null || !to.healthComponent.alive) return;
            var info = new DamageInfo
            {
                attacker = v1.gameObject, inflictor = v1.gameObject, damage = ParryUnits * Monsters.UnitDamage(v1), crit = v1.RollCrit(),
                position = to.corePosition, procCoefficient = 1f, damageType = DamageType.Stun1s
            };
            to.healthComponent.TakeDamage(info);
            if (!info.rejected && GlobalEventManager.instance != null) GlobalEventManager.instance.OnHitEnemy(info, to.gameObject);
        }

        /// <summary>Every frame of a bridged run: held hits that nobody parried land, and the projectiles in flight are listed.</summary>
        public static void Tick(CharacterBody v1)
        {
            for (int i = held.Count - 1; i >= 0; i--)
            {
                if (Time.time < held[i].due) continue;
                V1HealthLink.Send(held[i].share);
                held.RemoveAt(i);
            }
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

        public static void OnMessage(string cmd, string rest)
        {
            if (cmd != Proto.Parry) return;
            var a = rest.Split(' ');
            var v1 = V1Drive.Body;
            if (v1 == null || a.Length < 2) return;
            if (a[0] == "b")
            {
                // ULTRAKILL blew its copy up where it flew (the Sharpshooter's shot through it): the explosion and what
                // it does to the monsters round it are ULTRAKILL's; here the projectile is simply gone
                ShotsBlown++;
                if (shots.TryGetValue(int.Parse(a[1]), out var blown) && blown != null)
                {
                    shots.Remove(int.Parse(a[1]));
                    Object.Destroy(blown.gameObject);
                    return;
                }
                // it had just landed here: the hit it left waiting goes with it
                for (int i = held.Count - 1; i >= 0; i--)
                {
                    if (!held[i].shot) continue;
                    held.RemoveAt(i);
                    break;
                }
                return;
            }
            if (a[0] != "p") return;
            if (!shots.TryGetValue(int.Parse(a[1]), out var p) || p == null)
            {
                // it has already landed here: the hit it left waiting is the one the fist caught
                for (int i = held.Count - 1; i >= 0; i--)
                {
                    if (!held[i].shot) continue;
                    var from = held[i].owner;
                    held.RemoveAt(i);
                    ShotParries++;
                    Return(from, v1);
                    return;
                }
                return;
            }
            ShotParries++;
            var owner = p.owner != null ? p.owner.GetComponent<CharacterBody>() : null;
            Object.Destroy(p.gameObject);
            Return(owner, v1);
        }
    }
}
