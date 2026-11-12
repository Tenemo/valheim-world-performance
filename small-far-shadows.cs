using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Every renderer that casts the sun's shadows is a render node in each of the sun's cascades it reaches, and outside the
    // base of another player most of the casters the engine prepares are small things far away: planted crops, mushrooms, flint
    // and dropped items beyond 60 m, whose shadows land in the sun's coarsest cascades a few shadow-map texels across. Such an
    // object stops casting beyond 60 m from the camera and casts again within 55 m; it still receives shadows and draws as
    // before. Only natural objects, crops and items take part, the objects of the game's network world without wear, a
    // character or a vehicle, whose mesh renderers are at most 2 m corner to corner and cast natively. The spatial building
    // renderer reads their native setting through NativeShadows, so a member it merges keeps its own shadows. The isolated
    // client switches it off and on for checks and paired measurements.
    internal static class SmallFarShadows
    {
        private const float Far = 60f, Near = 55f, LargestSize = 2f, RegisterDelay = .5f;
        internal const float FarDistance = Far, NearDistance = Near;
        // Each object is looked at twice a second whatever the frame rate, so a change waits half a second at most, 3.5 m at
        // running speed and within the 55 to 60 m band.
        private const float Period = .5f;
        private const int RegistrationsPerFrame = 64;

        private sealed class Caster
        {
            internal MeshRenderer Renderer;
            internal Transform Moving;
            internal Vector3 Centre;
            internal float Radius;
            internal bool Off;
        }

        private static readonly Harmony patches = new Harmony(Plugin.Id + ".small-far-shadows");
        private static readonly List<Caster> casters = new List<Caster>();
        private static readonly HashSet<MeshRenderer> held = new HashSet<MeshRenderer>();
        private static readonly Queue<(ZNetView view, float time)> pending = new Queue<(ZNetView, float)>();
        // Whether a prefab can hold such a renderer at all, decided once, so most objects cost one lookup as they appear.
        private static readonly Dictionary<int, bool> prefabs = new Dictionary<int, bool>();
        private static readonly List<MeshRenderer> found = new List<MeshRenderer>();
        private static int next;
        // Objects due for a look but not yet visited, carried between frames.
        private static float owed;
        internal static bool Enabled { get; private set; }
        internal static string Failure { get; private set; }

        internal static void SetEnabled(bool enabled)
        {
            if (enabled == Enabled) return;
            if (!enabled) { Stop(); return; }
            Failure = null;
            Enabled = true;
            try
            {
                patches.Patch(AccessTools.Method(typeof(ZNetView), "Awake"), postfix: new HarmonyMethod(typeof(SmallFarShadows), nameof(Created)));
                if (ZNetScene.instance)
                    foreach (var view in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                        if (Candidate(view)) pending.Enqueue((view, 0f));
            }
            catch (Exception error) { Fail(error); }
        }

        // Every object casts natively again.
        internal static void Stop()
        {
            Enabled = false;
            patches.UnpatchSelf();
            foreach (var caster in casters)
                if (caster.Off && caster.Renderer) caster.Renderer.shadowCastingMode = ShadowCastingMode.On;
            casters.Clear();
            held.Clear();
            pending.Clear();
            prefabs.Clear();
            next = 0;
            owed = 0f;
        }

        // The renderer's own setting, which this feature changes only between On and Off.
        internal static ShadowCastingMode NativeShadows(Renderer renderer) =>
            renderer is MeshRenderer mesh && held.Contains(mesh) ? ShadowCastingMode.On : renderer.shadowCastingMode;

        internal static string Report()
        {
            int off = 0, live = 0;
            foreach (var caster in casters) if (caster.Renderer) { live++; if (caster.Off) off++; }
            return "Small far shadows: " + (Enabled ? "on" : "off") + "; " + live + " small renderers tracked, " + off + " casting no sun shadows beyond " + Far + " m" +
                (Failure != null ? "; failed: " + Failure : "");
        }

        private static void Created(ZNetView __instance)
        {
            if (!Enabled) return;
            try { if (Candidate(__instance)) pending.Enqueue((__instance, Time.realtimeSinceStartup)); }
            catch (Exception error) { Fail(error); }
        }

        private static bool Candidate(ZNetView view)
        {
            var zdo = view.GetZDO();
            if (zdo == null) return false;
            int hash = zdo.GetPrefab();
            if (!prefabs.TryGetValue(hash, out bool eligible))
            {
                var source = ZNetScene.instance ? ZNetScene.instance.GetPrefab(hash) : null;
                if (!source) return true;
                prefabs[hash] = eligible = Eligible(source) && Small(source).Count > 0;
                found.Clear();
            }
            return eligible;
        }

        // Natural objects, crops and items: no wear, character or vehicle.
        private static bool Eligible(GameObject root) => !root.GetComponent<WearNTear>() && !root.GetComponentInChildren<Character>(true) && !root.GetComponent<Ship>() && !root.GetComponent<Vagon>();

        // The hierarchy's mesh renderers that cast shadows natively and are at most LargestSize corner to corner, in found.
        private static List<MeshRenderer> Small(GameObject root)
        {
            root.GetComponentsInChildren(true, found);
            found.RemoveAll(renderer => renderer.shadowCastingMode != ShadowCastingMode.On || !renderer.TryGetComponent<MeshFilter>(out var filter) || !filter.sharedMesh ||
                Size(renderer.transform, filter.sharedMesh) > LargestSize);
            return found;
        }

        // Whether a natural object's renderer is small enough for this feature; the spatial building renderer gives merged ones
        // the same distances.
        internal static bool IsSmall(MeshRenderer renderer) => renderer.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh && Size(renderer.transform, filter.sharedMesh) <= LargestSize;
        internal static float Radius(MeshRenderer renderer) => Size(renderer.transform, renderer.GetComponent<MeshFilter>().sharedMesh) * .5f;

        private static float Size(Transform transform, Mesh mesh)
        {
            var scale = transform.lossyScale;
            return mesh.bounds.size.magnitude * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        }

        internal static void LateUpdate()
        {
            if (!Enabled) return;
            try
            {
                Register();
                var camera = Utils.GetMainCamera();
                if (!camera || casters.Count == 0) return;
                Vector3 eye = camera.transform.position;
                owed = Mathf.Min(owed + casters.Count * Time.unscaledDeltaTime / Period, casters.Count);
                int step = (int)owed;
                owed -= step;
                for (int visited = 0; visited < step && casters.Count > 0; visited++)
                {
                    if (next >= casters.Count) next = 0;
                    var caster = casters[next];
                    var renderer = caster.Renderer;
                    if (!renderer)
                    {
                        held.Remove(renderer);
                        casters[next] = casters[casters.Count - 1];
                        casters.RemoveAt(casters.Count - 1);
                        continue;
                    }
                    next++;
                    Vector3 centre = caster.Moving ? caster.Moving.position : caster.Centre;
                    float distance = Vector3.Distance(eye, centre) - caster.Radius;
                    if (!caster.Off && distance > Far) Switch(caster, true);
                    else if (caster.Off && distance < Near) Switch(caster, false);
                }
            }
            catch (Exception error) { Fail(error); }
        }

        private static void Switch(Caster caster, bool off)
        {
            // Another mod or the game may have changed the setting meanwhile; such a renderer is no longer this feature's.
            var mode = caster.Renderer.shadowCastingMode;
            if (mode != (caster.Off ? ShadowCastingMode.Off : ShadowCastingMode.On)) { held.Remove(caster.Renderer); caster.Radius = float.PositiveInfinity; caster.Off = false; return; }
            caster.Renderer.shadowCastingMode = off ? ShadowCastingMode.Off : ShadowCastingMode.On;
            caster.Off = off;
            if (off) held.Add(caster.Renderer); else held.Remove(caster.Renderer);
        }

        // Objects a moment after they are created, once their own setup has run.
        private static void Register()
        {
            float now = Time.realtimeSinceStartup;
            for (int done = 0; done < RegistrationsPerFrame && pending.Count > 0 && now - pending.Peek().time >= RegisterDelay; done++)
            {
                var view = pending.Dequeue().view;
                if (!view || view.GetZDO() == null || !Eligible(view.gameObject)) continue;
                bool moving = view.GetComponentInChildren<Rigidbody>(true);
                foreach (var renderer in Small(view.gameObject))
                {
                    var transform = renderer.transform;
                    var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                    casters.Add(new Caster { Renderer = renderer, Moving = moving ? transform : null, Centre = transform.TransformPoint(mesh.bounds.center), Radius = Size(transform, mesh) * .5f });
                }
                found.Clear();
            }
        }

        private static void Fail(Exception error)
        {
            Failure = error.GetType().Name + ": " + error.Message;
            Plugin.Log?.LogError("World Performance small far shadows stopped: " + error);
            Stop();
        }
    }
}
