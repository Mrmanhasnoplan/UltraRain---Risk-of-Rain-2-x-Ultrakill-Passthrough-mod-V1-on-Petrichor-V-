using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;
using Path = System.IO.Path;

namespace V1Bridge.Ror
{
    /// <summary>The stage as ULTRAKILL needs it: every solid collider of the world, as triangles in ULTRAKILL's
    /// coordinates, written to a file UkBridge turns back into colliders. Mesh colliders give their own triangles
    /// (read back from the graphics card when the game shipped them unreadable), boxes, spheres and capsules are
    /// tessellated, terrains are sampled.</summary>
    internal static class StageGeometry
    {
        public const int Magic = 0x4F454731; // "1GEO"

        public struct Result
        {
            public string path, problem;
            public int objects, triangles, vertices, skipped, unreadable;
            public Bounds bounds; // RoR2 coordinates
            public double ms;
        }

        static readonly Dictionary<Mesh, KeyValuePair<Vector3[], int[]>> cache = new Dictionary<Mesh, KeyValuePair<Vector3[], int[]>>();

        public static Result Export(Vector3 origin, float k, int serial)
        {
            var sw = Stopwatch.StartNew();
            var r = new Result { path = Path.Combine(Path.GetTempPath(), "v1bridge_geo_" + serial + ".bin") };
            cache.Clear();
            bool any = false;
            try
            {
                using (var fs = new FileStream(r.path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(Magic);
                    w.Write(0); // object count, filled in at the end
                    int world = LayerIndex.world.intVal;
                    foreach (var col in UnityEngine.Object.FindObjectsOfType<Collider>())
                    {
                        if (!col.enabled || col.isTrigger || !col.gameObject.activeInHierarchy || col.gameObject.layer != world) continue;
                        // loose props that physics moves about are not part of the stage
                        if (col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic) continue;
                        Vector3[] verts;
                        int[] tris;
                        Matrix4x4 m = col.transform.localToWorldMatrix;
                        try
                        {
                            if (!Shape(col, out verts, out tris, ref m, ref r)) { r.skipped++; continue; }
                        }
                        catch (Exception e)
                        {
                            r.skipped++;
                            if (r.problem == null) r.problem = col.name + ": " + e.Message;
                            continue;
                        }
                        if (verts.Length == 0 || tris.Length < 3) { r.skipped++; continue; }
                        bool flip = m.determinant < 0f;
                        w.Write(verts.Length);
                        w.Write(tris.Length);
                        for (int i = 0; i < verts.Length; i++)
                        {
                            Vector3 p = m.MultiplyPoint3x4(verts[i]);
                            if (!any) { r.bounds = new Bounds(p, Vector3.zero); any = true; }
                            else r.bounds.Encapsulate(p);
                            p = (p - origin) * k;
                            w.Write(p.x);
                            w.Write(p.y);
                            w.Write(p.z);
                        }
                        for (int i = 0; i + 2 < tris.Length; i += 3)
                        {
                            w.Write(tris[i]);
                            w.Write(flip ? tris[i + 2] : tris[i + 1]);
                            w.Write(flip ? tris[i + 1] : tris[i + 2]);
                        }
                        r.objects++;
                        r.vertices += verts.Length;
                        r.triangles += tris.Length / 3;
                    }
                    w.Flush();
                    fs.Position = 4;
                    w.Write(r.objects);
                }
            }
            catch (Exception e) { r.problem = e.ToString(); }
            cache.Clear();
            r.ms = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        static bool Shape(Collider col, out Vector3[] verts, out int[] tris, ref Matrix4x4 m, ref Result r)
        {
            verts = null;
            tris = null;
            switch (col)
            {
                case MeshCollider mc:
                {
                    var mesh = mc.sharedMesh;
                    if (mesh == null) return false;
                    if (!cache.TryGetValue(mesh, out var kv))
                    {
                        kv = Read(mesh, ref r);
                        cache[mesh] = kv;
                    }
                    verts = kv.Key;
                    tris = kv.Value;
                    return verts != null;
                }
                case BoxCollider b:
                    Primitives.Box(b.center, b.size, out verts, out tris);
                    return true;
                case SphereCollider s:
                    Primitives.Sphere(s.center, s.radius, out verts, out tris);
                    return true;
                case CapsuleCollider c:
                    Primitives.Capsule(c.center, c.radius, c.height, c.direction, out verts, out tris);
                    return true;
                case TerrainCollider t:
                    if (t.terrainData == null) return false;
                    Primitives.Terrain(t.terrainData, out verts, out tris);
                    // terrains ignore rotation and scale
                    m = Matrix4x4.Translate(col.transform.position);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>A mesh's positions and triangles. Meshes shipped without CPU access are read from their vertex and
        /// index buffers on the graphics card.</summary>
        static KeyValuePair<Vector3[], int[]> Read(Mesh mesh, ref Result r)
        {
            if (mesh.isReadable) return new KeyValuePair<Vector3[], int[]>(mesh.vertices, mesh.triangles);
            r.unreadable++;
            if (!mesh.HasVertexAttribute(VertexAttribute.Position) || mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32
                || mesh.GetVertexAttributeDimension(VertexAttribute.Position) < 3) return default;
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            int offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            Vector3[] verts;
            using (var vb = mesh.GetVertexBuffer(stream))
            {
                int stride = vb.stride, count = vb.count;
                var data = new byte[stride * count];
                vb.GetData(data);
                verts = new Vector3[count];
                for (int i = 0; i < count; i++)
                {
                    int o = i * stride + offset;
                    verts[i] = new Vector3(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8));
                }
            }
            var tris = new List<int>();
            using (var ib = mesh.GetIndexBuffer())
            {
                bool wide = mesh.indexFormat == IndexFormat.UInt32;
                var data = new byte[ib.stride * ib.count];
                ib.GetData(data);
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var sm = mesh.GetSubMesh(s);
                    if (sm.topology != MeshTopology.Triangles) continue;
                    for (int i = 0; i < sm.indexCount; i++)
                    {
                        int at = sm.indexStart + i;
                        int idx = wide ? BitConverter.ToInt32(data, at * 4) : BitConverter.ToUInt16(data, at * 2);
                        tris.Add(idx + sm.baseVertex);
                    }
                }
            }
            return new KeyValuePair<Vector3[], int[]>(verts, tris.ToArray());
        }
    }

    internal static class Primitives
    {
        public static void Box(Vector3 c, Vector3 size, out Vector3[] v, out int[] t)
        {
            Vector3 h = size * 0.5f;
            v = new Vector3[8];
            for (int i = 0; i < 8; i++) v[i] = c + new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
            // outward-facing (clockwise seen from outside, Unity's front face)
            t = new[]
            {
                0, 2, 1, 1, 2, 3, // -z
                4, 5, 6, 5, 7, 6, // +z
                0, 4, 2, 2, 4, 6, // -x
                1, 3, 5, 3, 7, 5, // +x
                0, 1, 4, 1, 5, 4, // -y
                2, 6, 3, 3, 6, 7  // +y
            };
        }

        public static void Sphere(Vector3 c, float radius, out Vector3[] v, out int[] t) => Capsule(c, radius, radius * 2f, 1, out v, out t);

        /// <summary>A capsule (a sphere when its height is its diameter) as rings of latitude round its axis.</summary>
        public static void Capsule(Vector3 c, float radius, float height, int direction, out Vector3[] v, out int[] t)
        {
            const int seg = 12, rings = 4; // per hemisphere
            float half = Mathf.Max(0f, height * 0.5f - radius);
            var verts = new List<Vector3>();
            var tris = new List<int>();
            // rings from the bottom pole to the top pole, the two hemispheres pushed apart by the cylinder
            int rows = rings * 2 + 2;
            for (int row = 0; row < rows; row++)
            {
                bool top = row >= rings + 1;
                int ri = top ? row - 1 : row;
                float lat = -Mathf.PI * 0.5f + Mathf.PI * ri / (rings * 2);
                float y = Mathf.Sin(lat) * radius + (top ? half : -half), rr = Mathf.Cos(lat) * radius;
                for (int s = 0; s < seg; s++)
                {
                    float a = Mathf.PI * 2f * s / seg;
                    var p = new Vector3(Mathf.Cos(a) * rr, y, Mathf.Sin(a) * rr);
                    if (direction == 0) p = new Vector3(p.y, p.x, p.z);
                    else if (direction == 2) p = new Vector3(p.x, p.z, p.y);
                    verts.Add(c + p);
                }
            }
            for (int row = 0; row < rows - 1; row++)
            {
                for (int s = 0; s < seg; s++)
                {
                    int a = row * seg + s, b = row * seg + (s + 1) % seg, d = a + seg, e = b + seg;
                    tris.Add(a); tris.Add(d); tris.Add(b);
                    tris.Add(b); tris.Add(d); tris.Add(e);
                }
            }
            // swapping two axes mirrors the shape: put the faces back outward
            if (direction != 1) for (int i = 0; i + 2 < tris.Count; i += 3) { int x = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = x; }
            v = verts.ToArray();
            t = tris.ToArray();
        }

        public static void Terrain(TerrainData td, out Vector3[] v, out int[] t)
        {
            int res = td.heightmapResolution;
            int step = Mathf.Max(1, (res - 1) / 256);
            int n = (res - 1) / step + 1;
            float[,] h = td.GetHeights(0, 0, res, res);
            Vector3 size = td.size;
            v = new Vector3[n * n];
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    int hx = Mathf.Min(x * step, res - 1), hz = Mathf.Min(z * step, res - 1);
                    v[z * n + x] = new Vector3(hx / (float)(res - 1) * size.x, h[hz, hx] * size.y, hz / (float)(res - 1) * size.z);
                }
            }
            t = new int[(n - 1) * (n - 1) * 6];
            int k = 0;
            for (int z = 0; z < n - 1; z++)
            {
                for (int x = 0; x < n - 1; x++)
                {
                    int a = z * n + x, b = a + 1, c = a + n, d = c + 1;
                    t[k++] = a; t[k++] = c; t[k++] = b;
                    t[k++] = b; t[k++] = c; t[k++] = d;
                }
            }
        }
    }
}
