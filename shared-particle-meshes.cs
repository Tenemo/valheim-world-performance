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
        // The meshes this feature replaced, so switching it off gives each renderer its own back.
        private static readonly Dictionary<ParticleSystemRenderer, Mesh[]> replaced = new Dictionary<ParticleSystemRenderer, Mesh[]>(Identity<ParticleSystemRenderer>.Instance);
        private static readonly List<Mesh> copies = new List<Mesh>();
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

        // After the frame in which a world's scene and its prefabs were set up, including those mods add then.
        internal static void LateUpdate()
        {
            if (!Running || !ZNetScene.instance || ZNetScene.instance == scene) return;
            scene = ZNetScene.instance;
            if (copying) CopyAll();
        }

        private static void CopyAll()
        {
            if (!Running) return;
            var renderers = Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>()
                .Where(renderer => renderer && renderer.renderMode == ParticleSystemRenderMode.Mesh && renderer.meshCount > 0 && !replaced.ContainsKey(renderer))
                .Select(renderer => { var meshes = new Mesh[renderer.meshCount]; renderer.GetMeshes(meshes); return (Renderer: renderer, Meshes: meshes); }).ToList();
            var users = new Dictionary<Mesh, int>(Identity<Mesh>.Instance);
            foreach (var entry in renderers)
                foreach (var mesh in entry.Meshes.Where(mesh => mesh).Distinct(Identity<Mesh>.Instance))
                    users[mesh] = users.TryGetValue(mesh, out int count) ? count + 1 : 1;
            bool Shared(Mesh mesh) => mesh && mesh.isReadable && mesh.vertexCount <= MostVertices && users[mesh] >= SharedBy;
            var made = new Dictionary<Mesh, Mesh>(Identity<Mesh>.Instance);
            foreach (var entry in renderers.Where(entry => entry.Meshes.Any(Shared)))
            {
                made.Clear();
                var own = entry.Meshes.Select(mesh => !Shared(mesh) ? mesh : made.TryGetValue(mesh, out var copy) ? copy : made[mesh] = Copy(mesh)).ToArray();
                replaced.Add(entry.Renderer, entry.Meshes);
                entry.Renderer.SetMeshes(own);
            }
        }

        private static Mesh Copy(Mesh mesh)
        {
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            copies.Add(copy);
            return copy;
        }

        private static void RestoreAll()
        {
            foreach (var entry in replaced)
                if (entry.Key) entry.Key.SetMeshes(entry.Value);
            replaced.Clear();
            foreach (var copy in copies)
                if (copy) Object.Destroy(copy);
            copies.Clear();
        }
    }
}
