using System;
using System.Globalization;
using RoR2;
using UnityEngine;

namespace V1Bridge.Ror
{
    /// <summary>ULTRAKILL's HP (0-100) written onto a V1 body as its health and shield, on the game that runs the body's
    /// health (the host). The HP stands for health and shield together, as a share of the two (V1HealthLink.Pool).
    /// Which part of what is left is shield is kept here: damage comes off the shield first, as in Risk of Rain 2;
    /// ULTRAKILL's healing (blood, parries) and Risk of Rain 2's healing fill health only; the shield fills only the
    /// way Risk of Rain 2 fills it, out of danger for a while, and that is passed on to ULTRAKILL as it happens (HEAL).
    /// So that ULTRAKILL's healing cannot fill the shield's share, ULTRAKILL is told the most HP V1 may have (CAP):
    /// full health plus the shield as it stands. One of these per V1: the host's own, and each joining player's.</summary>
    internal sealed class HealthMirror
    {
        float shieldPts, shieldWrote, shieldFull, shieldCredit, shieldCreditAt;
        int shieldHp, capSent = -1;
        bool ready;
        /// <summary>For the tests: until then the shield is simply kept at ULTRAKILL's HP share (V1 was made whole by hand).</summary>
        public float SyncUntil;
        /// <summary>The shield part of V1's health, in Risk of Rain 2 points, and the HP limit last sent to ULTRAKILL.</summary>
        public float ShieldPoints => shieldPts;
        public int Cap => capSent;

        /// <summary>A new body (a new run, a new stage): the split is made again, and the limit said again.</summary>
        public void Reset()
        {
            ready = false;
            capSent = -1;
        }

        /// <summary><paramref name="send"/> takes the HEAL and CAP lines meant for that V1's ULTRAKILL.</summary>
        public void Apply(HealthComponent hc, int ukHp, Action<string> send)
        {
            float pool = V1HealthLink.Pool(hc), fs = hc.fullShield, share = Mathf.Clamp01(ukHp / 100f);
            if (!ready || fs <= 0f || Time.time < SyncUntil)
            {
                shieldPts = fs * share;
                shieldFull = fs;
                shieldCredit = 0f;
                ready = true;
            }
            else
            {
                if (Mathf.Abs(fs - shieldFull) > 0.001f)
                {
                    // a bigger or smaller shield (an item, a level): what is left of it keeps its share
                    shieldPts = shieldFull > 0f ? shieldPts * fs / shieldFull : fs * share;
                    shieldFull = fs;
                }
                else
                {
                    // Risk of Rain 2 has been filling the shield since it was last written: V1 gains that in ULTRAKILL
                    float grown = hc.shield - shieldWrote;
                    if (grown > 0.0005f)
                    {
                        shieldCredit += grown;
                        shieldCreditAt = Time.time;
                        send(Proto.Heal + " " + (grown / pool * 100f).ToString("0.####", CultureInfo.InvariantCulture));
                    }
                }
                float change = (ukHp - shieldHp) / 100f * pool;
                if (change < 0f) shieldPts += change;
                else if (change > 0f)
                {
                    float toShield = Mathf.Min(change, shieldCredit);
                    shieldCredit -= toShield;
                    shieldPts += toShield;
                }
                if (Time.time - shieldCreditAt > 1.5f) shieldCredit = 0f;
            }
            shieldHp = ukHp;
            float left = share * pool;
            shieldPts = Mathf.Clamp(shieldPts, 0f, Mathf.Min(fs, left));
            // ULTRAKILL's HP comes in whole points: the last fraction of one goes to the shield, not past full health
            if (left - shieldPts > hc.fullHealth) shieldPts = Mathf.Min(fs, left - hc.fullHealth);
            shieldCredit = Mathf.Clamp(shieldCredit, 0f, fs - shieldPts);
            hc.Networkhealth = Mathf.Clamp(left - shieldPts, 1f, hc.fullHealth);
            if (fs > 0f || hc.shield > 0f) hc.Networkshield = shieldPts;
            shieldWrote = shieldPts;
            float most = hc.fullHealth + shieldPts + shieldCredit;
            int cap = fs > 0f && most < pool - 0.01f ? Mathf.Clamp(Mathf.CeilToInt(most / pool * 100f - 0.001f), 1, 100) : 999;
            if (cap != capSent)
            {
                capSent = cap;
                send(Proto.Cap + " " + cap);
            }
        }
    }
}
