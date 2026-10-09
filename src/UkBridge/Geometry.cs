using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace V1Bridge.Uk
{
    // The Risk of Rain 2 stage inside ULTRAKILL: each solid collider RorBridge exported becomes a MeshCollider on the
    // Environment layer, with a renderer that draws only depth and transparent black, so ULTRAKILL's shots, enemies
    // and effects are hidden behind the stage exactly where RoR2 draws it in front (Ultracraft's occluder, MIT).
    public partial class Bridge
    {
        const int GeoMagic = 0x4F454731;
        GameObject stageRoot;
        readonly List<Mesh> stageMeshes = new List<Mesh>();
        Material occluderMat;
        Coroutine geoLoad;

        void ClearStage()
        {
            if (geoLoad != null) StopCoroutine(geoLoad);
            geoLoad = null;
            if (stageRoot != null) Destroy(stageRoot);
            stageRoot = null;
            foreach (var m in stageMeshes) if (m != null) Destroy(m);
            stageMeshes.Clear();
        }

        void LoadStage(string path)
        {
            if (!levelPrepared) { Net.Send(Proto.GeoDone + " 0 0 0 not-ready"); return; }
            ClearStage();
            // V1 waits for the ground
            var nm = MonoSingleton<NewMovement>.Instance;
            if (nm != null) nm.rb.isKinematic = true;
            geoLoad = StartCoroutine(LoadStageRoutine(path));
        }

        IEnumerator LoadStageRoutine(string path)
        {
            var total = Stopwatch.StartNew();
            stageRoot = new GameObject("RoR2 stage");
            stageRoot.transform.SetParent(worldRoot.transform, false);
            int objects = 0, tris = 0;
            string problem = "ok";
            FileStream fs = null;
            BinaryReader r = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
                r = new BinaryReader(fs);
                if (r.ReadInt32() != GeoMagic) throw new Exception("not a geometry file");
            }
            catch (Exception e)
            {
                problem = e.Message.Replace(' ', '_');
                r = null;
            }
            if (r != null)
            {
                int count = r.ReadInt32();
                var frame = Stopwatch.StartNew();
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        int vn = r.ReadInt32(), tn = r.ReadInt32();
                        var verts = new Vector3[vn];
                        for (int v = 0; v < vn; v++) verts[v] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        var idx = new int[tn];
                        for (int t = 0; t < tn; t++) idx[t] = r.ReadInt32();
                        AddStageMesh(i, verts, idx);
                        objects++;
                        tris += tn / 3;
                    }
                    catch (Exception e)
                    {
                        problem = ("object_" + i + ":" + e.Message).Replace(' ', '_');
                        break;
                    }
                    // cooking colliders is slow: a slice per frame keeps ULTRAKILL answering
                    if (frame.ElapsedMilliseconds > 12)
                    {
                        yield return null;
                        frame.Restart();
                    }
                }
            }
            r?.Dispose();
            fs?.Dispose();
            geoLoad = null;
            Physics.SyncTransforms();
            Plugin.Log.LogInfo("stage loaded: " + objects + " colliders, " + tris + " triangles, " + total.ElapsedMilliseconds + " ms (" + problem + ")");
            Net.Send(Proto.GeoDone + " " + objects + " " + tris + " " + total.ElapsedMilliseconds + " " + problem);
        }

        void AddStageMesh(int i, Vector3[] verts, int[] idx)
        {
            var mesh = new Mesh { name = "stage " + i };
            if (verts.Length > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.triangles = idx;
            mesh.RecalculateBounds();
            stageMeshes.Add(mesh);
            var go = new GameObject("stage " + i);
            go.transform.SetParent(stageRoot.transform, false);
            go.layer = 8; // Environment
            go.tag = "Floor";
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            // a collider with a renderer is what ULTRAKILL's weapons count as static environment
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = OccluderMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.allowOcclusionWhenDynamic = false;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        Material OccluderMaterial()
        {
            if (occluderMat != null) return occluderMat;
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) Plugin.Log.LogWarning("Hidden/Internal-Colored missing: the stage won't hide ULTRAKILL's effects");
            occluderMat = new Material(sh) { renderQueue = 1990, name = "V1Bridge occluder" };
            occluderMat.SetColor("_Color", new Color(0, 0, 0, 0));
            occluderMat.SetInt("_SrcBlend", (int)BlendMode.One);
            occluderMat.SetInt("_DstBlend", (int)BlendMode.Zero);
            occluderMat.SetInt("_ZWrite", 1);
            occluderMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            occluderMat.SetInt("_Cull", (int)CullMode.Off);
            return occluderMat;
        }

        /// <summary>For the tests: what is under a point, the way V1's feet would find it.</summary>
        string Probe(string rest)
        {
            var a = rest.Split(' ');
            var from = new Vector3(F(a[0]), F(a[1]), F(a[2]));
            if (Physics.Raycast(from, Vector3.down, out var hit, 2000f, 1 << 8, QueryTriggerInteraction.Ignore))
                return S(hit.point.y) + " " + hit.collider.name.Replace(' ', '_');
            return "none";
        }
    }
}
