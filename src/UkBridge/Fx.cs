using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace V1Bridge.Uk
{
    // What V1's weapons are doing, said in plain facts for the other players' games to draw something simple from: a
    // beam went from here to there, something blew up there, these things are in the air. ULTRAKILL's own picture of
    // them is the V1 player's alone (it is drawn into their window); none of its art is sent, only where and what.
    // Only while Risk of Rain 2 asks for it (FXON: somebody else is in the game).
    public partial class Bridge
    {
        bool fxWanted, fxListSent;
        readonly HashSet<int> fxBeams = new HashSet<int>(), fxBlasts = new HashSet<int>();
        readonly StringBuilder fxLine = new StringBuilder(256);
        public int FxBeams, FxBlasts, FxFlying;
        const int MaxFlying = 40;

        void SetFx(string rest)
        {
            fxWanted = rest.Trim() == "1";
        }

        void SendFx()
        {
            if (!fxWanted || !levelPrepared || !v1Landed || idle) return;
            if (fxBeams.Count > 400) fxBeams.Clear();
            if (fxBlasts.Count > 400) fxBlasts.Clear();

            // hitscan shots: the line each one drew, once it has one (revolvers, the railcannon, ricochets off coins)
            foreach (var beam in FindObjectsOfType<RevolverBeam>())
            {
                if (beam.beamType == BeamType.Enemy || beam.beamType == BeamType.MaliciousFace) continue;
                int id = beam.GetInstanceID();
                if (fxBeams.Contains(id)) continue;
                var lr = beam.GetComponent<LineRenderer>();
                if (lr == null || lr.positionCount < 2) continue;
                Vector3 a = lr.GetPosition(0), b = lr.GetPosition(lr.positionCount - 1);
                if ((b - a).sqrMagnitude < 0.01f) continue;
                fxBeams.Add(id);
                FxBeams++;
                Net.Send(Proto.Fx + " b " + S(a.x) + " " + S(a.y) + " " + S(a.z) + " " + S(b.x) + " " + S(b.y) + " " + S(b.z) + " " + (beam.beamType == BeamType.Railgun ? 1 : 0));
            }

            // explosions: where, and how big they grow
            foreach (var ex in FindObjectsOfType<Explosion>())
            {
                if (ex.enemy) continue;
                int id = ex.GetInstanceID();
                if (!fxBlasts.Add(id)) continue;
                FxBlasts++;
                var p = ex.transform.position;
                Net.Send(Proto.Fx + " e " + S(p.x) + " " + S(p.y) + " " + S(p.z) + " " + S(ex.maxSize));
            }

            // what is in the air right now: g rockets and grenades, n nails, s saws, p pellets and other shots of V1's,
            // c coins, h the Whiplash's hook
            fxLine.Length = 0;
            fxLine.Append(Proto.FxList).Append(' ');
            int n = 0;
            void Add(Object o, char kind, Vector3 at)
            {
                if (n >= MaxFlying) return;
                fxLine.Append(o.GetInstanceID()).Append(',').Append(kind).Append(',').Append(S(at.x)).Append(',').Append(S(at.y)).Append(',').Append(S(at.z)).Append(';');
                n++;
            }
            var hook = MonoSingleton<HookArm>.Instance;
            if (hook != null && hook.equipped && hook.state != HookState.Ready && hook.hook != null) Add(hook, 'h', hook.hook.position);
            foreach (var g in FindObjectsOfType<Grenade>()) if (!g.enemy) Add(g, 'g', g.transform.position);
            foreach (var c in FindObjectsOfType<Cannonball>()) Add(c, 'g', c.transform.position);
            var coins = MonoSingleton<CoinTracker>.Instance;
            if (coins != null) foreach (var c in coins.revolverCoinsList) if (c != null && c.gameObject.activeInHierarchy) Add(c, 'c', c.transform.position);
            foreach (var p in FindObjectsOfType<Projectile>())
            {
                // not Risk of Rain 2's own projectiles, which are here as stand-ins for the fist
                if (StandInProjectile.Ours.Contains(p) || !(p.friendly || p.playerBullet)) continue;
                Add(p, 'p', p.transform.position);
            }
            foreach (var nail in FindObjectsOfType<Nail>())
            {
                if (nail.enemy || nail.hit || nail.stopped) continue;
                Add(nail, nail.sawblade || nail.chainsaw ? 's' : 'n', nail.transform.position);
            }
            FxFlying = n;
            // every frame while there is anything, and once more when the last is gone
            if (n == 0 && !fxListSent) return;
            fxListSent = n > 0;
            Net.Send(fxLine.ToString());
        }
    }
}
