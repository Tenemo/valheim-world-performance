using System;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace WorldPerformance
{
    // Network Performance System does two of the things in this file better than this mod can, and says so in its own log:
    // it replaces the game's two-second ownership pass with one that picks an owner by how stale the object looks to the
    // players who can see it and moves creatures off a player whose upload or frame rate cannot carry them, and it finds
    // the rate the server sends each player at by itself, backing off when the line starts losing packets. Where it owns
    // one of those, this mod stands down rather than writing the same setting or reordering a list it no longer reads.
    // Its own update window looks for the two limits this mod's window has already replaced, so that one stays with this
    // mod. A version whose state cannot be read counts as owning both, since two mods assigning owners or pacing the same
    // connection is worse than losing one of these.
    public static class ForeignOwnership
    {
        internal const string NetworkPerformance = "MidnightsFX.NetworkPerformanceSystem";
        private static Func<bool> owners, rate;
        private static bool resolved, reported;

        // Whether another mod decides which player owns an object.
        public static bool SelectsOwners() { Resolve(); return owners != null && owners(); }

        // Whether another mod sets the rate Steam sends each player at.
        internal static bool SetsSendRate() { Resolve(); return rate != null && rate(); }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            if (!Chainloader.PluginInfos.ContainsKey(NetworkPerformance)) return;
            owners = Mechanism("EnableOwnershipArbitration", "Ownership");
            rate = Mechanism("EnableSteamTransportTuning", "SteamTransport");
        }

        // Its own two conditions: the setting is on and the mechanism did not stand itself down against this game.
        private static Func<bool> Mechanism(string setting, string mechanism)
        {
            var configuration = AccessTools.TypeByName("NetworkPerformanceSystem.ValConfig");
            var mechanisms = AccessTools.TypeByName("NetworkPerformanceSystem.Runtime.Mechanism");
            var guard = AccessTools.TypeByName("NetworkPerformanceSystem.Runtime.PatchGuard");
            var entry = configuration == null ? null : AccessTools.Field(configuration, setting)?.GetValue(null);
            var value = entry == null ? null : AccessTools.Property(entry.GetType(), "Value");
            var active = mechanisms == null || guard == null ? null : AccessTools.Method(guard, "IsActive", new[] { mechanisms });
            if (value == null || value.PropertyType != typeof(bool) || active == null || !Enum.GetNames(mechanisms).Contains(mechanism))
            {
                if (!reported)
                {
                    reported = true;
                    Plugin.Log.LogWarning("Network Performance System's own state cannot be read, so object ownership and the send rate are left to it.");
                }
                return () => true;
            }
            var arguments = new[] { Enum.Parse(mechanisms, mechanism) };
            return () => (bool)value.GetValue(entry) && (bool)active.Invoke(null, arguments);
        }
    }
}
