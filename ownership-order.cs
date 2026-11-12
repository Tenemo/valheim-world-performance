using System;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Valheim offers an object whose owner has left or moved away to each player in the order they connected, and the first
    // one in range takes it, so a creature can land on the slowest connection in the group and everyone else then sees it
    // late. Putting the players in delay order before the ownership pass offers each object to the player who can carry it
    // best. The same order decides which player the server sends updates to next, which costs at most one skipped turn
    // every two seconds.
    internal static class OwnershipOrder
    {
        private const int UnmeasuredDelay = 10000;
        private const float OrderSeconds = 2f;
        private static float nextOrder;

        internal static void Initialize(PerformancePatches harmony)
        {
            PeerConnections.Initialize();
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), "ReleaseZDOS"), prefix: new HarmonyMethod(typeof(OwnershipOrder), nameof(OrderByDelay)));
        }

        private static void OrderByDelay()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || Time.time < nextOrder) return;
            nextOrder = Time.time + OrderSeconds;
            if (ForeignOwnership.SelectsOwners()) return;
            var peers = PeerConnections.UpdatePeers();
            int count = peers == null ? 0 : peers.Count;
            if (count < 2) return;
            var order = new object[count];
            var keys = new long[count];
            peers.CopyTo(order, 0);
            for (int index = 0; index < count; index++)
            {
                int delay = PeerConnections.Delay(PeerConnections.Peer(order[index]));
                // Equal delays keep the order they had, so connections Steam cannot measure change nothing.
                keys[index] = ((long)(delay < 0 ? UnmeasuredDelay : delay) << 20) | (uint)index;
            }
            Array.Sort(keys, order);
            for (int index = 0; index < count; index++) peers[index] = order[index];
        }
    }
}
