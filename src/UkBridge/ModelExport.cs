using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace V1Bridge.Uk
{
    // V1's look for Risk of Rain 2's character select, taken from the player's own ULTRAKILL as it runs: the body of
    // the platformer V1 (the same source Ultracraft uses for other players' V1s, MIT), posed by its own animator, its
    // meshes baked and its textures read back, written to a file RorBridge builds a display model from. Nothing of
    // it is shipped with the mod.
    public partial class Bridge
    {
        public const int ModelMagic = 0x4C444D31; // "1MDL"
        bool modelSent, modelRunning;

        void UpdateModelExport()
        {
            if (modelSent || modelRunning || !levelPrepared || !Net.Connected) return;
            modelRunning = true;
            StartCoroutine(ExportModel());
        }

        IEnumerator ExportModel()
        {
            GameObject holder = null;
            string result = "none";
            GameObject body = null;
            try
            {
                var pt = MonoSingleton<PlayerTracker>.Instance;
                var prefab = pt != null ? pt.platformerPlayerPrefab : null;
                var pm = prefab != null ? prefab.GetComponentInChildren<PlatformerMovement>(true) : null;
                if (pm != null)
                {
                    holder = new GameObject("V1Bridge model holder");
                    holder.SetActive(false);
                    holder.transform.position = new Vector3(0f, -5000f, 0f);
                    body = Instantiate(pm.gameObject, holder.transform);
                    body.transform.localPosition = Vector3.zero;
                    body.transform.localRotation = Quaternion.identity;
                    foreach (var c in body.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(c);
                    foreach (var c in body.GetComponentsInChildren<Joint>(true)) DestroyImmediate(c);
                    foreach (var c in body.GetComponentsInChildren<Collider>(true)) DestroyImmediate(c);
                    foreach (var c in body.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(c);
                    foreach (var c in body.GetComponentsInChildren<AudioSource>(true)) DestroyImmediate(c);
                    foreach (var c in body.GetComponentsInChildren<Camera>(true)) DestroyImmediate(c);
                    foreach (var c in body.GetComponentsInChildren<Light>(true)) DestroyImmediate(c);
                    var anim = body.GetComponent<Animator>();
                    if (anim != null) anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    holder.SetActive(true);
                }
            }
            catch (Exception e) { result = "setup:" + e.Message; }
            if (body == null)
            {
                modelSent = true;
                modelRunning = false;
                Plugin.Log.LogWarning("V1 model: no platformer V1 to take it from (" + result + ")");
                Net.Send(Proto.Model + " none");
                yield break;
            }
            // a moment for its animator to stand it up
            yield return new WaitForSeconds(0.6f);
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "v1bridge_model.bin");
                result = WriteModel(body, path);
                Net.Send(Proto.Model + " " + path + " " + result);
                Plugin.Log.LogInfo("V1 model written: " + result);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("V1 model: " + e);
                Net.Send(Proto.Model + " none");
            }
            Destroy(holder);
            modelSent = true;
            modelRunning = false;
        }

        class Part
        {
            public int tex;
            public Color color;
            public List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
            public List<Vector2> uv = new List<Vector2>();
            public List<int> tri = new List<int>();
        }

        static string WriteModel(GameObject body, string path)
        {
            var parts = new List<Part>();
            var textures = new List<Texture>();
            var rootInv = body.transform.worldToLocalMatrix;
            var baked = new Mesh();
            foreach (var r in body.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                Mesh mesh;
                Matrix4x4 toRoot;
                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null) continue;
                    smr.BakeMesh(baked, true);
                    mesh = baked;
                    toRoot = rootInv * Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                }
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    mesh = mf.sharedMesh;
                    toRoot = rootInv * r.transform.localToWorldMatrix;
                }
                else continue;
                var verts = mesh.vertices;
                var norms = mesh.normals;
                var uvs = mesh.uv;
                var mats = r.sharedMaterials;
                for (int sub = 0; sub < mesh.subMeshCount && sub < mats.Length; sub++)
                {
                    var mat = mats[sub];
                    if (mat == null) continue;
                    var tex = mat.HasProperty("_MainTex") ? mat.mainTexture : null;
                    int ti = -1;
                    if (tex != null)
                    {
                        ti = textures.IndexOf(tex);
                        if (ti < 0) { ti = textures.Count; textures.Add(tex); }
                    }
                    var part = new Part { tex = ti, color = mat.HasProperty("_Color") ? mat.color : Color.white };
                    var remap = new Dictionary<int, int>();
                    foreach (int i in mesh.GetTriangles(sub))
                    {
                        if (!remap.TryGetValue(i, out int j))
                        {
                            j = part.v.Count;
                            remap[i] = j;
                            part.v.Add(toRoot.MultiplyPoint3x4(verts[i]));
                            part.n.Add(i < norms.Length ? toRoot.MultiplyVector(norms[i]).normalized : Vector3.up);
                            part.uv.Add(i < uvs.Length ? uvs[i] : Vector2.zero);
                        }
                        part.tri.Add(j);
                    }
                    if (part.tri.Count >= 3) parts.Add(part);
                }
            }
            Destroy(baked);
            if (parts.Count == 0) throw new Exception("no meshes on the body");

            // standing on the origin, one unit tall: Risk of Rain 2 sizes it
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            foreach (var p in parts) foreach (var v in p.v) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            float height = Mathf.Max(0.001f, max.y - min.y);
            var shift = new Vector3((min.x + max.x) * 0.5f, min.y, (min.z + max.z) * 0.5f);
            int vertCount = 0;
            using (var w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)))
            {
                w.Write(ModelMagic);
                w.Write(parts.Count);
                foreach (var p in parts)
                {
                    w.Write(p.tex);
                    w.Write(p.color.r); w.Write(p.color.g); w.Write(p.color.b); w.Write(p.color.a);
                    w.Write(p.v.Count);
                    for (int i = 0; i < p.v.Count; i++)
                    {
                        var v = (p.v[i] - shift) / height;
                        w.Write(v.x); w.Write(v.y); w.Write(v.z);
                        w.Write(p.n[i].x); w.Write(p.n[i].y); w.Write(p.n[i].z);
                        w.Write(p.uv[i].x); w.Write(p.uv[i].y);
                    }
                    w.Write(p.tri.Count);
                    foreach (int t in p.tri) w.Write(t);
                    vertCount += p.v.Count;
                }
                w.Write(textures.Count);
                foreach (var tex in textures)
                {
                    var png = ReadablePng(tex);
                    w.Write(tex.filterMode == FilterMode.Point ? 1 : 0);
                    w.Write(png.Length);
                    w.Write(png);
                }
            }
            return "parts=" + parts.Count + " verts=" + vertCount + " textures=" + textures.Count;
        }

        /// <summary>A texture's pixels as PNG, whether or not the game keeps them readable.</summary>
        static byte[] ReadablePng(Texture tex)
        {
            int w = Mathf.Min(tex.width, 1024), h = Mathf.Min(tex.height, 1024);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var png = copy.EncodeToPNG();
            Destroy(copy);
            return png;
        }
    }
}
