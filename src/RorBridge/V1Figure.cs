using System;
using System.Collections.Generic;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace V1Bridge.Ror
{
    /// <summary>V1 as the other players see it. V1's body is a copy of Commando's, and to everyone but the player inside it
    /// (whose view is ULTRAKILL's) it was a Commando gliding about. On the body's model this puts the mod's own figure
    /// of V1 (V1Survivor.BuildFigure: primitives, nothing of ULTRAKILL's, so it is the same for players who do not own
    /// ULTRAKILL) and keeps the Commando's own meshes switched off. Its limbs are moved by V1FigureMotion. It is not
    /// shown to the player whose body it is.</summary>
    public class V1Figure : MonoBehaviour
    {
        /// <summary>How tall the figure stands in a run, metres (a survivor's capsule is 1.82).</summary>
        public const float Height = 1.9f;
        const float BuiltHeight = 2.05f;

        static readonly HashSet<CharacterModel> models = new HashSet<CharacterModel>();
        public static string Problem;
        public static int Built;
        CharacterModel model;
        GameObject figure;

        /// <summary>For the tests: the figure on that body's model, shown or not.</summary>
        public static GameObject On(CharacterBody body)
        {
            var t = body != null && body.modelLocator != null ? body.modelLocator.modelTransform : null;
            var f = t != null ? t.GetComponent<V1Figure>() : null;
            return f != null ? f.figure : null;
        }

        void Awake()
        {
            model = GetComponent<CharacterModel>();
            if (model != null) models.Add(model);
            figure = new GameObject("V1 figure");
            figure.SetActive(false);
            figure.layer = gameObject.layer;
            figure.transform.SetParent(transform, false);
            figure.transform.localScale = Vector3.one * (Height / BuiltHeight);
            try
            {
                V1Survivor.BuildFigure(figure.transform);
                figure.AddComponent<V1FigureMotion>();
                Built++;
            }
            catch (Exception e)
            {
                Problem = e.Message;
                Plugin.Log.LogWarning("V1 figure: " + e);
            }
        }

        void OnDestroy()
        {
            if (model != null) models.Remove(model);
        }

        void LateUpdate()
        {
            var body = model != null ? model.body : null;
            bool show = body != null && body.healthComponent != null && body.healthComponent.alive;
            if (show)
            {
                // not from the inside: the player whose body this is looks out of it
                foreach (var user in LocalUserManager.readOnlyLocalUsersList)
                {
                    if (user != null && user.cachedBody == body) { show = false; break; }
                }
            }
            if (figure.activeSelf != show) figure.SetActive(show);
        }

        /// <summary>Applied by hand: a Risk of Rain 2 update that renames the method must not take the other patches with it.</summary>
        public static void Apply(Harmony h)
        {
            try
            {
                var update = AccessTools.Method(typeof(CharacterModel), "UpdateMaterials");
                if (update == null) Problem = "CharacterModel.UpdateMaterials not found";
                else h.Patch(update, postfix: new HarmonyMethod(typeof(V1Figure), nameof(AfterMaterials)));
            }
            catch (Exception e) { Problem = e.Message; }
            if (Problem != null) Plugin.Log.LogError("V1 figure: " + Problem);
        }

        /// <summary>Risk of Rain 2 has just set which of the model's own renderers are drawn (it does so whenever the body's
        /// look changes, and when a skin is applied): on V1's model none of the Commando's are.</summary>
        static void AfterMaterials(CharacterModel __instance)
        {
            if (!models.Contains(__instance)) return;
            var infos = __instance.baseRendererInfos;
            if (infos == null) return;
            for (int i = 0; i < infos.Length; i++)
            {
                var r = infos[i].renderer;
                if (r != null && r.enabled) r.enabled = false;
            }
        }
    }
    /// <summary>The figure's limbs, moved by the mod's own code from nothing but how the figure is being carried about:
    /// its speed over the ground swings the legs and arms and leans it into the run, having no ground under it puts it
    /// in a falling pose, and standing still it holds its stance. Each player's game works this out for itself from
    /// the positions Risk of Rain 2 already sends, so nothing more goes over the network, and nothing of ULTRAKILL's
    /// animation is used. Two things it is told (V1Fx): a punch, and that V1 is riding a rocket.
    /// The stance is the mod's own reading of how ULTRAKILL's machines stand (V2 was the reference the owner named):
    /// knees bent and feet apart, one a little ahead, the body leaning in, the gun arm up and forward, the other out
    /// from the side, wings open.</summary>
    public class V1FigureMotion : MonoBehaviour
    {
        Transform body, head;
        readonly Transform[] arm = new Transform[2], elbow = new Transform[2], leg = new Transform[2], knee = new Transform[2];
        readonly Transform[,] wing = new Transform[2, 4];
        Vector3 bodyAt, last, velocity;
        bool have;
        float phase, swing, air, ride, clock;
        /// <summary>For the tests: how much of a run the limbs are swinging (0-1), how much of the falling pose is on, and of the riding one.</summary>
        public float Swing => swing;
        public float Air => air;
        public float Ride => ride;
        public float LegSpread => leg[0] != null && leg[1] != null ? Quaternion.Angle(leg[0].localRotation, leg[1].localRotation) : 0f;
        /// <summary>V1 is on a rocket until then (said again every frame it is: V1Fx).</summary>
        public float RidingUntil = -10f;

        // the stance, degrees (index 0 the left side, 1 the right; about x, forward is negative)
        static readonly float[] StandThigh = { -20f, -6f }, StandKnee = { 30f, 18f }, StandArm = { -10f, -40f }, StandElbow = { -26f, -62f }, StandArmOut = { 13f, 5f };
        const float StandLean = 8f, StandFeetApart = 6f, StandWings = 10f, StandDrop = 0.035f;
        // on a rocket: low, front foot well forward, arms out for balance
        static readonly float[] RideThigh = { -62f, 4f }, RideKnee = { 78f, 58f };
        const float RideLean = 20f, RideArmOut = 48f, RideDrop = 0.2f;

        void Awake()
        {
            body = transform.Find("body");
            if (body == null) { enabled = false; return; }
            bodyAt = body.localPosition;
            head = body.Find("head");
            for (int k = 0; k < 2; k++)
            {
                string s = k == 0 ? "L" : "R";
                arm[k] = body.Find("arm" + s);
                elbow[k] = arm[k] != null ? arm[k].Find("elbow" + s) : null;
                leg[k] = transform.Find("leg" + s);
                knee[k] = leg[k] != null ? leg[k].Find("knee" + s) : null;
                for (int i = 0; i < 4; i++) wing[k, i] = body.Find("wing" + s + i);
            }
        }

        void OnEnable() => have = false;

        float punchAt = -10f;
        bool punchHeavy;
        public int Punches;

        /// <summary>V1 threw a punch (V1Fx): the left arm goes out and comes back, the shoulder behind it.</summary>
        public void Punch(bool heavy)
        {
            punchAt = Time.time;
            punchHeavy = heavy;
            Punches++;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            Vector3 p = transform.position;
            // just shown, or moved in one go (a teleport, a new stage): that is not speed
            if (!have || (p - last).sqrMagnitude > 100f) { last = p; velocity = Vector3.zero; have = true; }
            velocity = Vector3.Lerp(velocity, (p - last) / dt, 1f - Mathf.Exp(-12f * dt));
            last = p;
            var over = new Vector3(velocity.x, 0f, velocity.z);
            float speed = over.magnitude;
            bool riding = Time.time < RidingUntil;
            bool grounded = riding || Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, 1.2f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
            ride = Mathf.MoveTowards(ride, riding ? 1f : 0f, dt * 6f);
            air = Mathf.MoveTowards(air, grounded ? 0f : 1f, dt * 6f);
            swing = Mathf.MoveTowards(swing, grounded && !riding ? Mathf.Clamp01(speed / 7f) : 0f, dt * 5f);
            // a stride every 2.4 m, however fast
            phase += speed * dt * 2.6f;
            clock += dt;
            float sin = Mathf.Sin(phase), cos = Mathf.Cos(phase), reach = 42f * swing;
            // how much of each pose is on: standing (less of it the faster the run), in the air, on a rocket
            float feet = (1f - air) * (1f - ride), stand = feet * (1f - 0.65f * swing);
            // into the run, or back from it when V1 is moving backwards
            float along = speed > 0.2f ? Vector3.Dot(over / speed, transform.forward) : 0f;
            float lean = Mathf.Min(speed * 1.6f, 16f) * along * feet + StandLean * stand - 8f * air * (1f - ride) + RideLean * ride;
            body.localRotation = Quaternion.Euler(lean, 0f, 0f);
            body.localPosition = bodyAt + Vector3.up * (Mathf.Sin(phase * 2f) * 0.025f * swing + Mathf.Sin(clock * 1.8f) * 0.008f - StandDrop * stand - RideDrop * ride);
            if (head != null) head.localRotation = Quaternion.Euler(-lean * 0.7f, 0f, 0f);
            for (int k = 0; k < 2; k++)
            {
                // the left leg and the right arm go forward together
                float side = k == 0 ? -1f : 1f, turn = k == 0 ? 1f : -1f, falling = air * (1f - ride);
                if (leg[k] != null) leg[k].localRotation = Quaternion.Euler(
                    StandThigh[k] * stand - sin * turn * reach * feet + (k == 0 ? -28f : 12f) * falling + RideThigh[k] * ride,
                    0f, side * StandFeetApart * (stand + ride));
                if (knee[k] != null) knee[k].localRotation = Quaternion.Euler(
                    StandKnee[k] * stand + Mathf.Max(0f, cos * turn) * reach * 1.3f * feet + 40f * falling + RideKnee[k] * ride, 0f, 0f);
                if (arm[k] != null) arm[k].localRotation = Quaternion.Euler(
                    StandArm[k] * stand + sin * turn * reach * 0.8f * feet - 10f * ride,
                    0f, side * (StandArmOut[k] * stand + 30f * falling + RideArmOut * ride));
                if (elbow[k] != null) elbow[k].localRotation = Quaternion.Euler(
                    StandElbow[k] * stand - (12f + 28f * swing) * feet * (1f - stand) - 20f * falling - 15f * ride, 0f, 0f);
                // a punch, on the left arm, over whatever else it was doing
                if (k == 0)
                {
                    float thrown = (Time.time - punchAt) / (punchHeavy ? 0.45f : 0.3f);
                    if (thrown >= 0f && thrown < 1f)
                    {
                        float out_ = Mathf.Sin(thrown * Mathf.PI);
                        if (arm[k] != null) arm[k].localRotation = Quaternion.Slerp(arm[k].localRotation, Quaternion.Euler(-95f, 0f, 0f), out_);
                        if (elbow[k] != null) elbow[k].localRotation = Quaternion.Slerp(elbow[k].localRotation, Quaternion.Euler(-4f, 0f, 0f), out_);
                        body.localRotation = body.localRotation * Quaternion.Euler(0f, (punchHeavy ? 30f : 20f) * out_, 0f);
                    }
                }
                // the wings: open in the stance, swept back in a run or on a rocket, spread in the air, never quite still
                float spread = StandWings * stand + 6f * swing + 14f * falling + Mathf.Sin(clock * 1.6f) * 2.5f;
                for (int i = 0; i < 4; i++)
                {
                    if (wing[k, i] != null) wing[k, i].localRotation = Quaternion.Euler(0f, side * (14f * swing + 26f * ride), side * spread * (1.5f - i) / 1.5f);
                }
            }
        }
    }
}