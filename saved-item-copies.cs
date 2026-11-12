using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Loading a saved item, as a chest's first load does for every stack and a character's login does for every carried
    // item, makes the game instantiate the whole item prefab (models, colliders, particle systems and all) only to take the
    // item data Unity's serializer copies into it, wake the copy for the wake's additions to that data, then destroy it.
    // The item data is now copied directly from the prefab by Unity's own rules (serialized fields only, every nested class
    // and list a new copy, empty text and lists where the prefab holds none, other fields as a new instance starts them,
    // references to other objects kept) and given what the copy's wake gives it: its drop prefab and the additions of the
    // wake's known postfixes, Epic Loot's and Item Stacks'. Progression Fix's three postfixes act only on a dropped item with
    // a network record, which the throwaway copy never has. A prefab whose data points into its own objects, which Unity
    // would point at the throwaway instead, loads natively, as does any load while the wake carries other additions.
    internal static class SavedItemCopies
    {
        // The isolated test client switches this off to compare the native instantiation.
        internal static bool Copying = true;
        internal static bool Active { get; private set; }
        internal static long Copied, Native;
        private static readonly AccessTools.FieldRef<Inventory, bool> temporary = AccessTools.FieldRefAccess<Inventory, bool>("m_temoraryInventory");
        private static Func<Inventory, ItemDrop.ItemData, int, int, int, bool, bool> addToCell;
        // Each known wake postfix's addition to the item data, or null where it adds nothing to a copy without a network record.
        private static readonly Dictionary<MethodInfo, Action<ItemDrop.ItemData>> known = new Dictionary<MethodInfo, Action<ItemDrop.ItemData>>();
        private static readonly List<Action<ItemDrop.ItemData>> wake = new List<Action<ItemDrop.ItemData>>();
        private static readonly List<MethodInfo> wakeSeen = new List<MethodInfo>();
        private static bool knownState;
        // Progression Fix may load after this mod, so its postfixes are known by name: each acts only on a dropped item's network record.
        private static readonly HashSet<string> recordOnly = new HashSet<string> { "EpicLootProgressionFix.CoinIncome.Created", "EpicLootProgressionFix.ItemIceDrag.Postfix", "EpicLootProgressionFix.ItemActivity.Stamp" };
        private static bool Known(MethodInfo method) => known.ContainsKey(method) || recordOnly.Contains(method.DeclaringType.FullName + "." + method.Name);
        private static MethodBase awake, setQuality, ensureValid, savedItem;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            savedItem = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(int), typeof(int), typeof(float), typeof(Vector2i), typeof(bool), typeof(int), typeof(int),
                typeof(long), typeof(string), typeof(Dictionary<string, string>), typeof(int), typeof(bool), typeof(bool), typeof(bool) });
            var cell = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
            awake = AccessTools.Method(typeof(ItemDrop), "Awake");
            setQuality = AccessTools.Method(typeof(ItemDrop), "SetQuality");
            ensureValid = AccessTools.Method(typeof(ItemDrop), "EnsureValid");
            if (savedItem == null || cell == null || awake == null || setQuality == null || ensureValid == null) throw new MissingMethodException("The game's saved item load changed.");
            addToCell = AccessTools.MethodDelegate<Func<Inventory, ItemDrop.ItemData, int, int, int, bool, bool>>((MethodInfo)cell);
            // The wake's additions to the item data, in the order Harmony runs them.
            if (PluginIds.TryGet(PluginIds.EpicLoot, out var epicLoot))
            {
                if (epicLoot.Metadata.Version.ToString() != "0.15.6") throw new InvalidOperationException("Epic Loot " + epicLoot.Metadata.Version + "'s item wake is unread.");
                var assembly = epicLoot.Instance.GetType().Assembly;
                var customData = AccessTools.Method(assembly.GetType("EpicLoot.ItemDrop_Awake_Patch", true), "Postfix");
                var crafting = assembly.GetType("EpicLoot.Crafting.ItemDrop_Awake_Patch", true);
                var initialize = AccessTools.MethodDelegate<Action<ItemDrop.ItemData>>(AccessTools.Method(assembly.GetType("EpicLoot.ItemDataExtensions", true), "InitializeCustomData"));
                var initializeItem = AccessTools.Method(crafting, "InitializeItemData");
                var arguments = new object[2];
                known[customData] = data => { if (data != null) initialize(data); };
                // Its other half adds particle and beam components to the throwaway object, which is destroyed unseen.
                known[AccessTools.Method(crafting, "Postfix")] = data => { arguments[0] = data; arguments[1] = null; initializeItem.Invoke(null, arguments); };
            }
            if (PluginIds.TryGet(PluginIds.ItemStacks, out var itemStacks))
            {
                if (itemStacks.Metadata.Version.ToString() != "1.1.1") throw new InvalidOperationException("Item Stacks " + itemStacks.Metadata.Version + "'s item wake is unread.");
                var processing = itemStacks.Instance.GetType().Assembly.GetType("ItemStacksItemWeights.ItemProcessing", true);
                var patch = AccessTools.MethodDelegate<Func<ItemDrop.ItemData, int>>(AccessTools.Method(processing, "PatchItem"));
                known[AccessTools.Method(processing.GetNestedType("ItemDrop_Awake_PatchItems", AccessTools.all), "Postfix")] = data => { if (!(FejdStartup.instance != null)) patch(data); };
            }
            // World Performance's own hand-over notes the woken copy; a direct copy is handed to it instead.
            known[AccessTools.Method(typeof(SavedItemMagic), "Awakened")] = null;
            harmony.Patch(savedItem, prefix: new HarmonyMethod(typeof(SavedItemCopies), nameof(Load)) { priority = Priority.Last });
            UnityCopies.Prepare();
            Active = true;
            log.LogInfo("Loaded items copy their prefab's item data instead of instantiating the prefab.");
        }

        // The wake must carry only known additions, run in Harmony's order, and nothing else may change what this load replaces.
        private static bool PatchesKnown()
        {
            var info = Harmony.GetPatchInfo(awake);
            var postfixes = info?.Postfixes;
            bool same = postfixes != null && postfixes.Count == wakeSeen.Count;
            for (int index = 0; same && index < postfixes.Count; index++) same = postfixes[index].PatchMethod == wakeSeen[index];
            if (!same)
            {
                wakeSeen.Clear();
                wake.Clear();
                knownState = info == null || info.Prefixes.Count == 0 && info.Transpilers.Count == 0 && info.Finalizers.Count == 0 && info.Postfixes.All(patch => Known(patch.PatchMethod));
                if (info != null)
                {
                    wakeSeen.AddRange(info.Postfixes.Select(patch => patch.PatchMethod));
                    if (knownState) foreach (var method in PatchProcessor.GetSortedPatchMethods(awake, info.Postfixes.ToArray())) if (known.TryGetValue(method, out var addition) && addition != null) wake.Add(addition);
                }
                if (!knownState)
                    Plugin.Fallback("loaded items are instantiated natively: the item wake carries " + string.Join(", ", (info?.Prefixes.Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.Postfixes) ?? Enumerable.Empty<Patch>())
                        .Where(patch => !Known(patch.PatchMethod)).Select(patch => patch.owner + " " + patch.PatchMethod.DeclaringType.FullName + "." + patch.PatchMethod.Name)));
            }
            if (!knownState || Harmony.GetPatchInfo(setQuality) != null || Harmony.GetPatchInfo(ensureValid) != null) return false;
            var saved = Harmony.GetPatchInfo(savedItem);
            return saved == null || saved.Transpilers.Count == 0;
        }

        // An inactive object's ItemDrop is never woken, so it holds whatever item data the load hands it and registers nowhere.
        private static ItemDrop holder;
        private static ItemDrop Holder()
        {
            if (holder) return holder;
            var carrier = new GameObject("WorldPerformance item holder");
            carrier.SetActive(false);
            carrier.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(carrier);
            holder = carrier.AddComponent<ItemDrop>();
            return holder;
        }

        private static bool Load(Inventory __instance, int prefabHash, int stack, float durability, Vector2i pos, bool equipped, int quality, int variant,
            long crafterID, string crafterName, Dictionary<string, string> customData, int worldLevel, bool pickedUp, bool cheated, bool skipValidPositionCheck,
            ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!Copying || temporary(__instance) || !PatchesKnown()) return true;
            var prefab = ObjectDB.instance.GetItemPrefab(prefabHash);
            var source = prefab ? prefab.GetComponent<ItemDrop>() : null;
            var data = source ? UnityCopies.Copy(source.m_itemData, prefab.transform) : null;
            if (data == null) { Native++; return true; }
            // The copy's wake; a copy of an inactive prefab, as Epic Loot keeps some of its own, is never woken.
            if (prefab.activeSelf)
            {
                data.m_dropPrefab = ObjectDB.instance.GetItemPrefab(Utils.GetPrefabName(prefab.name + "(Clone)"));
                foreach (var addition in wake) addition(data);
            }
            // The rest of the game's load, without the copy's object.
            data.m_stack = Mathf.Min(stack, data.m_shared.m_maxStackSize);
            data.m_durability = durability;
            data.m_equipped = equipped;
            // The game's own quality and validity steps, on an item holder that is never woken.
            var holder = Holder();
            holder.m_itemData = data;
            try { holder.SetQuality(quality); }
            finally { holder.m_itemData = null; }
            data.m_variant = variant;
            data.m_crafterID = crafterID;
            data.m_crafterName = crafterName;
            data.m_customData = customData;
            data.m_worldLevel = (byte)worldLevel;
            data.m_pickedUp = pickedUp;
            data.m_cheated = cheated;
            holder.m_itemData = data;
            try { holder.EnsureValid(); }
            finally { holder.m_itemData = null; }
            SavedItemMagic.Copied(data);
            addToCell(__instance, data, data.m_stack, pos.x, pos.y, skipValidPositionCheck);
            __result = true;
            Copied++;
            return false;
        }
    }

    // Copies a serializable object the way Unity's serializer clones it.
    internal static class UnityCopies
    {
        [ThreadStatic] private static Transform root;
        [ThreadStatic] private static bool refused;
        // Compiling the copiers takes about 0.1 s, so a worker builds them at start-up; loads before that are native.
        private static Func<object, object> itemCopier;
        private static string failure;
        private static double buildMilliseconds;
        private static int built, reported;

        internal static void Prepare() => ThreadPool.QueueUserWorkItem(_ =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try { Volatile.Write(ref itemCopier, Build(typeof(ItemDrop.ItemData), new HashSet<Type>())); }
            catch (Exception error) { failure = error.GetBaseException().Message; }
            buildMilliseconds = started.Elapsed.TotalMilliseconds;
            Volatile.Write(ref built, 1);
        });

        internal static ItemDrop.ItemData Copy(ItemDrop.ItemData source, Transform prefab)
        {
            if (Volatile.Read(ref built) == 0) return null;
            if (reported == 0)
            {
                reported = 1;
                if (failure != null) Plugin.Fallback("loaded items are instantiated natively: " + failure);
                else Plugin.Log.LogInfo("Built the item data copier in " + buildMilliseconds.ToString("F1") + " ms on a worker.");
            }
            var copier = Volatile.Read(ref itemCopier);
            if (copier == null) return null;
            root = prefab;
            refused = false;
            try
            {
                var copy = (ItemDrop.ItemData)copier(source);
                return refused ? null : copy;
            }
            finally { root = null; }
        }

        // Throws NotSupportedException for a type this copier does not reproduce exactly.
        private static Func<object, object> Build(Type type, HashSet<Type> path)
        {
            if (!path.Add(type)) throw new NotSupportedException(type.FullName + " contains itself.");
            if (path.Count > 7) throw new NotSupportedException(type.FullName + " is nested too deeply.");
            if (type.GetConstructor(Type.EmptyTypes) == null) throw new NotSupportedException(type.FullName + " has no default constructor.");
            var input = Expression.Parameter(typeof(object), "input");
            var source = Expression.Variable(type, "source");
            var copy = Expression.Variable(type, "copy");
            var body = new List<Expression>
            {
                Expression.Assign(source, Expression.Convert(input, type)),
                // Unity serializes an absent object as a new one.
                Expression.IfThen(Expression.Equal(source, Expression.Constant(null, type)), Expression.Assign(source, Expression.New(type))),
                Expression.Assign(copy, Expression.New(type)),
            };
            foreach (var field in Serialized(type)) body.Add(Expression.Assign(Expression.Field(copy, field), Value(Expression.Field(source, field), field.FieldType, path)));
            body.Add(Expression.Convert(copy, typeof(object)));
            path.Remove(type);
            return Expression.Lambda<Func<object, object>>(Expression.Block(new[] { source, copy }, body), input).Compile();
        }

        private static readonly Type serializeReference = AccessTools.TypeByName("UnityEngine.SerializeReference");

        // Unity's rule: public or [SerializeField] instance fields, not [NonSerialized], readonly or constant, of a type it serializes.
        private static IEnumerable<FieldInfo> Serialized(Type type)
        {
            var levels = new List<Type>();
            for (var level = type; level != null && level != typeof(object); level = level.BaseType) levels.Insert(0, level);
            foreach (var level in levels)
                foreach (var field in level.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsNotSerialized || field.IsInitOnly || field.IsLiteral || !(field.IsPublic || field.IsDefined(typeof(SerializeField), true))) continue;
                    if (serializeReference != null && field.IsDefined(serializeReference, true)) throw new NotSupportedException(type.FullName + "." + field.Name + " is a serialized reference.");
                    if (Kind(field.FieldType) == ValueKind.Skipped) continue;
                    yield return field;
                }
        }

        private enum ValueKind { Skipped, Plain, Text, Reference, Curve, Gradient, Class, Struct, Array, List }

        private static bool Custom(Type type) => type.IsDefined(typeof(SerializableAttribute), false) && !type.IsAbstract && !type.IsGenericTypeDefinition &&
            type.Assembly != typeof(object).Assembly && !(type.Namespace ?? "").StartsWith("System") && !(type.Namespace ?? "").StartsWith("UnityEngine");

        private static ValueKind Kind(Type type)
        {
            if (type.IsPrimitive) return type == typeof(IntPtr) || type == typeof(UIntPtr) ? ValueKind.Skipped : ValueKind.Plain;
            if (type.IsEnum) return ValueKind.Plain;
            if (type == typeof(string)) return ValueKind.Text;
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return ValueKind.Reference;
            if (type == typeof(AnimationCurve)) return ValueKind.Curve;
            if (type == typeof(Gradient)) return ValueKind.Gradient;
            if (type.IsArray) return type.GetArrayRank() == 1 && Element(type.GetElementType()) ? ValueKind.Array : ValueKind.Skipped;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return Element(type.GetGenericArguments()[0]) ? ValueKind.List : ValueKind.Skipped;
            if (type.IsValueType && (type.Namespace ?? "").StartsWith("UnityEngine"))
            {
                if (unityValues.Contains(type)) return ValueKind.Plain;
                throw new NotSupportedException("Unity value " + type.FullName + " is unread.");
            }
            if (Custom(type)) return type.IsValueType ? ValueKind.Struct : ValueKind.Class;
            if (type.IsClass && (type.Namespace ?? "").StartsWith("UnityEngine") && type.IsDefined(typeof(SerializableAttribute), false))
                throw new NotSupportedException("Unity class " + type.FullName + " is unread.");
            return ValueKind.Skipped;
        }

        private static readonly HashSet<Type> unityValues = new HashSet<Type> { typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Vector2Int), typeof(Vector3Int),
            typeof(Quaternion), typeof(Color), typeof(Color32), typeof(Rect), typeof(RectInt), typeof(Bounds), typeof(BoundsInt), typeof(Matrix4x4), typeof(LayerMask) };

        // Unity serializes no list of lists.
        private static bool Element(Type type) => !type.IsArray && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) && Kind(type) != ValueKind.Skipped;

        private static Expression Value(Expression value, Type type, HashSet<Type> path)
        {
            switch (Kind(type))
            {
                case ValueKind.Plain: return value;
                case ValueKind.Text: return Expression.Coalesce(value, Expression.Constant(""));
                case ValueKind.Reference: return Expression.Convert(Expression.Call(AccessTools.Method(typeof(UnityCopies), nameof(Reference)), Expression.Convert(value, typeof(UnityEngine.Object))), type);
                case ValueKind.Curve: return Expression.Call(AccessTools.Method(typeof(UnityCopies), nameof(Curve)), value);
                case ValueKind.Gradient: return Expression.Call(AccessTools.Method(typeof(UnityCopies), nameof(CopyGradient)), value);
                case ValueKind.Class:
                    {
                        var copier = Build(type, path);
                        return Expression.Convert(Expression.Invoke(Expression.Constant(copier), Expression.Convert(value, typeof(object))), type);
                    }
                case ValueKind.Struct:
                    {
                        // A struct starts from its default value; only its serialized fields are copied.
                        var copy = Expression.Variable(type, "value");
                        var source = Expression.Variable(type, "source");
                        var body = new List<Expression> { Expression.Assign(source, value), Expression.Assign(copy, Expression.Default(type)) };
                        if (!path.Add(type)) throw new NotSupportedException(type.FullName + " contains itself.");
                        foreach (var field in Serialized(type)) body.Add(Expression.Assign(Expression.Field(copy, field), Value(Expression.Field(source, field), field.FieldType, path)));
                        path.Remove(type);
                        body.Add(copy);
                        return Expression.Block(new[] { copy, source }, body);
                    }
                case ValueKind.Array:
                case ValueKind.List:
                    {
                        var element = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];
                        var item = Expression.Parameter(element, "item");
                        var copyElement = Expression.Lambda(typeof(Func<,>).MakeGenericType(element, element), Value(item, element, path), item).Compile();
                        var method = AccessTools.Method(typeof(UnityCopies), type.IsArray ? nameof(CopyArray) : nameof(CopyList)).MakeGenericMethod(element);
                        return Expression.Call(method, value, Expression.Constant(copyElement));
                    }
                default: throw new NotSupportedException(type.FullName + " is not serialized.");
            }
        }

        private static T[] CopyArray<T>(T[] source, Func<T, T> element)
        {
            if (source == null) return new T[0];
            var copy = new T[source.Length];
            for (int index = 0; index < source.Length; index++) copy[index] = element(source[index]);
            return copy;
        }

        private static List<T> CopyList<T>(List<T> source, Func<T, T> element)
        {
            if (source == null) return new List<T>();
            var copy = new List<T>(source.Count);
            foreach (var value in source) copy.Add(element(value));
            return copy;
        }

        private static AnimationCurve Curve(AnimationCurve source) =>
            source == null ? new AnimationCurve() : new AnimationCurve(source.keys) { preWrapMode = source.preWrapMode, postWrapMode = source.postWrapMode };

        private static Gradient CopyGradient(Gradient source)
        {
            var copy = new Gradient();
            if (source != null) { copy.mode = source.mode; copy.colorSpace = source.colorSpace; copy.SetKeys(source.colorKeys, source.alphaKeys); }
            return copy;
        }

        // References stay, except into the prefab's own objects, which Unity points at the throwaway copy instead, and
        // destroyed objects, whose clone Unity reads back in its own way: those loads stay native.
        private static UnityEngine.Object Reference(UnityEngine.Object value)
        {
            if (ReferenceEquals(value, null)) return null;
            if (!value) { refused = true; return value; }
            var transform = value is GameObject gameObject ? gameObject.transform : value is Component component ? component.transform : null;
            if (transform != null && root != null && transform.root == root) refused = true;
            return value;
        }
    }
}
