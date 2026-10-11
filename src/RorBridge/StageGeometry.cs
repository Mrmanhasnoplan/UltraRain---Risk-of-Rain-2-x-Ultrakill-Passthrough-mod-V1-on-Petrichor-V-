using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
            /// <summary>The first few colliders that could not be exported: "name (kind)".</summary>
            public string skippedNames;
            public int objects, triangles, vertices, skipped, unreadable;
            /// <summary>Of the unreadable meshes, how many Unity would not hand over and were read straight from Direct3D.</summary>
            public int readBack;
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
                            if (!Shape(col, out verts, out tris, ref m, ref r)) { Skip(ref r, col); continue; }
                        }
                        catch (Exception e)
                        {
                            Skip(ref r, col);
                            if (r.problem == null) r.problem = col.name + ": " + e.Message;
                            continue;
                        }
                        if (verts.Length == 0 || tris.Length < 3) { Skip(ref r, col); continue; }
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

        static void Skip(ref Result r, Collider col)
        {
            if (r.skipped++ < 8) r.skippedNames = (r.skippedNames != null ? r.skippedNames + ", " : "") + col.name + " (" + col.GetType().Name + ")";
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
        /// index buffers on the graphics card: through Unity first, and where that comes back empty (it does here: all
        /// zeros, and Unity's asynchronous read-back refuses a vertex buffer) straight from Direct3D (D3D11Read). What
        /// was read is checked against the size the mesh itself reports.</summary>
        static KeyValuePair<Vector3[], int[]> Read(Mesh mesh, ref Result r)
        {
            if (mesh.isReadable) return new KeyValuePair<Vector3[], int[]>(mesh.vertices, mesh.triangles);
            r.unreadable++;
            // positions are whole floats, or half floats where the game shipped the mesh with compressed vertices
            var format = mesh.HasVertexAttribute(VertexAttribute.Position) ? mesh.GetVertexAttributeFormat(VertexAttribute.Position) : (VertexAttributeFormat)(-1);
            bool half = format == VertexAttributeFormat.Float16;
            if ((!half && format != VertexAttributeFormat.Float32) || mesh.GetVertexAttributeDimension(VertexAttribute.Position) < 3)
            {
                if (r.problem == null) r.problem = "mesh " + mesh.name + ": positions are " + format + " x" + (mesh.HasVertexAttribute(VertexAttribute.Position) ? mesh.GetVertexAttributeDimension(VertexAttribute.Position) : 0) + ", not read";
                return default;
            }
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            int offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            string tried = "";
            for (int method = 0; method < 2; method++)
            {
                Vector3[] verts;
                var tris = new List<int>();
                try
                {
                    {
                        int stride = mesh.GetVertexBufferStride(stream), count = mesh.vertexCount;
                        var data = Bytes(mesh, stream, method);
                        if (data.Length < stride * count) throw new Exception("vertex buffer of " + data.Length + " bytes for " + count + " x " + stride);
                        verts = new Vector3[count];
                        for (int i = 0; i < count; i++)
                        {
                            int o = i * stride + offset;
                            verts[i] = half
                                ? new Vector3(Mathf.HalfToFloat(BitConverter.ToUInt16(data, o)), Mathf.HalfToFloat(BitConverter.ToUInt16(data, o + 2)), Mathf.HalfToFloat(BitConverter.ToUInt16(data, o + 4)))
                                : new Vector3(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8));
                        }
                    }
                    {
                        bool wide = mesh.indexFormat == IndexFormat.UInt32;
                        var data = Bytes(mesh, -1, method);
                        for (int s = 0; s < mesh.subMeshCount; s++)
                        {
                            var sm = mesh.GetSubMesh(s);
                            if (sm.topology != MeshTopology.Triangles) continue;
                            for (int i = 0; i < sm.indexCount; i++)
                            {
                                int at = sm.indexStart + i;
                                if ((at + 1) * (wide ? 4 : 2) > data.Length) throw new Exception("index buffer of " + data.Length + " bytes is too short");
                                int idx = wide ? BitConverter.ToInt32(data, at * 4) : BitConverter.ToUInt16(data, at * 2);
                                tris.Add(idx + sm.baseVertex);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    tried += (method == 0 ? " through Unity: " : " from Direct3D: ") + e.Message + ";";
                    continue;
                }
                // the mesh still knows its own size: what was read has to fit it
                var read = new Bounds(verts.Length > 0 ? verts[0] : Vector3.zero, Vector3.zero);
                foreach (var v in verts) read.Encapsulate(v);
                float slack = 0.05f + mesh.bounds.size.magnitude * 0.01f;
                if ((read.min - mesh.bounds.min).magnitude < slack && (read.max - mesh.bounds.max).magnitude < slack)
                {
                    if (method == 1) r.readBack++;
                    return new KeyValuePair<Vector3[], int[]>(verts, tris.ToArray());
                }
                tried += (method == 0 ? " through Unity: " : " from Direct3D: ") + read.size + ";";
            }
            if (r.problem == null) r.problem = "mesh " + mesh.name + " could not be read from the graphics card (it is " + mesh.bounds.size + ";" + tried + ")";
            return default;
        }

        /// <summary>The bytes of one of a mesh's vertex streams, or of its index buffer (stream -1).</summary>
        static byte[] Bytes(Mesh mesh, int stream, int method)
        {
            if (method == 1) return D3D11Read.Buffer(stream < 0 ? mesh.GetNativeIndexBufferPtr() : mesh.GetNativeVertexBufferPtr(stream));
            using (var b = stream < 0 ? mesh.GetIndexBuffer() : mesh.GetVertexBuffer(stream))
            {
                var data = new byte[b.stride * b.count];
                b.GetData(data);
                return data;
            }
        }
    }

    /// <summary>A Direct3D 11 buffer's contents, for the meshes Unity will not hand over: the buffer is copied into a
    /// staging buffer of the same size, which can be mapped and read. Unity draws on another thread, so the device is
    /// first told to guard its context (ID3D10Multithread), once, and every copy is made inside that guard. Plain COM
    /// calls through the objects' function tables; nothing is installed and no game file is involved.</summary>
    internal static class D3D11Read
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BufferDesc { public uint ByteWidth; public int Usage; public uint BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }
        [StructLayout(LayoutKind.Sequential)]
        struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetChildFn(IntPtr self, out IntPtr child);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDescFn(IntPtr self, out BufferDesc desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBufferFn(IntPtr self, ref BufferDesc desc, IntPtr initial, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyResourceFn(IntPtr self, IntPtr to, IntPtr from);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr resource, uint subresource, int mapType, uint flags, out Mapped mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr resource, uint subresource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GuardFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetGuardedFn(IntPtr self, int on);

        // places in the function tables (d3d11.h): IUnknown 0-2; ID3D11DeviceChild.GetDevice 3; ID3D11Buffer.GetDesc 10;
        // ID3D11Device.CreateBuffer 3, GetImmediateContext 40; ID3D11DeviceContext.Map 14, Unmap 15, CopyResource 47;
        // ID3D10Multithread.Enter 3, Leave 4, SetMultithreadProtected 5
        static T Fn<T>(IntPtr obj, int slot) where T : class
        {
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T)) as T;
        }
        static void Release(IntPtr obj) { if (obj != IntPtr.Zero) Fn<ReleaseFn>(obj, 2)(obj); }

        static bool guarded;
        public static int Reads;

        public static byte[] Buffer(IntPtr buffer)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) throw new Exception("not Direct3D 11 (" + SystemInfo.graphicsDeviceType + ")");
            if (buffer == IntPtr.Zero) throw new Exception("no buffer on the graphics card");
            IntPtr device = IntPtr.Zero, context = IntPtr.Zero, guard = IntPtr.Zero, staging = IntPtr.Zero;
            try
            {
                Fn<GetChildFn>(buffer, 3)(buffer, out device);
                Fn<GetChildFn>(device, 40)(device, out context);
                var iid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0"); // ID3D10Multithread
                if (Fn<QueryInterfaceFn>(context, 0)(context, ref iid, out guard) < 0 || guard == IntPtr.Zero) throw new Exception("the Direct3D context cannot be guarded");
                if (!guarded)
                {
                    Fn<SetGuardedFn>(guard, 5)(guard, 1);
                    // whatever the drawing thread was in the middle of when the guard went up has finished after this
                    System.Threading.Thread.Sleep(60);
                    guarded = true;
                }
                Fn<GetDescFn>(buffer, 10)(buffer, out var desc);
                if (desc.ByteWidth == 0 || desc.ByteWidth > 1u << 30) throw new Exception("buffer of " + desc.ByteWidth + " bytes");
                var want = new BufferDesc { ByteWidth = desc.ByteWidth, Usage = 3 /* staging */, CPUAccessFlags = 0x20000 /* read */ };
                int hr = Fn<CreateBufferFn>(device, 3)(device, ref want, IntPtr.Zero, out staging);
                if (hr < 0 || staging == IntPtr.Zero) throw new Exception("no staging buffer (0x" + hr.ToString("X8") + ")");
                var data = new byte[desc.ByteWidth];
                Fn<GuardFn>(guard, 3)(guard);
                try
                {
                    Fn<CopyResourceFn>(context, 47)(context, staging, buffer);
                    hr = Fn<MapFn>(context, 14)(context, staging, 0, 1 /* read */, 0, out var mapped);
                    if (hr < 0 || mapped.Data == IntPtr.Zero) throw new Exception("the copy could not be mapped (0x" + hr.ToString("X8") + ")");
                    Marshal.Copy(mapped.Data, data, 0, data.Length);
                    Fn<UnmapFn>(context, 15)(context, staging, 0);
                }
                finally { Fn<GuardFn>(guard, 4)(guard); }
                Reads++;
                return data;
            }
            finally
            {
                Release(staging);
                Release(guard);
                Release(context);
                Release(device);
            }
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
