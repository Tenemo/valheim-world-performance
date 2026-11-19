using System.Collections.Generic;
using UnityEngine;

namespace WorldPerformance
{
    // When a streamed dungeon leaves, its network instance and generator still go in their normal frame.
    // Its room models stop drawing, making sound and participating in physics at departure. Unity can
    // then free one disabled model a frame without destroying an entire interior in a single update.
    internal sealed class DungeonRoomDisposal : MonoBehaviour
    {
        private readonly Queue<GameObject> rooms = new Queue<GameObject>();
        private readonly List<ZNetView> activeViews = new List<ZNetView>();
        private int firstFrame;

        internal static void Retire(ZNetScene scene, Transform generator)
        {
            if (!scene.TryGetComponent<DungeonRoomDisposal>(out var disposal))
                disposal = scene.gameObject.AddComponent<DungeonRoomDisposal>();
            disposal.Enqueue(generator);
        }

        private void Enqueue(Transform generator)
        {
            // Keep at most four seven-room interiors during consecutive departures.
            if (rooms.Count + generator.childCount > 28) return;
            if (rooms.Count == 0) firstFrame = Time.frameCount + 1;
            for (int index = generator.childCount - 1; index >= 0; index--)
            {
                var child = generator.GetChild(index);
                if (!child.TryGetComponent<Room>(out _)) continue;
                child.GetComponentsInChildren(false, activeViews);
                if (activeViews.Count != 0) continue;
                var model = child.gameObject;
                model.SetActive(false);
                child.SetParent(null, true);
                rooms.Enqueue(model);
            }
            activeViews.Clear();
            enabled = rooms.Count != 0;
        }

        private void Update()
        {
            if (Time.frameCount < firstFrame) return;
            if (rooms.Count != 0) Destroy(rooms.Dequeue());
            enabled = rooms.Count != 0;
        }

        private void OnDestroy()
        {
            while (rooms.Count != 0) Destroy(rooms.Dequeue());
        }
    }
}
