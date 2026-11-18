using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WorldPerformance
{
    internal sealed class CompactLocationJob
    {
        private sealed class Node
        {
            public Transform Source;
            public Node Parent, Owner;
            public ZNetView NetworkView;
            public bool CanSplitChildren;
            public Component[] Components;
            public readonly List<Node> Children = new List<Node>();
        }

        private sealed class Plan
        {
            public Node Root;
            public Node[] Nodes;
            public Dictionary<Transform, Node> Lookup;
            public HashSet<Node> Candidates = new HashSet<Node>();
            public readonly HashSet<Node> Protected = new HashSet<Node>();
            public readonly HashSet<Node> Split = new HashSet<Node>();
            // Topology is shared; seed-dependent admission and reference reachability belong to the request.
            public Plan ForCopy() => new Plan
            {
                Root = Root,
                Nodes = Nodes,
                Lookup = Lookup,
                Candidates = Candidates
            };
            public int OmittedNetworkRoots;
            public string Fallback;
        }

        private sealed class ReferenceIdentity : IEqualityComparer<object>
        {
            public new bool Equals(object first, object second) => ReferenceEquals(first, second);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        private static readonly Dictionary<Type, FieldInfo[]> fields = new Dictionary<Type, FieldInfo[]>();
        private static readonly ReferenceIdentity referenceIdentity = new ReferenceIdentity();
        private static int budgetFrame = -1;
        private static long usedTicks;
        private static readonly long slice = Stopwatch.Frequency / 500;
        // The isolated client switches the compiler off for comparisons.
        internal static bool Enabled { get; set; } = true;
        internal static readonly string[] Families = { "TheHole01", "StoneTower1", "CharredRuins1", "CharredTowerRuins1" };

        internal static int Completed, Fallbacks, Prepared, Reused;
        // Plans do not own game assets. A new prefab instance starts with a new plan.
        private static ConditionalWeakTable<GameObject, Plan> plans = new ConditionalWeakTable<GameObject, Plan>();
        internal static void LeaveWorld() => plans = new ConditionalWeakTable<GameObject, Plan>();

        internal static IEnumerator Prepare(GameObject source)
        {
            var cache = plans;
            if (!source || cache.TryGetValue(source, out _))
                yield break;
            var plan = new Plan
            {
                Lookup = new Dictionary<Transform, Node>()
            };
            var topology = Discover(source, plan);
            while (source && ReferenceEquals(cache, plans) && topology.MoveNext())
                yield return null;
            if (source && ReferenceEquals(cache, plans) && plan.Fallback == null && !cache.TryGetValue(source, out _))
            {
                cache.Add(source, plan);
                Prepared++;
            }
        }

        internal static bool Supports(string name) => Enabled && Array.IndexOf(Families, name) >= 0;
        private readonly IEnumerator work;
        internal GameObject Result { get; private set; }
        internal string FallbackReason { get; private set; }
        internal bool Unsupported => FallbackReason != null;

        private bool finished;
        internal CompactLocationJob(GameObject asset, Vector3 position, Quaternion rotation)
        {
            work = Construct(asset, position, rotation);
        }

        internal bool Advance()
        {
            if (finished)
                return true;
            if (budgetFrame != Time.frameCount)
            {
                budgetFrame = Time.frameCount;
                usedTicks = 0;
            }

            while (usedTicks < slice)
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    finished = !work.MoveNext();
                }
                finally
                {
                    usedTicks += Stopwatch.GetTimestamp() - start;
                }

                if (finished)
                    return true;
            }

            return false;
        }

        internal void Cancel()
        {
            (work as IDisposable)?.Dispose();
            if (Result)
                Object.Destroy(Result);
            Result = null;
            finished = true;
        }

        private IEnumerator Construct(GameObject source, Vector3 position, Quaternion rotation)
        {
            Plan plan;
            if (plans.TryGetValue(source, out var prepared))
            {
                plan = prepared.ForCopy();
                var components = new List<Component>();
                int checkedNodes = 0;
                foreach (var node in plan.Nodes)
                {
                    if (!Matches(node, components))
                    {
                        plans.Remove(source);
                        plan = null;
                        break;
                    }

                    if ((++checkedNodes & 15) == 0) yield return null;
                }

                if (plan != null)
                    Reused++;
            }
            else
                plan = null;
            if (plan == null)
            {
                plan = new Plan
                {
                    Lookup = new Dictionary<Transform, Node>()
                };
                var discovery = Discover(source, plan);
                while (discovery.MoveNext())
                    yield return null;
                if (plan.Fallback == null && !plans.TryGetValue(source, out _))
                    plans.Add(source, plan.ForCopy());
            }

            var scan = Analyze(plan);
            while (scan.MoveNext())
                yield return null;
            if (plan.OmittedNetworkRoots == 0)
            {
                FallbackReason = plan.Fallback ?? "all network subtrees are referenced";
                Fallbacks++;
                yield break;
            }

            var map = new Dictionary<Object, Object>();
            var manual = new List<(MonoBehaviour source, MonoBehaviour target)>();
            Result = new GameObject(source.name + "(Clone)");
            Result.SetActive(false);
            CopyObject(plan.Root, Result);
            map[source] = Result;
            map[source.transform] = Result.transform;
            CopyManualComponents(plan.Root, Result, map, manual);
            foreach (var child in plan.Root.Children)
            {
                foreach (var step in CloneNode(child, Result.transform, plan, map, manual))
                    yield return step;
            }

            foreach (var item in manual)
            {
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(item.source), item.target);
                item.target.enabled = item.source.enabled;
                yield return null;
            }

            foreach (var pair in map)
            {
                if (!(pair.Key is MonoBehaviour) || !(pair.Value is MonoBehaviour target))
                    continue;
                var visited = new HashSet<object>(referenceIdentity);
                foreach (var field in SerializedFields(target.GetType()))
                    field.SetValue(target, Remap(field.GetValue(target), map, plan.Lookup, visited));
                yield return null;
            }

            Result.transform.SetPositionAndRotation(position, rotation);
            Completed++;
        }

        private static IEnumerator Discover(GameObject source, Plan plan)
        {
            if (source.GetComponents<Component>().Any(c => !c || !CanSynthesize(c)))
            {
                plan.Fallback = "root requires native component cloning";
                yield break;
            }

            var nodes = new List<Node>();
            var pending = new Stack<(Transform source, Node parent)>();
            pending.Push((source.transform, null));
            while (pending.Count > 0)
            {
                var next = pending.Pop();
                if (!next.source)
                {
                    plan.Fallback = "source hierarchy changed while preparing";
                    yield break;
                }
                var node = BuildNode(next.source, next.parent, plan, nodes);
                if (plan.Fallback != null)
                    yield break;
                if (node.Owner == null)
                    for (int i = next.source.childCount - 1; i >= 0; i--)
                        pending.Push((next.source.GetChild(i), node));
                if ((nodes.Count & 31) == 0)
                    yield return null;
            }

            plan.Nodes = nodes.ToArray();
        }

        private static bool Matches(Node node, List<Component> components)
        {
            if (!node.Source)
                return false;
            if (node.Owner != null)
                return node.NetworkView;
            if (node.Source.childCount != node.Children.Count)
                return false;
            for (int i = 0; i < node.Children.Count; i++)
                if (node.Source.GetChild(i) != node.Children[i].Source)
                    return false;
            node.Source.GetComponents(components);
            if (components.Count != node.Components.Length)
                return false;
            for (int i = 0; i < components.Count; i++)
                if (!components[i] || components[i] != node.Components[i])
                    return false;
            return true;
        }

        private static IEnumerator Analyze(Plan plan)
        {
            // Resolve active state and serialized references after the game applies this seed.
            if (plan.Fallback == null && plan.Candidates.Any(node => RelativeActive(node, plan.Root)))
                plan.Fallback = "active network object";
            if (plan.Fallback != null)
                yield break;
            foreach (var candidate in plan.Candidates)
                if (!candidate.Parent.CanSplitChildren) plan.Protected.Add(candidate);

            bool changed;
            do
            {
                int before = plan.Protected.Count;
                foreach (var node in plan.Nodes)
                {
                    if (node.Owner != null && !plan.Protected.Contains(node.Owner))
                        continue;
                    yield return null;
                    var retainedComponents = node.Owner == node ? node.Source.GetComponentsInChildren<Component>(true) : node.Components;
                    if (retainedComponents.Any(c => !c))
                    {
                        plan.Fallback = "missing script component";
                        yield break;
                    }

                    foreach (var component in retainedComponents)
                    {
                        if (component is MonoBehaviour)
                        {
                            if (component.GetType().Assembly != typeof(Location).Assembly)
                            {
                                plan.Fallback = "external behaviour " + component.GetType().FullName;
                                yield break;
                            }
                            var visited = new HashSet<object>(referenceIdentity);
                            foreach (var field in SerializedFields(component.GetType()))
                                foreach (var reference in References(field.GetValue(component), visited))
                                    if (Target(reference, plan.Lookup, out var target) && target.Owner != null)
                                        plan.Protected.Add(target.Owner);
                        }
                        else
                        {
                            if (!KnownNativeComponent(component))
                            {
                                plan.Fallback = "unclassified native references on " + component.GetType().Name;
                                yield break;
                            }

                            foreach (var reference in NativeReferences(component))
                                if (Target(reference, plan.Lookup, out var target))
                                {
                                    var ancestor = node;
                                    while (ancestor != null && !Inside(target, ancestor))
                                        ancestor = ancestor.Parent;
                                    if (ancestor != null)
                                        foreach (var candidate in plan.Candidates)
                                            if (Inside(candidate, ancestor))
                                                plan.Protected.Add(candidate);
                                }
                        }
                    }
                }

                changed = before != plan.Protected.Count;
            }
            while (changed);
            foreach (var candidate in plan.Candidates.Where(n => !plan.Protected.Contains(n)))
                for (var parent = candidate.Parent; parent != null; parent = parent.Parent)
                    plan.Split.Add(parent);
            plan.OmittedNetworkRoots = plan.Candidates.Count(n => !plan.Protected.Contains(n));
            yield break;
        }

        private static Node BuildNode(Transform source, Node parent, Plan plan, List<Node> nodes)
        {
            var node = new Node
            {
                Source = source,
                Parent = parent
            };
            nodes.Add(node);
            plan.Lookup.Add(source, node);
            if (parent == null)
                plan.Root = node;
            else
                parent.Children.Add(node);
            node.NetworkView = source.GetComponent<ZNetView>();
            if (node.NetworkView)
            {
                if (parent == null)
                {
                    plan.Fallback = "active or root network object";
                    return node;
                }

                node.Owner = node;
                node.Components = Array.Empty<Component>();
                plan.Candidates.Add(node);
                return node;
            }

            node.Components = source.GetComponents<Component>();
            if (node.Components.Any(c => !c))
            {
                plan.Fallback = "missing script component";
                return node;
            }

            node.CanSplitChildren = (parent == null || parent.CanSplitChildren) && node.Components.All(CanSynthesize);
            return node;
        }

        private static bool RelativeActive(Node node, Node root)
        {
            for (var current = node; current != null && current != root; current = current.Parent)
                if (!current.Source.gameObject.activeSelf)
                    return false;
            return true;
        }

        private static bool Inside(Node node, Node parent)
        {
            for (; node != null; node = node.Parent)
                if (node == parent)
                    return true;
            return false;
        }

        private static bool CanSynthesize(Component component) => component.GetType() == typeof(Transform) || component.GetType() == typeof(Location) || component.GetType() == typeof(RandomSpawn) || component.GetType() == typeof(RandomObject);
        private static bool Target(Object reference, Dictionary<Transform, Node> nodes, out Node node)
        {
            node = null;
            var transform = reference is GameObject gameObject ? gameObject.transform : reference is Component component ? component.transform : null;
            while (transform)
            {
                if (nodes.TryGetValue(transform, out node))
                    return true;
                transform = transform.parent;
            }

            return false;
        }

        // These types either hold asset references only or have their object links enumerated below.
        // An unrecognized component keeps the entire copy on Unity's native path.
        private static bool KnownNativeComponent(Component component) =>
            component.GetType() == typeof(Transform) || component is MeshFilter || component is MeshRenderer ||
            component is SkinnedMeshRenderer || component is LODGroup || component is BoxCollider ||
            component is SphereCollider || component is CapsuleCollider || component is MeshCollider ||
            component is Light || component is AudioSource || component is Rigidbody;

        private static IEnumerable<Object> NativeReferences(Component component)
        {
            if (component is LODGroup lod)
                foreach (var level in lod.GetLODs())
                    foreach (var renderer in level.renderers)
                        if (renderer)
                            yield return renderer;
            if (component is Renderer render)
            {
                if (render.probeAnchor) yield return render.probeAnchor;
                if (render.lightProbeProxyVolumeOverride) yield return render.lightProbeProxyVolumeOverride;
            }
            if (component is SkinnedMeshRenderer skin)
            {
                if (skin.rootBone)
                    yield return skin.rootBone;
                foreach (var bone in skin.bones)
                    if (bone)
                        yield return bone;
            }


        }

        private static FieldInfo[] SerializedFields(Type type)
        {
            if (fields.TryGetValue(type, out var result))
                return result;
            var list = new List<FieldInfo>();
            for (var current = type; current != null && current != typeof(MonoBehaviour) && current != typeof(Object); current = current.BaseType)
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!field.IsStatic && !field.IsInitOnly && !field.IsNotSerialized && !typeof(Delegate).IsAssignableFrom(field.FieldType) && !typeof(IDictionary).IsAssignableFrom(field.FieldType) && (field.IsPublic || field.IsDefined(typeof(SerializeField), true) || field.IsDefined(typeof(SerializeReference), true)))
                        list.Add(field);
            fields[type] = result = list.ToArray();
            return result;
        }

        private static IEnumerable<Object> References(object value, HashSet<object> visited)
        {
            if (value == null)
                yield break;
            if (value is Object reference)
            {
                if (reference)
                    yield return reference;
                yield break;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || !visited.Add(value))
                yield break;
            if (value is IList list)
            {
                foreach (var item in list)
                    foreach (var found in References(item, visited))
                        yield return found;
                yield break;
            }

            foreach (var field in SerializedFields(type))
                foreach (var found in References(field.GetValue(value), visited))
                    yield return found;
        }

        private static object Remap(object value, Dictionary<Object, Object> map, Dictionary<Transform, Node> nodes, HashSet<object> visited)
        {
            if (value == null)
                return null;
            if (value is Object reference)
            {
                if (!reference)
                    return value;
                if (map.TryGetValue(reference, out var replacement))
                    return replacement;
                if (Target(reference, nodes, out _))
                    throw new InvalidOperationException("A retained reference points into an omitted subtree.");
                return value;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || !visited.Add(value))
                return value;
            if (value is IList list)
            {
                for (int i = 0; i < list.Count; i++)
                    list[i] = Remap(list[i], map, nodes, visited);
                return value;
            }

            foreach (var field in SerializedFields(type))
                field.SetValue(value, Remap(field.GetValue(value), map, nodes, visited));
            return value;
        }

        private static void CopyObject(Node node, GameObject target)
        {
            target.layer = node.Source.gameObject.layer;
            target.tag = node.Source.gameObject.tag;
            target.isStatic = node.Source.gameObject.isStatic;
            target.transform.localPosition = node.Source.localPosition;
            target.transform.localRotation = node.Source.localRotation;
            target.transform.localScale = node.Source.localScale;
        }

        private static void CopyManualComponents(Node node, GameObject target, Dictionary<Object, Object> map, List<(MonoBehaviour, MonoBehaviour)> manual)
        {
            foreach (var component in node.Components)
            {
                if (component is Transform)
                    continue;
                if (!CanSynthesize(component))
                    throw new NotSupportedException("Unsupported split component: " + component.GetType().FullName);
                var created = (MonoBehaviour)target.AddComponent(component.GetType());
                created.enabled = ((MonoBehaviour)component).enabled;
                map.Add(component, created);
                manual.Add(((MonoBehaviour)component, created));
            }
        }

        private static IEnumerable<object> CloneNode(Node node, Transform parent, Plan plan, Dictionary<Object, Object> map, List<(MonoBehaviour, MonoBehaviour)> manual)
        {
            if (node.Owner != null && !plan.Protected.Contains(node.Owner))
                yield break;
            if (!plan.Split.Contains(node))
            {
                var clone = Object.Instantiate(node.Source.gameObject, parent, false);
                clone.name = node.Source.name;
                var source = node.Source.GetComponentsInChildren<Component>(true);
                var targets = clone.GetComponentsInChildren<Component>(true);
                if (source.Length != targets.Length)
                    throw new InvalidOperationException("Native subtree clone changed its component structure.");
                for (int i = 0; i < source.Length; i++)
                {
                    if (!source[i] || !targets[i] || source[i].GetType() != targets[i].GetType())
                        throw new InvalidOperationException("Native subtree component correspondence changed.");
                    map.Add(source[i], targets[i]);
                    if (source[i] is Transform)
                        map.Add(source[i].gameObject, targets[i].gameObject);
                }

                yield return null;
                yield break;
            }

            var target = new GameObject(node.Source.name);
            target.SetActive(false);
            target.transform.SetParent(parent, false);
            CopyObject(node, target);
            map.Add(node.Source, target.transform);
            map.Add(node.Source.gameObject, target);
            CopyManualComponents(node, target, map, manual);
            foreach (var child in node.Children)
                foreach (var step in CloneNode(child, target.transform, plan, map, manual))
                    yield return step;
            target.SetActive(node.Source.gameObject.activeSelf);
        }
    }
}
