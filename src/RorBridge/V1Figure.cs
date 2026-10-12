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
    /// ULTRAKILL) and keeps the Commando's own meshes switched off. The figure does not move its limbs. It is not
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
}
