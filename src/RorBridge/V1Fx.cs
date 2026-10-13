using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RoR2;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;

namespace V1Bridge.Ror
{
    /// <summary>What V1's weapons do, shown to the other players. ULTRAKILL's own picture of its shots, explosions and
    /// projectiles exists only in the V1 player's window; to everyone else V1's attacks were invisible. The V1
    /// player's ULTRAKILL says in plain facts what happened (FX, FXP: Proto), their game passes that on through the
    /// host (V1Net), and every other player's game draws something simple of the mod's own from it: a bright line for
    /// a hitscan shot, a ball of light for an explosion, a small light for each thing in the air (rockets, nails,
    /// saws, pellets, coins, the Whiplash's hook with its line). Meant to be read, not admired; nothing of
    /// ULTRAKILL's art is involved. Never drawn in the V1 player's own game, which has the real thing.
    /// Between games the lines carry the V1 body's network id and Risk of Rain 2 coordinates:
    ///   FX owner b x1 y1 z1 x2 y2 z2 kind | FX owner e x y z radius | FX owner p arm | FXP owner id,kind,x,y,z;...
    /// (p: that V1 threw a punch, and its figure swings its arm: V1FigureMotion.Punch)</summary>
    internal static class V1Fx
    {
        /// <summary>For the tests: ask ULTRAKILL for the facts even with nobody else in the game, and draw them here too.</summary>
        public static bool Force, ShowOwn;
        public static int LinesFromUk, BeamsDrawn, BlastsDrawn, MarkersPeak, PunchesShown;
        public static string KindsSeen = "";
        public static string Problem;
        static bool asked;

        static string S(float f) => f.ToString("0.##", CultureInfo.InvariantCulture);
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        // ------------------------------------------------------------ the V1 player's game

        /// <summary>Every frame of a bridged run: ULTRAKILL reports only while there is somebody to show it to.</summary>
        public static void Tick()
        {
            bool want = Force || NetworkUser.readOnlyInstancesList.Count > 1;
            if (want == asked) return;
            asked = want;
            UkLink.Send(Proto.FxWanted + " " + (want ? 1 : 0));
        }

        /// <summary>From this game's ULTRAKILL, in its coordinates: on to the other players, in Risk of Rain 2's.</summary>
        public static void OnMessage(string cmd, string rest)
        {
            if (cmd == "DISCONNECTED" || cmd == "CONNECTED") { asked = false; return; }
            if (cmd != Proto.Fx && cmd != Proto.FxList) return;
            var body = V1Drive.Body;
            var net = body != null ? body.GetComponent<NetworkIdentity>() : null;
            if (net == null) return;
            LinesFromUk++;
            var sb = new StringBuilder(cmd).Append(' ').Append(net.netId.Value).Append(' ');
            if (cmd == Proto.Fx)
            {
                var a = rest.Split(' ');
                if (a[0] == "b" && a.Length >= 8)
                {
                    Vector3 from = V1Drive.ToRor(new Vector3(F(a[1]), F(a[2]), F(a[3]))), to = V1Drive.ToRor(new Vector3(F(a[4]), F(a[5]), F(a[6])));
                    sb.Append("b ").Append(S(from.x)).Append(' ').Append(S(from.y)).Append(' ').Append(S(from.z)).Append(' ').Append(S(to.x)).Append(' ').Append(S(to.y)).Append(' ').Append(S(to.z)).Append(' ').Append(a[7]);
                }
                else if (a[0] == "e" && a.Length >= 5)
                {
                    Vector3 at = V1Drive.ToRor(new Vector3(F(a[1]), F(a[2]), F(a[3])));
                    // ULTRAKILL's size is how wide the blast grows, in its units
                    sb.Append("e ").Append(S(at.x)).Append(' ').Append(S(at.y)).Append(' ').Append(S(at.z)).Append(' ').Append(S(Mathf.Clamp(F(a[4]) * 0.5f / V1Drive.K, 1f, 14f)));
                }
                else if (a[0] == "p") sb.Append("p ").Append(a.Length > 1 ? a[1] : "0");
                else return;
            }
            else
            {
                foreach (var entry in rest.Split(';'))
                {
                    var a = entry.Split(',');
                    if (a.Length < 5) continue;
                    Vector3 at = V1Drive.ToRor(new Vector3(F(a[2]), F(a[3]), F(a[4])));
                    sb.Append(a[0]).Append(',').Append(a[1]).Append(',').Append(S(at.x)).Append(',').Append(S(at.y)).Append(',').Append(S(at.z)).Append(';');
                }
            }
            string line = sb.ToString();
            if (ShowOwn) Show(line);
            V1Net.ShareFx(line, cmd == Proto.FxList);
        }

        // ------------------------------------------------------------ every other player's game

