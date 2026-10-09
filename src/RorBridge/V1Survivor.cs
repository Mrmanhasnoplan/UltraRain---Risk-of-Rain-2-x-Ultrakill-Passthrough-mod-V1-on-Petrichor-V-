using System;
using EntityStates;
using R2API;
using RoR2;
using RoR2.Skills;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace V1Bridge.Ror
{
    /// <summary>V1 as a Risk of Rain 2 survivor: a body for RoR2 to keep stats, items and health on, with four skill
    /// entries that only describe what ULTRAKILL does (RoR2 never runs them; V1's weapons are ULTRAKILL's). Nothing here
    /// comes from ULTRAKILL's files: the look is a placeholder built from primitives and drawn icons.</summary>
    internal static class V1Survivor
    {
        public const string BodyName = "V1Body";
        public static SurvivorDef Def;
        public static GameObject BodyPrefab;
        public static string Problem;

        static readonly Color Blue = new Color(0.16f, 0.36f, 0.78f);
        static readonly Color Yellow = new Color(1f, 0.85f, 0.1f);

        public static BodyIndex BodyIndex => BodyPrefab != null ? BodyPrefab.GetComponent<CharacterBody>().bodyIndex : BodyIndex.None;

        public static void Register()
        {
            try { Build(); }
            catch (Exception e)
            {
                Problem = e.Message;
                Plugin.Log.LogError("V1 survivor: " + e);
            }
        }

        static void Build()
        {
            Text();

            // RoR2's side of V1 starts as a copy of Commando's body: a humanoid with the state machines, hurtbox,
            // interactor and camera hooks every system expects. Its own skills are replaced below.
            var commando = Addressables.LoadAssetAsync<GameObject>("RoR2/Base/Commando/CommandoBody.prefab").WaitForCompletion();
            if (commando == null) throw new Exception("CommandoBody.prefab not found");
            BodyPrefab = PrefabAPI.InstantiateClone(commando, BodyName, true);
            var body = BodyPrefab.GetComponent<CharacterBody>();
            body.baseNameToken = "V1BRIDGE_V1_NAME";
            body.subtitleNameToken = "V1BRIDGE_V1_SUBTITLE";
            body.portraitIcon = Art.Portrait();
            body.bodyColor = Blue;

            var skills = BodyPrefab.GetComponent<SkillLocator>();
            Skill(skills.primary, "Primary", "V1BRIDGE_V1_PRIMARY", new Color(0.25f, 0.55f, 1f));
            Skill(skills.secondary, "Secondary", "V1BRIDGE_V1_SECONDARY", new Color(0.3f, 0.85f, 0.35f));
            Skill(skills.utility, "Utility", "V1BRIDGE_V1_UTILITY", new Color(0.9f, 0.25f, 0.25f));
            Skill(skills.special, "Special", "V1BRIDGE_V1_SPECIAL", Yellow);

            Def = ScriptableObject.CreateInstance<SurvivorDef>();
            Def.cachedName = "V1";
            Def.bodyPrefab = BodyPrefab;
            Def.displayPrefab = Display();
            Def.displayNameToken = "V1BRIDGE_V1_NAME";
            Def.descriptionToken = "V1BRIDGE_V1_DESCRIPTION";
            Def.outroFlavorToken = "V1BRIDGE_V1_OUTRO";
            Def.mainEndingEscapeFailureFlavorToken = "V1BRIDGE_V1_OUTRO_FAILURE";
            Def.primaryColor = Blue;
            Def.desiredSortPosition = 100f;

            ContentAddition.AddBody(BodyPrefab);
            ContentAddition.AddSurvivorDef(Def);
            Plugin.Log.LogInfo("V1 survivor registered");
        }

        static void Skill(GenericSkill slot, string name, string token, Color color)
        {
            if (slot == null) throw new Exception("the body has no " + name + " skill slot");
            var def = ScriptableObject.CreateInstance<SkillDef>();
            def.skillName = "V1" + name;
            ((ScriptableObject)def).name = def.skillName;
            def.skillNameToken = token + "_NAME";
            def.skillDescriptionToken = token + "_DESCRIPTION";
            def.icon = Art.SkillIcon(color);
            // RoR2 never fires anything for V1: the key goes to ULTRAKILL
            def.activationState = new SerializableEntityStateType(typeof(Idle));
            def.activationStateMachineName = "Weapon";
            def.baseRechargeInterval = 0f;
            def.baseMaxStock = 1;
            def.rechargeStock = 1;
            def.requiredStock = 0;
            def.stockToConsume = 0;
            def.beginSkillCooldownOnSkillEnd = false;
            def.canceledFromSprinting = false;
            def.cancelSprintingOnActivation = false;
            def.fullRestockOnAssign = true;
            def.interruptPriority = InterruptPriority.Any;
            def.isCombatSkill = false;
            def.mustKeyPress = true;

            var family = ScriptableObject.CreateInstance<SkillFamily>();
            ((ScriptableObject)family).name = "V1" + name + "Family";
            family.variants = new[]
            {
                new SkillFamily.Variant { skillDef = def, viewableNode = new ViewablesCatalog.Node(def.skillNameToken, false, null) }
            };
            typeof(GenericSkill).GetField("_skillFamily", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(slot, family);
            ContentAddition.AddSkillDef(def);
            ContentAddition.AddSkillFamily(family);
        }

        /// <summary>What stands in the character select screen until V1's own model can be shown from the local
        /// ULTRAKILL install: a blue figure with a yellow eye and wings.</summary>
        static GameObject Display()
        {
            var shader = Addressables.LoadAssetAsync<Shader>("RoR2/Base/Shaders/HGStandard.shader").WaitForCompletion() ?? LegacyShaderAPI.Find("Hopoo Games/Deferred/Standard");
            Material Mat(Color c)
            {
                var m = new Material(shader) { color = c };
                m.SetColor("_EmColor", c * 0.25f);
                return m;
            }
            var blue = Mat(Blue);
            var yellow = Mat(Yellow);
            var dark = Mat(new Color(0.12f, 0.12f, 0.14f));

            var root = new GameObject("V1DisplayTemp");
            // character select looks at the display from its back unless it is turned round
            var model = new GameObject("Model");
            model.transform.SetParent(root.transform, false);
            model.transform.localEulerAngles = new Vector3(0f, 180f, 0f);
            void Part(PrimitiveType type, Material m, Vector3 pos, Vector3 scale, Vector3 euler = default)
            {
                var go = GameObject.CreatePrimitive(type);
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.SetParent(model.transform, false);
                go.transform.localPosition = pos;
                go.transform.localScale = scale;
                go.transform.localEulerAngles = euler;
                go.GetComponent<MeshRenderer>().sharedMaterial = m;
            }
            Part(PrimitiveType.Capsule, blue, new Vector3(0f, 1.15f, 0f), new Vector3(0.55f, 0.5f, 0.4f));              // torso
            Part(PrimitiveType.Sphere, dark, new Vector3(0f, 1.85f, 0f), new Vector3(0.42f, 0.42f, 0.42f));               // head
            Part(PrimitiveType.Sphere, yellow, new Vector3(0f, 1.87f, 0.17f), new Vector3(0.18f, 0.18f, 0.12f));          // eye
            Part(PrimitiveType.Capsule, dark, new Vector3(-0.16f, 0.4f, 0f), new Vector3(0.2f, 0.42f, 0.2f));             // legs
            Part(PrimitiveType.Capsule, dark, new Vector3(0.16f, 0.4f, 0f), new Vector3(0.2f, 0.42f, 0.2f));
            Part(PrimitiveType.Capsule, blue, new Vector3(-0.42f, 1.2f, 0.05f), new Vector3(0.16f, 0.38f, 0.16f), new Vector3(0f, 0f, -12f)); // arms
            Part(PrimitiveType.Capsule, blue, new Vector3(0.42f, 1.2f, 0.05f), new Vector3(0.16f, 0.38f, 0.16f), new Vector3(0f, 0f, 12f));
            for (int side = -1; side <= 1; side += 2)                                                                     // wings
            {
                for (int i = 0; i < 4; i++)
                {
                    Part(PrimitiveType.Cube, yellow, new Vector3(side * (0.35f + i * 0.09f), 1.55f - i * 0.2f, -0.3f),
                        new Vector3(0.75f, 0.07f, 0.03f), new Vector3(0f, side * 25f, side * (40f - i * 18f)));
                }
            }
            var prefab = PrefabAPI.InstantiateClone(root, "V1Display", false);
            UnityEngine.Object.Destroy(root);
            return prefab;
        }

        static void Text()
        {
            LanguageAPI.Add("V1BRIDGE_V1_NAME", "V1");
            LanguageAPI.Add("V1BRIDGE_V1_SUBTITLE", "Supreme Machine");
            LanguageAPI.Add("V1BRIDGE_V1_DESCRIPTION",
                "V1 is played by the real ULTRAKILL, running beside Risk of Rain 2 and drawn into this window.<style=cSub>\r\n\r\n"
                + "< ! > Movement, weapons, arms, style and the HUD are ULTRAKILL's own: dash, slide, slam, wall jump, parry, coins.\r\n\r\n"
                + "< ! > Damage follows your Risk of Rain 2 damage stat, so levels and items still count.\r\n\r\n"
                + "< ! > V1's health is ULTRAKILL's. Blood is fuel: heal by fighting up close. Risk of Rain 2 healing reaches V1 too.\r\n\r\n"
                + "< ! > Chests, shrines and the teleporter use Risk of Rain 2's interact key.</style>\r\n");
            LanguageAPI.Add("V1BRIDGE_V1_OUTRO", "..and so it left, tanks full.");
            LanguageAPI.Add("V1BRIDGE_V1_OUTRO_FAILURE", "..and so it vanished, out of fuel.");
            LanguageAPI.Add("V1BRIDGE_V1_PRIMARY_NAME", "Arsenal");
            LanguageAPI.Add("V1BRIDGE_V1_PRIMARY_DESCRIPTION", "Fire the weapon ULTRAKILL has out: revolver, shotgun, nailgun, railcannon or rocket launcher. <style=cIsDamage>Damage scales with your damage stat</style>.");
            LanguageAPI.Add("V1BRIDGE_V1_SECONDARY_NAME", "Alternate Fire");
            LanguageAPI.Add("V1BRIDGE_V1_SECONDARY_DESCRIPTION", "The weapon's second function: charge shots, <style=cIsUtility>coins</style>, core eject, magnets, and the rest.");
            LanguageAPI.Add("V1BRIDGE_V1_UTILITY_NAME", "Dash");
            LanguageAPI.Add("V1BRIDGE_V1_UTILITY_DESCRIPTION", "ULTRAKILL's movement, unchanged: <style=cIsUtility>dash</style> through attacks, slide, ground slam and wall jump.");
            LanguageAPI.Add("V1BRIDGE_V1_SPECIAL_NAME", "Feedbacker");
            LanguageAPI.Add("V1BRIDGE_V1_SPECIAL_DESCRIPTION", "Punch. Timed against an attack or projectile it is a <style=cIsDamage>parry</style>: the hit is returned and V1 heals.");
        }

        /// <summary>Icons drawn in code.</summary>
        static class Art
        {
            static Texture2D Canvas(int size, Func<float, float, Color> pixel)
            {
                var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var px = new Color[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++) px[y * size + x] = pixel((x + 0.5f) / size * 2f - 1f, (y + 0.5f) / size * 2f - 1f);
                }
                t.SetPixels(px);
                t.Apply();
                return t;
            }

            /// <summary>A dark head with one yellow eye, on blue.</summary>
            public static Texture2D Portrait()
            {
                return Canvas(128, (x, y) =>
                {
                    float head = Mathf.Sqrt(x * x + (y - 0.05f) * (y - 0.05f));
                    float eye = Mathf.Sqrt(x * x / 1.4f + (y - 0.1f) * (y - 0.1f));
                    if (eye < 0.3f) return Color.Lerp(Color.white, Yellow, eye / 0.3f);
                    if (head < 0.72f) return new Color(0.1f, 0.1f, 0.13f);
                    return Color.Lerp(Blue, Blue * 0.45f, (y + 1f) * 0.5f) + new Color(0, 0, 0, 1);
                });
            }

            public static Sprite SkillIcon(Color c)
            {
                var t = Canvas(128, (x, y) =>
                {
                    float edge = Mathf.Max(Mathf.Abs(x), Mathf.Abs(y));
                    if (edge > 0.9f) return c;
                    float diamond = Mathf.Abs(x) + Mathf.Abs(y);
                    return diamond < 0.6f ? c : new Color(0.08f, 0.08f, 0.1f, 1f);
                });
                return Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f));
            }
        }
    }
}
