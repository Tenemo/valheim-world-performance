using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Every lit torch runs two particle systems, a flame of about 25 billboards and a glow of one, and the engine's per-system
    // work for them, not the particles, is most of what lit torches cost in view: at another player's base, not drawing
    // the flames beyond 30 m took about 1 ms of main-thread work off the exterior and distant views. Beyond that distance a
    // torch's flames come instead from shared copies of its own systems, one per torch type, 64 m area and distance band: each
    // copy emits from a point mesh of its torches' flame places, one particle per torch in turn at their combined rate, so the
    // engine emits them without per-frame script work, and they are the game's particles with the same material, flipbook,
    // colours, noise and lifetimes. Each glow moves into a shared copy unchanged. A torch that comes within 29 m gets its own
    // systems back; its flame stops emitting first, so the copy's particles burn out as the torch's own grow back, and in the
    // other direction the torch's own particles burn out while the copy's grow. Torches on ships and carts keep their own
    // systems, since a copy stays where a torch was handed over. Progression Fix's smallest flame size for distant torches goes
    // to the copies' far band. The isolated client switches it off and on for checks and paired measurements.
    internal static class FarFlames
    {
        private const float Far = 31f, Near = 29f, AreaSize = 64f, FarBand = 140f;
        // Each frame looks at an eighth of the torches, so a handover waits a few frames at most, well within the 29 to 31 m band.
        private const int Slices = 8;
        private static readonly HashSet<string> Torches = new HashSet<string> { "piece_groundtorch", "piece_groundtorch_wood", "piece_groundtorch_green", "piece_groundtorch_blue", "piece_walltorch" };

        private sealed class Torch
        {
            internal Fireplace Fire;
            internal GameObject Holder;
            internal string Type;
            internal ParticleSystem Flame, Glow;
            internal ParticleSystemRenderer FlameRenderer, GlowRenderer;
            internal Vector3 Place, FlamePosition;
            internal Vector2Int Area;
            internal float Rate, Lifetime, Since;
            internal bool Shared, Hidden, Distant;
            internal ParticleSystem.Particle GlowParticle;
            internal Group FlameGroup, GlowGroup;
        }

        private sealed class Group
        {
            internal ParticleSystem System;
            internal readonly List<Torch> Members = new List<Torch>();
            internal bool Changed;
            // For flame copies: the points it emits from, the rate and lifetime of one torch's flame, and when it lost its last
            // torch.
            internal Mesh Places;
            internal float Rate, Lifetime, Emptied;
        }

        private static readonly Harmony patches = new Harmony(Plugin.Id + ".far-flames");
        private static readonly Dictionary<Fireplace, Torch> torches = new Dictionary<Fireplace, Torch>();
        private static readonly List<Torch> order = new List<Torch>();
        // Torches handed over whose own particles are still burning out.
        private static readonly List<Torch> settling = new List<Torch>();
        private static int next;
        private static readonly Dictionary<(string type, Vector2Int area, bool far), Group> flames = new Dictionary<(string, Vector2Int, bool), Group>();
        private static readonly Dictionary<(string type, Vector2Int area), Group> glows = new Dictionary<(string, Vector2Int), Group>();
        private static readonly Dictionary<string, (GameObject flame, GameObject glow)> templates = new Dictionary<string, (GameObject, GameObject)>();
        private static readonly List<Torch> gone = new List<Torch>();
        private static readonly List<(string, Vector2Int, bool)> idleFlames = new List<(string, Vector2Int, bool)>();
        private static readonly List<(string, Vector2Int)> emptyGlows = new List<(string, Vector2Int)>();
        private static readonly List<Vector3> points = new List<Vector3>();
        private static readonly List<int> indices = new List<int>();
        private static ParticleSystem.Particle[] buffer = new ParticleSystem.Particle[256];
        private static GameObject root, inactive;
        private static float farMinimum;
        private static long advanceTicks, advanceFrames;
        internal static bool Enabled { get; private set; }
        internal static string Failure { get; private set; }

        internal static void SetEnabled(bool enabled)
        {
            if (enabled == Enabled) return;
            if (!enabled) { Stop(); return; }
            Failure = null;
            Enabled = true;
            advanceTicks = advanceFrames = 0;
            root = new GameObject("World Performance far flames");
            UnityEngine.Object.DontDestroyOnLoad(root);
            inactive = new GameObject("templates");
            inactive.SetActive(false);
            inactive.transform.SetParent(root.transform, false);
            patches.Patch(AccessTools.Method(typeof(Fireplace), "Start"), postfix: new HarmonyMethod(typeof(FarFlames), nameof(Started)));
            foreach (var fire in UnityEngine.Object.FindObjectsByType<Fireplace>(FindObjectsSortMode.None)) Register(fire);
        }

        // Gives every torch its own systems back and removes the copies.
        internal static void Stop()
        {
            if (!Enabled) return;
            Enabled = false;
            patches.UnpatchSelf();
            foreach (var torch in torches.Values) Restore(torch);
            foreach (var group in flames.Values)
                if (group.Places) UnityEngine.Object.Destroy(group.Places);
            torches.Clear();
            order.Clear();
            settling.Clear();
            flames.Clear();
            glows.Clear();
            templates.Clear();
            if (root) UnityEngine.Object.Destroy(root);
        }

        private static void Started(Fireplace __instance) => Register(__instance);

        // A torch whose lit object holds a flame system with a glow child; other fires keep their own systems.
        private static void Register(Fireplace fire)
        {
            if (!fire || torches.ContainsKey(fire) || !Torches.Contains(Utils.GetPrefabName(fire.gameObject)) || !fire.m_enabledObject ||
                fire.m_enabledObjectHigh || fire.m_enabledObjectLow || fire.GetComponentInParent<Ship>() || fire.GetComponentInParent<Vagon>()) return;
            ParticleSystem flame = null, glow = null;
            foreach (var system in fire.m_enabledObject.GetComponentsInChildren<ParticleSystem>(true))
                if (system.name.StartsWith("fx_Torch")) flame = system;
                else if (system.name == "flare") glow = system;
            if (!flame || !glow || glow.transform.parent != flame.transform || flame.main.simulationSpace != ParticleSystemSimulationSpace.World ||
                flame.emission.rateOverTime.mode != ParticleSystemCurveMode.Constant || flame.emission.burstCount != 0 || !float.IsInfinity(glow.main.startLifetime.constant)) return;
            var lifetime = flame.main.startLifetime;
            var torch = new Torch
            {
                Fire = fire, Holder = fire.m_enabledObject, Type = Utils.GetPrefabName(fire.gameObject) + "/" + flame.name, Place = fire.transform.position,
                Flame = flame, Glow = glow, FlameRenderer = flame.GetComponent<ParticleSystemRenderer>(), GlowRenderer = glow.GetComponent<ParticleSystemRenderer>(),
                Rate = flame.emission.rateOverTime.constant,
                Lifetime = lifetime.mode == ParticleSystemCurveMode.TwoConstants ? lifetime.constantMax : lifetime.mode == ParticleSystemCurveMode.Constant ? lifetime.constant : lifetime.curveMultiplier
            };
            torches.Add(fire, torch);
            order.Add(torch);
        }

        internal static void Update()
        {
            if (!Enabled) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Advance(); }
            catch (Exception error)
            {
                Failure = error.ToString();
                Plugin.Log.LogError(error);
                Stop();
            }
            advanceTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            advanceFrames++;
        }

        private static void Advance()
        {
            var camera = Utils.GetMainCamera();
            if (!camera) return;
            Vector3 eye = camera.transform.position;
            float now = Time.time;
            for (int step = (order.Count + Slices - 1) / Slices; step > 0; step--)
            {
                if (next >= order.Count) next = 0;
                var torch = order[next++];
                // A torch's systems go with it.
                if (!torch.Fire) { gone.Add(torch); continue; }
                bool lit = torch.Holder.activeInHierarchy;
                float distance = Vector3.Distance(eye, torch.Place);
                if (torch.Shared && (!lit || distance < Near)) Restore(torch);
                else if (!torch.Shared && lit && distance > Far) Share(torch, now, distance > FarBand);
                if (torch.Shared && (distance > FarBand) != torch.Distant)
                {
                    Leave(torch.FlameGroup, torch);
                    torch.Distant = !torch.Distant;
                    Join(torch.FlameGroup = FlameGroup(torch), torch);
                }
            }
            for (int index = settling.Count - 1; index >= 0; index--)
            {
                var torch = settling[index];
                if (torch.Shared && torch.Fire && now - torch.Since < torch.Lifetime) continue;
                settling.RemoveAt(index);
                if (!torch.Shared || !torch.Fire) continue;
                // The torch's own particles have lived out their lifetime; its systems stop and stay out of the camera's passes.
                // The last particle update ran a fraction of a frame earlier, so a particle born just before the handover can still
                // be finishing; it goes too rather than waiting frozen for the torch's return.
                torch.Flame.Pause(false);
                torch.Flame.Clear(false);
                torch.Glow.Pause(false);
                torch.FlameRenderer.forceRenderingOff = true;
                torch.Hidden = true;
            }
            foreach (var torch in gone)
            {
                Restore(torch);
                torches.Remove(torch.Fire);
                order.Remove(torch);
            }
            gone.Clear();
            // Copies whose torches have all left go once their last particles burn out, so they don't pile up along a journey.
            foreach (var pair in flames)
            {
                if (pair.Value.Changed) RebuildFlames(pair.Value, now);
                if (pair.Value.Members.Count == 0 && now - pair.Value.Emptied > pair.Value.Lifetime) idleFlames.Add(pair.Key);
            }
            foreach (var key in idleFlames) Remove(flames, key);
            idleFlames.Clear();
            foreach (var pair in glows)
                if (pair.Value.Members.Count == 0) emptyGlows.Add(pair.Key);
                else if (pair.Value.Changed) RebuildGlows(pair.Value);
            foreach (var key in emptyGlows) Remove(glows, key);
            emptyGlows.Clear();
        }

        // The torch's flame stops emitting and its copy takes over at once; its glow moves to the glow copy unchanged.
        private static void Share(Torch torch, float now, bool distant)
        {
            torch.Shared = true;
            torch.Hidden = false;
            torch.Since = now;
            torch.Distant = distant;
            torch.FlamePosition = torch.Flame.transform.position;
            torch.Area = new Vector2Int(Mathf.FloorToInt(torch.FlamePosition.x / AreaSize), Mathf.FloorToInt(torch.FlamePosition.z / AreaSize));
            var emission = torch.Flame.emission;
            emission.enabled = false;
            int alive = torch.Glow.GetParticles(buffer, 1);
            var particle = alive > 0 ? buffer[0] : new ParticleSystem.Particle { startSize = torch.Glow.main.startSize.constant, startColor = torch.Glow.main.startColor.color };
            particle.position = torch.Glow.transform.position;
            particle.velocity = Vector3.zero;
            particle.startLifetime = particle.remainingLifetime = float.PositiveInfinity;
            torch.GlowParticle = particle;
            torch.GlowRenderer.forceRenderingOff = true;
            if (!glows.TryGetValue((torch.Type, torch.Area), out var glow)) glows.Add((torch.Type, torch.Area), glow = Copy(torch, false));
            Join(torch.GlowGroup = glow, torch);
            Join(torch.FlameGroup = FlameGroup(torch), torch);
            settling.Add(torch);
        }

        // The torch's own systems return as the game runs them; the copy's particles from it burn out on their own. A stopped
        // flame starts again empty, so UnseenEffects treats it as a relit torch's and simulates it until formed even out of
        // view, rather than leaving it empty until the camera turns to it.
        private static void Restore(Torch torch)
        {
            if (!torch.Shared) return;
            torch.Shared = false;
            Leave(torch.GlowGroup, torch);
            Leave(torch.FlameGroup, torch);
            torch.GlowGroup = torch.FlameGroup = null;
            if (!torch.Flame || !torch.Glow) return;
            var emission = torch.Flame.emission;
            emission.enabled = true;
            torch.FlameRenderer.forceRenderingOff = false;
            torch.GlowRenderer.forceRenderingOff = false;
            if (torch.Hidden && torch.Flame.gameObject.activeInHierarchy)
            {
                torch.Flame.Play(false);
                torch.Glow.Play(false);
                if (torch.Flame.TryGetComponent(out UnseenEffect effect)) UnseenEffects.Started(effect);
            }
            torch.Hidden = false;
        }

        private static Group FlameGroup(Torch torch)
        {
            var key = (torch.Type, torch.Area, torch.Distant);
            if (!flames.TryGetValue(key, out var group)) flames.Add(key, group = Copy(torch, true));
            return group;
        }

        private static void Join(Group group, Torch torch)
        {
            group.Members.Add(torch);
            group.Changed = true;
        }

        private static void Leave(Group group, Torch torch)
        {
            if (group == null) return;
            group.Members.Remove(torch);
            group.Changed = true;
        }

        private static void Remove<TKey>(Dictionary<TKey, Group> groups, TKey key)
        {
            var group = groups[key];
            if (group.System) UnityEngine.Object.Destroy(group.System.gameObject);
            if (group.Places) UnityEngine.Object.Destroy(group.Places);
            groups.Remove(key);
        }

        // A copy of the torch's flame or glow system in world space. A flame copy emits from its torches' places in turn at their
        // combined rate, within the flame's own emitter radius and in any direction, as a sphere emitter sends them; a glow
        // copy holds its torches' saved glow particles. Copies are made under an inactive object, so no feature registers them
        // as effects of their own.
        private static Group Copy(Torch torch, bool flame)
        {
            if (!templates.TryGetValue(torch.Type, out var pair))
            {
                var flameTemplate = UnityEngine.Object.Instantiate(torch.Flame.gameObject, inactive.transform, false);
                foreach (var child in flameTemplate.transform.Cast<Transform>().ToArray()) UnityEngine.Object.DestroyImmediate(child.gameObject);
                var glowTemplate = UnityEngine.Object.Instantiate(torch.Glow.gameObject, inactive.transform, false);
                foreach (var template in new[] { flameTemplate, glowTemplate })
                {
                    foreach (var component in template.GetComponents<Component>())
                        if (!(component is Transform) && !(component is ParticleSystem) && !(component is ParticleSystemRenderer)) UnityEngine.Object.DestroyImmediate(component);
                    var system = template.GetComponent<ParticleSystem>();
                    system.Clear(false);
                    var main = system.main;
                    main.simulationSpace = ParticleSystemSimulationSpace.World;
                    main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                    main.maxParticles = 100000;
                    main.playOnAwake = true;
                    var emission = system.emission;
                    emission.enabled = false;
                    template.GetComponent<ParticleSystemRenderer>().forceRenderingOff = false;
                }
                var shape = flameTemplate.GetComponent<ParticleSystem>().shape;
                float radius = shape.enabled ? shape.radius : 0f;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Mesh;
                shape.meshShapeType = ParticleSystemMeshShapeType.Vertex;
                shape.meshSpawnMode = ParticleSystemShapeMultiModeValue.Loop;
                // A loop a torch's emission interval long gives each place one particle in turn.
                shape.meshSpawnSpeed = torch.Rate;
                shape.meshSpawnSpread = 0f;
                shape.useMeshColors = false;
                shape.position = Vector3.zero;
                shape.rotation = Vector3.zero;
                shape.scale = Vector3.one;
                shape.randomPositionAmount = radius;
                shape.randomDirectionAmount = 1f;
                // Glows of a group sort among themselves as the game sorts their separate systems, farthest first.
                glowTemplate.GetComponent<ParticleSystemRenderer>().sortMode = ParticleSystemSortMode.Distance;
                templates.Add(torch.Type, pair = (flameTemplate, glowTemplate));
            }
            var copy = UnityEngine.Object.Instantiate(flame ? pair.flame : pair.glow, inactive.transform, false);
            copy.name = (flame ? "flames " : "glows ") + torch.Type + " " + torch.Area + (flame && torch.Distant ? " far" : "");
            copy.transform.SetPositionAndRotation(new Vector3((torch.Area.x + .5f) * AreaSize, torch.FlamePosition.y, (torch.Area.y + .5f) * AreaSize), Quaternion.identity);
            copy.transform.localScale = Vector3.one;
            copy.transform.SetParent(root.transform, true);
            var group = new Group { System = copy.GetComponent<ParticleSystem>(), Rate = torch.Rate, Lifetime = torch.Lifetime };
            if (flame)
            {
                group.Places = new Mesh { name = copy.name };
                var shape = group.System.shape;
                shape.mesh = group.Places;
                if (torch.Distant) group.System.GetComponent<ParticleSystemRenderer>().minParticleSize = farMinimum;
            }
            group.System.Play(false);
            return group;
        }

        // The copy emits from its torches' places, relative to itself, at as many torches' rate.
        private static void RebuildFlames(Group group, float now)
        {
            group.Changed = false;
            int count = group.Members.Count;
            if (count == 0) group.Emptied = now;
            var origin = group.System.transform.position;
            points.Clear();
            indices.Clear();
            // Each place is a triangle with all three corners on it, since vertex emission reads the vertices of triangles.
            for (int index = 0; index < count; index++)
            {
                points.Add(group.Members[index].FlamePosition - origin);
                indices.Add(index);
                indices.Add(index);
                indices.Add(index);
            }
            group.Places.Clear();
            group.Places.SetVertices(points);
            group.Places.SetTriangles(indices, 0);
            var shape = group.System.shape;
            shape.mesh = group.Places;
            var emission = group.System.emission;
            emission.enabled = count > 0;
            emission.rateOverTime = group.Rate * count;
        }

        private static void RebuildGlows(Group group)
        {
            group.Changed = false;
            if (buffer.Length < group.Members.Count) buffer = new ParticleSystem.Particle[Mathf.NextPowerOfTwo(group.Members.Count)];
            for (int index = 0; index < group.Members.Count; index++) buffer[index] = group.Members[index].GlowParticle;
            group.System.SetParticles(buffer, group.Members.Count);
        }

        // Progression Fix keeps distant torch flames at least three pixels tall from 160 m, blending in from 80 m; the far band
        // takes its size at 150 m. Without Progression Fix the copies keep the game's own sizes.
        internal static void BeforeCamera(Camera camera)
        {
            if (!Enabled || camera != Utils.GetMainCamera() || !Chainloader.PluginInfos.ContainsKey("valheim.epiclootprogressionfix")) return;
            float minimum = 3f / Mathf.Max(1, camera.pixelHeight) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(80f, 160f, 150f));
            if (minimum == farMinimum) return;
            farMinimum = minimum;
            foreach (var pair in flames)
                if (pair.Key.far && pair.Value.System) pair.Value.System.GetComponent<ParticleSystemRenderer>().minParticleSize = minimum;
        }

        // The script's average is over the frames since the last report.
        internal static string Report()
        {
            double script = advanceFrames > 0 ? advanceTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / advanceFrames : double.NaN;
            advanceTicks = advanceFrames = 0;
            int shared = torches.Values.Count(torch => torch.Shared), hidden = torches.Values.Count(torch => torch.Hidden);
            return torches.Count + " torches, " + shared + " drawn by copies (" + hidden + " with their own systems stopped), " + flames.Count + " flame copies with " +
                flames.Values.Sum(group => group.System ? group.System.particleCount : 0) + " particles, " + glows.Count + " glow copies with " +
                glows.Values.Sum(group => group.Members.Count) + " glows, " + script.ToString("0.000") + " ms of script a frame";
        }
    }
}
