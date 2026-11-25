using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace WorldPerformance
{
    // Mesh particles draw a mesh, and about 160 of the game's mesh particle systems draw the same four-vertex Quad. Every
    // copy of such a system that comes alive adds itself to that mesh's users, which grows with their number: it was most
    // of an enemy's weakness hit effect. Once a world's prefabs are loaded, each mesh particle renderer whose small mesh
    // many particle renderers draw gets its own copy of that mesh, with the same vertices, so it looks as before and a new
    // effect joins only the users of its own prefab's copy.
    internal static class SharedParticleMeshes
    {
        // Copying only small meshes that many renderers draw keeps the copies to a few hundred tiny meshes.
        private const int SharedBy = 8, MostVertices = 1024;
        // The renderers this feature gave copies, and each copy's original, so switching it off gives every renderer drawing
        // a copy, prefab or live effect, the original back.
        private static readonly HashSet<ParticleSystemRenderer> replaced = new HashSet<ParticleSystemRenderer>(Identity<ParticleSystemRenderer>.Instance);
        private static readonly Dictionary<Mesh, Mesh> originals = new Dictionary<Mesh, Mesh>(Identity<Mesh>.Instance);
        private static ZNetScene scene;
        private static bool copying = true;
        internal static bool Running { get; private set; }
        internal static int Copied => replaced.Count;

        // Off gives every renderer its shared meshes back, as natively; the isolated client switches it for checks and paired
        // measurements.
        internal static bool Copying
        {
            get => copying;
            set
            {
                copying = value;
                if (value) CopyAll();
                else RestoreAll();
            }
        }

        internal static void Start() => Running = true;
        internal static void ForgetWorld() => scene = null;

        // After the frame in which a world's scene and its prefabs were set up, including those mods add then.
        internal static void LateUpdate()
        {
            if (!Running || !ZNetScene.instance || ZNetScene.instance == scene) return;
            scene = ZNetScene.instance;
            ReleaseUnused();
            if (copying) CopyAll();
        }

        private static Mesh[] Meshes(ParticleSystemRenderer renderer)
        {
            var meshes = new Mesh[renderer.meshCount];
            renderer.GetMeshes(meshes);
            return meshes;
        }

        // A renderer with several meshes keeps how often it draws each.
        private static void SetMeshes(ParticleSystemRenderer renderer, Mesh[] meshes)
        {
            var weights = new float[meshes.Length];
            int weighted = renderer.GetMeshWeightings(weights);
            renderer.SetMeshes(meshes);
            if (weighted > 0) renderer.SetMeshWeightings(weights, weighted);
        }

        private static void CopyAll()
        {
            if (!Running) return;
            var renderers = Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>()
                .Where(renderer => renderer && renderer.renderMode == ParticleSystemRenderMode.Mesh && renderer.meshCount > 0 && !replaced.Contains(renderer))
                .Select(renderer => (Renderer: renderer, Meshes: Meshes(renderer))).ToList();
            var users = new Dictionary<Mesh, int>(Identity<Mesh>.Instance);
            foreach (var entry in renderers)
                foreach (var mesh in entry.Meshes.Where(mesh => mesh).Distinct(Identity<Mesh>.Instance))
                    users[mesh] = users.TryGetValue(mesh, out int count) ? count + 1 : 1;
            bool Shared(Mesh mesh) => mesh && mesh.isReadable && mesh.vertexCount <= MostVertices && users[mesh] >= SharedBy && !originals.ContainsKey(mesh);
            var made = new Dictionary<Mesh, Mesh>(Identity<Mesh>.Instance);
            foreach (var entry in renderers.Where(entry => entry.Meshes.Any(Shared)))
            {
                made.Clear();
                var own = entry.Meshes.Select(mesh => !Shared(mesh) ? mesh : made.TryGetValue(mesh, out var copy) ? copy : made[mesh] = Copy(mesh)).ToArray();
                replaced.Add(entry.Renderer);
                SetMeshes(entry.Renderer, own);
            }
        }

        private static Mesh Copy(Mesh mesh)
        {
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            originals.Add(copy, mesh);
            return copy;
        }

        // A new world: renderers of the last one are gone, and copies nothing draws any more are let go.
        private static void ReleaseUnused()
        {
            replaced.RemoveWhere(renderer => !renderer);
            if (originals.Count == 0) return;
            var drawn = new HashSet<Mesh>(Identity<Mesh>.Instance);
            foreach (var renderer in Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>())
                if (renderer && renderer.meshCount > 0)
                    foreach (var mesh in Meshes(renderer))
                        if (mesh) drawn.Add(mesh);
            foreach (var copy in originals.Keys.Where(copy => !drawn.Contains(copy)).ToList())
            {
                originals.Remove(copy);
                if (copy) Object.Destroy(copy);
            }
        }

        private static void RestoreAll()
        {
            foreach (var renderer in Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>())
            {
                if (!renderer || renderer.meshCount == 0) continue;
                var meshes = Meshes(renderer);
                bool changed = false;
                for (int index = 0; index < meshes.Length; index++)
                    if (meshes[index] && originals.TryGetValue(meshes[index], out var original)) { meshes[index] = original; changed = true; }
                if (changed) SetMeshes(renderer, meshes);
            }
            replaced.Clear();
            foreach (var copy in originals.Keys)
                if (copy) Object.Destroy(copy);
            originals.Clear();
        }
    }
}
