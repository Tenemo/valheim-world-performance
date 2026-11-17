using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
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
            // A station's update every frame, CustomUpdate, is followed where it switches its in-use object; see InUseSwitches.
            (typeof(CraftingStation), new[] { "m_areaMarker", "m_inUseObject", "m_haveFireObject" }, new[] { "ShowAreaMarker", "HideMarker", "CheckFire" }),
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
        private static readonly Dictionary<WearNTear, Member> pieces = new Dictionary<WearNTear, Member>(Identity<WearNTear>.Instance);
        private static readonly Dictionary<ZNetView, Member> natural = new Dictionary<ZNetView, Member>(Identity<ZNetView>.Instance);
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
        private static readonly CheckHeap checks = new CheckHeap();
        // Checks that wait for the camera to go far enough from the anchor: a camera turning around the player travels far
        // without going far, and a member's distance to a group cannot change by more than the camera's distance from where
        // it was checked, which is at most its distance from the anchor plus the anchor's from there.
        private static readonly CheckHeap parked = new CheckHeap();
        private static Vector3 anchor;
        private static bool anchored;
        // The camera sets the anchor again once it is this far from it, and the parked checks are then due by the new one.
        private const float AnchorReach = 16f;
        // The isolated client can turn parking off to compare.
        internal static bool ParkedChecks = true;
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
        // The meshes with twins casting small parts' shadows, which the shadow distance checks look through.
        private static readonly List<Batch> twins = new List<Batch>();
        private static readonly AccessTools.FieldRef<WearNTear, ZNetView> wearViews = AccessTools.FieldRefAccess<WearNTear, ZNetView>("m_nview");
        // Whether the stations' update reports the switch of its in-use object, rather than being checked after every call.
        private static bool inUseFollowed;
        private static readonly int slotCompactionId = Shader.PropertyToID("_SlotCompaction"), slotRunsId = Shader.PropertyToID("_SlotRuns"),
            mergedIndicesId = Shader.PropertyToID("_MergedIndices");
        private static bool flushPending, skyDepth, linear;
        // The merged meshes with slots to upload in the next flush.
        private static readonly List<Batch> flushing = new List<Batch>();
        // The isolated client can turn this off to look through every merged mesh in a flush, as before, to compare.
        internal static bool ListedFlush = true;
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
                var update = AccessTools.Method(typeof(CraftingStation), nameof(CraftingStation.CustomUpdate));
                inUseFollowed = false;
                patches.Patch(update, transpiler: new HarmonyMethod(typeof(SpatialBuildings), nameof(InUseSwitches)));
                // A changed update is checked after every call instead, as the other visual methods are.
                if (!inUseFollowed) patches.Patch(update, postfix: switched);
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
            twins.Clear();
            foreach (var batch in flushing) batch.Flushing = false;
            flushing.Clear();
            skyPending.Clear();
            skyScanned = false;
            foreach (var member in Members()) if (member.Owned && !member.Away) ShowNative(member);
            cells.Clear();
            active.Clear();
            checks.Clear();
            parked.Clear();
            anchored = false;
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
                    if (ListedFlush) foreach (var batch in flushing) Upload(batch);
                    else foreach (var cell in cells.Values) foreach (var batch in cell.Batches) Upload(batch);
                    foreach (var batch in flushing) batch.Flushing = false;
                    flushing.Clear();
                    if (compactions.Count > 0) Compact();
                }
                flushPending = false;
            }
            catch (Exception error) { Fail(error); }
        }

        // Queues a merged mesh whose slot ranges changed for the next flush.
        private static void Flush(Batch batch)
        {
            if (batch.Flushing) return;
            batch.Flushing = true;
            flushing.Add(batch);
        }

        // Uploads a merged mesh's changed slots; a mesh destroyed since has no buffers left and uploads nothing.
        private static void Upload(Batch batch)
        {
            if (batch.TransformTo >= 0 && batch.Transforms != null)
            {
                batch.Transforms.SetData(batch.TransformData, batch.TransformFrom * 2, batch.TransformFrom * 2, (batch.TransformTo - batch.TransformFrom + 1) * 2);
                UploadTwin(batch, batch.TransformFrom, batch.TransformTo);
                Drawn(batch, batch.TransformFrom, batch.TransformTo);
            }
            if (batch.AppearanceTo >= 0 && batch.Appearances != null)
                batch.Appearances.SetData(batch.AppearanceData, batch.AppearanceFrom, batch.AppearanceFrom, batch.AppearanceTo - batch.AppearanceFrom + 1);
            batch.TransformFrom = batch.AppearanceFrom = int.MaxValue;
            batch.TransformTo = batch.AppearanceTo = -1;
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

        // The main camera and its position, which the plugin reads once for this and small far shadows.
        internal static void LateUpdate(Camera camera, Vector3 position)
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
                // Steps with nothing waiting are passed over, untimed.
                if (camera)
                {
                    if (builds.Count > 0) using (new Timed(Applies)) Complete(position);
                    if (owning.Count > 0 || finishing.Count > 0) using (new Timed(Arrivals)) { OwnCompleted(position); CheckFinishing(position); }
                    if (checks.Count + parked.Count > (pieces.Count + natural.Count) * 2 + 64) using (new Timed(DetailChecks)) DropVoidChecks();
                }
                if (removing.Count > 0) using (new Timed(Removals)) Forget();
                using (new Timed(SkyPreparation)) PrepareSky();
                if (received.Count > 0) using (new Timed(Readbacks)) CopyReadables();
                if (camera && active.Count > 0) using (new Timed(Planning)) Schedule(position);
                if (requests.Count > 0) using (new Timed(Requests)) while (requests.Count > 0 && Open()) Request(requests.Dequeue());
                if (pending.Count > 0 || pendingNatural.Count > 0) using (new Timed(Registration)) Register();
                using (new Timed(ShadowedLights)) UpdateLights();
                lateFrame = Time.frameCount;
                lateTicks = frameTicks;
            }
            catch (Exception error) { Fail(error); }
        }
    }
}
