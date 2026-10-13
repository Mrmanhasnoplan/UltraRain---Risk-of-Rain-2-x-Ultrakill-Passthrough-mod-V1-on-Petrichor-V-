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
            // no drop pod: V1 is put down where the run starts, and ULTRAKILL takes it from there
            body.preferredPodPrefab = null;
            // no healing by standing about: V1 heals the way ULTRAKILL has it, and through Risk of Rain 2's items
            body.baseRegen = 0f;
            body.levelRegen = 0f;
            ApplyHealth();
            BodyPrefab.AddComponent<V1HealthLink>();
            // what the other players see where V1 is: the mod's own figure, in place of the Commando the body was copied from
            var modelTransform = BodyPrefab.GetComponent<ModelLocator>() != null ? BodyPrefab.GetComponent<ModelLocator>().modelTransform : null;
            if (modelTransform != null) modelTransform.gameObject.AddComponent<V1Figure>();
            else Plugin.Log.LogWarning("V1's body has no model to put the figure on");

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

        /// <summary>The configured base health, on the prefab and on any V1 alive now (the setting can change mid-run).</summary>
        public static void ApplyHealth()
        {
            if (BodyPrefab == null) return;
            float health = Mathf.Clamp(Plugin.V1Health.Value, 1f, 100000f);
            void Set(CharacterBody b)
            {
                b.baseMaxHealth = health;
                b.levelMaxHealth = health * 0.3f;
            }
            Set(BodyPrefab.GetComponent<CharacterBody>());
            var index = BodyIndex;
            if (index == BodyIndex.None) return;
            foreach (var b in CharacterBody.readOnlyInstancesList)
            {
                if (b == null || b.bodyIndex != index) continue;
                Set(b);
                // worked out now, with the forwarding of heals off: RoR2 tops a body up by whatever its maximum grew,
                // and a moved slider must not heal V1 in ULTRAKILL
                HealsReachV1.Muted = true;
                try { b.RecalculateStats(); }
                finally { HealsReachV1.Muted = false; }
            }
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
            var root = new GameObject("V1DisplayTemp");
            // character select looks at the display's front (+z), as it does at the model read out of ULTRAKILL
            var model = new GameObject("Model");
            model.transform.SetParent(root.transform, false);
            BuildFigure(model.transform);
            var prefab = PrefabAPI.InstantiateClone(root, "V1Display", false);
            UnityEngine.Object.Destroy(root);
            return prefab;
        }

        static Material[] figureMaterials;

        /// <summary>The mod's own V1, made of primitives under <paramref name="parent"/>: feet at its origin, facing +z,
        /// 2.05 units tall. It stands in character select until ULTRAKILL's model has arrived, and is what the other
        /// players see where V1 is in a run (V1Figure). Nothing in it comes from ULTRAKILL.</summary>
        public static void BuildFigure(Transform parent)
        {
            if (figureMaterials == null || figureMaterials[0] == null)
            {
                var shader = Addressables.LoadAssetAsync<Shader>("RoR2/Base/Shaders/HGStandard.shader").WaitForCompletion() ?? LegacyShaderAPI.Find("Hopoo Games/Deferred/Standard");
                Material Mat(Color c, float glow = 0.25f)
                {
                    var m = new Material(shader) { color = c, hideFlags = HideFlags.HideAndDontSave };
                    m.SetColor("_EmColor", c * glow);
                    return m;
                }
                figureMaterials = new[] { Mat(Blue), Mat(new Color(0.28f, 0.5f, 0.92f), 0.35f), Mat(Yellow, 0.6f), Mat(new Color(1f, 0.97f, 0.75f), 0.8f), Mat(new Color(0.12f, 0.12f, 0.14f)) };
            }
            Material blue = figureMaterials[0], plate = figureMaterials[1], yellow = figureMaterials[2], lens = figureMaterials[3], dark = figureMaterials[4];
            // joints: empty transforms the parts hang on, so V1FigureMotion can swing them. Positions are the figure's own.
            var at = new System.Collections.Generic.Dictionary<Transform, Vector3> { { parent, Vector3.zero } };
            Transform Joint(string name, Vector3 pos, Transform under)
            {
                var t = new GameObject(name).transform;
                t.gameObject.layer = parent.gameObject.layer;
                t.SetParent(under, false);
                t.localPosition = pos - at[under];
                at[t] = pos;
                return t;
            }
            void Part(PrimitiveType type, Material m, Vector3 pos, Vector3 scale, Vector3 euler = default, Transform under = null)
            {
                if (under == null) under = parent;
                var go = GameObject.CreatePrimitive(type);
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                go.layer = parent.gameObject.layer;
                go.transform.SetParent(under, false);
                go.transform.localPosition = pos - at[under];
                go.transform.localScale = scale;
                go.transform.localEulerAngles = euler;
                go.GetComponent<MeshRenderer>().sharedMaterial = m;
            }
            // a slim machine, about 2 m tall: dark frame, blue plating, a box of a head with one lit lens
            var body = Joint("body", new Vector3(0f, 1f, 0f), parent);
            Part(PrimitiveType.Cube, plate, new Vector3(0f, 1.46f, 0.01f), new Vector3(0.46f, 0.3f, 0.27f), default, body);              // chest
            Part(PrimitiveType.Cube, blue, new Vector3(0f, 1.5f, 0.13f), new Vector3(0.3f, 0.18f, 0.06f), default, body);                // chest plate
            Part(PrimitiveType.Capsule, dark, new Vector3(0f, 1.2f, 0f), new Vector3(0.25f, 0.17f, 0.2f), default, body);                // waist
            Part(PrimitiveType.Cube, blue, new Vector3(0f, 1.03f, 0f), new Vector3(0.36f, 0.15f, 0.23f), default, body);                 // hips
            Part(PrimitiveType.Cylinder, dark, new Vector3(0f, 1.66f, 0f), new Vector3(0.11f, 0.05f, 0.11f), default, body);             // neck
            var head = Joint("head", new Vector3(0f, 1.7f, 0f), body);
            Part(PrimitiveType.Cube, dark, new Vector3(0f, 1.87f, 0.01f), new Vector3(0.3f, 0.3f, 0.34f), default, head);                // head
            Part(PrimitiveType.Cube, blue, new Vector3(0f, 2.03f, -0.01f), new Vector3(0.22f, 0.03f, 0.3f), default, head);              // its top plate
            Part(PrimitiveType.Cylinder, yellow, new Vector3(0f, 1.87f, 0.18f), new Vector3(0.2f, 0.015f, 0.2f), new Vector3(90f, 0f, 0f), head); // eye
            Part(PrimitiveType.Sphere, lens, new Vector3(0f, 1.87f, 0.185f), new Vector3(0.1f, 0.1f, 0.05f), default, head);
            for (int side = -1; side <= 1; side += 2)
            {
                string s = side < 0 ? "L" : "R";
                Part(PrimitiveType.Sphere, plate, new Vector3(side * 0.31f, 1.56f, 0f), new Vector3(0.2f, 0.2f, 0.2f), default, body);   // shoulder
                var arm = Joint("arm" + s, new Vector3(side * 0.33f, 1.52f, 0f), body);
                Part(PrimitiveType.Capsule, dark, new Vector3(side * 0.36f, 1.33f, 0f), new Vector3(0.11f, 0.2f, 0.11f), new Vector3(0f, 0f, side * 8f), arm); // upper arm
                var elbow = Joint("elbow" + s, new Vector3(side * 0.385f, 1.17f, 0.01f), arm);
                Part(PrimitiveType.Capsule, blue, new Vector3(side * 0.4f, 1.01f, 0.04f), new Vector3(0.13f, 0.2f, 0.13f), new Vector3(-10f, 0f, side * 4f), elbow); // forearm
                Part(PrimitiveType.Sphere, dark, new Vector3(side * 0.41f, 0.79f, 0.08f), new Vector3(0.12f, 0.14f, 0.12f), default, elbow); // hand
                // the gun in the right hand: a plain block, pointing the way the forearm does when the arm is up (V1FigureMotion)
                if (side > 0) Part(PrimitiveType.Cube, dark, new Vector3(side * 0.41f, 0.66f, 0.1f), new Vector3(0.06f, 0.26f, 0.1f), default, elbow);
                var leg = Joint("leg" + s, new Vector3(side * 0.13f, 0.97f, 0f), parent);
                Part(PrimitiveType.Capsule, dark, new Vector3(side * 0.13f, 0.8f, 0f), new Vector3(0.16f, 0.24f, 0.16f), default, leg); // thigh
                var knee = Joint("knee" + s, new Vector3(side * 0.13f, 0.57f, 0.01f), leg);
                Part(PrimitiveType.Sphere, blue, new Vector3(side * 0.13f, 0.57f, 0.03f), new Vector3(0.15f, 0.15f, 0.15f), default, knee); // knee
                Part(PrimitiveType.Capsule, blue, new Vector3(side * 0.13f, 0.31f, 0f), new Vector3(0.14f, 0.26f, 0.14f), default, knee); // shin
                Part(PrimitiveType.Cube, dark, new Vector3(side * 0.13f, 0.04f, 0.05f), new Vector3(0.14f, 0.08f, 0.3f), default, knee); // foot
                // wings: four blades fanned out from the back, the top one longest
                for (int i = 0; i < 4; i++)
                {
                    float angle = 52f - i * 27f, length = 0.85f - i * 0.1f;
                    var dir = new Vector3(side * Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad), 0f);
                    var root = new Vector3(side * 0.08f, 1.5f, -0.24f);
                    var wing = Joint("wing" + s + i, root, body);
                    Part(PrimitiveType.Cube, plate, root + dir * (0.12f + length * 0.5f), new Vector3(length, 0.1f, 0.025f), new Vector3(0f, 0f, side * angle), wing);
                }
                Part(PrimitiveType.Cube, yellow, new Vector3(side * 0.09f, 1.5f, 0.165f), new Vector3(0.07f, 0.025f, 0.01f), new Vector3(0f, 0f, side * 20f), body); // chest light
            }
            Part(PrimitiveType.Cube, dark, new Vector3(0f, 1.47f, -0.18f), new Vector3(0.26f, 0.3f, 0.12f), default, body);              // where the wings sit
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