        struct Fading { public Transform t; public float born, life, size, length; public bool blast; }
        sealed class Marker { public Transform t, line; public Vector3 target, from; public float seen; public uint owner; public char kind; }
        static readonly List<Fading> fading = new List<Fading>();
        static readonly Dictionary<long, Marker> markers = new Dictionary<long, Marker>();
        static readonly List<long> gone = new List<long>();
        static readonly HashSet<long> listed = new HashSet<long>();
        static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        static Runner runner;

        static Material Mat(string name, Color c, float glow)
        {
            if (materials.TryGetValue(name, out var m) && m != null) return m;
            var shader = Addressables.LoadAssetAsync<Shader>("RoR2/Base/Shaders/HGStandard.shader").WaitForCompletion() ?? LegacyShaderAPI.Find("Hopoo Games/Deferred/Standard");
            m = new Material(shader) { color = c, hideFlags = HideFlags.HideAndDontSave };
            m.SetColor("_EmColor", c);
            m.SetFloat("_EmPower", glow);
            materials[name] = m;
            return m;
        }

        // the shapes are kept and used again: a nailgun or a shotgun asks for dozens a second
        static readonly Stack<Transform> cubes = new Stack<Transform>(), balls = new Stack<Transform>();

        static Transform Shape(PrimitiveType type, Material m, string name)
        {
            var pool = type == PrimitiveType.Cube ? cubes : balls;
            Transform t = null;
            // (a stage change takes the ones lying in the pool with it)
            while (pool.Count > 0 && t == null) t = pool.Pop();
            if (t == null)
            {
                var go = GameObject.CreatePrimitive(type);
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
                var made = go.GetComponent<MeshRenderer>();
                made.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                made.receiveShadows = false;
                t = go.transform;
            }
            t.name = name;
            t.GetComponent<MeshRenderer>().sharedMaterial = m;
            t.rotation = Quaternion.identity;
            t.gameObject.SetActive(true);
            return t;
        }

        static void Done(Transform t, bool cube)
        {
            if (t == null) return;
            var pool = cube ? cubes : balls;
            if (pool.Count >= 96) { UnityEngine.Object.Destroy(t.gameObject); return; }
            t.gameObject.SetActive(false);
            pool.Push(t);
        }

        static void Stretch(Transform t, Vector3 from, Vector3 to, float width)
        {
            Vector3 d = to - from;
            float length = d.magnitude;
            t.position = (from + to) * 0.5f;
            if (length > 0.001f) t.rotation = Quaternion.LookRotation(d / length);
            t.localScale = new Vector3(width, width, Mathf.Max(0.01f, length));
        }

        /// <summary>One line as the games pass it on: draw it.</summary>
        public static void Show(string line)
        {
            try
            {
                if (runner == null && Bridge.I != null) runner = Bridge.I.gameObject.AddComponent<Runner>();
                var a = line.Split(' ');
                if (a.Length < 3) return;
                uint owner = uint.Parse(a[1]);
                if (a[0] == Proto.FxList) { List(owner, a[2]); return; }
                if (a[0] != Proto.Fx) return;
                if (a[2] == "p")
                {
                    // that V1 threw a punch: its figure does
                    var go = NetworkServer.active ? NetworkServer.FindLocalObject(new NetworkInstanceId(owner)) : ClientScene.FindLocalObject(new NetworkInstanceId(owner));
                    var figure = go != null ? V1Figure.On(go.GetComponent<CharacterBody>()) : null;
                    var motion = figure != null ? figure.GetComponent<V1FigureMotion>() : null;
                    if (motion != null) motion.Punch(a.Length > 3 && a[3] == "1");
                    PunchesShown++;
                }
                else if (a[2] == "b" && a.Length >= 10)
                {
                    bool rail = a[9] == "1";
                    Vector3 from = new Vector3(F(a[3]), F(a[4]), F(a[5])), to = new Vector3(F(a[6]), F(a[7]), F(a[8]));
                    float width = rail ? 0.3f : 0.09f;
                    var t = Shape(PrimitiveType.Cube, rail ? Mat("rail", new Color(0.35f, 0.8f, 1f), 1.6f) : Mat("shot", new Color(1f, 0.8f, 0.25f), 1.6f), "V1 shot");
                    Stretch(t, from, to, width);
                    fading.Add(new Fading { t = t, born = Time.time, life = rail ? 0.5f : 0.28f, size = width, length = (to - from).magnitude });
                    BeamsDrawn++;
                }
                else if (a[2] == "e" && a.Length >= 7)
                {
                    var t = Shape(PrimitiveType.Sphere, Mat("blast", new Color(1f, 0.45f, 0.1f), 1.2f), "V1 blast");
                    t.position = new Vector3(F(a[3]), F(a[4]), F(a[5]));
                    t.localScale = Vector3.one * 0.1f;
                    fading.Add(new Fading { t = t, born = Time.time, life = 0.4f, size = F(a[6]) * 2f, blast = true });
                    BlastsDrawn++;
                }
            }
            catch (Exception e) { Problem = e.Message; }
        }

