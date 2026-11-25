using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace WorldPerformance
{
    // About 2,900 of the game's particle systems name a mesh their renderer never draws: billboard and stretched particles
    // are drawn as quads the renderer makes itself, and only mesh particles use the mesh. Almost all of them name the same
    // four-vertex Quad, and every copy of such a system that comes alive adds itself to that mesh's users, which costs about
    // 0.8 ms when the mesh already has thousands: most of a Staff of Fracturing splinter impact's 1.4 ms, and the same for an
    // enemy's weakness hit and many other effects. Once a world's prefabs are loaded, those renderers stop naming the mesh
    // they do not draw. Mesh particles keep theirs, so every particle looks as before.
    internal static class UnusedParticleMeshes
    {
        // The meshes this feature took away, so switching it off gives each renderer its own back.
        private static readonly Dictionary<ParticleSystemRenderer, Mesh[]> removed = new Dictionary<ParticleSystemRenderer, Mesh[]>(Identity<ParticleSystemRenderer>.Instance);
        private static ZNetScene scene;
        private static bool removing = true;
        internal static bool Running { get; private set; }
        internal static int Removed => removed.Count;

        // Off gives every renderer its meshes back, as natively; the isolated client switches it for checks and paired
        // measurements.
        internal static bool Removing
        {
            get => removing;
            set
            {
                removing = value;
                if (value) RemoveAll();
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
            foreach (var renderer in removed.Keys.Where(renderer => !renderer).ToArray()) removed.Remove(renderer);
            if (removing) RemoveAll();
        }

        private static void RemoveAll()
        {
            if (!Running) return;
            foreach (var renderer in Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>())
            {
                if (!renderer || renderer.renderMode == ParticleSystemRenderMode.Mesh || renderer.meshCount == 0 || removed.ContainsKey(renderer)) continue;
                var meshes = new Mesh[renderer.meshCount];
                renderer.GetMeshes(meshes);
                removed.Add(renderer, meshes);
                renderer.SetMeshes(Array.Empty<Mesh>());
            }
        }

        private static void RestoreAll()
        {
            foreach (var entry in removed)
                if (entry.Key) entry.Key.SetMeshes(entry.Value);
            removed.Clear();
        }
    }
}
