using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // A dedicated server has no graphics device, yet ambient occlusion still built its materials, meshes and command buffers for a
    // camera nobody sees and logged errors at every start. It stays off on the server; clients render as before. The video
    // shader lookups and two asset uploads that still fail come from Unity loading the start scene, before any script runs,
    // exactly as on an unmodded server.
    [HarmonyPatch(typeof(AmplifyOcclusionEffect), "OnEnable")]
    internal static class HeadlessRendering
    {
        private static bool Prefix(AmplifyOcclusionEffect __instance)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) return true;
            __instance.enabled = false;
            return false;
        }
    }

    // The dedicated start scene still selects a saved character and loads its inventory for a menu mannequin.
    // It has no viewer, and world-only items such as Andvaranaut are not registered until ZNetScene awakens.
    [HarmonyPatch(typeof(FejdStartup), "SetupCharacterPreview")]
    internal static class HeadlessCharacterPreview
    {
        private static bool Prefix() => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
    }
}
