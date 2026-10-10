using System;
using System.Collections.Generic;
using System.IO;
using RoR2;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Path = System.IO.Path;

namespace V1Bridge.Ror
{
    /// <summary>V1's real look in character select. The model is not part of the mod: UkBridge reads it out of the
    /// player's own running ULTRAKILL and writes it to a file (ModelExport), and this builds the display from it. A
    /// copy is kept in this profile so later sessions show it at once, before ULTRAKILL has started. Until a model
    /// has ever arrived, the placeholder figure stands there.</summary>
    internal static class V1Model
    {
        const int Magic = 0x4C444D31;
        /// <summary>How tall V1 stands in character select, metres.</summary>
        const float Height = 2.05f;
        const string Child = "V1 model";

        public static int Parts, Vertices, Textures;
        public static bool FromCache;
        public static string Problem;
        static byte[] data;

        static string CachePath => Path.Combine(Plugin.ProfileDir, "cache", "v1model.bin");

        /// <summary>At start-up: the copy kept from an earlier session, if there is one.</summary>
        public static void LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                data = File.ReadAllBytes(CachePath);
                FromCache = true;
                Apply();
            }
            catch (Exception e) { Plugin.Log.LogWarning("V1 model cache: " + e.Message); }
        }

        public static void OnMessage(string cmd, string rest)
        {
            if (cmd != Proto.Model) return;
            var a = rest.Split(' ');
            if (a[0] == "none") { Problem = "ULTRAKILL had no V1 body to give"; return; }
            try
            {
                data = File.ReadAllBytes(a[0]);
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllBytes(CachePath, data);
                FromCache = false;
                Apply();
                Plugin.Log.LogInfo("V1's model arrived from ULTRAKILL: " + Parts + " parts, " + Vertices + " vertices, " + Textures + " textures");
            }
            catch (Exception e)
            {
                Problem = e.Message;
                Plugin.Log.LogWarning("V1 model: " + e);
            }
        }

        /// <summary>Onto the display prefab (for character selects to come) and onto any display standing right now.</summary>
        static void Apply()
        {
            if (data == null || V1Survivor.Def == null || V1Survivor.Def.displayPrefab == null) return;
            Build(V1Survivor.Def.displayPrefab.transform);
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t.name == V1Survivor.Def.displayPrefab.name + "(Clone)") Build(t);
            }
        }

        static void Build(Transform display)
        {
            var old = display.Find(Child);
            if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
            var root = new GameObject(Child);
            root.transform.SetParent(display, false);
            root.transform.localScale = Vector3.one * Height;
            root.layer = display.gameObject.layer;
            var shader = Addressables.LoadAssetAsync<Shader>("RoR2/Base/Shaders/HGStandard.shader").WaitForCompletion() ?? LegacyShaderAPI.Find("Hopoo Games/Deferred/Standard");
            using (var r = new BinaryReader(new MemoryStream(data)))
            {
                if (r.ReadInt32() != Magic) throw new Exception("not a V1 model file");
                int parts = r.ReadInt32();
                var renderers = new List<KeyValuePair<MeshRenderer, int>>();
                var colors = new List<Color>();
                Vertices = 0;
                for (int p = 0; p < parts; p++)
                {
                    int tex = r.ReadInt32();
                    var color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    int vn = r.ReadInt32();
                    var v = new Vector3[vn];
                    var n = new Vector3[vn];
                    var uv = new Vector2[vn];
                    for (int i = 0; i < vn; i++)
                    {
                        v[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        n[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                    }
                    int tn = r.ReadInt32();
                    var tri = new int[tn];
                    for (int i = 0; i < tn; i++) tri[i] = r.ReadInt32();
                    var mesh = new Mesh { name = "V1 part " + p };
                    if (vn > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    mesh.vertices = v;
                    mesh.normals = n;
                    mesh.uv = uv;
                    mesh.triangles = tri;
                    mesh.RecalculateBounds();
                    var go = new GameObject("part " + p);
                    go.layer = root.layer;
                    go.transform.SetParent(root.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    renderers.Add(new KeyValuePair<MeshRenderer, int>(go.AddComponent<MeshRenderer>(), tex));
                    colors.Add(color);
                    Vertices += vn;
                }
                int tc = r.ReadInt32();
                var textures = new Texture2D[tc];
                for (int t = 0; t < tc; t++)
                {
                    bool point = r.ReadInt32() == 1;
                    var png = r.ReadBytes(r.ReadInt32());
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "V1 texture " + t };
                    tex.LoadImage(png);
                    // ULTRAKILL's textures are small and meant to be seen as pixels
                    tex.filterMode = point ? FilterMode.Point : FilterMode.Bilinear;
                    textures[t] = tex;
                }
                for (int i = 0; i < renderers.Count; i++)
                {
                    var m = new Material(shader) { color = colors[i] };
                    int ti = renderers[i].Value;
                    if (ti >= 0 && ti < tc)
                    {
                        m.mainTexture = textures[ti];
                        // ULTRAKILL lights V1 flatly; under character select's dim lamps alone it would stand in the dark
                        m.SetTexture("_EmTex", textures[ti]);
                        m.SetColor("_EmColor", Color.white);
                        m.SetFloat("_EmPower", 0.6f);
                    }
                    renderers[i].Key.sharedMaterial = m;
                }
                Parts = parts;
                Textures = tc;
            }
            // the placeholder steps aside
            var placeholder = display.Find("Model");
            if (placeholder != null) placeholder.gameObject.SetActive(false);
        }
    }
}
