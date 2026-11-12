using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // The production spatial renderer. Stable building pieces and natural objects, such as trees, rocks, bushes and crops,
    // draw from merged meshes per 64 m cell and render state in the verified reference format: original local vertices
    // carry a slot index into transform buffers and the appearance or ambient lighting buffers read by derived copies of
    // the native piece and scenery shaders, and native colour and shadow passes draw them. A member renders natively until
    // every merged mesh holding its current state is live, and every change other than a material-manager value shows its
    // native renderers in the frame it happens; its meshes are rebuilt by jobs once its cell has settled. The meshes hold
    // every detail level, so the camera's travel only switches which slots draw, and a kernel copies the indices of the
    // slots drawing now into the region of each mesh that the renderer draws, so slots at other levels run no vertex
    // program. Shadowed point and spot lights draw only the slots within their range (spatial-light-shadows.cs). Main-thread
    // work runs within a per-frame budget, one mesh at a time. Any error restores native rendering.
    internal static partial class SpatialBuildings
    {
        // Cells cover the game's 64 m zones, whose objects load and unload together, so streaming fills and empties whole cells
        // instead of changing the meshes of four.
        private const float CellSize = 64f;
        private const int SlotLimit = 128;
        // A merged mesh holds at most this many vertices unless one member's parts need more, so applying it takes one
        // short step and its indices take 16 bits.
        private const int VertexLimit = ushort.MaxValue;
        private const float SettleSeconds = 2f;
        // Within this distance of a detail-level change a merged member with a crossfading group shows its native renderers,
        // and draws merged again from four times as far, once the crossfade has ended.
        private const float DetailMargin = .5f;
        // A member whose groups switch at once is checked this far before a change, which covers rounding in the distances,
        // and switches its slots in the frame the engine switches its native levels.
        private const float SwitchMargin = .001f;
        // Allowance beyond the engine's crossfade duration for the frame in which the fade started.
        private const float FadeSlack = .1f;
        // Main-thread time each frame for the renderer's work that can wait: making new meshes' objects, applying job results,
        // owning members, drawing members merged again after a detail-level change, removing destroyed members, sky slots,
        // readable mesh copies, planning, job starts, registration and light takeovers. A share of the recent frame time within
        // limits, so the work takes about the same share at any frame rate. The renderer spends no more than the frame maximum in
        // a frame: its work earlier in the frame and the work it did after LateUpdate in recent frames, in the cameras' culling
        // and in hooks, come off that maximum, and a step that would not fit in what is left waits for a frame it fits in.
        private const float BudgetShare = .05f, BudgetMinimum = .3f, BudgetMaximum = 1.5f, FrameMaximum = 2.5f;
        // None of that work runs in a frame after a long one, over this many times the steady frame time, since long frames
        // come in runs, as while the game creates the objects around an arrival. The steady time starts at a short frame, since
        // the renderer may start in such a run, and follows shorter frames at the first rate a frame and longer ones at the
        // second, so a run barely raises it while a slower machine's frames raise it within seconds.
        private const float LongFrame = 2f, SteadyStart = 7f, SteadyFall = .1f, SteadyRise = .002f;
        // The least the budget reserves for the work after LateUpdate, how fast a reserve and a step's expected time decay, per
        // frame and per step, and the share of the most a frame can give a step beyond which it is long: a long step runs in a
        // frame with that much left, whether or not it fits, instead of waiting for ever.
        private const float MinimumReserve = .2f, ReserveDecay = .9f, StepDecay = .9f, LongShare = .75f;
        private const int AppliesPerFrame = 2;
        private const int BuildsInFlight = 8;
        // The share of the graphics card's memory the merged meshes may take.
        private const long MemoryShare = 8;
        // The game's sky depth camera selects detail levels with this bias.
        private const float SkyDetailBias = 10f;
        // Natural objects register this long after they are created, once the game's fade-in of distant spawns and a grown
        // tree's growth animation have ended.
        private const float NaturalDelay = 1f;
        // A hit tree shakes for a second and a growing one swaps its trunk for 0.3 seconds; each stays native a little longer.
        private const float ShakeSeconds = 1.1f, GrowthSeconds = .4f;
        // A natural object that is still fading in or growing registers later; building pieces register on their own.
        private const string Waiting = "waiting", BuildingPiece = "building piece";

        // The attributes the derived piece and sky depth programs read: every Direct3D 11 vertex program of the derived
        // piece, rock and grass shaders reads only these, with the slot index in a second stream. Normals and tangents are
        // kept as 16-bit signed normalized values, which the input assembler expands to the floats the programs read.
        [StructLayout(LayoutKind.Sequential)]
        internal struct Vertex
        {
            public Vector3 Position;
            public Unit Normal, Tangent;
            public Vector2 Texture0;
        }

        // The derived vegetation and Standard shaders' programs also read the second texture coordinates.
        [StructLayout(LayoutKind.Sequential)]
        internal struct VegetationVertex
        {
            public Vector3 Position;
            public Unit Normal, Tangent;
            public Vector2 Texture0, Texture1;
        }

        // A vector with components from -1 to 1 in 16-bit signed normalized values, within 0.000016 of each.
        [StructLayout(LayoutKind.Sequential)]
        internal struct Unit
        {
            public short X, Y, Z, W;

            internal Unit(Vector4 value)
            {
                X = Normalized(value.x);
                Y = Normalized(value.y);
                Z = Normalized(value.z);
                W = Normalized(value.w);
            }

            private static short Normalized(float value) => (short)Mathf.RoundToInt(Mathf.Clamp(value, -1f, 1f) * 32767f);
        }

        // The derived shader's per-slot material values, in its constant buffer order.
        [StructLayout(LayoutKind.Sequential)]
        internal struct Appearance
        {
            public Vector4 TextureOffset, AshlandsDamage;
            public Color Color, Emission;
            public Vector4 NoiseRippleSnow;
        }

        private struct Input { public int Source, Submesh, Slot, First, Count; }

        // A range of slots next to each other in a merged mesh's index buffer, as the compaction kernel copies it: its first
        // index, its first place in the drawn region and its index count.
        [StructLayout(LayoutKind.Sequential)]
        private struct Run { public uint First, Place, Count, Unused; }

        // A member's detail-level check, due once the camera has travelled this far in total.
        private struct Check { internal double Due; internal Member Member; internal int Stamp; }

        private sealed class Source
        {
            internal Mesh Mesh, Readable;
            // Each submesh's vertex range, from its indices.
            internal (int first, int count)[] Ranges;
            internal NativeArray<byte>[] Streams;
            internal NativeArray<byte> Indices;
            internal int Waiting;
            internal bool Requested, Queued, Failed;
        }

        // A merged mesh's identity: the cell, the native material's instance and the renderer settings it draws with.
        private struct BatchKey : IEquatable<BatchKey>
        {
            internal Vector3Int Cell;
            internal int Material, Layer;
            internal ShadowCastingMode Shadows;
            internal bool Receive;
            internal LightProbeUsage Probes;
            internal ReflectionProbeUsage Reflections;
            internal uint Mask;
            internal MotionVectorGenerationMode Motion;
            // Crops draw from merged meshes of their own, which the isolated client can switch back to native drawing.
            internal bool Crop;
            // Small natural parts, whose shadows follow small far shadows' distances.
            internal bool Small;

            public bool Equals(BatchKey other) => Crop == other.Crop && Small == other.Small && Material == other.Material && Cell == other.Cell && Layer == other.Layer && Shadows == other.Shadows &&
                Receive == other.Receive && Probes == other.Probes && Reflections == other.Reflections && Mask == other.Mask && Motion == other.Motion;
            public override bool Equals(object other) => other is BatchKey key && Equals(key);
            public override int GetHashCode() => ((Material * 397 ^ Cell.GetHashCode()) * 397 ^ Layer) * 397 ^ (int)Shadows ^ (int)Mask << 3;
        }

        // One submesh of a supported native renderer.
        private sealed class Part
        {
            internal int Renderer, Submesh, Detail = -1, Levels;
            internal Source Source;
            internal Material Material;
            internal BatchKey Key;
        }

        // A native detail-level group whose level the merged slots draw for the main camera. A crossfading group stays
        // enabled while merged, so the engine keeps its own fade state for the moment the member shows natively again.
        private sealed class Detail
        {
            internal LODGroup Group;
            internal Vector3 Reference;
            internal float Size;
            internal float[] Thresholds;
            internal int Level;
            internal bool Owned, Merging, NativeEnabled, Fades;
        }

        // A building piece or a natural object, such as a tree, rock, bush or crop, whose stable renderers can merge.
        private sealed class Member
        {
            internal WearNTear Wear;
            internal ZNetView View;
            internal GameObject Root;
            // A natural object's ground physics, which can move it, and until when a shake or growth keeps it native.
            internal StaticPhysics Physics;
            internal float BusyUntil;
            internal Cell Cell;
            internal MeshRenderer[] Renderers;
            internal int[] RendererIds;
            internal bool[] NativeForcedOff, Merging;
            // Each merging renderer's matrices and bounds, read when the member is planned.
            internal Matrix4x4[] ToWorld, ToLocal;
            internal Bounds[] RendererBounds;
            internal Bounds Bounds;
            internal Part[] Parts;
            internal Detail[] Details;
            // Groups with visible merging renderers, whose levels decide which merged slots draw.
            internal readonly List<Detail> Active = new List<Detail>();
            // The objects the game's wear, wetness and snow visuals switch, and which of them are active.
            internal GameObject[] Switches = Array.Empty<GameObject>();
            internal ulong Switched;
            // The instance ids of the piece's switched behaviours that map to it.
            internal int[] SwitchedIds = Array.Empty<int>();
            internal readonly List<Part> Merged = new List<Part>();
            // Slots of live meshes that hold the member's current parts; they draw only while it is owned.
            internal readonly List<(Batch batch, int slot, Part part)> Slots = new List<(Batch, int, Part)>();
            // Batches holding the member's merged parts, with how many parts and vertices each.
            internal readonly Dictionary<Batch, (int slots, int vertices)> Batches = new Dictionary<Batch, (int, int)>();
            internal Dictionary<int, object> Properties = new Dictionary<int, object>();
            internal ulong Signature;
            internal int Version;
            // Identifies the member's current detail-level check; its earlier entries in the queue are void.
            internal int CheckStamp;
            // An owned member with a crossfading group that shows its native renderers near a detail-level change, with its
            // slots drawing nothing, until the camera is clear of the change and a crossfade started there has ended, in real
            // and game time.
            internal bool Away;
            internal float FadeUntil, FadeUntilGame;
            internal bool Owned, Dirty, Queued, Removed, OwnQueued;
            // A natural object a player planted, and a member of the crops' merged meshes: planted, or with a material
            // that has instancing off.
            internal bool Planted, Crop;
            // Each small merging renderer's radius, as small far shadows measures it.
            internal float[] Radius;
            // The kinds of added coverage the member belongs to.
            internal int Coverage;
            // The member's place among those finishing, or -1.
            internal int Finishing = -1;
        }

        private sealed class Batch
        {
            internal BatchKey Key;
            internal Material Material;
            internal Cell Cell;
            internal readonly List<Member> Members = new List<Member>();
            internal int Count, Vertices;
            internal GameObject Object;
            internal MeshFilter Filter, TwinFilter;
            // The twin casting the mesh's shadows, when it casts them one-sided, and the buffers both draw with.
            internal MeshRenderer Renderer, Twin;
            internal MaterialPropertyBlock Properties;
            internal Mesh Mesh;
            // The mesh's graphics memory, counted in the merged meshes' total.
            internal long Bytes;
            internal Bounds Bounds;
            // The lights taken over that reach the mesh's slots, the first ones with ranges of its drawn region.
            internal readonly List<LightRange> Lights = new List<LightRange>();
            // What each slot of the live mesh holds.
            internal (Member member, Part part, int version)[] Live = Array.Empty<(Member, Part, int)>();
            // Pieces read appearance slots; scenery reads the shared ambient lighting slots instead.
            internal GraphicsBuffer Transforms, Appearances, Context, SkyTransforms;
            internal bool Vegetation;
            // A mesh of small natural parts casts shadows from its own copy of the slots' matrices, in which a slot farther than
            // small far shadows' distance holds nothing, and its property block.
            internal GraphicsBuffer TwinTransforms;
            internal MaterialPropertyBlock TwinProperties;
            internal Matrix4x4[] TwinData;
            internal bool[] SlotFar;
            internal MaterialPropertyBlock SkyProperties;
            // The native material's render type, which decides whether the sky depth render draws it.
            internal string SkyType;
            internal readonly Matrix4x4[] TransformData = new Matrix4x4[SlotLimit * 2];
            // Each slot's range in the index buffer, the slots in index order and the range the renderer draws, which is all the
            // cameras draw: a slot drawing nothing would still run the vertex program for each of its vertices. With the
            // compaction kernel that range lies in the region after the whole mesh, from DrawnFirst on, and holds only the slots
            // drawing now, at most Capacity indices; otherwise it is the span of the whole mesh holding them. Indices counts
            // the whole mesh's indices and Total the buffer's.
            internal int[] IndexFirst = Array.Empty<int>(), IndexCount = Array.Empty<int>(), Order = Array.Empty<int>();
            internal int SpanFirst, SpanEnd = -1, Indices, Total, DrawnFirst, Capacity;
            // The index buffer's raw view for the compaction kernel, the slots its drawn region holds in two words, and the
            // runs of a queued compaction.
            internal GraphicsBuffer IndexBuffer;
            internal ulong DrawnLow, DrawnHigh;
            internal bool DrawnKnown, Compacting;
            internal int FirstRun, RunCount, DrawnCount, CopiedCount;
            // The light submeshes holding a light's copy, emptied before the drawn range moves so that no two overlap.
            internal int LightSubmeshes;
            // The slots the sky depth camera draws: those of members drawing merged, at the levels that camera selects. Kept
            // once the mesh is within that camera's reach, uploaded within the frame budget beforehand and in its render where
            // they changed since.
            internal Matrix4x4[] SkyData;
            internal bool SkyQueued;
            internal readonly Appearance[] AppearanceData = new Appearance[SlotLimit];
            internal int TransformFrom = int.MaxValue, TransformTo = -1, AppearanceFrom = int.MaxValue, AppearanceTo = -1, SkyFrom = int.MaxValue, SkyTo = -1;
            internal Build Build;
            internal bool Dirty;
        }

        private sealed class Build
        {
            internal Batch Batch;
            internal JobHandle Handle;
            internal Mesh.MeshDataArray Sources, Output;
            internal NativeArray<Input> Inputs;
            internal NativeArray<int> Result;
            internal (Member member, Part part, int version)[] Slots;
            internal int[] IndexFirst, IndexCount, Order;
            internal int Total, DrawnFirst, Capacity, Vertices;
            internal Bounds Bounds;
        }

        private sealed class Cell
        {
            internal Vector3Int Key;
            internal Vector3 Center;
            internal readonly HashSet<Member> Members = new HashSet<Member>();
            internal readonly List<Batch> Batches = new List<Batch>();
            internal readonly Dictionary<BatchKey, List<Batch>> ByKey = new Dictionary<BatchKey, List<Batch>>();
            // Changed members waiting to be planned.
            internal readonly List<Member> Changes = new List<Member>();
            internal float Changed;
            // The member whose change or removal last restarted the cell's settling time.
            internal Member ChangedBy;
        }

        // Reads every source vertex a batch uses and writes the merged vertices, slot indices and indices.
        private struct BuildJob : IJob
        {
            [ReadOnly] public Mesh.MeshDataArray Sources;
            [ReadOnly] public NativeArray<Input> Inputs;
            public Mesh.MeshDataArray Output;
            public NativeArray<int> Result;
            // Vegetation and Standard vertices also carry the second texture coordinates.
            public bool Vegetation;
            // A mesh of at most 65,535 vertices takes 16-bit indices.
            public bool ShortIndices;

            public void Execute()
            {
                var output = Output[0];
                var vertices = Vegetation ? default : output.GetVertexData<Vertex>(0);
                var vegetation = Vegetation ? output.GetVertexData<VegetationVertex>(0) : default;
                var slots = output.GetVertexData<uint>(1);
                var indices = ShortIndices ? default : output.GetIndexData<uint>();
                var shortIndices = ShortIndices ? output.GetIndexData<ushort>() : default;
                int vertexOffset = 0, indexOffset = 0, current = -1;
                var positions = default(NativeArray<Vector3>);
                var normals = default(NativeArray<Vector3>);
                var tangents = default(NativeArray<Vector4>);
                var uv0 = default(NativeArray<Vector2>);
                var uv1 = default(NativeArray<Vector2>);
                bool hasNormals = false, hasTangents = false, has0 = false, has1 = false;
                for (int member = 0; member < Inputs.Length; member++)
                {
                    var input = Inputs[member];
                    var source = Sources[input.Source];
                    if (input.Source != current)
                    {
                        current = input.Source;
                        if (positions.IsCreated)
                        {
                            positions.Dispose();
                            if (normals.IsCreated) normals.Dispose();
                            if (tangents.IsCreated) tangents.Dispose();
                            if (uv0.IsCreated) uv0.Dispose();
                            if (uv1.IsCreated) uv1.Dispose();
                        }
                        normals = default; tangents = default; uv0 = default; uv1 = default;
                        int count = source.vertexCount;
                        positions = new NativeArray<Vector3>(count, Allocator.Temp);
                        source.GetVertices(positions);
                        if (hasNormals = source.HasVertexAttribute(VertexAttribute.Normal)) source.GetNormals(normals = new NativeArray<Vector3>(count, Allocator.Temp));
                        if (hasTangents = source.HasVertexAttribute(VertexAttribute.Tangent)) source.GetTangents(tangents = new NativeArray<Vector4>(count, Allocator.Temp));
                        if (has0 = source.HasVertexAttribute(VertexAttribute.TexCoord0)) source.GetUVs(0, uv0 = new NativeArray<Vector2>(count, Allocator.Temp));
                        if (has1 = Vegetation && source.HasVertexAttribute(VertexAttribute.TexCoord1)) source.GetUVs(1, uv1 = new NativeArray<Vector2>(count, Allocator.Temp));
                    }
                    int first = input.First, length = input.Count;
                    for (int vertex = 0; vertex < length; vertex++)
                    {
                        int at = first + vertex;
                        // A three-component normal reaches the programs with a fourth of 1, as natively.
                        var direction = hasNormals ? normals[at] : Vector3.zero;
                        var normal = new Unit(new Vector4(direction.x, direction.y, direction.z, 1f));
                        var tangent = hasTangents ? new Unit(tangents[at]) : default;
                        var texture0 = has0 ? uv0[at] : Vector2.zero;
                        if (Vegetation)
                            vegetation[vertexOffset + vertex] = new VegetationVertex {
                                Position = positions[at], Normal = normal, Tangent = tangent, Texture0 = texture0, Texture1 = has1 ? uv1[at] : Vector2.zero };
                        else
                            vertices[vertexOffset + vertex] = new Vertex { Position = positions[at], Normal = normal, Tangent = tangent, Texture0 = texture0 };
                        slots[vertexOffset + vertex] = (uint)input.Slot;
                    }
                    var sourceIndices = new NativeArray<int>(source.GetSubMesh(input.Submesh).indexCount, Allocator.Temp);
                    source.GetIndices(sourceIndices, input.Submesh, true);
                    for (int index = 0; index < sourceIndices.Length; index++)
                    {
                        int local = sourceIndices[index] - first;
                        if (local < 0 || local >= length) { Result[0] = 1; return; }
                        if (ShortIndices) shortIndices[indexOffset + index] = (ushort)(vertexOffset + local);
                        else indices[indexOffset + index] = (uint)(vertexOffset + local);
                    }
                    vertexOffset += length;
                    indexOffset += sourceIndices.Length;
                    sourceIndices.Dispose();
                }
                // Any rest of the buffer is the drawn region, which the compaction kernel fills.
                if (ShortIndices) for (int index = indexOffset; index < shortIndices.Length; index++) shortIndices[index] = 0;
                else for (int index = indexOffset; index < indices.Length; index++) indices[index] = 0;
            }
        }

        private static readonly VertexAttributeDescriptor[] layout = {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm16, 4),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.SNorm16, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.UInt32, 1, 1) };
        private static readonly VertexAttributeDescriptor[] vegetationLayout = {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm16, 4),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.SNorm16, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.UInt32, 1, 1) };
        private const MeshUpdateFlags quiet = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices |
            MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;
        // Behaviours that never change how a piece renders: building, wear, network and material-manager components, the
        // ledge auto-jump trigger, item drops on destruction and the unseen-effect marker on a piece's own particle
        // systems.
        private static readonly HashSet<Type> pieceBehaviours = new HashSet<Type> { typeof(Piece), typeof(WearNTear), typeof(ZNetView), typeof(MaterialManNotifier),
            typeof(AutoJumpLedge), typeof(DropOnDestroyed), typeof(UnseenEffect) };
        // Behaviours that touch no renderer a record holds: they read the piece, show hover text, hold an area, a seat, a spawn
        // point or a station connection, switch a light, rotate the piece once as it spawns, or create objects of their own,
        // which a record never holds and the game keeps drawing natively.
        private static readonly HashSet<Type> inertBehaviours = new HashSet<Type> { typeof(EffectArea), typeof(GuidePoint), typeof(Bed), typeof(Chair),
            typeof(Switch), typeof(HoverText), typeof(Sign), typeof(MapTable), typeof(MusicVolume), typeof(Incinerator), typeof(OfferingBowl),
            typeof(StationExtension), typeof(LightLod), typeof(RandomPieceRotation), typeof(ZSFX), typeof(SmokeSpawner), typeof(CinderSpawner),
            typeof(SpawnOnDamaged), typeof(ProximityState), typeof(TimedDestruction) };
        // Behaviours that change how a piece looks only by switching objects of its own on and off, with the objects each one
        // switches and the methods that do it. The methods are patched to compare those objects and hand the piece back to
        // native drawing when they changed what it draws, as the game's own wear, wetness and snow visuals do.
        private static readonly (Type type, string[] objects, string[] methods)[] switchedBehaviours = {
            (typeof(Container), new[] { "m_open", "m_closed" }, new[] { "UpdateUseVisual" }),
            (typeof(Fireplace), new[] { "m_enabledObject", "m_enabledObjectHigh", "m_enabledObjectLow", "m_fullObject", "m_halfObject", "m_emptyObject",
                "m_playerBaseObject" }, new[] { "UpdateState" }),
            (typeof(CookingStation), new[] { "m_cookingObject", "m_haveFireObject", "m_haveFuelObject" }, new[] { "UpdateVisual", "SetSlotVisual" }),
            (typeof(Smelter), new[] { "m_enabledObject", "m_disabledObject", "m_haveFuelObject", "m_haveOreObject", "m_noOreObject" }, new[] { "UpdateState" }),
            (typeof(Fermenter), new[] { "m_fermentingObject", "m_readyObject", "m_topObject" }, new[] { "SlowUpdate" }),
            (typeof(SapCollector), new[] { "m_notEmptyEffect", "m_workingEffect" }, new[] { "UpdateEffects" }),
            (typeof(Beehive), new[] { "m_beeEffect" }, new[] { "UpdateBees" }),
            (typeof(WispSpawner), new[] { "m_wispsNearbyObject" }, new[] { "UpdateDemister" }),
            (typeof(Trap), new[] { "m_visualArmed", "m_visualUnarmed" }, new[] { "UpdateState", "TriggerTrap" }),
            (typeof(CraftingStation), new[] { "m_areaMarker", "m_inUseObject", "m_haveFireObject" }, new[] { "ShowAreaMarker", "HideMarker", "CustomUpdate", "CheckFire" }),
            // An item stand destroys and creates the model of the item it holds instead of switching one, so its method
            // compares the renderers themselves.
            (typeof(ItemStand), new string[0], new[] { "SetVisualItem" }) };
        // Behaviours that move, scale or replace objects under their own object. Every renderer there keeps drawing natively
        // while the rest of the piece merges, so one of these on the piece's own object keeps the whole piece native.
        private static readonly HashSet<Type> localBehaviours = new HashSet<Type> { typeof(Aoe), typeof(LightFlicker), typeof(GlobalWind),
            typeof(CircleProjector), typeof(ShieldGenerator), typeof(Turret), typeof(WaterVolume), typeof(Windmill) };
        private static readonly HashSet<Type> allowedBehaviours = new HashSet<Type>(pieceBehaviours.Concat(inertBehaviours)
            .Concat(switchedBehaviours.Select(entry => entry.type)).Concat(localBehaviours));
        // Each switched behaviour's object fields, resolved once.
        private static readonly Dictionary<Type, FieldInfo[]> switchedFields = switchedBehaviours.ToDictionary(entry => entry.type,
            entry => entry.objects.Select(name => AccessTools.Field(entry.type, name)).Where(field => field != null).ToArray());
        // Behaviours of natural objects that never change how they render or whose changes the hooks below follow: network,
        // ground physics, destruction, picking, a tree's shake and growth, an ore rock's hit areas, attack targets, hover text,
        // drops, the fade-in of distant spawns, terrain shaping around rocks, a natural object's piece record and the
        // unseen-effect marker.
        private static readonly HashSet<Type> naturalBehaviours = new HashSet<Type> { typeof(ZNetView), typeof(StaticPhysics), typeof(Destructible),
            typeof(Pickable), typeof(TreeBase), typeof(MineRock), typeof(StaticTarget), typeof(HoverText), typeof(DropOnDestroyed), typeof(LodFadeInOut),
            typeof(TerrainModifier), typeof(Piece), typeof(UnseenEffect) };
        // Components that touch no renderer a record holds, allowed by name because their assemblies are not referenced here: the
        // game's editor-time mesh combiner, whose result is an ordinary renderer; Progression Fix's distant torch flames, which
        // write a particle renderer's smallest size; Multi User Chest's container extension; and this mod's own container and
        // inventory lifetimes. Checked against each one's code.
        private static readonly HashSet<string> inertComponents = new HashSet<string> { "SimpleMeshCombine", "DistantTorchFlames",
            "ContainerExtend", "ContainerLifetime", "SupplyLifetime", "CanvasScaler", "TextMeshProUGUI" };
        // The objects local behaviours move within the hierarchy being recorded, while it is recorded.
        private static readonly HashSet<Transform> localRoots = new HashSet<Transform>();
        // The game shaders whose derived copies draw merged meshes, with the bundle holding each copy. The piece shader's
        // copy reads appearance slots; the scenery shaders' copies and the copy of Unity's Standard shader, which the Ashlands
        // fortress walls draw with, read ambient lighting slots instead.
        private const string pieceShader = "Custom/Piece", vegetationShader = "Custom/Vegetation", standardShader = "Standard";
        private static readonly (string shader, string bundle)[] sceneryShaders = { (vegetationShader, "world-vegetation-windows.bundle"),
            ("Custom/StaticRock", "world-staticrock-windows.bundle"), ("Custom/Grass", "world-grass-windows.bundle"),
            (standardShader, "world-standard-windows.bundle") };
        private static readonly int colorId = Shader.PropertyToID("_Color"), emissionId = Shader.PropertyToID("_EmissionColor"),
            ashlandsId = Shader.PropertyToID("_TakingAshlandsDamage"), snowId = Shader.PropertyToID("_SnowLevel"),
            noiseId = Shader.PropertyToID("_ValueNoise"), rippleId = Shader.PropertyToID("_RippleDistance"), offsetId = Shader.PropertyToID("_UVOffset"),
            mainTextureId = Shader.PropertyToID("_MainTex"), mainTextureScaleId = Shader.PropertyToID("_MainTex_ST"), cutoffId = Shader.PropertyToID("_Cutoff"),
            skySlotsId = Shader.PropertyToID("TenemoPieces"), transformsId = Shader.PropertyToID("TenemoBuildingTransforms"),
            appearanceId = Shader.PropertyToID("TenemoPieceProperties"), probeStateId = Shader.PropertyToID("TenemoBuildingProbeState"),
            contextId = Shader.PropertyToID("TenemoDrawContext");
        private static readonly Type containerType = AccessTools.Inner(typeof(MaterialMan), "PropertyContainer");
        private static readonly AccessTools.FieldRef<object, List<Renderer>> containerRenderers = AccessTools.FieldRefAccess<List<Renderer>>(containerType, "m_assignedRenderers");
        private static readonly AccessTools.FieldRef<object, IDictionary> containerProperties = AccessTools.FieldRefAccess<IDictionary>(containerType, "m_shaderProperties");
        private static readonly AccessTools.FieldRef<object, IDictionary> materialBlocks = AccessTools.FieldRefAccess<IDictionary>(typeof(MaterialMan), "m_blocks");
        private static readonly AccessTools.FieldRef<ZNetScene, List<ZNetView>> unloadedViews = AccessTools.FieldRefAccess<ZNetScene, List<ZNetView>>("m_tempRemoved");
        private static readonly Type propertyType = AccessTools.Inner(typeof(MaterialMan), "ShaderProperty`1");
        private static readonly AccessTools.FieldRef<object, int> propertyName = AccessTools.FieldRefAccess<int>(AccessTools.Inner(typeof(MaterialMan), "ShaderPropertyBase"), "NameID");
        // The value types the merged slots can express, read without reflection calls or boxing; others use their getter.
        private static readonly PropertyReader<float> floatValues = new PropertyReader<float>();
        private static readonly PropertyReader<Color> colorValues = new PropertyReader<Color>();
        private static readonly PropertyReader<Vector4> vector4Values = new PropertyReader<Vector4>();
        private static readonly PropertyReader<Vector3> vector3Values = new PropertyReader<Vector3>();
        private static readonly PropertyReader<Vector2> vector2Values = new PropertyReader<Vector2>();
        private static readonly Dictionary<Type, MethodInfo> propertyValues = new Dictionary<Type, MethodInfo>();

        private sealed class PropertyReader<T> where T : struct, IEquatable<T>
        {
            internal readonly Type Type = propertyType.MakeGenericType(typeof(T));
            internal readonly AccessTools.FieldRef<object, T> Value;
            internal PropertyReader() => Value = AccessTools.FieldRefAccess<T>(Type, "_value");
            internal bool Same(object property, object stored) => stored is T known && known.Equals(Value(property));
        }

        private static readonly Harmony patches = new Harmony(Plugin.Id + ".spatial-buildings");
        private static readonly Dictionary<WearNTear, Member> pieces = new Dictionary<WearNTear, Member>();
        private static readonly Dictionary<ZNetView, Member> natural = new Dictionary<ZNetView, Member>();
        private static readonly Dictionary<int, Member> rendererMembers = new Dictionary<int, Member>();
        // Each registered piece's switched behaviours by instance id, with the piece's object and its member, or none for a
        // piece that stays native, which their visual methods would otherwise find by searching up the hierarchy on every
        // call. A piece that stays native is never recorded later; the ids of its behaviours are kept until it is destroyed.
        private static readonly Dictionary<int, (Member member, GameObject root)> switchedMembers = new Dictionary<int, (Member, GameObject)>();
        private static readonly Dictionary<WearNTear, int[]> nativeSwitched = new Dictionary<WearNTear, int[]>();
        private static readonly HashSet<Type> switchedTypes = new HashSet<Type>(switchedBehaviours.Select(entry => entry.type));
        private static readonly List<MonoBehaviour> behaviourList = new List<MonoBehaviour>();
        private static readonly List<int> switchedIdList = new List<int>();
        private static readonly Dictionary<Vector3Int, Cell> cells = new Dictionary<Vector3Int, Cell>();
        // Cells with changed pieces or meshes.
        private static readonly HashSet<Cell> active = new HashSet<Cell>();
        private static readonly List<Build> builds = new List<Build>();
        // Owned members' next detail-level checks, a min-heap by the camera travel at which each is due. The camera's
        // distance to a group changes no faster than the camera moves, so a member needs no check before the camera has
        // travelled as far as its nearest level change.
        private static readonly List<Check> checks = new List<Check>();
        // Owned members showing natively near a detail-level change or while a tree moves.
        private static readonly HashSet<Member> near = new HashSet<Member>();
        // The members showing natively that are clear of every change, followed within the frame budget until a crossfade
        // started near one has ended and a shaken or growing tree has finished, and the next one to look at.
        private static readonly List<Member> finishing = new List<Member>();
        private static int finishingNext;
        private static double travelled;
        private static Vector3 lastCamera;
        private static bool cameraKnown;
        private static readonly Dictionary<Mesh, Source> sources = new Dictionary<Mesh, Source>();
        private static readonly Queue<Source> received = new Queue<Source>();
        // Source meshes newly registered pieces use, requested within the frame's budget.
        private static readonly Queue<Source> requests = new Queue<Source>();
        private static readonly Dictionary<Material, Material> materials = new Dictionary<Material, Material>();
        // The game shader each material's merged copy derives from, or null when it cannot merge.
        private static readonly Dictionary<Material, string> supportedMaterials = new Dictionary<Material, string>();
        private static readonly Dictionary<Material, Appearance> materialAppearance = new Dictionary<Material, Appearance>();
        private static readonly Dictionary<int, string> prefabExclusions = new Dictionary<int, string>();
        private static readonly Queue<(WearNTear wear, int frame)> pending = new Queue<(WearNTear, int)>();
        // Every network object's view from its creation, in order; those that are natural objects register once due.
        private static readonly Queue<(ZNetView view, float time)> pendingNatural = new Queue<(ZNetView, float)>();
        private static readonly Dictionary<int, string> naturalExclusions = new Dictionary<int, string>();
        // The last reason an instance of a prefab stayed native although its components allow merging, such as its renderers,
        // materials or detail levels. One write when an instance is rejected; the census reads it.
        private static readonly Dictionary<int, string> recordExclusions = new Dictionary<int, string>();
        private static readonly Dictionary<string, long> excluded = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> naturalExcluded = new Dictionary<string, long>();
        private static readonly AccessTools.FieldRef<ZNetView, bool> ghostView = AccessTools.FieldRefAccess<ZNetView, bool>("m_ghost");
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> sceneViews =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
        private static readonly Dictionary<string, long> handedBack = new Dictionary<string, long>();
        // Reused lists for registration, planning and the sky depth render.
        private static readonly List<Component> components = new List<Component>();
        private static readonly List<MeshRenderer> rendererList = new List<MeshRenderer>();
        private static readonly List<LODGroup> groupList = new List<LODGroup>();
        private static readonly List<Material> materialList = new List<Material>();
        private static readonly List<int> indexList = new List<int>();
        private static readonly List<Part> partList = new List<Part>();
        private static readonly Dictionary<int, (int detail, int levels)> membership = new Dictionary<int, (int, int)>();
        private static readonly HashSet<int> mergingIds = new HashSet<int>();
        private static readonly List<Cell> cellOrder = new List<Cell>();
        private static readonly List<Cell> idle = new List<Cell>();
        // Members whose merged parts went live, owned within the frame budget.
        private static readonly Queue<Member> owning = new Queue<Member>();
        // Destroyed members, which leave their cells and merged meshes within the frame budget.
        private static readonly Queue<Member> removing = new Queue<Member>();
        private static readonly List<Mesh> uniqueSources = new List<Mesh>();
        private static readonly Dictionary<Source, int> sourceIndices = new Dictionary<Source, int>();
        private static readonly List<Batch> skyBatches = new List<Batch>();
        private static readonly Plane[] frustum = new Plane[6];
        private static readonly Comparison<Cell> nearestFirst = (a, b) => (a.Center - orderFrom).sqrMagnitude.CompareTo((b.Center - orderFrom).sqrMagnitude);
        private static Vector3 orderFrom;
        private static AssetBundle bundle;
        private static Shader shader;
        private static readonly List<AssetBundle> sceneryBundles = new List<AssetBundle>();
        // The derived shader for each supported game shader.
        private static readonly Dictionary<string, Shader> derived = new Dictionary<string, Shader>();
        // Every scenery slot's ambient lighting, the same for all: with no light probes in the world, each native renderer
        // lights from the ambient probe. Rewritten when that probe changes, as it does through the day.
        private static GraphicsBuffer probeState;
        private static readonly Vector4[] probeData = new Vector4[SlotLimit * 7];
        private static SphericalHarmonicsL2 probeSource;
        private static bool probeKnown;
        private static Material skyMaterial;
        private static CommandBuffer skyCommands;
        private static Camera skyCamera;
        private static DepthCamera skyDepthCamera;
        private static float skyScale;
        // The layers the sky depth camera renders, read as its render starts.
        private static int skyMask;
        // Meshes whose sky slots are prepared before that camera's next render, and where the player was when meshes it may
        // reach were last looked for; they are looked for again once the player has moved this far.
        private static readonly Queue<Batch> skyPending = new Queue<Batch>();
        private static Vector3 skyScanFrom;
        private static bool skyScanned;
        private const float SkyScanStep = 8f;
        // The render types the sky depth camera's replacement shader draws; other materials are left out of that render.
        private static Shader skyReplacement;
        private static readonly HashSet<string> skyTypes = new HashSet<string>();
        private static readonly ShaderTagId renderType = new ShaderTagId("RenderType");
        // The kernels that fill merged meshes' drawn regions, the meshes queued for them and the runs they copy; without
        // compute shaders the renderer draws spans of the whole meshes instead.
        private static ComputeShader compaction;
        private static int compact16, compact32;
        private static bool compactIndices;
        private static GraphicsBuffer slotRuns;
        private static Run[] runs = new Run[SlotLimit * 8];
        private static readonly List<Batch> compactions = new List<Batch>();
        private static readonly int slotCompactionId = Shader.PropertyToID("_SlotCompaction"), slotRunsId = Shader.PropertyToID("_SlotRuns"),
            mergedIndicesId = Shader.PropertyToID("_MergedIndices");
        private static bool flushPending, skyDepth, linear;
        // The main camera's detail settings, compared as read: a value computed from them may keep extra precision.
        private static float detailScale = float.NaN, detailBias = float.NaN, detailView = float.NaN;
        private static int detailMaximum = -1;
        private static long rebuilds, swaps, switches, evaluations, frames, mainTicks, maximumTicks, jobTicks, compacted, drawnWhole;
        private static int mainFrame = -1, healthVisuals;
        private static long slowestRegistration, slowestApply;
        private static string slowestPrefab;
        private static int slowestApplyVertices;
        private static long frameTicks;
        private static float frameMilliseconds;
        private static float budgetMilliseconds;
        // When this frame's budgeted work started, its ends by its budget and by the most the renderer may spend in a frame, and
        // whether a large step has run beyond its budget; the frame whose LateUpdate ended last, the renderer's work by then, and
        // the reserve for its work after LateUpdate.
        private static long budgetStart, budgetEnd, capEnd, lateTicks, afterReserve;
        private static bool overrun;
        // The frame time long frames are judged against, and the frames whose work waited after a long one.
        private static float steadyMilliseconds;
        private static long waitedFrames;
        // The merged meshes' graphics memory, and the most it may reach before new meshes wait: a share of the graphics card's
        // memory, so a smaller card keeps more of the loaded area native, nearest cells merged first. The isolated client
        // lowers it for checks.
        private static long mergedBytes;
        internal static long MemoryLimit;
        private static int lateFrame = -1;
        // While a measurement counts them, the main thread's processor cycles in the renderer's work, which preemption does
        // not inflate as it does its time, and the last frame in which that work saw the collector's events, counted at the
        // address the measurement gives: a collector stop lands in whatever code allocates when it is due.
        internal static bool CountCycles;
        internal static IntPtr CollectorEvents;
        private static ulong mainCycles;
        private static long collectorFrame = -1;
        internal static bool Running { get; private set; }
        internal static bool Native { get; private set; }
        internal static string Failure { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                throw new NotSupportedException("The spatial building renderer needs Direct3D 11; rendering stays native.");
            string folder = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            bundle = AssetBundle.LoadFromFile(Path.Combine(folder, "building-chunk-windows.bundle"));
            if (!bundle) throw new InvalidOperationException("The spatial building shader bundle is unavailable; rendering stays native.");
            shader = bundle.LoadAsset<Shader>("building-buffer.shader");
            var skyShader = bundle.LoadAsset<Shader>("building-sky-depth.shader");
            bool supported = shader && shader.isSupported && skyShader && skyShader.isSupported;
            derived.Clear();
            derived[pieceShader] = shader;
            foreach (var (name, file) in sceneryShaders)
            {
                if (!supported) break;
                var loaded = AssetBundle.LoadFromFile(Path.Combine(folder, file));
                if (!loaded) { supported = false; break; }
                sceneryBundles.Add(loaded);
                var copy = loaded.LoadAsset<Shader>("building-buffer.shader");
                if (copy && copy.isSupported) derived[name] = copy; else supported = false;
            }
            if (supported && SystemInfo.supportsComputeShaders)
            {
                compaction = bundle.LoadAsset<ComputeShader>("building-visibility.compute");
                supported = compaction && compaction.HasKernel("CompactSlots16") && compaction.HasKernel("CompactSlots32");
                if (supported)
                {
                    compact16 = compaction.FindKernel("CompactSlots16");
                    compact32 = compaction.FindKernel("CompactSlots32");
                }
            }
            compactIndices = compaction;
            if (!supported)
            {
                UnloadShaders();
                throw new InvalidOperationException("The spatial building shaders are unavailable or unsupported; rendering stays native.");
            }
            var lightProbes = LightmapSettings.lightProbes;
            if (lightProbes && lightProbes.count > 0)
            {
                UnloadShaders();
                throw new NotSupportedException("The world has light probes, which the merged scenery's ambient lighting slots do not interpolate; rendering stays native.");
            }
            Running = true;
            Native = false;
            Failure = null;
            // Each start reports its own work.
            rebuilds = swaps = switches = evaluations = frames = mainTicks = maximumTicks = jobTicks = compacted = drawnWhole = slowestRegistration = slowestApply = waitedFrames = 0;
            mainCycles = 0;
            collectorFrame = -1;
            slowestPrefab = null;
            slowestApplyVertices = 0;
            Array.Clear(phaseTicks, 0, phaseTicks.Length);
            Array.Clear(phaseMaximum, 0, phaseMaximum.Length);
            Array.Clear(phaseCollected, 0, phaseCollected.Length);
            excluded.Clear();
            naturalExcluded.Clear();
            handedBack.Clear();
            linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            detailScale = detailBias = detailView = float.NaN;
            frameMilliseconds = 0f;
            steadyMilliseconds = SteadyStart;
            mergedBytes = 0;
            MemoryLimit = SystemInfo.graphicsMemorySize * 1048576L / MemoryShare;
            try
            {
                Prepare();
                skyMaterial = new Material(skyShader) { name = "World Performance spatial buildings sky depth", hideFlags = HideFlags.HideAndDontSave };
                skyCommands = new CommandBuffer { name = "World Performance spatial buildings sky depth" };
                probeState = new GraphicsBuffer(GraphicsBuffer.Target.Constant, SlotLimit * 7, 16);
                probeKnown = false;
                StartLights();
                patches.Patch(AccessTools.Method(typeof(WearNTear), "Awake"), postfix: new HarmonyMethod(typeof(SpatialBuildings), nameof(Created)));
                patches.Patch(AccessTools.Method(typeof(WearNTear), "OnDestroy"), prefix: new HarmonyMethod(typeof(SpatialBuildings), nameof(Destroyed)));
                healthVisuals = 0;
                patches.Patch(AccessTools.Method(typeof(WearNTear), "SetHealthVisual"), prefix: new HarmonyMethod(typeof(SpatialBuildings), nameof(HealthVisualStarting)),
                    finalizer: new HarmonyMethod(typeof(SpatialBuildings), nameof(HealthVisualChanged)));
                patches.Patch(AccessTools.Method(typeof(WearNTear), "UpdateSnowVisual"), postfix: new HarmonyMethod(typeof(SpatialBuildings), nameof(SnowVisualChanged)));
                patches.Patch(AccessTools.Method(typeof(ZNetView), "SetLocalScale"), postfix: new HarmonyMethod(typeof(SpatialBuildings), nameof(Scaled)));
                patches.Patch(AccessTools.Method(containerType, "UpdateBlock"), postfix: new HarmonyMethod(typeof(SpatialBuildings), nameof(PropertiesApplied)));
                patches.Patch(AccessTools.Method(typeof(ZNetScene), "OnDestroy"), prefix: new HarmonyMethod(typeof(SpatialBuildings), nameof(WorldClosed)));
                patches.Patch(AccessTools.Method(typeof(DepthCamera), "RenderDepth"), prefix: new HarmonyMethod(typeof(SpatialBuildings), nameof(BeforeSkyDepth)),
                    finalizer: new HarmonyMethod(typeof(SpatialBuildings), nameof(AfterSkyDepth)));
                patches.Patch(AccessTools.Method(typeof(Destructible), nameof(Destructible.CreateFragments)),
                    transpiler: new HarmonyMethod(typeof(SpatialBuildings), nameof(FragmentsFromMerged)));
                // Natural objects: creation and removal, and the game's own paths that change how they render or move them.
                patches.Patch(AccessTools.Method(typeof(ZNetView), "Awake"), postfix: new HarmonyMethod(typeof(SpatialBuildings), nameof(ViewCreated)));
                patches.Patch(AccessTools.Method(typeof(ZNetView), "OnDestroy"), prefix: new HarmonyMethod(typeof(SpatialBuildings), nameof(ViewDestroyed)));
                patches.Patch(AccessTools.Method(typeof(ZNetScene), "RemoveObjects"), postfix: new HarmonyMethod(typeof(SpatialBuildings), nameof(Unloaded)));
                var pickable = new HarmonyMethod(typeof(SpatialBuildings), nameof(PickableChanged));
                patches.Patch(AccessTools.Method(typeof(Pickable), nameof(Pickable.SetPicked)), postfix: pickable);
                patches.Patch(AccessTools.Method(typeof(Pickable), nameof(Pickable.SetEnabled), new[] { typeof(int) }), postfix: pickable);
                patches.Patch(AccessTools.Method(typeof(Pickable), "UpdateRespawn"), postfix: pickable);
                patches.Patch(AccessTools.Method(typeof(TreeBase), "RPC_Shake"), prefix: new HarmonyMethod(typeof(SpatialBuildings), nameof(TreeShaking)));
                patches.Patch(AccessTools.Method(typeof(TreeBase), "RPC_Grow"), prefix: new HarmonyMethod(typeof(SpatialBuildings), nameof(TreeGrowing)));
                var rock = new HarmonyMethod(typeof(SpatialBuildings), nameof(RockChanged));
                patches.Patch(AccessTools.Method(typeof(MineRock), "UpdateVisability"), postfix: rock);
                patches.Patch(AccessTools.Method(typeof(MineRock), "RPC_Hide"), postfix: rock);
                patches.Patch(AccessTools.Method(typeof(StaticPhysics), "Fall"), postfix: new HarmonyMethod(typeof(SpatialBuildings), nameof(Falling)));
                patches.Patch(AccessTools.Method(typeof(StaticPhysics), "PushUp"), prefix: new HarmonyMethod(typeof(SpatialBuildings), nameof(PushStarting)),
                    postfix: new HarmonyMethod(typeof(SpatialBuildings), nameof(Pushed)));
                // The visual methods of the behaviours whose pieces merge; a missing method leaves that behaviour's pieces native.
                var switched = new HarmonyMethod(typeof(SpatialBuildings), nameof(SwitchedVisualChanged));
                foreach (var entry in switchedBehaviours)
                    foreach (string name in entry.methods)
                    {
                        var method = AccessTools.Method(entry.type, name);
                        if (method != null) patches.Patch(method, postfix: switched);
                        else { allowedBehaviours.Remove(entry.type); Plugin.Fallback("World Performance: " + entry.type.Name + "." + name + " is missing; its pieces stay native"); }
                    }
                var depth = UnityEngine.Object.FindAnyObjectByType<DepthCamera>();
                if (depth) SkyCamera(depth);
                foreach (var wear in WearNTear.GetAllInstances()) if (wear) pending.Enqueue((wear, -1));
                if (ZNetScene.instance)
                    foreach (var view in sceneViews(ZNetScene.instance).Values) if (view) pendingNatural.Enqueue((view, Time.realtimeSinceStartup));
            }
            catch (Exception error) { Fail(error); throw; }
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            CropsNative = false;
            patches.UnpatchSelf();
            Clear();
            StopLights();
            if (skyCamera && skyCommands != null) skyCamera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, skyCommands);
            skyCamera = null;
            skyDepthCamera = null;
            skyCommands?.Release();
            skyCommands = null;
            if (skyMaterial) UnityEngine.Object.Destroy(skyMaterial);
            skyMaterial = null;
            foreach (var source in sources.Values) Release(source);
            sources.Clear();
            received.Clear();
            requests.Clear();
            foreach (var material in materials.Values) if (material) UnityEngine.Object.Destroy(material);
            materials.Clear();
            supportedMaterials.Clear();
            materialAppearance.Clear();
            prefabExclusions.Clear();
            naturalExclusions.Clear();
            recordExclusions.Clear();
            probeState?.Release();
            probeState = null;
            UnloadShaders();
        }

        private static void UnloadShaders()
        {
            if (bundle) bundle.Unload(true);
            bundle = null;
            shader = null;
            compaction = null;
            compactIndices = false;
            slotRuns?.Dispose();
            slotRuns = null;
            foreach (var loaded in sceneryBundles) if (loaded) loaded.Unload(true);
            sceneryBundles.Clear();
            derived.Clear();
        }

        private static IEnumerable<Member> Members() => pieces.Values.Concat(natural.Values);

        // Shows every member natively and forgets the world's cells, as when the world closes or the renderer stops.
        private static void Clear()
        {
            ReleaseLights();
            foreach (var build in builds) Discard(build);
            builds.Clear();
            foreach (var cell in cells.Values)
                for (int index = cell.Batches.Count - 1; index >= 0; index--) DestroyBatch(cell.Batches[index]);
            compactions.Clear();
            skyPending.Clear();
            skyScanned = false;
            foreach (var member in Members()) if (member.Owned && !member.Away) ShowNative(member);
            cells.Clear();
            active.Clear();
            checks.Clear();
            near.Clear();
            foreach (var member in finishing) member.Finishing = -1;
            finishing.Clear();
            foreach (var member in owning) member.OwnQueued = false;
            owning.Clear();
            removing.Clear();
            cameraKnown = false;
            pieces.Clear();
            natural.Clear();
            rendererMembers.Clear();
            switchedMembers.Clear();
            nativeSwitched.Clear();
            pending.Clear();
            pendingNatural.Clear();
        }

        private static void Fail(Exception error)
        {
            if (Failure != null) return;
            Failure = error.ToString();
            Plugin.Log?.LogError("The spatial building renderer failed and stopped; native rendering is restored. " + error);
            try { Stop(); }
            catch (Exception restoring) { Plugin.Log?.LogError(restoring); }
        }

        // Compiles the renderer's own methods at start, so the first frames that use them do not.
        private static void Prepare()
        {
            const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var type in new[] { typeof(SpatialBuildings) }.Concat(typeof(SpatialBuildings).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
            {
                if (type.ContainsGenericParameters) continue;
                foreach (var method in type.GetMethods(declared))
                    if (!method.IsAbstract && !method.ContainsGenericParameters && (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) == 0)
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
        }

        // Coverage added in renderer coverage step 6, by kind, which a paired measurement can return to native drawing in place:
        // members with uninstanced piece materials, such as bird nests and beehives.
        internal const int UninstancedPieces = 1;
        internal static int NativeCoverage { get; private set; }

        // Hands the members of the coverage kinds in the mask back to native drawing, or merges them again, for paired
        // measurements: their cells rebuild without them or with them, and the other members keep drawing merged.
        internal static void SetNativeCoverage(int mask)
        {
            if (!Running || mask == NativeCoverage) return;
            int changed = mask ^ NativeCoverage;
            NativeCoverage = mask;
            try
            {
                foreach (var member in Members().ToList())
                    if ((member.Coverage & changed) != 0) { if ((member.Coverage & mask) != 0) HandBack(member, "coverage switched"); else MarkDirty(member, true); }
            }
            catch (Exception error) { Fail(error); }
        }

        // Whether every member of the added coverage has been planned again and its meshes built and owned since the last switch.
        internal static bool CoverageSettled => !Running || builds.Count == 0 && received.Count == 0 && owning.Count == 0 &&
            !Members().Any(member => member.Coverage != 0 && (member.Dirty || member.Queued || member.OwnQueued));

        // Shows every member natively without forgetting the merged meshes, and back, for paired measurements.
        internal static void SetNative(bool native)
        {
            if (!Running || native == Native) return;
            Native = native;
            try
            {
                foreach (var member in Members())
                    if (member.Owned && !member.Away) { if (native) ShowNative(member); else HideNative(member); }
                foreach (var cell in cells.Values)
                    foreach (var batch in cell.Batches)
                    {
                        if (batch.Renderer) batch.Renderer.enabled = Drawn(batch.Key);
                        if (batch.Twin) batch.Twin.enabled = Casts(batch);
                    }
                if (native) ReleaseLights(); else if (TakingLights) TakeLights();
            }
            catch (Exception error) { Fail(error); }
        }

        // Natural objects players planted, such as PlantEverything's berry bushes, and scenery materials with instancing off,
        // such as the ripe barley's grass, merge too, into merged meshes of their own. The isolated client switches MergeCrops
        // off, which restarts the renderer, and sets CropsNative for paired measurements, when those meshes give way to the
        // members' native renderers in place.
        internal static bool MergeCrops = true;
        internal static bool CropsNative { get; private set; }

        // Whether a merged mesh draws, and whether its twin casts the sun's shadows.
        private static bool Drawn(BatchKey key) => !Native && !(key.Crop && CropsNative);
        private static bool Casts(Batch batch) => Drawn(batch.Key);

        // Merged small natural parts cast the sun's shadows only near the camera, as small far shadows does; the isolated client
        // switches it off to compare their shadows to 150 m.
        internal static bool SmallShadowsNear = true;
        private static float nextShadowCheck;

        // Twice a second each slot of a mesh of small natural parts stops casting when the camera is more than 60 m from its
        // renderer and casts again within 55 m, as small far shadows switches native renderers.
        private static void CheckSmallShadows(Vector3 camera, bool now)
        {
            if (!now && Time.realtimeSinceStartup < nextShadowCheck) return;
            nextShadowCheck = Time.realtimeSinceStartup + .5f;
            foreach (var cell in cells.Values)
                foreach (var batch in cell.Batches)
                    if (batch.TwinTransforms != null && FarSlots(batch, camera)) UploadTwin(batch, 0, batch.Live.Length - 1);
        }

        // Whether any slot's distance band changed.
        private static bool FarSlots(Batch batch, Vector3 camera)
        {
            bool changed = false, switching = SmallShadowsNear && SmallFarShadows.Enabled;
            for (int slot = 0; slot < batch.Live.Length; slot++)
            {
                var (member, part, _) = batch.Live[slot];
                bool far = false;
                if (switching && member != null && member.Radius != null)
                {
                    float distance = Vector3.Distance(camera, member.RendererBounds[part.Renderer].center) - member.Radius[part.Renderer];
                    far = batch.SlotFar[slot] ? distance >= SmallFarShadows.NearDistance : distance > SmallFarShadows.FarDistance;
                }
                if (far == batch.SlotFar[slot]) continue;
                batch.SlotFar[slot] = far;
                changed = true;
            }
            return changed;
        }

        // Gives the twin the slots' matrices from first to last, except those of slots too far to cast.
        private static void UploadTwin(Batch batch, int first, int last)
        {
            if (batch.TwinTransforms == null || last < first) return;
            for (int slot = first; slot <= last; slot++)
            {
                bool far = slot < batch.SlotFar.Length && batch.SlotFar[slot];
                batch.TwinData[slot * 2] = far ? default : batch.TransformData[slot * 2];
                batch.TwinData[slot * 2 + 1] = far ? default : batch.TransformData[slot * 2 + 1];
            }
            batch.TwinTransforms.SetData(batch.TwinData, first * 2, first * 2, (last - first + 1) * 2);
        }

        internal static void SetSmallShadowsNear(bool near)
        {
            SmallShadowsNear = near;
            if (Running) try { CheckSmallShadows(lastCamera, true); } catch (Exception error) { Fail(error); }
        }

        internal static void SetCropsNative(bool native)
        {
            if (!Running || Native || native == CropsNative) return;
            CropsNative = native;
            try
            {
                var shown = new HashSet<Member>();
                foreach (var cell in cells.Values)
                    foreach (var batch in cell.Batches)
                    {
                        if (!batch.Key.Crop) continue;
                        if (batch.Renderer) batch.Renderer.enabled = Drawn(batch.Key);
                        if (batch.Twin) batch.Twin.enabled = Casts(batch);
                        foreach (var member in batch.Members)
                            if (member.Owned && !member.Away && shown.Add(member)) { if (native) ShowNative(member); else HideNative(member); }
                    }
            }
            catch (Exception error) { Fail(error); }
        }

        // The crops' merged meshes: meshes, slots, vertices, graphics bytes, members, planted members, shadow twins and vertex
        // bytes; then all meshes of small natural parts with twins and how many of their slots cast nothing.
        internal static long[] CropCounts()
        {
            var batches = cells.Values.SelectMany(cell => cell.Batches).Where(batch => batch.Mesh && batch.Key.Crop).ToList();
            var members = new HashSet<Member>(batches.SelectMany(batch => batch.Members));
            var small = cells.Values.SelectMany(cell => cell.Batches).Where(batch => batch.Mesh && batch.TwinTransforms != null).ToList();
            return new[] { batches.Count, batches.Sum(batch => (long)batch.Count), batches.Sum(batch => (long)batch.Mesh.vertexCount), batches.Sum(GraphicsBytes),
                members.Count, members.Count(member => member.Planted), batches.Count(batch => batch.Twin),
                batches.Sum(batch => { long bytes = 0; for (int stream = 0; stream < batch.Mesh.vertexBufferCount; stream++) bytes += (long)batch.Mesh.GetVertexBufferStride(stream) * batch.Mesh.vertexCount; return bytes; }),
                small.Count, small.Sum(batch => batch.Live.Where((entry, slot) => entry.member != null && batch.SlotFar[slot]).Count()) };
        }

        // For the isolated client: what the renderer recorded for a natural object, its groups and parts.
        internal static string Describe(ZNetView view, Vector3 camera)
        {
            if (!natural.TryGetValue(view, out var member)) return "not a natural member";
            var text = new System.Text.StringBuilder();
            text.Append("owned " + member.Owned + ", away " + member.Away + ", planted " + member.Planted + ", dirty " + member.Dirty + ", signature " + member.Signature + "\n");
            for (int index = 0; index < member.Details.Length; index++)
            {
                var detail = member.Details[index];
                text.Append("detail " + index + ": group " + (detail.Group ? detail.Group.name + " enabled " + detail.Group.enabled + " size " + detail.Group.size + " scale " + detail.Group.transform.lossyScale : "gone") +
                    ", recorded size " + detail.Size + ", reference " + detail.Reference + (detail.Group ? " now " + detail.Group.transform.TransformPoint(detail.Group.localReferencePoint) : "") +
                    ", thresholds " + string.Join("/", detail.Thresholds) + ", level " + detail.Level + ", computed " + Level(detail, camera, out _) + ", owned " + detail.Owned + ", merging " + detail.Merging +
                    ", active " + member.Active.Contains(detail) + ", fades " + detail.Fades + "\n");
            }
            foreach (var part in member.Parts)
            {
                var renderer = member.Renderers[part.Renderer];
                text.Append("part " + (renderer ? renderer.name : "gone") + " submesh " + part.Submesh + ": detail " + part.Detail + ", levels " + part.Levels + ", material " + (part.Material ? part.Material.name : "none") +
                    ", merged " + member.Merged.Contains(part) + ", shown " + Shown(member, part) + ", shadows " + part.Key.Shadows + ", forced off " + (renderer && renderer.forceRenderingOff) + "\n");
            }
            return text.ToString();
        }

        internal static bool Settled => !Running || pending.Count == 0 && pendingNatural.Count == 0 && active.Count == 0 && builds.Count == 0 && received.Count == 0;

        // Merged members the sky depth camera draws at other detail levels than the main camera, counted when asked.
        internal static int SkyLevels => skyCamera ? Members().Count(member => member.Owned && !member.Away && OtherSkyLevels(member)) : 0;

        // Frames with renderer work, its main-thread time in stopwatch ticks, its worst frame, the job results applied, the
        // processor cycles counted and the last frame whose counted work saw the collector's events.
        internal static long[] Timing() => new[] { frames, mainTicks, maximumTicks, jobTicks, (long)mainCycles, collectorFrame };

        internal static bool Owns(GameObject root)
        {
            if (!Running || !root) return false;
            Member member = null;
            if (root.TryGetComponent<WearNTear>(out var wear)) pieces.TryGetValue(wear, out member);
            else if (root.TryGetComponent<ZNetView>(out var view)) natural.TryGetValue(view, out member);
            return member != null && member.Owned && !member.Away;
        }

        internal static string Report()
        {
            double Milliseconds(long ticks) => Math.Round(ticks * 1000d / Stopwatch.Frequency, 3);
            var batches = cells.Values.SelectMany(cell => cell.Batches).Where(batch => batch.Mesh).ToList();
            return "Spatial building renderer: " + (Running ? Native ? "native (switched for measurement)" : "running" : "stopped") +
                "; pieces " + pieces.Count + " (" + pieces.Values.Count(member => member.Owned && !member.Away) + " merged, " + pending.Count + " registering)" +
                "; natural objects " + natural.Count + " (" + natural.Values.Count(member => member.Owned && !member.Away) + " merged, " + pendingNatural.Count + " registering)" +
                "; " + near.Count + " members drawn natively near a detail level change or while a tree moves, " + SkyLevels +
                " drawn at other detail levels for the sky depth camera; cells " + cells.Count +
                "; meshes " + batches.Count + " with " + batches.Sum(batch => (long)batch.Mesh.vertexCount) + " vertices in " +
                Math.Round(batches.Sum(GraphicsBytes) / 1048576d, 1) + " MB of graphics memory (new meshes wait from " + Math.Round(MemoryLimit / 1048576d) + " MB), " +
                Math.Round(batches.Sum(batch => (long)(batch.Total - batch.Indices) * (batch.Mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4)) / 1048576d, 1) +
                " MB of it in drawn regions, whose drawn ranges hold " +
                Math.Round(100d * batches.Sum(batch => (long)Math.Max(0, batch.SpanEnd - batch.SpanFirst)) / Math.Max(1L, batches.Sum(batch => (long)batch.Indices)), 1) + "% of their indices; slots " +
                batches.Sum(batch => batch.Live.Count(slot => !slot.member.Removed && slot.member.Version == slot.version)) +
                "; source meshes " + sources.Count + " (" + sources.Values.Count(source => source.Readable) + " ready, " + sources.Values.Count(source => source.Failed) + " unreadable)" +
                "; rebuilds " + rebuilds + ", swaps " + swaps + ", compactions " + compacted + (drawnWhole > 0 ? ", " + drawnWhole + " drawn whole beyond their drawn regions" : "") +
                "; shadowed lights drawing their merged slots " + shadowLights.Count + " (" + lightsTaken + " taken over; " +
                shadowLights.Sum(state => state.Ranges.Count(range => range.Submesh < 0)) + " of the " + shadowLights.Sum(state => state.Ranges.Count) +
                " merged meshes they reach draw whole drawn regions, " + shadowLights.Sum(state => state.Ranges.Count(range => range.Whole)) + " of them without room for a copy)" +
                ", merged detail level switches " + switches + ", member detail-level evaluations " +
                evaluations + "; handed back " + Describe(handedBack) +
                "; excluded " + Describe(excluded) + "; natural objects excluded " + Describe(naturalExcluded) +
                "; main thread " + (frames == 0 ? 0 : Milliseconds(mainTicks / frames)) + " ms a frame on average, at most " + Milliseconds(maximumTicks) +
                " ms; work budget " + Math.Round(budgetMilliseconds, 2) + " ms a frame, " + waitedFrames + " frames waiting after long ones; applying job results " + Milliseconds(jobTicks) + " ms, slowest " + Milliseconds(slowestApply) +
                " ms with " + slowestApplyVertices + " vertices; slowest registration " +
                Milliseconds(slowestRegistration) + " ms (" + (slowestPrefab ?? "none") + "); by phase " + string.Join(", ", phaseNames.Select((name, index) =>
                    name + " " + Milliseconds(phaseTicks[index]) + " ms (worst " + Milliseconds(phaseMaximum[index]) + " ms" + (phaseCollected[index] ? " with a garbage collection" : "") + ")")) +
                (Failure != null ? "; failed: " + Failure : "");
        }

        // A merged mesh's vertex and index buffers; their processor copies are released once uploaded.
        private static long GraphicsBytes(Batch batch)
        {
            var mesh = batch.Mesh;
            long bytes = 0;
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++) bytes += (long)mesh.GetVertexBufferStride(stream) * mesh.vertexCount;
            return bytes + (long)batch.Total * (mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4);
        }

        private static string Describe(Dictionary<string, long> counts) => counts.Count == 0 ? "none" :
            string.Join(", ", counts.OrderByDescending(entry => entry.Value).Select(entry => entry.Key + " " + entry.Value));

        private static void Count(Dictionary<string, long> counts, string reason) => counts[reason] = counts.TryGetValue(reason, out long value) ? value + 1 : 1;

        // Main-thread time the renderer spends in hooks and its frame work, in total, in its worst frame and by phase.
        private const int Registration = 0, DetailChecks = 1, Applies = 2, Planning = 3, Requests = 4, Readbacks = 5, Uploads = 6, SkyDepth = 7, VisualHooks = 8,
            PropertyHooks = 9, RemovalHooks = 10, ShadowedLights = 11, SkyPreparation = 12, Arrivals = 13, Removals = 14;
        private static readonly string[] phaseNames = { "registration", "detail checks", "applies and swaps", "planning and job starts", "readback requests", "readbacks",
            "slot uploads", "sky depth", "visual hooks", "property hooks", "removal hooks", "shadowed lights", "sky preparation", "arrivals", "removals" };
        private static readonly long[] phaseTicks = new long[phaseNames.Length], phaseMaximum = new long[phaseNames.Length];
        // Whether a garbage collection ran during each phase's worst span.
        private static readonly bool[] phaseCollected = new bool[phaseNames.Length];

        private struct Timed : IDisposable
        {
            private readonly long start, events;
            private readonly ulong cycles;
            private readonly int phase, collections;
            internal Timed(int phase)
            {
                this.phase = phase;
                collections = GC.CollectionCount(0);
                cycles = 0;
                events = 0;
                if (CountCycles)
                {
                    QueryThreadCycleTime(GetCurrentThread(), out cycles);
                    if (CollectorEvents != IntPtr.Zero) events = Marshal.ReadInt64(CollectorEvents);
                }
                start = Stopwatch.GetTimestamp();
            }
            public void Dispose()
            {
                long spent = Stopwatch.GetTimestamp() - start;
                if (CountCycles && QueryThreadCycleTime(GetCurrentThread(), out ulong now))
                {
                    mainCycles += now - cycles;
                    if (CollectorEvents != IntPtr.Zero && Marshal.ReadInt64(CollectorEvents) != events) collectorFrame = Time.frameCount;
                }
                FrameStarted();
                frameTicks += spent;
                mainTicks += spent;
                if (frameTicks > maximumTicks) maximumTicks = frameTicks;
                phaseTicks[phase] += spent;
                if (spent > phaseMaximum[phase]) { phaseMaximum[phase] = spent; phaseCollected[phase] = GC.CollectionCount(0) != collections; }
            }
        }

        // Starts a frame's accounting with its first work. What the renderer did after LateUpdate in the frame before, in the
        // cameras' culling and in hooks, is what later frames' budgets reserve for it, as the most recently seen, decaying.
        private static void FrameStarted()
        {
            if (mainFrame == Time.frameCount) return;
            if (mainFrame == lateFrame) afterReserve = Math.Max(frameTicks - lateTicks, (long)(afterReserve * ReserveDecay));
            mainFrame = Time.frameCount;
            frameTicks = 0;
            frames++;
        }

        [DllImport("kernel32.dll")]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        private static long Ticks(float milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000d);

        // Whether a small budgeted step may start: while the frame's budget lasts.
        private static bool Open() => Stopwatch.GetTimestamp() <= Math.Min(budgetEnd, capEnd);

        // Whether a large budgeted step expected to take the given ticks may start: one that fits in what is left of the
        // frame's budget, or, once a frame, one beyond it that fits within the most the renderer may spend in the frame or a
        // long one in a frame with a long step's share of that left.
        private static bool Admit(long expected)
        {
            long now = Stopwatch.GetTimestamp(), left = capEnd - now;
            if (now + expected <= budgetEnd && expected <= left) return true;
            if (overrun || left < Math.Min(expected, Ticks((FrameMaximum - MinimumReserve) * LongShare))) return false;
            return overrun = true;
        }

        // The most a kind of budgeted step has recently taken per unit of its work, beyond a fixed part counted in units,
        // which tells whether the next one fits. Spans with a garbage collection are left out, and the figure decays with each
        // step, so a slow step is soon forgotten.
        private sealed class StepCost
        {
            private readonly int fixedUnits;
            private double rate;
            internal StepCost(double microseconds, int fixedUnits) { rate = microseconds * Stopwatch.Frequency / 1e6; this.fixedUnits = fixedUnits; }
            internal long Expected(int units) => (long)(rate * (units + fixedUnits));
            internal void Spent(long ticks, int units, bool collected)
            {
                if (!collected) rate = Math.Max(rate * StepDecay, (double)ticks / (units + fixedUnits));
            }
        }

        // Applying a mesh by its vertices, starting a build by its slots, a readable copy by its vertices, finding the lights,
        // dropping void detail-level checks by the queue's entries and making a new mesh's objects.
        private static readonly StepCost applyCost = new StepCost(.025, 8192), buildCost = new StepCost(5, 16), copyCost = new StepCost(.02, 4096),
            findCost = new StepCost(500, 0), cleanupCost = new StepCost(.05, 0), createCost = new StepCost(500, 0);

        private readonly struct Step
        {
            private readonly long start;
            private readonly int collections;
            private Step(long start, int collections) { this.start = start; this.collections = collections; }
            internal static Step Start() => new Step(Stopwatch.GetTimestamp(), GC.CollectionCount(0));
            internal void Done(StepCost cost, int units) => cost.Spent(Stopwatch.GetTimestamp() - start, units, GC.CollectionCount(0) != collections);
        }

        private static void Created(WearNTear __instance)
        {
            if (!Running) return;
            // A placement ghost has no network record and stays native.
            var view = __instance.GetComponent<ZNetView>();
            if (view && view.GetZDO() != null) pending.Enqueue((__instance, Time.frameCount));
        }

        private static void Destroyed(WearNTear __instance)
        {
            if (!Running) return;
            if (nativeSwitched.Count > 0 && nativeSwitched.TryGetValue(__instance, out var ids))
            {
                foreach (int id in ids) switchedMembers.Remove(id);
                nativeSwitched.Remove(__instance);
            }
            if (!pieces.TryGetValue(__instance, out var member)) return;
            using (new Timed(RemovalHooks))
                try
                {
                    pieces.Remove(__instance);
                    Remove(member);
                }
                catch (Exception error) { Fail(error); }
        }

        // Cleared before the next render, so destroyed and removed members never linger in a merged mesh. A zone's objects are
        // destroyed together, so the rest of their removal waits for the frame's budget.
        private static void Remove(Member member)
        {
            foreach (int id in member.SwitchedIds) switchedMembers.Remove(id);
            ClearSlots(member);
            member.Owned = member.Away = false;
            near.Remove(member);
            Unfinish(member);
            member.Removed = true;
            member.Version++;
            removing.Enqueue(member);
        }

        // Takes destroyed members out of their cells and merged meshes within the frame's budget; a mesh built before then
        // leaves their slots empty.
        private static void Forget()
        {
            while (removing.Count > 0 && Open())
            {
                var member = removing.Dequeue();
                Unassign(member);
                member.Cell.Members.Remove(member);
                Changed(member.Cell);
                member.Cell.ChangedBy = member;
                foreach (int id in member.RendererIds) rendererMembers.Remove(id);
            }
        }

        // Every network object is considered once it has existed for a while; building pieces register on their own.
        private static void ViewCreated(ZNetView __instance)
        {
            if (Running && __instance.GetZDO() != null) pendingNatural.Enqueue((__instance, Time.realtimeSinceStartup));
        }

        private static void ViewDestroyed(ZNetView __instance)
        {
            if (!Running || !natural.TryGetValue(__instance, out var member)) return;
            using (new Timed(RemovalHooks))
                try
                {
                    natural.Remove(__instance);
                    Remove(member);
                }
                catch (Exception error) { Fail(error); }
        }

        // A zone leaving the active area destroys its objects together at the end of the frame. Their members are removed as
        // the scene unloads them, before the frame's budget is set, so that budget makes room for their removal.
        private static void Unloaded(ZNetScene __instance)
        {
            var views = unloadedViews(__instance);
            if (!Running || views.Count == 0) return;
            using (new Timed(RemovalHooks))
                try
                {
                    foreach (var view in views)
                    {
                        if (natural.TryGetValue(view, out var member)) natural.Remove(view);
                        else if (pieces.Count == 0 || !view.TryGetComponent<WearNTear>(out var wear) || !pieces.TryGetValue(wear, out member)) continue;
                        else pieces.Remove(wear);
                        Remove(member);
                    }
                }
                catch (Exception error) { Fail(error); }
        }

        private static Member NaturalOf(Component component) =>
            natural.Count > 0 && component.TryGetComponent<ZNetView>(out var view) && natural.TryGetValue(view, out var member) ? member : null;

        // Picking, respawning and enabling switch a pickable's picked object; a mined area switches an ore rock's renderers.
        private static void PickableChanged(Pickable __instance) => VisibilityChanged(__instance, "picked");

        private static void RockChanged(MineRock __instance) => VisibilityChanged(__instance, "mined");

        // A behaviour switched objects of its own piece or natural object: its own objects first, which is a few reads, then the
        // renderers only when those changed. An item stand has no switched objects and compares the renderers directly.
        private static void SwitchedVisualChanged(Component __instance)
        {
            if (!Running || !__instance) return;
            var member = SwitchedMember(__instance);
            if (member == null) return;
            using (new Timed(VisualHooks))
                try
                {
                    if (member.Switches.Length > 0)
                    {
                        ulong switched = SwitchState(member);
                        if (switched == member.Switched) return;
                        member.Switched = switched;
                    }
                    ulong signature = Signature(member);
                    if (signature == member.Signature) return;
                    member.Signature = signature;
                    HandBack(member, "switched visual");
                }
                catch (Exception error) { Fail(error); }
        }

        // The member a switched behaviour belongs to, or none. The one mapped with its piece is what the search up its hierarchy
        // finds while the piece's object is active; that search skips inactive objects, so otherwise it runs as before.
        private static Member SwitchedMember(Component behaviour) =>
            switchedMembers.TryGetValue(behaviour.GetInstanceID(), out var entry) && entry.root.activeInHierarchy ? entry.member : SearchedMember(behaviour);

        private static Member SearchedMember(Component behaviour)
        {
            var wear = behaviour.GetComponentInParent<WearNTear>();
            return wear && pieces.TryGetValue(wear, out var piece) ? piece : NaturalOf(behaviour);
        }

        private static void VisibilityChanged(Component component, string reason)
        {
            if (!Running) return;
            var member = NaturalOf(component);
            if (member == null) return;
            using (new Timed(VisualHooks))
                try
                {
                    ulong signature = Signature(member);
                    if (signature == member.Signature) return;
                    member.Signature = signature;
                    HandBack(member, reason);
                }
                catch (Exception error) { Fail(error); }
        }

        // A hit tree's trunk shakes for a second, and a grown tree swaps its trunk for an animated copy: the tree shows its
        // native renderers meanwhile and draws merged again once its trunk is back where its slots hold it.
        private static void TreeShaking(TreeBase __instance) => Busy(__instance, ShakeSeconds, "shaking");

        private static void TreeGrowing(TreeBase __instance) => Busy(__instance, GrowthSeconds, "growing");

        private static void Busy(Component component, float seconds, string reason)
        {
            if (!Running) return;
            var member = NaturalOf(component);
            if (member == null) return;
            using (new Timed(VisualHooks))
                try
                {
                    member.BusyUntil = Mathf.Max(member.BusyUntil, Time.time + seconds);
                    if (member.Owned && !member.Away) Leave(member, reason);
                }
                catch (Exception error) { Fail(error); }
        }

        private static bool Busy(Member member) => Time.time < member.BusyUntil || member.Physics && member.Physics.IsFalling;

        // Ground physics moves a natural object: falling over a fraction of a second, pushing up at once. It shows its
        // native renderers from that frame and merges again once it rests.
        private static void Falling(StaticPhysics __instance) => Moved(__instance, "falling");

        private static void PushStarting(StaticPhysics __instance, out Vector3 __state) => __state = __instance.transform.position;

        private static void Pushed(StaticPhysics __instance, Vector3 __state)
        {
            if (Running && __instance.transform.position != __state) Moved(__instance, "pushed up");
        }

        private static void Moved(Component component, string reason)
        {
            if (!Running) return;
            var member = NaturalOf(component);
            if (member == null) return;
            using (new Timed(VisualHooks))
                try { HandBack(member, reason); }
                catch (Exception error) { Fail(error); }
        }

        // The health visual ends by updating the snow visual; the member is checked once, after both.
        private static void HealthVisualStarting() => healthVisuals++;

        private static void HealthVisualChanged(WearNTear __instance)
        {
            healthVisuals--;
            VisualChanged(__instance);
        }

        private static void SnowVisualChanged(WearNTear __instance)
        {
            if (healthVisuals == 0) VisualChanged(__instance);
        }

        private static void VisualChanged(WearNTear __instance)
        {
            if (!Running || !pieces.TryGetValue(__instance, out var member)) return;
            using (new Timed(VisualHooks))
                try
                {
                    // Renderers change visibility here only when one of the wear, wetness and snow objects switches.
                    ulong switched = SwitchState(member);
                    if (switched == member.Switched) return;
                    member.Switched = switched;
                    ulong signature = Signature(member);
                    if (signature == member.Signature) return;
                    member.Signature = signature;
                    HandBack(member, "visual state");
                }
                catch (Exception error) { Fail(error); }
        }

        private static void Scaled(ZNetView __instance)
        {
            if (!Running) return;
            Member member = null;
            if (__instance.TryGetComponent<WearNTear>(out var wear)) pieces.TryGetValue(wear, out member);
            else natural.TryGetValue(__instance, out member);
            if (member == null) return;
            using (new Timed(VisualHooks))
                try
                {
                    foreach (var detail in member.Details) Place(detail);
                    HandBack(member, "scale");
                }
                catch (Exception error) { Fail(error); }
        }

        // The material manager has just given the member's native renderers its property block; merged slots follow in the
        // same frame, and a property the merged shader cannot express hands the member back.
        private static void PropertiesApplied(object __instance)
        {
            if (!Running) return;
            var renderers = containerRenderers(__instance);
            if (renderers == null || renderers.Count == 0 || !renderers[0] || !rendererMembers.TryGetValue(renderers[0].GetInstanceID(), out var member)) return;
            using (new Timed(PropertyHooks))
                try
                {
                    var container = containerProperties(__instance);
                    if (Unchanged(container, member.Properties)) return;
                    member.Properties = Properties(container);
                    if (!Expressible(member)) { HandBack(member, "material property"); return; }
                    // A member the merged meshes could not express before is planned again; one waiting for its meshes
                    // takes the values when it becomes owned.
                    if (!member.Owned) { if (member.Merged.Count == 0) MarkDirty(member, true); return; }
                    foreach (var slot in member.Slots)
                    {
                        if (slot.batch.Appearances == null) continue;
                        slot.batch.AppearanceData[slot.slot] = AppearanceOf(member, slot.part);
                        slot.batch.AppearanceFrom = Math.Min(slot.batch.AppearanceFrom, slot.slot);
                        slot.batch.AppearanceTo = Math.Max(slot.batch.AppearanceTo, slot.slot);
                    }
                    flushPending = true;
                }
                catch (Exception error) { Fail(error); }
        }

        private static void WorldClosed()
        {
            if (!Running) return;
            try { Clear(); }
            catch (Exception error) { Fail(error); }
        }

        // Destruction breaks only the renderers the cameras drew into fragments, and a merged member's native renderers are
        // never drawn, so the visibility test asks whether its merged mesh draws them in the main camera's view instead.
        private static IEnumerable<CodeInstruction> FragmentsFromMerged(IEnumerable<CodeInstruction> instructions)
        {
            var visible = AccessTools.PropertyGetter(typeof(Renderer), nameof(Renderer.isVisible));
            var code = instructions.ToList();
            var tests = code.Where(instruction => instruction.Calls(visible)).ToList();
            if (tests.Count != 1) throw new InvalidOperationException("Destructible.CreateFragments no longer tests visibility as expected; rendering stays native.");
            tests[0].opcode = OpCodes.Call;
            tests[0].operand = AccessTools.Method(typeof(SpatialBuildings), nameof(FragmentVisible));
            return code;
        }

        private static bool FragmentVisible(Renderer renderer)
        {
            if (renderer.isVisible) return true;
            if (!Running || Native || !renderer.enabled || !rendererMembers.TryGetValue(renderer.GetInstanceID(), out var member) || !member.Owned || member.Away) return false;
            int index = Array.IndexOf(member.RendererIds, renderer.GetInstanceID());
            bool drawn = false;
            foreach (var (_, _, part) in member.Slots) if (part.Renderer == index && Shown(member, part)) { drawn = true; break; }
            var camera = Utils.GetMainCamera();
            if (!drawn || !camera) return false;
            GeometryUtility.CalculateFrustumPlanes(camera, frustum);
            return GeometryUtility.TestPlanesAABB(frustum, renderer.bounds);
        }

        private static Camera SkyCamera(DepthCamera depth)
        {
            var camera = depth.GetComponent<Camera>();
            skyDepthCamera = depth;
            if (camera == skyCamera) return camera;
            if (skyCamera) skyCamera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, skyCommands);
            skyCamera = camera;
            camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, skyCommands);
            // An orthographic camera selects detail levels from its size alone, wherever it is.
            skyScale = SkyDetailBias * .5f / camera.orthographicSize;
            ForgetSky();
            return camera;
        }

        // The sky slots follow that camera's detail levels, and are filled again where it draws next.
        private static void ForgetSky()
        {
            foreach (var cell in cells.Values) foreach (var batch in cell.Batches) batch.SkyData = null;
            skyScanned = false;
        }

        // Where the sky depth camera renders from with the player or the main camera at the given position, as RenderDepth
        // places it.
        private static Vector3 SkyPosition(DepthCamera depth, Vector3 position)
        {
            position += Vector3.up * depth.m_offset;
            return new Vector3(Mathf.Round(position.x), Mathf.Round(position.y), Mathf.Round(position.z));
        }

        // Once a second the game's sky depth camera draws pieces from above with a replacement shader, for rain and snow
        // under roofs. Merged meshes draw in that render through the derived depth shader instead, from their sky slots, which
        // draw each merged member at the levels that camera selects, since the meshes hold them all. The slots of meshes it
        // can reach are prepared within the frame budget beforehand, and only the merged renderers on layers it sees are
        // switched off while it renders.
        private static void BeforeSkyDepth(DepthCamera __instance)
        {
            if (!Running || Native) return;
            using (new Timed(SkyDepth))
                try
                {
                    var main = Utils.GetMainCamera();
                    if (!main) return;
                    var camera = SkyCamera(__instance);
                    var box = SkyBox(camera, SkyPosition(__instance, Player.m_localPlayer ? Player.m_localPlayer.transform.position : main.transform.position));
                    skyDepth = true;
                    skyMask = camera.cullingMask;
                    skyCommands.Clear();
                    foreach (var cell in cells.Values)
                        foreach (var batch in cell.Batches)
                        {
                            if (!batch.Mesh || !batch.Renderer || !batch.Bounds.Intersects(box) || !SkyHides(batch, true)) continue;
                            skyBatches.Add(batch);
                            if ((skyMask & 1 << batch.Key.Layer) == 0 || !SkyDrawn(batch, __instance.m_depthShader)) continue;
                            UploadSky(batch);
                            skyCommands.DrawMesh(batch.Mesh, Matrix4x4.identity, skyMaterial, 1, 0, batch.SkyProperties);
                        }
                }
                catch (Exception error) { Fail(error); }
        }

        private static Exception AfterSkyDepth(Exception __exception)
        {
            if (!skyDepth) return __exception;
            skyDepth = false;
            using (new Timed(SkyDepth))
                try
                {
                    skyCommands?.Clear();
                    foreach (var batch in skyBatches) SkyHides(batch, false);
                }
                catch (Exception error) { Fail(error); }
                finally { skyBatches.Clear(); }
            return __exception;
        }

        // Switches a merged mesh's renderers on layers the sky depth camera sees off for its render, and back on; false when it
        // sees none of them. The replacement render would draw a merged mesh at its local vertices.
        private static bool SkyHides(Batch batch, bool hide)
        {
            bool seen = (skyMask & 1 << batch.Key.Layer) != 0, twin = batch.Twin && (skyMask & 1 << TwinLayerOf(batch.Key.Layer)) != 0, ranges = (skyMask & RangeLayers) != 0;
            if (!seen && !twin && !ranges) return false;
            if (seen && batch.Renderer) batch.Renderer.forceRenderingOff = hide;
            if (twin) batch.Twin.forceRenderingOff = hide;
            if (ranges) foreach (var range in batch.Lights) if (range.Renderer) range.Renderer.forceRenderingOff = hide;
            return true;
        }

        // Gives a merged mesh drawn by the sky depth camera its sky slots and their buffer, and uploads the slots changed since.
        private static void UploadSky(Batch batch)
        {
            if (batch.SkyData == null) FillSky(batch);
            if (batch.SkyTransforms == null)
            {
                batch.SkyTransforms = new GraphicsBuffer(GraphicsBuffer.Target.Constant, SlotLimit * 2, 64);
                batch.SkyProperties.SetConstantBuffer(skySlotsId, batch.SkyTransforms, 0, SlotLimit * 128);
            }
            if (batch.SkyTo >= 0) batch.SkyTransforms.SetData(batch.SkyData, batch.SkyFrom * 2, batch.SkyFrom * 2, (batch.SkyTo - batch.SkyFrom + 1) * 2);
            batch.SkyFrom = int.MaxValue;
            batch.SkyTo = -1;
        }

        // Prepares, within the frame's budget, the sky slots of new and rebuilt meshes and of those the player has come near,
        // which the sky depth camera's next renders can reach: within a cell of its view from the player's position now.
        private static void PrepareSky()
        {
            if (!skyCamera || !skyDepthCamera || Native) return;
            var player = Player.m_localPlayer ? Player.m_localPlayer.transform.position : lastCamera;
            var reach = SkyBox(skyCamera, SkyPosition(skyDepthCamera, player));
            reach.Expand(CellSize * 2f);
            if ((!skyScanned || (player - skyScanFrom).sqrMagnitude > SkyScanStep * SkyScanStep) && Open())
            {
                skyScanned = true;
                skyScanFrom = player;
                foreach (var cell in cells.Values)
                    foreach (var batch in cell.Batches)
                        if (!batch.SkyQueued && batch.Mesh && (batch.SkyData == null || batch.SkyTransforms == null) && batch.Bounds.Intersects(reach)) QueueSky(batch);
            }
            while (skyPending.Count > 0 && Open())
            {
                var batch = skyPending.Dequeue();
                batch.SkyQueued = false;
                if (batch.Mesh && batch.Renderer && (skyCamera.cullingMask & 1 << batch.Key.Layer) != 0 && SkyDrawn(batch, skyDepthCamera.m_depthShader) &&
                    batch.Bounds.Intersects(reach))
                    UploadSky(batch);
            }
        }

        private static void QueueSky(Batch batch)
        {
            if (batch.SkyQueued) return;
            batch.SkyQueued = true;
            skyPending.Enqueue(batch);
        }

        // Whether the replacement render draws the batch's native material: only when one of its subshaders has the same
        // render type, as for the native renderers. Layers the sky camera leaves out are skipped by the caller.
        private static bool SkyDrawn(Batch batch, Shader replacement)
        {
            if (replacement != skyReplacement)
            {
                skyReplacement = replacement;
                skyTypes.Clear();
                if (replacement)
                    for (int index = 0; index < replacement.subshaderCount; index++) skyTypes.Add(replacement.FindSubshaderTagValue(index, renderType).name ?? "");
            }
            if (batch.SkyType == null) batch.SkyType = batch.Material ? batch.Material.GetTag("RenderType", false) ?? "" : "";
            return skyTypes.Contains(batch.SkyType);
        }

        // The world-space box the orthographic sky depth camera sees from the given position, with a margin.
        private static Bounds SkyBox(Camera camera, Vector3 position)
        {
            var rotation = camera.transform.rotation;
            float height = camera.orthographicSize, width = height * camera.aspect;
            var box = new Bounds(position + rotation * new Vector3(-width, -height, camera.nearClipPlane), Vector3.zero);
            for (int corner = 1; corner < 8; corner++)
                box.Encapsulate(position + rotation * new Vector3((corner & 1) == 0 ? -width : width, (corner & 2) == 0 ? -height : height,
                    (corner & 4) == 0 ? camera.nearClipPlane : camera.farClipPlane));
            box.Expand(2f);
            return box;
        }

        private static Dictionary<int, object> Properties(IDictionary container)
        {
            var result = new Dictionary<int, object>();
            if (container == null) return result;
            var entries = container.GetEnumerator();
            while (entries.MoveNext()) result[propertyName(entries.Value)] = Value(entries.Value);
            return result;
        }

        // Whether a container holds exactly the recorded values, compared without allocating for each property.
        private static bool Unchanged(IDictionary container, Dictionary<int, object> stored)
        {
            if (container == null || container.Count != stored.Count) return container == null && stored.Count == 0;
            var entries = container.GetEnumerator();
            while (entries.MoveNext())
            {
                var property = entries.Value;
                if (!stored.TryGetValue(propertyName(property), out var value)) return false;
                var type = property.GetType();
                bool same = type == floatValues.Type ? floatValues.Same(property, value) : type == colorValues.Type ? colorValues.Same(property, value) :
                    type == vector4Values.Type ? vector4Values.Same(property, value) : type == vector3Values.Type ? vector3Values.Same(property, value) :
                    type == vector2Values.Type ? vector2Values.Same(property, value) : Equals(value, Value(property));
                if (!same) return false;
            }
            return true;
        }

        private static object Value(object property)
        {
            var type = property.GetType();
            if (type == floatValues.Type) return floatValues.Value(property);
            if (type == colorValues.Type) return colorValues.Value(property);
            if (type == vector4Values.Type) return vector4Values.Value(property);
            if (type == vector3Values.Type) return vector3Values.Value(property);
            if (type == vector2Values.Type) return vector2Values.Value(property);
            if (!propertyValues.TryGetValue(type, out var get)) propertyValues[type] = get = AccessTools.Method(type, "Get");
            return get.Invoke(property, null);
        }

        private static bool Expressible(Member member) => member.Wear ? Expressible(member.Properties) : member.Properties.Count == 0;

        private static bool Expressible(Dictionary<int, object> properties) => properties.All(entry =>
            (entry.Key == colorId || entry.Key == emissionId) && entry.Value is Color ||
            (entry.Key == ashlandsId || entry.Key == snowId || entry.Key == noiseId || entry.Key == rippleId) && entry.Value is float ||
            entry.Key == offsetId && (entry.Value is Vector4 || entry.Value is Vector3 || entry.Value is Vector2));

        private static Appearance AppearanceOf(Member member, Part part)
        {
            var material = part.Material;
            if (!materialAppearance.TryGetValue(material, out var defaults))
                materialAppearance[material] = defaults = new Appearance { Color = Tint(material.GetColor(colorId)), Emission = Tint(material.GetColor(emissionId)),
                    NoiseRippleSnow = new Vector4(material.GetFloat(noiseId), material.GetFloat(rippleId), 0, 0) };
            float Value(int id, float fallback) => member.Properties.TryGetValue(id, out var value) && value is float number ? number : fallback;
            var offset = member.Properties.TryGetValue(offsetId, out var stored) ? stored is Vector4 four ? four : stored is Vector3 three ? (Vector4)three :
                stored is Vector2 two ? (Vector4)two : Shader.GetGlobalVector(offsetId) : Shader.GetGlobalVector(offsetId);
            return new Appearance {
                TextureOffset = offset, AshlandsDamage = new Vector4(Value(ashlandsId, Shader.GetGlobalFloat(ashlandsId)), 0, 0, 0),
                Color = member.Properties.TryGetValue(colorId, out var color) && color is Color chosen ? Tint(chosen) : defaults.Color,
                Emission = member.Properties.TryGetValue(emissionId, out var emission) && emission is Color glow ? Tint(glow) : defaults.Emission,
                NoiseRippleSnow = new Vector4(Value(noiseId, defaults.NoiseRippleSnow.x), Value(rippleId, defaults.NoiseRippleSnow.y),
                    Value(snowId, Shader.GetGlobalFloat(snowId)), 0) };
        }

        private static Color Tint(Color color) => linear ? color.linear : color;

        // Which merging renderers are visible: active and enabled. Detail levels are tracked separately.
        private static ulong Signature(Member member)
        {
            ulong result = 0;
            for (int index = 0; index < member.Renderers.Length; index++)
            {
                var renderer = member.Renderers[index];
                if (member.Merging[index] && renderer && renderer.enabled && renderer.gameObject.activeInHierarchy) result |= 1UL << index;
            }
            return result;
        }

        // Which of the wear, wetness and snow objects are active.
        private static ulong SwitchState(Member member)
        {
            ulong result = 0;
            for (int index = 0; index < member.Switches.Length; index++)
            {
                var switched = member.Switches[index];
                if (switched && switched.activeSelf) result |= 1UL << index;
            }
            return result;
        }

        // The distinct objects the game's wear, wetness and snow visuals and the record's switched behaviours switch on and off,
        // compared before the more expensive renderer comparison. At most 64 fit the state's bits.
        private static GameObject[] SwitchedObjects(GameObject root, WearNTear wear)
        {
            var result = new List<GameObject>(7);
            void Add(GameObject switched) { if (switched && result.Count < 64 && !result.Contains(switched)) result.Add(switched); }
            if (wear)
                foreach (var switched in new[] { wear.m_new, wear.m_worn, wear.m_broken, wear.m_wet, wear.m_snow ? wear.m_snow.gameObject : null,
                    wear.m_snowWorn ? wear.m_snowWorn.gameObject : null, wear.m_snowBroken ? wear.m_snowBroken.gameObject : null })
                    Add(switched);
            root.GetComponentsInChildren(true, components);
            try
            {
                foreach (var component in components)
                    if (component is MonoBehaviour behaviour && switchedFields.TryGetValue(behaviour.GetType(), out var fields))
                        foreach (var field in fields) Add(field.GetValue(behaviour) as GameObject);
            }
            finally { components.Clear(); }
            return result.ToArray();
        }

        private static void Unassign(Member member)
        {
            foreach (var entry in member.Batches)
            {
                entry.Key.Members.Remove(member);
                entry.Key.Count -= entry.Value.slots;
                entry.Key.Vertices -= entry.Value.vertices;
                MarkBatch(entry.Key);
            }
            member.Batches.Clear();
        }

        private static void HandBack(Member member, string reason)
        {
            member.Version++;
            MarkDirty(member, true);
            bool owned = member.Owned, away = member.Away;
            ClearSlots(member);
            member.Away = false;
            near.Remove(member);
            Unfinish(member);
            if (!owned) return;
            member.Owned = false;
            if (!Native && !away) ShowNative(member);
            Count(handedBack, reason);
        }

        // Shows an owned member's native renderers near a detail-level change or while a tree moves; its slots stay live and
        // draw nothing. A member the camera reached a change for between checks may already be crossfading natively.
        private static void Leave(Member member, string reason)
        {
            member.Away = true;
            ZeroSlots(member);
            if (!Native) ShowNative(member);
            near.Add(member);
            Count(handedBack, reason);
            CheckAway(member, lastCamera, false);
        }

        // Draws an owned member from its slots at the levels the camera selects now.
        private static void Arrive(Member member, float closest)
        {
            member.Away = false;
            near.Remove(member);
            Unfinish(member);
            WriteSlots(member);
            WriteSky(member);
            if (!Native) HideNative(member);
            CheckAfter(member, closest - DetailMargin);
        }

        private static bool Crossfades(Member member)
        {
            foreach (var detail in member.Active) if (detail.Fades) return true;
            return false;
        }

        // A native crossfade may have started on one of the member's groups: it draws merged again only after the
        // engine's fade duration, counted in real and in game time.
        private static void Faded(Member member)
        {
            foreach (var detail in member.Active)
                if (detail.Fades && detail.Group && detail.Group.fadeMode == LODFadeMode.CrossFade)
                {
                    member.FadeUntil = Time.realtimeSinceStartup + LODGroup.crossFadeAnimationDuration + FadeSlack;
                    member.FadeUntilGame = Time.time + LODGroup.crossFadeAnimationDuration + FadeSlack;
                    return;
                }
        }

        // Orders parts by the detail levels they draw at, the lowest first and then the highest; a part outside detail groups
        // draws at every level, after the parts of the first level and before those of the second.
        private static int LevelOrder(Part part)
        {
            if (part.Detail < 0 || part.Levels == 0) return 31;
            int lowest = 0, highest = 31;
            while ((part.Levels & 1 << lowest) == 0) lowest++;
            while ((part.Levels & 1 << highest) == 0) highest--;
            return lowest * 32 + highest;
        }

        // Draws only the slots that draw now. A slot draws when its matrix is set, whose last element is 1, and holds nothing
        // otherwise. With the compaction kernel a changed set of slots is queued for the drawn region, filled before the next
        // camera draws; without it the renderer draws the span of the whole mesh holding them.
        private static void Drawn(Batch batch)
        {
            if (batch.IndexBuffer == null)
            {
                int first = int.MaxValue, end = 0;
                for (int slot = 0; slot < batch.IndexFirst.Length; slot++)
                    if (batch.TransformData[slot * 2].m33 != 0f)
                    {
                        first = Math.Min(first, batch.IndexFirst[slot]);
                        end = Math.Max(end, batch.IndexFirst[slot] + batch.IndexCount[slot]);
                    }
                if (first == int.MaxValue) first = end = 0;
                DrawRange(batch, first, end);
                return;
            }
            ulong low = 0, high = 0;
            for (int slot = 0; slot < batch.IndexFirst.Length; slot++)
                if (batch.TransformData[slot * 2].m33 != 0f)
                {
                    if (slot < 64) low |= 1UL << slot;
                    else high |= 1UL << (slot - 64);
                }
            if (batch.DrawnKnown && low == batch.DrawnLow && high == batch.DrawnHigh) return;
            batch.DrawnKnown = true;
            batch.DrawnLow = low;
            batch.DrawnHigh = high;
            if (!batch.Compacting) { batch.Compacting = true; compactions.Add(batch); }
            flushPending = true;
        }

        private static void DrawRange(Batch batch, int first, int end)
        {
            if (first == batch.SpanFirst && end == batch.SpanEnd) return;
            batch.SpanFirst = first;
            batch.SpanEnd = end;
            batch.Mesh.SetSubMesh(0, new SubMeshDescriptor(first, end - first) { firstVertex = 0, vertexCount = batch.Mesh.vertexCount, bounds = batch.Bounds }, quiet);
        }

        // Fills each queued mesh's drawn region with the indices of its slots drawing now, in their order, and draws that
        // region. Slots next to each other in the whole mesh copy as one run, and all runs upload at once. Each of the mesh's
        // first lights gets its own copy of the slots drawing now within its reach after the drawn region, where the buffer
        // has room, so that no submesh shares indices with another; a light without room draws the whole drawn region.
        private static void Compact()
        {
            int count = 0;
            foreach (var batch in compactions)
            {
                batch.FirstRun = count;
                int drawn = AddRuns(batch, null, ref count, 0);
                batch.DrawnCount = drawn;
                int lights = Math.Min(batch.Lights.Count, LightRanges), room = batch.Total - batch.DrawnFirst;
                for (int light = 0; light < lights; light++)
                {
                    var range = batch.Lights[light];
                    int runsBefore = count, copied = AddRuns(batch, range, ref count, drawn);
                    range.Whole = copied > room;
                    if (range.Whole) { count = runsBefore; continue; }
                    range.Place = drawn;
                    range.Length = copied - drawn;
                    drawn = copied;
                }
                batch.RunCount = count - batch.FirstRun;
                batch.CopiedCount = drawn;
            }
            if (count > 0)
            {
                if (slotRuns == null || slotRuns.count < count)
                {
                    slotRuns?.Dispose();
                    slotRuns = new GraphicsBuffer(GraphicsBuffer.Target.Structured, runs.Length, Marshal.SizeOf<Run>());
                }
                slotRuns.SetData(runs, 0, 0, count);
            }
            foreach (var batch in compactions)
            {
                batch.Compacting = false;
                if (!batch.Mesh || batch.IndexBuffer == null) continue;
                int drawn = batch.DrawnCount, copied = batch.CopiedCount;
                bool shortIndices = batch.Mesh.indexFormat == IndexFormat.UInt16;
                int groups = ((shortIndices ? (copied + 1) / 2 : copied) + 63) / 64;
                EmptyLightSubmeshes(batch);
                // Beyond the drawn region or what one dispatch reaches, the renderer draws the whole mesh, whose slots drawing
                // nothing collapse.
                if (drawn > batch.Capacity || groups > 65535) { DrawRange(batch, 0, batch.Indices); LightSubmeshes(batch, false); drawnWhole++; continue; }
                if (copied > 0)
                {
                    int kernel = shortIndices ? compact16 : compact32;
                    compaction.SetVector(slotCompactionId, new Vector4(batch.FirstRun, batch.RunCount, copied, batch.DrawnFirst));
                    compaction.SetBuffer(kernel, slotRunsId, slotRuns);
                    compaction.SetBuffer(kernel, mergedIndicesId, batch.IndexBuffer);
                    compaction.Dispatch(kernel, groups, 1, 1);
                    compacted++;
                }
                DrawRange(batch, batch.DrawnFirst, batch.DrawnFirst + drawn);
                LightSubmeshes(batch, true);
            }
            compactions.Clear();
        }

        // Adds the runs copying a mesh's slots drawing now, or those of them within a light's reach, after the places already
        // filled, and returns the places filled.
        private static int AddRuns(Batch batch, LightRange light, ref int count, int drawn)
        {
            int first = -1, end = -1;
            foreach (int slot in batch.Order)
            {
                if (batch.TransformData[slot * 2].m33 == 0f) continue;
                if (light != null && ((slot < 64 ? light.Low >> slot : light.High >> (slot - 64)) & 1UL) == 0) continue;
                int start = batch.IndexFirst[slot], length = batch.IndexCount[slot];
                if (start == end) { end += length; continue; }
                if (first >= 0) AddRun(ref count, first, end, ref drawn);
                first = start;
                end = start + length;
            }
            if (first >= 0) AddRun(ref count, first, end, ref drawn);
            return drawn;
        }

        private static void AddRun(ref int count, int first, int end, ref int drawn)
        {
            if (count == runs.Length) Array.Resize(ref runs, runs.Length * 2);
            runs[count++] = new Run { First = (uint)first, Place = (uint)drawn, Count = (uint)(end - first) };
            drawn += end - first;
        }

        // Whether a merged part draws at its group's current level.
        private static bool Shown(Member member, Part part)
        {
            if (part.Detail < 0) return true;
            int level = member.Details[part.Detail].Level;
            return level >= 0 && (part.Levels & 1 << level) != 0;
        }

        // Gives each of an owned member's slots its matrices when its part draws at the current levels, and nothing otherwise.
        private static void WriteSlots(Member member)
        {
            foreach (var (batch, slot, part) in member.Slots)
            {
                bool shown = Shown(member, part);
                batch.TransformData[slot * 2] = shown ? member.ToWorld[part.Renderer] : default;
                batch.TransformData[slot * 2 + 1] = shown ? member.ToLocal[part.Renderer] : default;
                if (batch.Appearances != null) batch.AppearanceData[slot] = AppearanceOf(member, part);
                batch.TransformFrom = Math.Min(batch.TransformFrom, slot);
                batch.TransformTo = Math.Max(batch.TransformTo, slot);
                batch.AppearanceFrom = Math.Min(batch.AppearanceFrom, slot);
                batch.AppearanceTo = Math.Max(batch.AppearanceTo, slot);
            }
            flushPending = true;
        }

        // Queues a changed member for planning; a change of the member itself restarts its cell's settling time.
        private static void MarkDirty(Member member, bool settle)
        {
            member.Dirty = true;
            if (!member.Queued) { member.Queued = true; member.Cell.Changes.Add(member); }
            if (settle) { member.Cell.Changed = Time.realtimeSinceStartup; member.Cell.ChangedBy = member; }
            active.Add(member.Cell);
        }

        private static void MarkBatch(Batch batch)
        {
            batch.Dirty = true;
            active.Add(batch.Cell);
        }

        private static void Changed(Cell cell)
        {
            cell.Changed = Time.realtimeSinceStartup;
            active.Add(cell);
        }

        // Forgets the member's live slots; an owned member's slots stop drawing before the next render.
        private static void ClearSlots(Member member)
        {
            if (member.Owned) ZeroSlots(member);
            member.Slots.Clear();
        }

        private static void ZeroSlots(Member member)
        {
            foreach (var slot in member.Slots)
            {
                slot.batch.TransformData[slot.slot * 2] = default;
                slot.batch.TransformData[slot.slot * 2 + 1] = default;
                slot.batch.TransformFrom = Math.Min(slot.batch.TransformFrom, slot.slot);
                slot.batch.TransformTo = Math.Max(slot.batch.TransformTo, slot.slot);
                SkySlot(slot.batch, slot.slot, member, slot.part, false);
                flushPending = true;
            }
        }

        // Gives a member drawing merged its sky slots at the levels the sky depth camera selects.
        private static void WriteSky(Member member)
        {
            foreach (var (batch, slot, part) in member.Slots) SkySlot(batch, slot, member, part, true);
        }

        private static void SkySlot(Batch batch, int slot, Member member, Part part, bool drawn)
        {
            if (batch.SkyData == null) return;
            bool shown = drawn && SkyShown(member, part);
            batch.SkyData[slot * 2] = shown ? member.ToWorld[part.Renderer] : default;
            batch.SkyData[slot * 2 + 1] = shown ? member.ToLocal[part.Renderer] : default;
            batch.SkyFrom = Math.Min(batch.SkyFrom, slot);
            batch.SkyTo = Math.Max(batch.SkyTo, slot);
        }

        // A mesh's sky slots from its live members, when the sky depth camera first draws it, when it goes live again and when
        // that camera selects levels anew.
        private static void FillSky(Batch batch)
        {
            if (batch.SkyData == null) batch.SkyData = new Matrix4x4[SlotLimit * 2];
            else Array.Clear(batch.SkyData, 0, batch.SkyData.Length);
            for (int slot = 0; slot < batch.Live.Length; slot++)
            {
                var (member, part, version) = batch.Live[slot];
                if (!member.Removed && member.Version == version && member.Owned && !member.Away) SkySlot(batch, slot, member, part, true);
            }
            batch.SkyFrom = 0;
            batch.SkyTo = SlotLimit - 1;
        }

        private static void HideNative(Member member)
        {
            // A crop shows natively while its merged meshes are switched off, also when it arrives meanwhile.
            if (CropsNative && member.Crop) return;
            for (int index = 0; index < member.Renderers.Length; index++)
            {
                var renderer = member.Renderers[index];
                if (!renderer || !member.Merging[index]) continue;
                member.NativeForcedOff[index] = renderer.forceRenderingOff;
                renderer.forceRenderingOff = true;
            }
            foreach (var detail in member.Details)
                if (detail.Owned && !detail.Fades && detail.Group) { detail.NativeEnabled = detail.Group.enabled; detail.Group.enabled = false; }
        }

        private static void ShowNative(Member member)
        {
            foreach (var detail in member.Details)
                if (detail.Owned && !detail.Fades && detail.Group) detail.Group.enabled = detail.NativeEnabled;
            for (int index = 0; index < member.Renderers.Length; index++)
            {
                var renderer = member.Renderers[index];
                if (renderer && member.Merging[index]) renderer.forceRenderingOff = member.NativeForcedOff[index];
            }
        }

        // Checks detail levels where the main camera is about to cull, and applies merged slot changes before the next camera
        // draws, whichever camera and frame phase that is.
        internal static void BeforeCamera(Camera camera)
        {
            if (!Running) return;
            try
            {
                if (camera == Utils.GetMainCamera()) using (new Timed(DetailChecks)) CheckDetails(camera, camera.transform.position);
                using (new Timed(Uploads)) UpdateProbes();
                if (!flushPending) return;
                using (new Timed(Uploads))
                {
                    foreach (var cell in cells.Values)
                        foreach (var batch in cell.Batches)
                        {
                            if (batch.TransformTo >= 0 && batch.Transforms != null)
                            {
                                batch.Transforms.SetData(batch.TransformData, batch.TransformFrom * 2, batch.TransformFrom * 2, (batch.TransformTo - batch.TransformFrom + 1) * 2);
                                UploadTwin(batch, batch.TransformFrom, batch.TransformTo);
                                Drawn(batch);
                            }
                            if (batch.AppearanceTo >= 0 && batch.Appearances != null)
                                batch.Appearances.SetData(batch.AppearanceData, batch.AppearanceFrom, batch.AppearanceFrom, batch.AppearanceTo - batch.AppearanceFrom + 1);
                            batch.TransformFrom = batch.AppearanceFrom = int.MaxValue;
                            batch.TransformTo = batch.AppearanceTo = -1;
                        }
                    if (compactions.Count > 0) Compact();
                }
                flushPending = false;
            }
            catch (Exception error) { Fail(error); }
        }

        // Every scenery slot lights from the ambient probe, as native renderers do in a world without light probes.
        private static void UpdateProbes()
        {
            if (probeState == null) return;
            var probe = RenderSettings.ambientProbe;
            if (probeKnown && probe == probeSource) return;
            probeKnown = true;
            probeSource = probe;
            for (int channel = 0; channel < 3; channel++)
            {
                probeData[channel] = new Vector4(probe[channel, 3], probe[channel, 1], probe[channel, 2], probe[channel, 0] - probe[channel, 6]);
                probeData[channel + 3] = new Vector4(probe[channel, 4], probe[channel, 5], probe[channel, 6] * 3f, probe[channel, 7]);
            }
            probeData[6] = new Vector4(probe[0, 8], probe[1, 8], probe[2, 8], 1f);
            for (int slot = 1; slot < SlotLimit; slot++) Array.Copy(probeData, 0, probeData, slot * 7, 7);
            probeState.SetData(probeData);
        }

        internal static void LateUpdate()
        {
            if (!Running || !ZNetScene.instance) return;
            try
            {
                FrameStarted();
                float frame = Time.unscaledDeltaTime * 1000f;
                frameMilliseconds = frameMilliseconds > 0f ? Mathf.Lerp(frameMilliseconds, frame, .1f) : frame;
                bool waiting = frame > LongFrame * steadyMilliseconds;
                steadyMilliseconds = Mathf.Lerp(steadyMilliseconds, frame, frame < steadyMilliseconds ? SteadyFall : SteadyRise);
                budgetMilliseconds = Mathf.Clamp(frameMilliseconds * BudgetShare, BudgetMinimum, BudgetMaximum);
                var camera = Utils.GetMainCamera();
                var position = camera ? camera.transform.position : Vector3.zero;
                // Also checked here for frames the main camera does not render by itself, as in batch mode.
                if (camera) using (new Timed(DetailChecks)) { CheckDetails(camera, position); CheckSmallShadows(position, false); }
                budgetStart = Stopwatch.GetTimestamp();
                if (waiting)
                {
                    budgetEnd = capEnd = budgetStart - 1;
                    waitedFrames++;
                }
                else
                {
                    budgetEnd = budgetStart + Ticks(budgetMilliseconds);
                    capEnd = budgetStart + Ticks(FrameMaximum) - frameTicks - Math.Max(afterReserve, Ticks(MinimumReserve));
                }
                overrun = false;
                if (camera)
                {
                    using (new Timed(Applies)) Complete(position);
                    using (new Timed(Arrivals)) { OwnCompleted(position); CheckFinishing(position); }
                    using (new Timed(DetailChecks)) DropVoidChecks();
                }
                using (new Timed(Removals)) Forget();
                using (new Timed(SkyPreparation)) PrepareSky();
                using (new Timed(Readbacks)) CopyReadables();
                if (camera) using (new Timed(Planning)) Schedule(position);
                using (new Timed(Requests)) while (requests.Count > 0 && Open()) Request(requests.Dequeue());
                using (new Timed(Registration)) Register();
                using (new Timed(ShadowedLights)) UpdateLights();
                lateFrame = Time.frameCount;
                lateTicks = frameTicks;
            }
            catch (Exception error) { Fail(error); }
        }

        private static void Register()
        {
            while (pending.Count > 0 && Open())
            {
                // Registered a frame after creation, once every component of the member has woken and started.
                if (pending.Peek().frame == Time.frameCount) break;
                var wear = pending.Dequeue().wear;
                if (!wear || pieces.ContainsKey(wear)) continue;
                long started = Stopwatch.GetTimestamp();
                string reason = Exclusion(wear, out var member);
                long spent = Stopwatch.GetTimestamp() - started;
                if (spent > slowestRegistration) { slowestRegistration = spent; slowestPrefab = Utils.GetPrefabName(wear.gameObject); }
                if (reason != null)
                {
                    Count(excluded, reason);
                    var ids = MapSwitched(wear, null);
                    if (ids.Length > 0) nativeSwitched[wear] = ids;
                    continue;
                }
                pieces.Add(wear, member);
                member.SwitchedIds = MapSwitched(wear, member);
                Join(member, wear.transform.position);
            }
            float now = Time.realtimeSinceStartup;
            while (pendingNatural.Count > 0 && Open())
            {
                var (view, time) = pendingNatural.Peek();
                if (now - time < NaturalDelay) break;
                pendingNatural.Dequeue();
                if (!view || natural.ContainsKey(view)) continue;
                long started = Stopwatch.GetTimestamp();
                string reason = NaturalExclusion(view, out var member);
                long spent = Stopwatch.GetTimestamp() - started;
                if (spent > slowestRegistration) { slowestRegistration = spent; slowestPrefab = Utils.GetPrefabName(view.gameObject); }
                if (reason == Waiting) { pendingNatural.Enqueue((view, now)); continue; }
                if (reason != null) { if (reason != BuildingPiece) Count(naturalExcluded, reason); continue; }
                natural.Add(view, member);
                Join(member, view.transform.position);
            }
        }

        // Maps the piece's switched behaviours, those whose nearest piece up the hierarchy is this one, to its member or to none.
        // A behaviour of a native piece with a network view of its own is left to the search, which looks for a natural object
        // with that view.
        private static int[] MapSwitched(WearNTear wear, Member member)
        {
            wear.GetComponentsInChildren(true, behaviourList);
            try
            {
                switchedIdList.Clear();
                var root = wear.gameObject;
                foreach (var behaviour in behaviourList)
                {
                    if (behaviour is null || !switchedTypes.Contains(behaviour.GetType()) || behaviour.GetComponentInParent<WearNTear>(true) != wear) continue;
                    if (member == null && behaviour.TryGetComponent<ZNetView>(out var view) && view.gameObject != root) continue;
                    int id = behaviour.GetInstanceID();
                    switchedMembers[id] = (member, root);
                    switchedIdList.Add(id);
                }
                return switchedIdList.Count > 0 ? switchedIdList.ToArray() : Array.Empty<int>();
            }
            finally { behaviourList.Clear(); }
        }

        // Adds a registered member to its cell; its source meshes are read while the cell settles.
        private static void Join(Member member, Vector3 position)
        {
            foreach (int id in member.RendererIds) rendererMembers[id] = member;
            var key = Vector3Int.FloorToInt(new Vector3(position.x + CellSize / 2, position.y, position.z + CellSize / 2) / CellSize);
            if (!cells.TryGetValue(key, out var cell))
                cells.Add(key, cell = new Cell { Key = key, Center = new Vector3(key.x, key.y + .5f, key.z) * CellSize });
            member.Cell = cell;
            cell.Members.Add(member);
            MarkDirty(member, true);
            foreach (var part in member.Parts)
                if (!part.Source.Requested && !part.Source.Queued) { part.Source.Queued = true; requests.Enqueue(part.Source); }
        }

        // Why a building piece keeps native rendering, or null with its record when stable parts of it can merge.
        private static string Exclusion(WearNTear wear, out Member member)
        {
            member = null;
            var view = wear.GetComponent<ZNetView>();
            if (!view || view.GetZDO() == null) return "no network record";
            int prefab = view.GetZDO().GetPrefab();
            if (!prefabExclusions.TryGetValue(prefab, out string reason))
            {
                var source = ZNetScene.instance.GetPrefab(prefab);
                prefabExclusions[prefab] = reason = source ? Components(source, null, null, allowedBehaviours) : "unknown prefab";
            }
            if (reason != null) return reason;
            var blocks = MaterialMan.instance ? materialBlocks(MaterialMan.instance) : null;
            bool managed = blocks != null && blocks.Contains(wear.gameObject.GetInstanceID());
            if ((reason = Record(wear.gameObject, allowedBehaviours, managed, true, out member)) != null) { recordExclusions[prefab] = reason; return reason; }
            member.Wear = wear;
            if (managed) member.Properties = Properties(containerProperties(blocks[wear.gameObject.GetInstanceID()]));
            return null;
        }

        // Why a natural object keeps native rendering, or null with its record. Pieces a player built stay with the building
        // path or native, and values from the material manager have no slots in the scenery shaders.
        private static string NaturalExclusion(ZNetView view, out Member member)
        {
            member = null;
            var zdo = view.GetZDO();
            if (zdo == null || ghostView(view)) return "no network record";
            int prefab = zdo.GetPrefab();
            if (!naturalExclusions.TryGetValue(prefab, out string reason))
            {
                var source = ZNetScene.instance.GetPrefab(prefab);
                naturalExclusions[prefab] = reason = !source ? "unknown prefab" : source.GetComponent<WearNTear>() ? BuildingPiece : Components(source, null, null, naturalBehaviours);
            }
            if (reason != null) return reason;
            bool planted = view.TryGetComponent<Piece>(out var piece) && piece.GetCreator() != 0;
            if (planted && !MergeCrops) return "built";
            if (view.TryGetComponent<TreeBase>(out var tree) && tree.m_trunk && !tree.m_trunk.activeSelf) return Waiting;
            var blocks = MaterialMan.instance ? materialBlocks(MaterialMan.instance) : null;
            if (blocks != null && blocks.Contains(view.gameObject.GetInstanceID())) return "material manager";
            if ((reason = Record(view.gameObject, naturalBehaviours, false, false, out member)) != null) { recordExclusions[prefab] = reason; return reason; }
            member.View = view;
            member.Planted = planted;
            member.Physics = view.GetComponent<StaticPhysics>();
            return null;
        }

        // A member's renderers, detail groups and mergeable parts, or why it keeps native rendering.
        private static string Record(GameObject root, HashSet<Type> allowed, bool managed, bool building, out Member member)
        {
            member = null;
            rendererList.Clear();
            groupList.Clear();
            string reason;
            if ((reason = Components(root, rendererList, groupList, allowed)) != null) return reason;
            if (rendererList.Count > 64) return "over 64 renderers";
            var renderers = rendererList.ToArray();
            var ids = new int[renderers.Length];
            for (int index = 0; index < renderers.Length; index++) ids[index] = renderers[index].GetInstanceID();
            membership.Clear();
            var details = new Detail[groupList.Count];
            for (int index = 0; index < details.Length; index++)
            {
                var group = groupList[index];
                // An animated crossfade runs for a fixed time after a level change; other fades depend on distance.
                bool fades = group.fadeMode != LODFadeMode.None;
                if (fades && (group.fadeMode != LODFadeMode.CrossFade || !group.animateCrossFading)) return "distance-faded detail levels";
                // The game's fade-in of a distant spawn moves its group's reference point away for a moment.
                if (group.localReferencePoint.x > 999998f) return Waiting;
                var levels = group.GetLODs();
                var thresholds = new float[levels.Length];
                for (int level = 0; level < levels.Length; level++)
                {
                    thresholds[level] = levels[level].screenRelativeTransitionHeight;
                    foreach (var renderer in levels[level].renderers)
                    {
                        // A missing renderer keeps its group choosing levels natively.
                        if (renderer is null) continue;
                        int id = renderer.GetInstanceID();
                        bool known = membership.TryGetValue(id, out var entry);
                        if (!known || entry.detail == index) membership[id] = (index, (known ? entry.levels : 0) | 1 << level);
                    }
                }
                details[index] = new Detail { Group = group, Thresholds = thresholds, Level = -2, Owned = true, Fades = fades };
                Place(details[index]);
            }
            var merging = new bool[renderers.Length];
            partList.Clear();
            mergingIds.Clear();
            string rendererReason = null;
            for (int index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];
                string excluded = RendererExclusion(renderer, managed, building, out var mesh);
                if (excluded != null) { if (rendererReason == null) rendererReason = excluded; continue; }
                if (!sources.TryGetValue(mesh, out var source)) sources.Add(mesh, source = new Source { Mesh = mesh });
                var (detail, mask) = membership.TryGetValue(ids[index], out var levels) ? levels : (-1, 0);
                for (int submesh = 0; submesh < materialList.Count; submesh++)
                    partList.Add(new Part { Renderer = index, Submesh = submesh, Source = source, Material = materialList[submesh], Detail = detail, Levels = mask });
                merging[index] = true;
                mergingIds.Add(ids[index]);
                if (detail >= 0) details[detail].Merging = true;
            }
            if (partList.Count == 0) return "no supported renderer: " + (rendererReason ?? "none in the hierarchy");
            if (partList.Count > SlotLimit) return "over " + SlotLimit + " merged parts";
            // A group whose renderers are not all merged keeps choosing its level natively.
            foreach (var entry in membership) if (!mergingIds.Contains(entry.Key)) details[entry.Value.detail].Owned = false;
            member = new Member { Root = root, Renderers = renderers, RendererIds = ids, NativeForcedOff = new bool[renderers.Length], Merging = merging,
                ToWorld = new Matrix4x4[renderers.Length], ToLocal = new Matrix4x4[renderers.Length], RendererBounds = new Bounds[renderers.Length],
                Parts = partList.ToArray(), Details = details };
            if (member.Parts.Any(part => !part.Material.enableInstancing)) member.Coverage |= UninstancedPieces;
            member.Switches = SwitchedObjects(root, building ? root.GetComponent<WearNTear>() : null);
            member.Switched = SwitchState(member);
            member.Signature = Signature(member);
            return null;
        }

        // Collects a hierarchy's mesh renderers and detail groups, or names the component that keeps it native. Objects a
        // local behaviour moves, and everything under them, are left out instead of keeping the whole piece native.
        private static string Components(GameObject root, List<MeshRenderer> renderers, List<LODGroup> groups, HashSet<Type> allowed)
        {
            root.GetComponentsInChildren(true, components);
            try
            {
                localRoots.Clear();
                foreach (var component in components)
                {
                    if (component is null) continue;
                    if (component is MonoBehaviour behaviour)
                    {
                        var type = behaviour.GetType();
                        if (!allowed.Contains(type) && !inertComponents.Contains(type.Name)) return "behaviour " + type.Name;
                        if (localBehaviours.Contains(type)) localRoots.Add(behaviour.transform);
                    }
                    else if (component is Rigidbody) return "rigidbody";
                    else if (component is Animator || component is Animation) return "animation";
                }
                if (renderers == null && groups == null) return null;
                foreach (var component in components)
                {
                    if (component is null) continue;
                    if (component is MeshRenderer renderer) { if (!Local(renderer.transform, root.transform)) renderers?.Add(renderer); }
                    else if (component is LODGroup group && !Local(group.transform, root.transform)) groups?.Add(group);
                }
                return null;
            }
            finally { components.Clear(); localRoots.Clear(); }
        }

        // Whether an object sits on or under an object a local behaviour moves.
        private static bool Local(Transform transform, Transform root)
        {
            if (localRoots.Count == 0) return false;
            for (var current = transform; current; current = current.parent)
            {
                if (localRoots.Contains(current)) return true;
                if (current == root) break;
            }
            return false;
        }

        // Leaves the renderer's materials in the shared material list when it can merge: piece or Standard materials for building
        // pieces, and scenery or piece materials for natural objects, such as a swamp tree's piece-shaded bark, whose piece
        // values keep their defaults, since only the game's wear code sets them. Natural objects with Standard materials and
        // pieces with scenery materials stay native: merged, they took more draw calls and render-thread time than natively.
        private static string RendererExclusion(MeshRenderer renderer, bool managed, bool building, out Mesh mesh)
        {
            mesh = null;
            if (!renderer.TryGetComponent<MeshFilter>(out var filter) || !(mesh = filter.sharedMesh)) return "no mesh";
            renderer.GetSharedMaterials(materialList);
            if (materialList.Count != mesh.subMeshCount) return "material count";
            foreach (var material in materialList)
            {
                string source = SupportedShader(material);
                if (source == null || (building ? source != pieceShader && source != standardShader : source == standardShader)) return "material";
            }
            if (renderer.forceRenderingOff || renderer.isPartOfStaticBatch || renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 65534 || renderer.probeAnchor ||
                renderer.lightProbeUsage == LightProbeUsage.UseProxyVolume || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) return "renderer settings";
            if (renderer.localToWorldMatrix.determinant <= 0f) return "mirrored";
            // Property blocks the material manager did not set are invisible to the merged slots.
            if (renderer.HasPropertyBlock() && !managed) return "property block";
            return null;
        }

        // Opaque materials of the game shaders with derived copies, instanced except for piece materials. The ripe barley's
        // grass material is not instanced, and the shader's instanced variant, which the copies derive from, bends its stalks
        // differently; the piece shader's two variants draw alike, so bird nests and beehives merge too.
        private static string SupportedShader(Material material)
        {
            if (!material) return null;
            if (!supportedMaterials.TryGetValue(material, out string source))
            {
                string name = material.shader ? material.shader.name : null;
                supportedMaterials[material] = source = name != null && derived.ContainsKey(name) && (material.enableInstancing || name == pieceShader || MergeCrops) && material.renderQueue <= 2500 ? name : null;
            }
            return source;
        }

        private static float MaximumScale(Vector3 scale) => Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));

        // A detail group's world reference point and size, which stay put while the member does.
        private static void Place(Detail detail)
        {
            var group = detail.Group;
            if (!group) return;
            detail.Reference = group.transform.TransformPoint(group.localReferencePoint);
            detail.Size = group.size * MaximumScale(group.transform.lossyScale);
        }

        // The level a native group shows for the camera, as the engine selects it, and how far the camera can move before it changes.
        private static int Level(Detail detail, Vector3 camera, out float margin)
        {
            float distance = Vector3.Distance(camera, detail.Reference);
            float scale = detail.Size * detailScale;
            float relative = scale / Mathf.Max(distance, .001f);
            int level = -1;
            for (int index = 0; index < detail.Thresholds.Length; index++)
                if (relative >= detail.Thresholds[index]) { level = index; break; }
            float near = level <= 0 ? 0f : scale / detail.Thresholds[level - 1];
            float far = level < 0 ? float.PositiveInfinity : scale / detail.Thresholds[level];
            if (level < 0) near = detail.Thresholds.Length == 0 ? 0f : scale / detail.Thresholds[detail.Thresholds.Length - 1];
            margin = Mathf.Min(distance - near, far - distance);
            return level < 0 ? -1 : Math.Max(level, detailMaximum);
        }

        // The level the orthographic sky depth camera selects for a group, with the maximum level read by the last detail-level
        // checks, whose change fills the sky slots again.
        private static int SkyLevel(Detail detail)
        {
            float relative = detail.Size * skyScale;
            for (int index = 0; index < detail.Thresholds.Length; index++)
                if (relative >= detail.Thresholds[index]) return Math.Max(index, detailMaximum >= 0 ? detailMaximum : QualitySettings.maximumLODLevel);
            return -1;
        }

        // Whether a merged part draws at the level the sky depth camera selects for its group.
        private static bool SkyShown(Member member, Part part)
        {
            if (part.Detail < 0) return true;
            int level = SkyLevel(member.Details[part.Detail]);
            return level >= 0 && (part.Levels & 1 << level) != 0;
        }

        // Whether the sky depth camera draws one of the member's merged parts, at another level than the main camera for one of
        // its groups.
        private static bool OtherSkyLevels(Member member)
        {
            int mask = skyCamera.cullingMask;
            bool drawn = false;
            foreach (var part in member.Merged) if ((mask & 1 << part.Key.Layer) != 0) { drawn = true; break; }
            if (drawn) foreach (var detail in member.Active) if (detail.Level != SkyLevel(detail)) return true;
            return false;
        }

        // Evaluates the owned members whose detail-level checks the camera's travel has made due, or every owned member when the
        // detail settings change.
        private static void CheckDetails(Camera camera, Vector3 position)
        {
            float bias = QualitySettings.lodBias, view = camera.fieldOfView;
            int maximum = QualitySettings.maximumLODLevel;
            bool settings = bias != detailBias || view != detailView || maximum != detailMaximum;
            if (settings)
            {
                // The sky depth camera's levels follow the same maximum level.
                if (maximum != detailMaximum) ForgetSky();
                detailBias = bias;
                detailView = view;
                detailMaximum = maximum;
                detailScale = bias * .5f / Mathf.Tan(view * Mathf.Deg2Rad * .5f);
            }
            if (cameraKnown) travelled += Vector3.Distance(position, lastCamera);
            lastCamera = position;
            cameraKnown = true;
            if (settings)
            {
                checks.Clear();
                foreach (var member in Members()) if (member.Owned) CheckLevels(member, position);
            }
            while (checks.Count > 0 && checks[0].Due <= travelled)
            {
                var check = checks[0];
                var last = checks[checks.Count - 1];
                checks.RemoveAt(checks.Count - 1);
                if (checks.Count > 0) SiftDown(0, last);
                if (check.Stamp == check.Member.CheckStamp && !check.Member.Removed) CheckLevels(check.Member, position);
            }
        }

        // Void detail-level checks are dropped within the frame's budget once they outnumber the members.
        private static void DropVoidChecks()
        {
            int entries = checks.Count;
            if (entries <= (pieces.Count + natural.Count) * 2 + 64 || !Admit(cleanupCost.Expected(entries))) return;
            var step = Step.Start();
            checks.RemoveAll(check => check.Stamp != check.Member.CheckStamp || check.Member.Removed);
            for (int index = checks.Count / 2 - 1; index >= 0; index--) SiftDown(index, checks[index]);
            step.Done(cleanupCost, entries);
        }

        // A merged member is checked again before the camera can reach its next detail-level change. One whose groups switch at
        // once switches its slots there; one with a crossfading group shows natively from a margin before it.
        private static void CheckLevels(Member member, Vector3 camera)
        {
            if (!member.Owned) return;
            if (member.Away) { CheckAway(member, camera, false); return; }
            if (!Crossfades(member))
            {
                float next = Levels(member, camera, out bool switched);
                if (switched) Switch(member);
                CheckAfter(member, next - SwitchMargin);
                return;
            }
            evaluations++;
            bool changed = false;
            float closest = float.PositiveInfinity;
            foreach (var detail in member.Active)
            {
                if (Level(detail, camera, out float margin) != detail.Level) changed = true;
                closest = Mathf.Min(closest, margin);
            }
            if (!changed && closest >= DetailMargin) { CheckAfter(member, closest - DetailMargin); return; }
            Leave(member, "detail level");
        }

        // Sets each of the member's groups to its current level, and returns how far the camera is from the nearest change.
        private static float Levels(Member member, Vector3 camera, out bool changed)
        {
            evaluations++;
            changed = false;
            float closest = float.PositiveInfinity;
            foreach (var detail in member.Active)
            {
                int level = Level(detail, camera, out float margin);
                if (level != detail.Level) { detail.Level = level; changed = true; }
                closest = Mathf.Min(closest, margin);
            }
            return closest;
        }

        private static void Switch(Member member)
        {
            WriteSlots(member);
            switches++;
        }

        // A member showing natively follows the level the engine selects, and a crossfade starts where it changes. Once the
        // camera is clear of every change, the fade has ended and a shaken or growing tree has finished, it draws merged again
        // where arriving is allowed, within the frame's budget, and otherwise waits among the finishing members for that.
        private static void CheckAway(Member member, Vector3 camera, bool arrive)
        {
            float closest = Levels(member, camera, out bool changed);
            if (changed) Faded(member);
            if (!arrive || !ClearOfChanges(member, closest) || !Finished(member, Time.realtimeSinceStartup, Time.time)) { Wait(member, closest); return; }
            if (Moved(member)) HandBack(member, "moved"); else Arrive(member, closest);
        }

        // A member showing natively is checked again before the camera can reach one of its changes or be clear of them; once
        // clear, its fade and tree are followed within the frame's budget.
        private static void Wait(Member member, float closest)
        {
            bool clear = ClearOfChanges(member, closest);
            if (clear) Finish(member); else Unfinish(member);
            CheckAfter(member, clear ? closest : Mathf.Min(closest, DetailMargin * 4f - closest));
        }

        private static void Finish(Member member)
        {
            if (member.Finishing >= 0) return;
            member.Finishing = finishing.Count;
            finishing.Add(member);
        }

        // The last finishing member takes the place of one that leaves.
        private static void Unfinish(Member member)
        {
            int index = member.Finishing;
            if (index < 0) return;
            var last = finishing[finishing.Count - 1];
            finishing[index] = last;
            last.Finishing = index;
            finishing.RemoveAt(finishing.Count - 1);
            member.Finishing = -1;
        }

        // A member with a crossfading group draws merged again only with the camera four margins from every change.
        private static bool ClearOfChanges(Member member, float closest) => closest >= DetailMargin * 4f || !Crossfades(member);

        // Whether a crossfade started on the member has ended, in real and game time, and a shaken or growing tree has finished.
        private static bool Finished(Member member, float now, float game) =>
            (now >= member.FadeUntil && game >= member.FadeUntilGame || !Crossfades(member)) && !Busy(member);

        // The members clear of every change draw merged again once their fade and tree have finished, looked through in turn
        // within the frame's budget, at most once each a frame.
        private static void CheckFinishing(Vector3 camera)
        {
            float now = Time.realtimeSinceStartup, game = Time.time;
            for (int looked = finishing.Count; looked > 0 && finishing.Count > 0 && Open(); looked--)
            {
                if (finishingNext >= finishing.Count) finishingNext = 0;
                var member = finishing[finishingNext];
                if (Finished(member, now, game)) CheckAway(member, camera, true);
                // One that drew merged again or went back to waiting near a change gave its place to the last one.
                if (member.Finishing == finishingNext) finishingNext++;
            }
        }

        // Whether a merging renderer has left the place its slots hold, as a shaken trunk might.
        private static bool Moved(Member member)
        {
            foreach (var part in member.Merged)
            {
                var renderer = member.Renderers[part.Renderer];
                if (renderer && renderer.localToWorldMatrix != member.ToWorld[part.Renderer]) return true;
            }
            return false;
        }

        // Queues the member's next check for when the camera has travelled the given distance, voiding its earlier ones. One on a
        // change is checked again as soon as the camera moves.
        private static void CheckAfter(Member member, float distance)
        {
            if (float.IsPositiveInfinity(distance)) { member.CheckStamp++; return; }
            var entry = new Check { Due = travelled + Math.Max(distance, .0001f), Member = member, Stamp = ++member.CheckStamp };
            int index = checks.Count;
            checks.Add(entry);
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (checks[parent].Due <= entry.Due) break;
                checks[index] = checks[parent];
                index = parent;
            }
            checks[index] = entry;
        }

        private static void SiftDown(int index, Check entry)
        {
            while (true)
            {
                int child = index * 2 + 1;
                if (child >= checks.Count) break;
                if (child + 1 < checks.Count && checks[child + 1].Due < checks[child].Due) child++;
                if (entry.Due <= checks[child].Due) break;
                checks[index] = checks[child];
                index = child;
            }
            checks[index] = entry;
        }

        // Applies finished jobs within the frame's budget; each mesh goes live on its own, and the members it completes are
        // owned within the budget after it. A new mesh's renderer objects are made in a step of their own while its job runs,
        // so its first apply takes no longer than later ones; they draw nothing until it goes live.
        private static void Complete(Vector3 camera)
        {
            int applied = 0;
            for (int index = 0; index < builds.Count && applied < AppliesPerFrame; index++)
            {
                var build = builds[index];
                if (!build.Batch.Object)
                {
                    if (!Admit(createCost.Expected(1))) break;
                    var creating = Step.Start();
                    CreateBatch(build.Batch);
                    creating.Done(createCost, 1);
                    continue;
                }
                if (!build.Handle.IsCompleted) continue;
                if (!Admit(applyCost.Expected(build.Vertices))) break;
                builds.RemoveAt(index--);
                var step = Step.Start();
                long start = Stopwatch.GetTimestamp();
                build.Handle.Complete();
                bool valid = build.Result[0] == 0;
                build.Sources.Dispose();
                build.Inputs.Dispose();
                build.Result.Dispose();
                if (!valid) { build.Output.Dispose(); build.Batch.Build = null; throw new InvalidOperationException("A source mesh's indices fall outside its submesh range."); }
                var mesh = new Mesh { name = "World Performance spatial buildings", hideFlags = HideFlags.HideAndDontSave };
                Mesh.ApplyAndDisposeWritableMeshData(build.Output, mesh, quiet);
                // The compaction kernel writes the drawn region through a raw view of the index buffer, set while the mesh still
                // has its processor copy to create the buffer from.
                if (compactIndices) mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
                mesh.bounds = build.Bounds;
                mesh.UploadMeshData(true);
                long spent = Stopwatch.GetTimestamp() - start;
                jobTicks += spent;
                if (spent > slowestApply) { slowestApply = spent; slowestApplyVertices = mesh.vertexCount; }
                Swap(build, mesh);
                step.Done(applyCost, build.Vertices);
                applied++;
            }
        }

        // Owns, within the frame's budget, the members whose merged parts went live.
        private static void OwnCompleted(Vector3 camera)
        {
            while (owning.Count > 0 && Open())
            {
                var member = owning.Dequeue();
                member.OwnQueued = false;
                Own(member, camera);
            }
        }

        // Puts a rebuilt mesh live. Owned members keep drawing from their new slots at their current levels; other members
        // reserve theirs with empty transforms and are queued to become owned once every merged part of their current version
        // is live.
        private static void Swap(Build build, Mesh mesh)
        {
            var batch = build.Batch;
            batch.Build = null;
            foreach (var (member, _, _) in batch.Live)
                for (int index = member.Slots.Count - 1; index >= 0; index--) if (member.Slots[index].batch == batch) member.Slots.RemoveAt(index);
            var old = batch.Mesh;
            batch.IndexBuffer?.Dispose();
            batch.Mesh = mesh;
            batch.Bounds = build.Bounds;
            batch.Filter.sharedMesh = mesh;
            if (batch.TwinFilter) batch.TwinFilter.sharedMesh = mesh;
            batch.LightSubmeshes = 0;
            if (old) UnityEngine.Object.Destroy(old);
            batch.IndexBuffer = compactIndices ? mesh.GetIndexBuffer() : null;
            if (batch.IndexBuffer != null && (batch.IndexBuffer.target & GraphicsBuffer.Target.Raw) == 0)
                throw new InvalidOperationException("A merged mesh's index buffer has no raw view for the compaction kernel: mesh " + mesh.indexBufferTarget +
                    ", buffer " + batch.IndexBuffer.target + ".");
            Array.Clear(batch.TransformData, 0, batch.TransformData.Length);
            for (int slot = 0; slot < build.Slots.Length; slot++)
            {
                var (member, part, version) = build.Slots[slot];
                if (member.Removed || member.Version != version) continue;
                member.Slots.Add((batch, slot, part));
                if (batch.Appearances != null) batch.AppearanceData[slot] = AppearanceOf(member, part);
                if (member.Owned)
                {
                    if (!member.Away && Shown(member, part))
                    {
                        batch.TransformData[slot * 2] = member.ToWorld[part.Renderer];
                        batch.TransformData[slot * 2 + 1] = member.ToLocal[part.Renderer];
                    }
                }
                else if (!member.OwnQueued) { member.OwnQueued = true; owning.Enqueue(member); }
            }
            batch.Live = build.Slots;
            batch.IndexFirst = build.IndexFirst;
            batch.IndexCount = build.IndexCount;
            batch.Order = build.Order;
            batch.Indices = (int)mesh.GetIndexCount(1);
            batch.Total = build.Total;
            mergedBytes -= batch.Bytes;
            mergedBytes += batch.Bytes = GraphicsBytes(batch);
            batch.DrawnFirst = build.DrawnFirst;
            batch.Capacity = build.Capacity;
            batch.SpanFirst = build.DrawnFirst;
            batch.SpanEnd = compactIndices ? build.DrawnFirst : -1;
            batch.DrawnKnown = false;
            if (batch.SkyData != null) FillSky(batch);
            QueueSky(batch);
            batch.Transforms.SetData(batch.TransformData);
            if (batch.TwinTransforms != null)
            {
                Array.Clear(batch.SlotFar, 0, batch.SlotFar.Length);
                FarSlots(batch, lastCamera);
                UploadTwin(batch, 0, SlotLimit - 1);
            }
            Drawn(batch);
            batch.Appearances?.SetData(batch.AppearanceData);
            batch.TransformFrom = batch.AppearanceFrom = int.MaxValue;
            batch.TransformTo = batch.AppearanceTo = -1;
            batch.Renderer.enabled = Drawn(batch.Key);
            if (batch.Twin) batch.Twin.enabled = Casts(batch);
            LightsReach(batch);
            swaps++;
        }

        // Takes over a member whose merged parts are all live: it draws merged at once at the levels the camera selects. One
        // with a crossfading group keeps showing natively while the camera is near one of its detail-level changes, and waits
        // one fade first, since the camera may have passed a change just before.
        private static void Own(Member member, Vector3 camera)
        {
            if (member.Owned || member.Removed || member.Dirty || member.Merged.Count == 0 || member.Slots.Count != member.Merged.Count) return;
            member.Owned = true;
            float closest = Levels(member, camera, out _);
            if (Crossfades(member)) Faded(member);
            if (ClearOfChanges(member, closest) && Finished(member, Time.realtimeSinceStartup, Time.time)) { Arrive(member, closest); return; }
            member.Away = true;
            near.Add(member);
            Wait(member, closest);
        }

        // Builds readable copies of source meshes the GPU has returned, one at a time within the frame's budget.
        private static void CopyReadables()
        {
            while (received.Count > 0)
            {
                var source = received.Peek();
                var mesh = source.Mesh;
                int vertices = mesh ? mesh.vertexCount : 0;
                if (!Admit(copyCost.Expected(vertices))) break;
                received.Dequeue();
                if (!mesh) { source.Failed = true; ReleaseData(source); continue; }
                var step = Step.Start();
                var readable = new Mesh { name = mesh.name + " readable copy", indexFormat = mesh.indexFormat, hideFlags = HideFlags.HideAndDontSave };
                readable.SetVertexBufferParams(mesh.vertexCount, mesh.GetVertexAttributes());
                for (int index = 0; index < source.Streams.Length; index++)
                    readable.SetVertexBufferData(source.Streams[index], 0, 0, source.Streams[index].Length, index, quiet);
                int size = mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4;
                readable.SetIndexBufferParams(source.Indices.Length / size, mesh.indexFormat);
                if (size == 2) readable.SetIndexBufferData(source.Indices.Reinterpret<ushort>(1), 0, 0, source.Indices.Length / 2, quiet);
                else readable.SetIndexBufferData(source.Indices.Reinterpret<uint>(1), 0, 0, source.Indices.Length / 4, quiet);
                readable.subMeshCount = mesh.subMeshCount;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++) readable.SetSubMesh(submesh, mesh.GetSubMesh(submesh), quiet);
                readable.bounds = mesh.bounds;
                source.Ranges = Ranges(readable);
                source.Readable = readable;
                ReleaseData(source);
                step.Done(copyCost, vertices);
            }
        }

        // Plans settled cells' changed members, nearest cells first, and starts jobs for the meshes they change.
        private static void Schedule(Vector3 camera)
        {
            float now = Time.realtimeSinceStartup;
            cellOrder.Clear();
            cellOrder.AddRange(active);
            orderFrom = camera;
            cellOrder.Sort(nearestFirst);
            foreach (var cell in cellOrder)
            {
                if (!Open()) break;
                if (now - cell.Changed < SettleSeconds) continue;
                // Every source mesh of a member must be readable before it plans, and a falling, shaken or growing natural object
                // must have come to rest; readbacks run while it waits.
                for (int index = cell.Changes.Count - 1; index >= 0 && Open(); index--)
                {
                    var member = cell.Changes[index];
                    if (member.Dirty && !member.Removed && (Busy(member) || !Readable(member))) continue;
                    cell.Changes.RemoveAt(index);
                    member.Queued = false;
                    if (!member.Dirty || member.Removed) continue;
                    Plan(member);
                    member.Dirty = false;
                }
                if (cell.Changes.Count > 0) continue;
                bool waiting = false;
                for (int index = cell.Batches.Count - 1; index >= 0; index--)
                {
                    var batch = cell.Batches[index];
                    if (!batch.Dirty) continue;
                    // A new mesh waits while the merged meshes take their share of graphics memory; its members stay native.
                    if (batch.Build != null || builds.Count >= BuildsInFlight || !batch.Mesh && batch.Members.Count > 0 && mergedBytes >= MemoryLimit ||
                        !Admit(buildCost.Expected(batch.Count))) { waiting = true; continue; }
                    batch.Dirty = false;
                    // Nothing to draw: the mesh goes, and members that lose their place are planned again.
                    if (batch.Members.Count == 0) { DestroyBatch(batch); continue; }
                    var step = Step.Start();
                    StartBuild(batch);
                    step.Done(buildCost, batch.Count);
                    rebuilds++;
                }
                if (!waiting) idle.Add(cell);
            }
            foreach (var cell in idle)
            {
                if (cell.Changes.Count > 0) continue;
                active.Remove(cell);
                // Cells the player has left are forgotten, so frame work follows the loaded area.
                if (cell.Members.Count == 0 && cell.Batches.Count == 0) cells.Remove(cell.Key);
            }
            idle.Clear();
        }

        private static bool Readable(Member member)
        {
            foreach (var part in member.Parts)
            {
                if (!part.Source.Requested) Request(part.Source);
                if (!part.Source.Readable && !part.Source.Failed) return false;
            }
            return true;
        }

        // Chooses the parts of a changed member's visible renderers at every detail level and assigns them to batches with
        // room. Members with values the merged shader cannot express or with unreadable meshes stay native.
        private static void Plan(Member member)
        {
            Unassign(member);
            // Jobs started before this plan no longer hold the member, and its earlier detail-level checks are void.
            member.Version++;
            member.CheckStamp++;
            member.Slots.Clear();
            member.Merged.Clear();
            member.Active.Clear();
            if (!Expressible(member) || (member.Coverage & NativeCoverage) != 0) return;
            foreach (var part in member.Parts) if (part.Source.Failed) return;
            member.Signature = Signature(member);
            member.Switched = SwitchState(member);
            foreach (var part in member.Parts)
                if (part.Detail >= 0 && (member.Signature & 1UL << part.Renderer) != 0 && !member.Active.Contains(member.Details[part.Detail]))
                    member.Active.Add(member.Details[part.Detail]);
            var cell = member.Cell;
            int previous = -1;
            BatchKey style = default;
            bool any = false, crop = member.Planted || member.Parts.Any(part => !part.Material.enableInstancing);
            member.Crop = crop;
            foreach (var part in member.Parts)
            {
                if ((member.Signature & 1UL << part.Renderer) == 0) continue;
                if (part.Source.Ranges[part.Submesh].count == 0) continue;
                if (part.Renderer != previous)
                {
                    // A renderer's settings, matrices and bounds are read once; its submeshes follow it.
                    previous = part.Renderer;
                    var renderer = member.Renderers[part.Renderer];
                    style = new BatchKey { Cell = cell.Key, Layer = renderer.gameObject.layer, Shadows = SmallFarShadows.NativeShadows(renderer), Receive = renderer.receiveShadows,
                        Probes = renderer.lightProbeUsage, Reflections = renderer.reflectionProbeUsage, Mask = renderer.renderingLayerMask, Motion = renderer.motionVectorGenerationMode };
                    member.ToWorld[part.Renderer] = renderer.localToWorldMatrix;
                    // The engine gives shaders the 3D affine inverse of the object's matrix. The renderer's own inverse differs
                    // from it in the last bits, enough to move the edges of stalks that the vegetation shader's wind bends in
                    // the object's space.
                    Matrix4x4.Inverse3DAffine(member.ToWorld[part.Renderer], ref member.ToLocal[part.Renderer]);
                    var bounds = member.RendererBounds[part.Renderer] = renderer.bounds;
                    if (any) member.Bounds.Encapsulate(bounds); else { member.Bounds = bounds; any = true; }
                }
                style.Material = part.Material.GetInstanceID();
                style.Crop = crop;
                style.Small = member.View && style.Shadows == ShadowCastingMode.On && SmallFarShadows.Enabled && SmallFarShadows.IsSmall(member.Renderers[part.Renderer]);
                if (style.Small)
                {
                    if (member.Radius == null) member.Radius = new float[member.Renderers.Length];
                    member.Radius[part.Renderer] = SmallFarShadows.Radius(member.Renderers[part.Renderer]);
                }
                part.Key = style;
                member.Merged.Add(part);
            }
            for (int first = 0; first < member.Merged.Count; first++)
            {
                var key = member.Merged[first].Key;
                bool assigned = false;
                for (int earlier = 0; earlier < first && !assigned; earlier++) assigned = member.Merged[earlier].Key.Equals(key);
                if (assigned) continue;
                int needed = 0, vertices = 0;
                for (int index = first; index < member.Merged.Count; index++)
                {
                    var part = member.Merged[index];
                    if (!part.Key.Equals(key)) continue;
                    needed++;
                    vertices += part.Source.Ranges[part.Submesh].count;
                }
                if (!cell.ByKey.TryGetValue(key, out var candidates)) cell.ByKey.Add(key, candidates = new List<Batch>());
                Batch batch = null;
                foreach (var candidate in candidates)
                    if (candidate.Count + needed <= SlotLimit && (candidate.Count == 0 || candidate.Vertices + vertices <= VertexLimit)) { batch = candidate; break; }
                if (batch == null)
                {
                    var material = member.Merged[first].Material;
                    string source = SupportedShader(material);
                    candidates.Add(batch = new Batch { Key = key, Material = material, Cell = cell, Vegetation = source == vegetationShader || source == standardShader });
                    cell.Batches.Add(batch);
                }
                batch.Members.Add(member);
                batch.Count += needed;
                batch.Vertices += vertices;
                MarkBatch(batch);
                member.Batches[batch] = (needed, vertices);
            }
        }

        private static void StartBuild(Batch batch)
        {
            var slots = new (Member member, Part part, int version)[batch.Count];
            int count = 0;
            foreach (var member in batch.Members)
                foreach (var part in member.Merged)
                    if (part.Key.Equals(batch.Key)) slots[count++] = (member, part, member.Version);
            if (count != slots.Length) throw new InvalidOperationException("A merged mesh's slot count disagrees with its members.");
            uniqueSources.Clear();
            sourceIndices.Clear();
            var order = new int[count];
            var sourceOf = new int[count];
            var levels = new int[count];
            for (int slot = 0; slot < count; slot++)
            {
                order[slot] = slot;
                var source = slots[slot].part.Source;
                if (!sourceIndices.TryGetValue(source, out sourceOf[slot])) { sourceIndices.Add(source, sourceOf[slot] = uniqueSources.Count); uniqueSources.Add(source.Readable); }
                levels[slot] = LevelOrder(slots[slot].part);
            }
            // Inputs are grouped by the detail levels their parts draw at, so the slots drawing at any moment lie in a short
            // span of the indices, and then by source mesh, so the job reads each once.
            Array.Sort(order, (a, b) => levels[a] != levels[b] ? levels[a].CompareTo(levels[b]) : sourceOf[a] != sourceOf[b] ? sourceOf[a].CompareTo(sourceOf[b]) : a.CompareTo(b));
            var inputs = new NativeArray<Input>(count, Allocator.Persistent);
            var indexFirst = new int[count];
            var indexCounts = new int[count];
            int vertexCount = 0, indexCount = 0;
            var bounds = new Bounds();
            for (int at = 0; at < order.Length; at++)
            {
                int slot = order[at];
                var (member, part, _) = slots[slot];
                var range = part.Source.Ranges[part.Submesh];
                vertexCount += range.count;
                indexFirst[slot] = indexCount;
                indexCounts[slot] = (int)part.Source.Readable.GetIndexCount(part.Submesh);
                indexCount += indexCounts[slot];
                inputs[at] = new Input { Source = sourceOf[slot], Submesh = part.Submesh, Slot = slot, First = range.first, Count = range.count };
                var rendererBounds = member.RendererBounds[part.Renderer];
                if (at == 0) bounds = rendererBounds; else bounds.Encapsulate(rendererBounds);
            }
            var output = Mesh.AllocateWritableMeshData(1);
            var data = output[0];
            data.SetVertexBufferParams(vertexCount, batch.Vegetation ? vegetationLayout : layout);
            bool shortIndices = vertexCount <= ushort.MaxValue;
            // With the compaction kernel the whole mesh is followed by a drawn region, which starts at an even index and holds an
            // even count, since the kernel reads and writes a 16-bit mesh's indices in pairs. Shadowed lights' copies of their
            // slots follow it, in what the drawn slots leave of the region and as much again: inside a base nearly every slot
            // draws, and with a quarter more most lights there had no room.
            int drawnFirst = compactIndices ? (indexCount + 1) & ~1 : 0, capacity = compactIndices ? (DrawnCapacity(slots, indexCounts) + 1) & ~1 : 0;
            int total = drawnFirst + 2 * capacity;
            data.SetIndexBufferParams(total, shortIndices ? IndexFormat.UInt16 : IndexFormat.UInt32);
            // The renderer draws the first submesh, the slots drawing now, empty until the first compaction; the sky depth render
            // draws the second, the whole mesh, since its slots follow that camera's own detail levels. Shadowed lights draw the
            // rest, their copies after the drawn region.
            data.subMeshCount = FirstLightSubmesh + LightRanges;
            var whole = new SubMeshDescriptor(0, indexCount) { firstVertex = 0, vertexCount = vertexCount, bounds = bounds };
            var empty = new SubMeshDescriptor(drawnFirst, 0) { firstVertex = 0, vertexCount = vertexCount, bounds = bounds };
            data.SetSubMesh(0, compactIndices ? empty : whole, quiet);
            data.SetSubMesh(1, whole, quiet);
            for (int submesh = FirstLightSubmesh; submesh < data.subMeshCount; submesh++) data.SetSubMesh(submesh, empty, quiet);
            var build = new Build { Batch = batch, Slots = slots, Bounds = bounds, Output = output, Inputs = inputs, IndexFirst = indexFirst, IndexCount = indexCounts,
                Order = order, Total = total, DrawnFirst = drawnFirst, Capacity = capacity, Vertices = vertexCount, Sources = Mesh.AcquireReadOnlyMeshData(uniqueSources),
                Result = new NativeArray<int>(1, Allocator.Persistent) };
            build.Handle = new BuildJob { Sources = build.Sources, Inputs = inputs, Output = output, Result = build.Result, Vegetation = batch.Vegetation, ShortIndices = shortIndices }.Schedule();
            batch.Build = build;
            builds.Add(build);
        }

        // The most indices a mesh's slots can draw at once: each member's parts outside detail groups, and in each of its
        // groups the parts of the level holding the most. A member's slots are next to each other.
        private static int DrawnCapacity((Member member, Part part, int version)[] slots, int[] indexCounts)
        {
            int capacity = 0;
            for (int start = 0, end; start < slots.Length; start = end)
            {
                var member = slots[start].member;
                for (end = start; end < slots.Length && slots[end].member == member; end++)
                    if (slots[end].part.Detail < 0) capacity += indexCounts[end];
                for (int slot = start; slot < end; slot++)
                {
                    int detail = slots[slot].part.Detail;
                    bool first = detail >= 0;
                    for (int earlier = start; earlier < slot && first; earlier++) first = slots[earlier].part.Detail != detail;
                    if (!first) continue;
                    int largest = 0;
                    for (int level = 0; level < 32; level++)
                    {
                        int sum = 0;
                        for (int other = slot; other < end; other++)
                            if (slots[other].part.Detail == detail && (slots[other].part.Levels & 1 << level) != 0) sum += indexCounts[other];
                        largest = Math.Max(largest, sum);
                    }
                    capacity += largest;
                }
            }
            return capacity;
        }

        private static void Discard(Build build)
        {
            build.Handle.Complete();
            build.Sources.Dispose();
            build.Inputs.Dispose();
            build.Result.Dispose();
            build.Output.Dispose();
            build.Batch.Build = null;
        }

        private static void CreateBatch(Batch batch)
        {
            var native = batch.Material;
            var key = batch.Key;
            if (!materials.TryGetValue(native, out var material))
            {
                material = new Material(native) { name = native.name + " spatial buildings", shader = derived[SupportedShader(native)], enableInstancing = false,
                    hideFlags = HideFlags.HideAndDontSave };
                material.EnableKeyword("TENEMO_PIECES");
                materials.Add(native, material);
            }
            batch.Object = new GameObject("World Performance spatial buildings") { hideFlags = HideFlags.HideAndDontSave, layer = key.Layer };
            batch.Filter = batch.Object.AddComponent<MeshFilter>();
            batch.Renderer = batch.Object.AddComponent<MeshRenderer>();
            batch.Renderer.sharedMaterial = material;
            batch.Renderer.shadowCastingMode = key.Shadows;
            batch.Renderer.receiveShadows = key.Receive;
            batch.Renderer.lightProbeUsage = key.Probes;
            batch.Renderer.reflectionProbeUsage = key.Reflections;
            batch.Renderer.renderingLayerMask = key.Mask;
            batch.Renderer.motionVectorGenerationMode = key.Motion;
            batch.Transforms = new GraphicsBuffer(GraphicsBuffer.Target.Constant, SlotLimit * 2, 64);
            if (SupportedShader(native) == pieceShader) batch.Appearances = new GraphicsBuffer(GraphicsBuffer.Target.Constant, SlotLimit, Marshal.SizeOf<Appearance>());
            batch.Context = new GraphicsBuffer(GraphicsBuffer.Target.Constant, 1, 16);
            batch.Context.SetData(new[] { Vector4.zero });
            var properties = new MaterialPropertyBlock();
            properties.SetConstantBuffer(transformsId, batch.Transforms, 0, SlotLimit * 128);
            if (batch.Appearances != null) properties.SetConstantBuffer(appearanceId, batch.Appearances, 0, SlotLimit * Marshal.SizeOf<Appearance>());
            else properties.SetConstantBuffer(probeStateId, probeState, 0, SlotLimit * 7 * 16);
            properties.SetConstantBuffer(contextId, batch.Context, 0, 16);
            batch.Renderer.SetPropertyBlock(properties);
            batch.Properties = properties;
            if (key.Shadows == ShadowCastingMode.On && key.Small)
            {
                batch.TwinTransforms = new GraphicsBuffer(GraphicsBuffer.Target.Constant, SlotLimit * 2, 64);
                batch.TwinData = new Matrix4x4[SlotLimit * 2];
                batch.SlotFar = new bool[SlotLimit];
                batch.TwinProperties = new MaterialPropertyBlock();
                batch.TwinProperties.SetConstantBuffer(transformsId, batch.TwinTransforms, 0, SlotLimit * 128);
                if (batch.Appearances != null) batch.TwinProperties.SetConstantBuffer(appearanceId, batch.Appearances, 0, SlotLimit * Marshal.SizeOf<Appearance>());
                else batch.TwinProperties.SetConstantBuffer(probeStateId, probeState, 0, SlotLimit * 7 * 16);
                batch.TwinProperties.SetConstantBuffer(contextId, batch.Context, 0, 16);
            }
            if (key.Shadows == ShadowCastingMode.On) CreateTwin(batch, material, batch.TwinProperties ?? properties);
            // The sky depth render reads the values the game's replacement shader takes from the native material.
            batch.SkyProperties = new MaterialPropertyBlock();
            var texture = native.GetTexture(mainTextureId);
            batch.SkyProperties.SetTexture(mainTextureId, texture ? texture : Texture2D.whiteTexture);
            var scale = native.GetTextureScale(mainTextureId);
            var offset = native.GetTextureOffset(mainTextureId);
            batch.SkyProperties.SetVector(mainTextureScaleId, new Vector4(scale.x, scale.y, offset.x, offset.y));
            // Rock materials have no cutoff.
            batch.SkyProperties.SetFloat(cutoffId, native.HasFloat(cutoffId) ? native.GetFloat(cutoffId) : 0f);
        }

        private static void DestroyBatch(Batch batch)
        {
            if (batch.Build != null) { builds.Remove(batch.Build); Discard(batch.Build); }
            LightsLeave(batch);
            // The twin goes with the object.
            if (batch.Object) UnityEngine.Object.Destroy(batch.Object);
            batch.IndexBuffer?.Dispose();
            batch.IndexBuffer = null;
            if (batch.Mesh) UnityEngine.Object.Destroy(batch.Mesh);
            mergedBytes -= batch.Bytes;
            batch.Bytes = 0;
            batch.Transforms?.Dispose();
            batch.TwinTransforms?.Dispose();
            batch.TwinTransforms = null;
            batch.TwinProperties = null;
            batch.Appearances?.Dispose();
            batch.Context?.Dispose();
            batch.SkyTransforms?.Dispose();
            batch.Object = null;
            batch.Twin = null;
            batch.TwinFilter = null;
            batch.Mesh = null;
            batch.Transforms = batch.Appearances = batch.Context = batch.SkyTransforms = null;
            batch.Live = Array.Empty<(Member, Part, int)>();
            var cell = batch.Cell;
            cell.Batches.Remove(batch);
            if (cell.ByKey.TryGetValue(batch.Key, out var candidates)) { candidates.Remove(batch); if (candidates.Count == 0) cell.ByKey.Remove(batch.Key); }
        }

        // Unreadable source meshes are copied from the GPU without stalling the frame.
        private static void Request(Source source)
        {
            if (source.Requested) return;
            source.Requested = true;
            var mesh = source.Mesh;
            if (!mesh) { source.Failed = true; return; }
            if (mesh.isReadable) { source.Ranges = Ranges(mesh); source.Readable = mesh; return; }
            if (!SystemInfo.supportsAsyncGPUReadback) { source.Failed = true; return; }
            source.Streams = new NativeArray<byte>[mesh.vertexBufferCount];
            source.Waiting = mesh.vertexBufferCount + 1;
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++)
            {
                int index = stream;
                var buffer = mesh.GetVertexBuffer(index);
                AsyncGPUReadback.Request(buffer, request => { buffer.Dispose(); Received(source, request, index); });
            }
            var indexBuffer = mesh.GetIndexBuffer();
            AsyncGPUReadback.Request(indexBuffer, request => { indexBuffer.Dispose(); Received(source, request, -1); });
        }

        private static void Received(Source source, AsyncGPUReadbackRequest request, int stream)
        {
            if (!Running || !sources.TryGetValue(source.Mesh, out var current) || current != source) return;
            using (new Timed(Readbacks))
                try
                {
                    if (request.hasError) { source.Failed = true; ReleaseData(source); return; }
                    if (source.Failed) return;
                    var copy = new NativeArray<byte>(request.GetData<byte>(), Allocator.Persistent);
                    if (stream < 0) source.Indices = copy; else source.Streams[stream] = copy;
                    if (--source.Waiting == 0) received.Enqueue(source);
                }
                catch (Exception error) { Fail(error); }
        }

        // The vertices each submesh's indices reach, once per source mesh.
        private static (int first, int count)[] Ranges(Mesh mesh)
        {
            var result = new (int, int)[mesh.subMeshCount];
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                mesh.GetIndices(indexList, submesh, true);
                if (indexList.Count == 0) continue;
                int first = int.MaxValue, last = int.MinValue;
                foreach (int index in indexList) { if (index < first) first = index; if (index > last) last = index; }
                result[submesh] = (first, last - first + 1);
            }
            indexList.Clear();
            return result;
        }

        private static void ReleaseData(Source source)
        {
            if (source.Streams != null) foreach (var stream in source.Streams) if (stream.IsCreated) stream.Dispose();
            if (source.Indices.IsCreated) source.Indices.Dispose();
            source.Streams = null;
            source.Indices = default;
        }

        private static void Release(Source source)
        {
            ReleaseData(source);
            if (source.Readable && source.Readable != source.Mesh) UnityEngine.Object.Destroy(source.Readable);
            source.Readable = null;
        }
    }
}
