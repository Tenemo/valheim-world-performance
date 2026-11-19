using SoftReferenceableAssets;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WorldPerformance
{
    // Clone the model beneath an inactive object. The game's placement still selects the seed's objects,
    // disables the network copies and activates the room under its generator with the native random state.
    internal sealed class DungeonRoomCopy
    {
        private readonly SoftReference<GameObject> prefab;
        private readonly GameObject holder;
        private readonly Transform generator;
        private bool taken, discarded;
        internal AsyncInstantiateOperation<GameObject> Operation { get; }

        internal DungeonRoomCopy(Transform generator, SoftReference<GameObject> prefab, Vector3 point, Quaternion rotation)
        {
            this.generator = generator;
            this.prefab = prefab;
            prefab.Load();
            holder = new GameObject("Dungeon room loading");
            holder.SetActive(false);
            try { Operation = Object.InstantiateAsync(prefab.Asset, holder.transform, point, rotation); }
            catch
            {
                Object.Destroy(holder);
                prefab.Release();
                throw;
            }
        }

        internal GameObject Take(SoftReference<GameObject> requested, Vector3 point, Quaternion rotation, Transform parent)
        {
            if (parent != generator || !prefab.Equals(requested)) return null;
            if (Operation.Result == null || Operation.Result.Length != 1)
            {
                Plugin.Fallback("The asynchronous dungeon room copy returned no model.");
                return null;
            }
            var instance = Operation.Result[0];
            if (!instance || !holder || instance.transform.parent != holder.transform)
            {
                Plugin.Fallback("The asynchronous dungeon room copy lost its inactive parent.");
                return null;
            }
            var source = prefab.Asset.GetComponentsInChildren<Transform>(true);
            var target = instance.GetComponentsInChildren<Transform>(true);
            if (source.Length != target.Length)
            {
                Plugin.Fallback("The dungeon room hierarchy changed during cloning.");
                return null;
            }
            for (int index = 1; index < source.Length; index++)
            {
                var child = target[index].gameObject;
                bool active = source[index].gameObject.activeSelf;
                if (child.activeSelf != active) child.SetActive(active);
            }
            instance.SetActive(false);
            instance.transform.SetParent(parent, false);
            instance.transform.SetPositionAndRotation(point, rotation);
            instance.transform.localScale = source[0].localScale;
            instance.SetActive(source[0].gameObject.activeSelf);
            instance.HoldReferenceTo(prefab);
            taken = true;
            return instance;
        }

        internal void Discard()
        {
            if (discarded) return;
            discarded = true;
            if (Operation.isDone) Release();
            else Operation.completed += _ => Release();
        }

        private void Release()
        {
            try
            {
                // A scene change can remove the inactive parent before Unity finishes cloning.
                if (!taken && Operation.Result is GameObject[] models)
                    foreach (var model in models)
                        if (model && (!holder || model.transform.parent != holder.transform)) Object.Destroy(model);
                if (holder) Object.Destroy(holder);
            }
            finally { prefab.Release(); }
        }
    }
}
