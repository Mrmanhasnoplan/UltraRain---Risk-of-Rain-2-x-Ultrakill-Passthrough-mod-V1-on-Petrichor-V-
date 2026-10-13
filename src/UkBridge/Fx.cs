using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace V1Bridge.Uk
{
    // What V1's weapons are doing, said in plain facts for the other players' games to draw something simple from: a
    // beam went from here to there, something blew up there, these things are in the air, a punch was thrown.
    // ULTRAKILL's own picture of them is the V1 player's alone (it is drawn into their window); none of its art is
    // sent, only where and what. Only while Risk of Rain 2 asks for it (FXON: somebody else is in the game).
    //
    // Each beam, explosion and projectile puts itself on a list as it starts (FxHooks). The first version looked them
    // all up in the scene every frame instead, which cost ULTRAKILL 32 ms a frame (60 fps down to 27) whenever
    // anybody else was in the game.
    public partial class Bridge
    {
        bool fxWanted, fxListSent, fxFistReady = true;
        float fxNextList;
        readonly StringBuilder fxLine = new StringBuilder(256);
        public int FxBeams, FxBlasts, FxFlying, FxPunches;
        const int MaxFlying = 40;
        /// <summary>How often what is in the air is said: the other games ease between the places they are told.</summary>
        const float ListInterval = 1f / 20f;
        public bool FxOn => fxWanted && levelPrepared && v1Landed && !idle;

        void SetFx(string rest)
        {
            fxWanted = rest.Trim() == "1";
            if (!fxWanted) FxHooks.Clear();
        }

        void SendFx()
        {
            if (!FxOn) return;

            // hitscan shots: the line each one drew, once it has one (revolvers, the railcannon, ricochets off coins)
            var beams = FxHooks.Beams;
            for (int i = beams.Count - 1; i >= 0; i--)
            {
                var beam = beams[i].Key;
                // gone, or it never drew a line
                if (beam == null || Time.frameCount - beams[i].Value > 30) { beams.RemoveAt(i); continue; }
                if (beam.beamType == BeamType.Enemy || beam.beamType == BeamType.MaliciousFace) { beams.RemoveAt(i); continue; }
                var lr = beam.GetComponent<LineRenderer>();
                if (lr == null || lr.positionCount < 2) continue;
                Vector3 a = lr.GetPosition(0), b = lr.GetPosition(lr.positionCount - 1);
                if ((b - a).sqrMagnitude < 0.01f) continue;
                beams.RemoveAt(i);
                FxBeams++;
                Net.Send(Proto.Fx + " b " + S(a.x) + " " + S(a.y) + " " + S(a.z) + " " + S(b.x) + " " + S(b.y) + " " + S(b.z) + " " + (beam.beamType == BeamType.Railgun ? 1 : 0));
            }

            // a punch: the fist stops being ready the moment it is thrown (0 the Feedbacker, 1 the Knuckleblaster)
            var fists = MonoSingleton<FistControl>.Instance;
            var fist = fists != null ? fists.currentPunch : null;
            bool fistReady = fist == null || fist.ready;
            if (!fistReady && fxFistReady)
            {
                FxPunches++;
                Net.Send(Proto.Fx + " p " + (fist.type == FistType.Heavy ? 1 : 0));
            }
            fxFistReady = fistReady;

            // explosions: where, and how big they grow
            var blasts = FxHooks.Blasts;
            for (int i = 0; i < blasts.Count; i++)
            {
                var ex = blasts[i];
                if (ex == null || ex.enemy) continue;
                FxBlasts++;
                var p = ex.transform.position;
                Net.Send(Proto.Fx + " e " + S(p.x) + " " + S(p.y) + " " + S(p.z) + " " + S(ex.maxSize));
            }
            blasts.Clear();

            // what is in the air or stuck in the world: g rockets and grenades (r the one V1 is riding), b cannonballs, n nails, s saws,
            // p pellets and other shots of V1's, c coins, h the Whiplash's hook, w the chainsaw out on its chain,
            // m magnets, d the railcannon's drill, f a monster on fire
            if (Time.unscaledTime < fxNextList) return;
            fxNextList = Time.unscaledTime + ListInterval;
            fxLine.Length = 0;
            fxLine.Append(Proto.FxList).Append(' ');
            int n = 0;
            void Add(UnityEngine.Object o, char kind, Vector3 at)
            {
                if (n >= MaxFlying) return;
                fxLine.Append(o.GetInstanceID()).Append(',').Append(kind).Append(',').Append(S(at.x)).Append(',').Append(S(at.y)).Append(',').Append(S(at.z)).Append(';');
                n++;
            }
            var hook = MonoSingleton<HookArm>.Instance;
            if (hook != null && hook.equipped && hook.state != HookState.Ready && hook.hook != null) Add(hook, 'h', hook.hook.position);
            var grenades = FxHooks.Grenades;
            for (int i = grenades.Count - 1; i >= 0; i--)
            {
                if (grenades[i] == null) { grenades.RemoveAt(i); continue; }
                if (!grenades[i].enemy) Add(grenades[i], grenades[i].playerRiding ? 'r' : 'g', grenades[i].transform.position);
            }
            var balls = FxHooks.Cannonballs;
            for (int i = balls.Count - 1; i >= 0; i--)
            {
                if (balls[i] == null) { balls.RemoveAt(i); continue; }
                Add(balls[i], 'b', balls[i].transform.position);
            }
            var coins = MonoSingleton<CoinTracker>.Instance;
            if (coins != null) foreach (var c in coins.revolverCoinsList) if (c != null && c.gameObject.activeInHierarchy) Add(c, 'c', c.transform.position);
            // (shotgun pellets are these; Risk of Rain 2's own projectiles, here as stand-ins for the fist, never start)
            var shots = FxHooks.Projectiles;
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                if (shots[i] == null) { shots.RemoveAt(i); continue; }
                if (shots[i].gameObject.activeInHierarchy) Add(shots[i], 'p', shots[i].transform.position);
            }
            var nails = FxHooks.Nails;
            for (int i = nails.Count - 1; i >= 0; i--)
            {
                var nail = nails[i];
                // gone, or stuck in something
                if (nail == null || nail.hit || nail.stopped) { nails.RemoveAt(i); continue; }
                if (!nail.enemy) Add(nail, nail.sawblade || nail.chainsaw ? 's' : 'n', nail.transform.position);
            }
            var saws = FxHooks.Chainsaws;
            for (int i = saws.Count - 1; i >= 0; i--)
            {
                if (saws[i] == null) { saws.RemoveAt(i); continue; }
                if (saws[i].gameObject.activeInHierarchy) Add(saws[i], 'w', saws[i].transform.position);
            }
            var magnets = FxHooks.Magnets;
            for (int i = magnets.Count - 1; i >= 0; i--)
            {
                if (magnets[i] == null) { magnets.RemoveAt(i); continue; }
                if (magnets[i].gameObject.activeInHierarchy) Add(magnets[i], 'm', magnets[i].transform.position);
            }
            var drills = FxHooks.Drills;
            for (int i = drills.Count - 1; i >= 0; i--)
            {
                if (drills[i] == null) { drills.RemoveAt(i); continue; }
                if (drills[i].gameObject.activeInHierarchy) Add(drills[i], 'd', drills[i].transform.position);
            }
            // a monster burning (gasoline set alight, or anything else that lit it): one fire each, at its middle
            foreach (var kv in proxies)
            {
                var p = kv.Value;
                if (p == null || p.eid == null || p.eid.flammables == null || p.center == null) continue;
                bool burning = false;
                foreach (var f in p.eid.flammables) if (f != null && f.burning) { burning = true; break; }
                if (burning) Add(p, 'f', p.center.position);
            }
            FxFlying = n;
            // while there is anything, and once more when the last is gone
            if (n == 0 && !fxListSent) return;
            fxListSent = n > 0;
            Net.Send(fxLine.ToString());
        }
    }

    /// <summary>Each of ULTRAKILL's beams, explosions and projectiles notes itself down as it starts, while the facts are
    /// wanted. Applied by hand, each on its own: an ULTRAKILL update that renames one costs only that kind.</summary>
    static class FxHooks
    {
        public static readonly List<KeyValuePair<RevolverBeam, int>> Beams = new List<KeyValuePair<RevolverBeam, int>>();
        public static readonly List<Explosion> Blasts = new List<Explosion>();
        public static readonly List<Grenade> Grenades = new List<Grenade>();
        public static readonly List<Cannonball> Cannonballs = new List<Cannonball>();
        public static readonly List<Projectile> Projectiles = new List<Projectile>();
        public static readonly List<Nail> Nails = new List<Nail>();
        public static readonly List<Chainsaw> Chainsaws = new List<Chainsaw>();
        public static readonly List<Magnet> Magnets = new List<Magnet>();
        public static readonly List<Harpoon> Drills = new List<Harpoon>();
        public static string Problem;
        const int Most = 600;

        public static void Clear()
        {
            Beams.Clear();
            Blasts.Clear();
            Grenades.Clear();
            Cannonballs.Clear();
            Projectiles.Clear();
            Nails.Clear();
            Chainsaws.Clear();
            Magnets.Clear();
            Drills.Clear();
        }

        public static void Apply(Harmony h)
        {
            Hook(h, typeof(RevolverBeam), nameof(Beam));
            Hook(h, typeof(Explosion), nameof(Blast));
            Hook(h, typeof(Grenade), nameof(AGrenade));
            Hook(h, typeof(Cannonball), nameof(ACannonball));
            Hook(h, typeof(Projectile), nameof(AProjectile));
            Hook(h, typeof(Nail), nameof(ANail));
            Hook(h, typeof(Chainsaw), nameof(AChainsaw));
            Hook(h, typeof(Magnet), nameof(AMagnet));
            Hook(h, typeof(Harpoon), nameof(ADrill));
            if (Problem != null) Plugin.Log.LogError("effects for the other players: " + Problem);
        }

        static void Hook(Harmony h, Type type, string postfix)
        {
            try
            {
                var start = AccessTools.Method(type, "Start");
                if (start == null) { Problem = (Problem != null ? Problem + ", " : "") + type.Name + ".Start not found"; return; }
                h.Patch(start, postfix: new HarmonyMethod(typeof(FxHooks), postfix));
            }
            catch (Exception e) { Problem = (Problem != null ? Problem + ", " : "") + type.Name + ": " + e.Message; }
        }

        static bool On => Bridge.I != null && Bridge.I.FxOn;

        static void Beam(RevolverBeam __instance) { if (On && Beams.Count < Most) Beams.Add(new KeyValuePair<RevolverBeam, int>(__instance, Time.frameCount)); }
        static void Blast(Explosion __instance) { if (On && Blasts.Count < Most) Blasts.Add(__instance); }
        static void AGrenade(Grenade __instance) { if (On && Grenades.Count < Most) Grenades.Add(__instance); }
        static void ACannonball(Cannonball __instance) { if (On && Cannonballs.Count < Most) Cannonballs.Add(__instance); }
        static void AProjectile(Projectile __instance) { if (On && Projectiles.Count < Most) Projectiles.Add(__instance); }
        static void ANail(Nail __instance) { if (On && Nails.Count < Most) Nails.Add(__instance); }
        static void AChainsaw(Chainsaw __instance) { if (On && Chainsaws.Count < Most) Chainsaws.Add(__instance); }
        static void AMagnet(Magnet __instance) { if (On && Magnets.Count < Most) Magnets.Add(__instance); }
        static void ADrill(Harpoon __instance) { if (On && Drills.Count < Most) Drills.Add(__instance); }
    }
}
