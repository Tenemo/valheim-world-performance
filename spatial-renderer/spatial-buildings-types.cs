using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // The vertex, slot and member records the spatial renderer keeps, and the merged meshes and cells that hold them.
    internal static partial class SpatialBuildings
    {
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
            // Whether no threshold is above the one before it, as the engine's levels are, so a level holds between two of them.
            internal bool Descending;
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
            // For a member with one group of descending thresholds, that group's level as last measured: its reference point and
            // scale, the bounds of the screen share within which the level holds, the distances of its changes and whether it
            // crossfades. While the member keeps its check and stays owned and drawing merged, none of these change.
            internal bool Banded, BandFades;
            internal Vector3 BandReference;
            internal float BandScale, BandLower, BandUpper, BandNear, BandFar;
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
            internal bool SkyHidden;
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
            internal bool Dirty, Flushing;
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
    }
}