        /// <summary>What of that V1's is in the air now: a light for each, moved to where it is, taken away when it is gone.</summary>
        static void List(uint owner, string list)
        {
            listed.Clear();
            foreach (var entry in list.Split(';'))
            {
                var a = entry.Split(',');
                if (a.Length < 5 || a[1].Length == 0) continue;
                long key = ((long)owner << 32) ^ (uint)int.Parse(a[0]);
                char kind = a[1][0];
                var at = new Vector3(F(a[2]), F(a[3]), F(a[4]));
                listed.Add(key);
                if (!markers.TryGetValue(key, out var m) || m.t == null)
                {
                    float size;
                    Material mat;
                    switch (kind)
                    {
                        case 'g': size = 0.45f; mat = Mat("rocket", new Color(1f, 0.45f, 0.1f), 1.5f); break;
                        case 'c': size = 0.2f; mat = Mat("coin", new Color(1f, 0.8f, 0.15f), 1.5f); break;
                        case 's': size = 0.4f; mat = Mat("saw", new Color(0.8f, 0.85f, 0.9f), 1.5f); break;
                        case 'h': size = 0.25f; mat = Mat("hook", new Color(0.5f, 1f, 0.6f), 2f); break;
                        case 'n': size = 0.1f; mat = Mat("nail", new Color(0.9f, 0.95f, 1f), 1.5f); break;
                        default: size = 0.14f; mat = Mat("pellet", new Color(1f, 0.9f, 0.5f), 1.5f); break;
                    }
                    m = new Marker { t = Shape(PrimitiveType.Sphere, mat, "V1 " + kind), owner = owner, kind = kind };
                    m.t.localScale = Vector3.one * size;
                    m.t.position = at;
                    // the hook has its line back to V1; a pellet or a nail is fast and small, and is drawn as the streak
                    // from where it last was to where it is, which is what makes it read as a shot
                    if (kind == 'h' || kind == 'p' || kind == 'n') m.line = Shape(PrimitiveType.Cube, mat, "V1 " + kind + " line");
                    if (m.line != null) m.line.localScale = Vector3.zero;
                    m.target = at;
                    markers[key] = m;
                    if (KindsSeen.IndexOf(kind) < 0) KindsSeen += kind;
                }
                m.from = m.target;
                m.target = at;
                m.seen = Time.time;
            }
            gone.Clear();
            foreach (var kv in markers) if (kv.Value.owner == owner && !listed.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (long key in gone) Remove(key);
            MarkersPeak = Mathf.Max(MarkersPeak, markers.Count);
        }

        static void Remove(long key)
        {
            if (!markers.TryGetValue(key, out var m)) return;
            Done(m.t, false);
            Done(m.line, true);
            markers.Remove(key);
        }

        sealed class Runner : MonoBehaviour
        {
            void LateUpdate()
            {
                for (int i = fading.Count - 1; i >= 0; i--)
                {
                    var f = fading[i];
                    float age = (Time.time - f.born) / f.life;
                    if (f.t == null || age >= 1f)
                    {
                        Done(f.t, !f.blast);
                        fading.RemoveAt(i);
                        continue;
                    }
                    // a blast swells and is gone; a shot's line thins away
                    if (f.blast) f.t.localScale = Vector3.one * Mathf.Lerp(f.size * 0.25f, f.size, Mathf.Sqrt(age)) * (age > 0.7f ? (1f - age) / 0.3f : 1f);
                    else f.t.localScale = new Vector3(f.size * (1f - age), f.size * (1f - age), f.length);
                }
                if (markers.Count == 0) return;
                gone.Clear();
                float ease = 1f - Mathf.Exp(-30f * Time.deltaTime);
                foreach (var kv in markers)
                {
                    var m = kv.Value;
                    // (the stage changed under it, or its V1's game stopped saying where it is)
                    if (m.t == null || Time.time - m.seen > 0.7f) { gone.Add(kv.Key); continue; }
                    m.t.position = Vector3.Lerp(m.t.position, m.target, ease);
                    if (m.line == null) continue;
                    if (m.kind != 'h')
                    {
                        Stretch(m.line, m.from, m.target, m.kind == 'n' ? 0.04f : 0.06f);
                        continue;
                    }
                    var go = NetworkServer.active ? NetworkServer.FindLocalObject(new NetworkInstanceId(m.owner)) : ClientScene.FindLocalObject(new NetworkInstanceId(m.owner));
                    var body = go != null ? go.GetComponent<CharacterBody>() : null;
                    if (body != null) Stretch(m.line, body.corePosition, m.t.position, 0.05f);
                }
                foreach (long key in gone) Remove(key);
            }
        }
    }
}
