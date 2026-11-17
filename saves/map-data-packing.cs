using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using HarmonyLib;

namespace WorldPerformance
{
    // Every character save packs the map: both exploration layers, 4.2 million flags each, go into an 8.4 MB package one byte
    // a flag, which is then gzipped, 35–73 ms on the main thread for Tenemo's map in the hidden client and 43 MB of garbage.
    // The server asks for a save every ten minutes and the client's own save timer at about the same moment. The mod keeps the
    // last packed map with a snapshot of everything it was packed from: the texture size, both layers' bits and the rest of the
    // package as the game writes it, the saved pins in order and whether the player's position is public. While all of that is
    // unchanged a save gets a copy of the kept bytes. When it changed, a save the server or the save timer asks for in play
    // takes the snapshot in the frame it is asked for, a worker packs it in kept buffers, and the game's own save runs once the
    // worker is done, with those bytes, in the order the saves were asked for. Any other save, a save in a natural pause and the
    // game shutting down first finish the waiting saves, then save as the game does. Saves pack natively until the worker has
    // packed the first map the game packed itself again and matched its bytes.
    internal static class MapDataPacking
    {
        // The map version the game's packing writes first.
        private const int MapVersion = 8;
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".map-data-packing");
        private static readonly AccessTools.FieldRef<Minimap, BitArray> exploredOf = AccessTools.FieldRefAccess<Minimap, BitArray>("m_explored");
        private static readonly AccessTools.FieldRef<Minimap, BitArray> exploredOthersOf = AccessTools.FieldRefAccess<Minimap, BitArray>("m_exploredOthers");
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> pinsOf = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
        // Saves asked for in play, in order; the first one's map packs on the worker.
        private static readonly Queue<Save> waiting = new Queue<Save>();
        // The last packed map with the snapshot it was packed from, and a spare snapshot the next one is taken into.
        private static Snapshot kept, spare;
        private static byte[] keptData;
        // The waiting save the game is running now.
        private static Save finishing;
        // The game's save timer is asking for a save.
        private static bool timerSave;
        // 0 until the worker has packed one of the game's own maps again, then 1 when the bytes matched and -1 when not.
        private static volatile int verified;
        private static bool verifying, reported;
        // The worker's buffers; it packs one map at a time.
        private static readonly object packing = new object();
        private static readonly byte[] stage = new byte[65536];
        private static readonly MemoryStream compressed = new MemoryStream();
        internal static bool Running { get; private set; }
        // Off packs every save natively; the isolated client switches it for paired measurements.
        internal static bool Reusing { get; set; } = true;
        // Off saves at once, packing on the main thread; the isolated client switches it for paired measurements.
        internal static bool Deferring { get; set; } = true;
        internal static int Verified => verified;
        internal static int Waiting => waiting.Count;
        internal static long Reused { get; private set; }
        internal static long Packed { get; private set; }
        internal static long Deferred { get; private set; }
        internal static long WorkerPacked { get; private set; }
        internal static long Failed { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            var game = typeof(Game);
            patches.Patch(AccessTools.Method(typeof(Minimap), "GetMapData"), prefix: new HarmonyMethod(typeof(MapDataPacking), nameof(Pack)),
                postfix: new HarmonyMethod(typeof(MapDataPacking), nameof(Keep)));
            patches.Patch(AccessTools.Method(game, nameof(Game.SavePlayerProfile)), prefix: new HarmonyMethod(typeof(MapDataPacking), nameof(Defer)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(game, "UpdateSaving"), prefix: new HarmonyMethod(typeof(MapDataPacking), nameof(TimerAsking)),
                finalizer: new HarmonyMethod(typeof(MapDataPacking), nameof(TimerDone)));
            // After ServerCharacters' prefix, which makes the shutdown's character uploads synchronous.
            patches.Patch(AccessTools.Method(game, "Shutdown"), prefix: new HarmonyMethod(typeof(MapDataPacking), nameof(FinishWaiting)) { priority = Priority.Last });
        }

        internal static void Stop()
        {
            if (!Running) return;
            FinishWaiting();
            Running = false;
            patches.UnpatchSelf();
        }

        private static void TimerAsking() => timerSave = true;
        private static Exception TimerDone(Exception __exception)
        {
            timerSave = false;
            return __exception;
        }

        // A save the server or the save timer asks for in play waits for the worker when the map changed since it was last
        // packed; every other save first finishes the waiting ones and then runs as the game's own.
        private static bool Defer(Game __instance, bool setLogoutPoint, bool isFromRpc)
        {
            if (finishing != null) return true;
            var minimap = Minimap.instance;
            if (!Deferring || verified != 1 || !isFromRpc && !timerSave || !minimap || !Player.m_localPlayer || !ZNet.instance || DeferredAssetUnload.NaturalPause() ||
                SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveCharacter) || isFromRpc && ZNet.instance.HardSaveBlock())
            {
                FinishWaiting();
                return true;
            }
            var snapshot = Snapshot.Take(minimap, Spare());
            if (snapshot == null)
            {
                FinishWaiting();
                return true;
            }
            // An unchanged map with nothing waiting saves at once with the kept bytes.
            if (waiting.Count == 0 && Reusing && keptData != null && snapshot.Same(kept))
            {
                spare = snapshot;
                return true;
            }
            waiting.Enqueue(new Save(setLogoutPoint, isFromRpc, snapshot));
            // The game resets its save timer as a save starts.
            __instance.m_saveTimer = 0f;
            Deferred++;
            StartFirst();
            return false;
        }

        // Finishes the first waiting save once its map is packed, one save a frame.
        internal static void LateUpdate()
        {
            if (verified == -1 && !reported)
            {
                reported = true;
                Plugin.Fallback("Saves pack their maps on the main thread: the worker's bytes for the game's own map differed from the game's.");
            }
            if (waiting.Count == 0) return;
            StartFirst();
            if (!waiting.Peek().Done.IsSet) return;
            Finish(waiting.Dequeue());
            StartFirst();
        }

        // Finishes every waiting save in order, waiting for the worker where it is packing.
        internal static void FinishWaiting()
        {
            while (waiting.Count > 0)
            {
                var save = waiting.Dequeue();
                if (save.Started && !save.Done.Wait(TimeSpan.FromSeconds(30))) save.Failed = true;
                Finish(save);
            }
        }

        private static Snapshot Spare()
        {
            var taken = spare;
            spare = null;
            return taken;
        }

        // Gives the first waiting save its map: a copy of the kept bytes when its snapshot equals the kept one, else a pack on the
        // worker.
        private static void StartFirst()
        {
            if (waiting.Count == 0) return;
            var save = waiting.Peek();
            if (save.Started) return;
            save.Started = true;
            if (Reusing && keptData != null && save.Snapshot.Same(kept))
            {
                save.Data = (byte[])keptData.Clone();
                save.Done.Set();
                return;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { save.Data = Encode(save.Snapshot); }
                catch (Exception) { save.Failed = true; }
                finally { save.Done.Set(); }
            });
        }

        // Runs the game's own save for a waiting save; its map packing takes the worker's bytes, or packs natively without them.
        private static void Finish(Save save)
        {
            var game = Game.instance;
            if (save.Failed)
            {
                Failed++;
                Plugin.Log.LogWarning("A save's map did not pack on the worker, so it packs on the main thread.");
            }
            try
            {
                if (!game) return;
                finishing = save;
                // The timer counts from when the save was asked for, as it does natively.
                float timer = game.m_saveTimer;
                try { game.SavePlayerProfile(save.SetLogoutPoint, save.IsFromRpc); }
                finally { game.m_saveTimer = timer; }
            }
            catch (Exception error) { Plugin.Log.LogError(error); }
            finally { finishing = null; }
        }

        // A finishing save takes its worker's bytes; any other packing reuses the kept bytes while the snapshot is unchanged and
        // otherwise leaves the packing to the game, keeping its bytes with the snapshot.
        private static bool Pack(Minimap __instance, ref byte[] __result, out object __state)
        {
            __state = null;
            var save = finishing;
            if (save != null && !save.Failed && save.Data != null)
            {
                __result = save.Data;
                save.Data = null;
                if (!save.Snapshot.Same(kept))
                {
                    spare = kept;
                    kept = save.Snapshot;
                    keptData = (byte[])__result.Clone();
                    WorkerPacked++;
                    ZLog.Log("Minimap: compressed mapData " + kept.Size + " => " + keptData.Length + " bytes");
                }
                else Reused++;
                return false;
            }
            var snapshot = Snapshot.Take(__instance, Spare());
            if (snapshot == null) return true;
            if (Reusing && keptData != null && snapshot.Same(kept))
            {
                spare = snapshot;
                __result = (byte[])keptData.Clone();
                Reused++;
                return false;
            }
            __state = snapshot;
            return true;
        }

        // Keeps the game's bytes with the snapshot they were packed from, and checks the worker against the first of them.
        private static void Keep(byte[] __result, object __state)
        {
            if (!(__state is Snapshot snapshot) || __result == null) return;
            spare = kept;
            kept = snapshot;
            keptData = (byte[])__result.Clone();
            Packed++;
            if (verified != 0 || verifying || !Deferring) return;
            verifying = true;
            var copy = kept.Copy();
            var native = keptData;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int result = -1;
                try
                {
                    var data = Encode(copy);
                    if (data.Length == native.Length)
                    {
                        result = 1;
                        for (int index = 0; index < data.Length; index++) if (data[index] != native[index]) { result = -1; break; }
                    }
                }
                catch (Exception) { }
                verified = result;
            });
        }

        // What the game's packing writes for the snapshot: the map version, then the size and bytes of the gzipped package of
        // the texture size, every flag of both layers as a byte, one layer after the other, and the rest; on the worker, with
        // the compression the game uses.
        private static byte[] Encode(Snapshot snapshot)
        {
            lock (packing)
            {
                compressed.SetLength(0);
                using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true))
                {
                    int filled = Write(stage, 0, snapshot.TextureSize);
                    foreach (var layer in new[] { snapshot.Explored, snapshot.ExploredOthers })
                        for (int flag = 0; flag < snapshot.Flags; flag++)
                        {
                            stage[filled++] = (byte)((layer[flag >> 5] >> (flag & 31)) & 1);
                            if (filled == stage.Length)
                            {
                                gzip.Write(stage, 0, filled);
                                filled = 0;
                            }
                        }
                    gzip.Write(stage, 0, filled);
                    gzip.Write(snapshot.Rest, 0, snapshot.Rest.Length);
                }
                int length = (int)compressed.Length;
                var data = new byte[8 + length];
                Write(data, Write(data, 0, MapVersion), length);
                Buffer.BlockCopy(compressed.GetBuffer(), 0, data, 8, length);
                return data;
            }
        }

        // Writes an int as the game's packages do, least significant byte first, and returns the position after it.
        private static int Write(byte[] buffer, int position, int value)
        {
            buffer[position] = (byte)value;
            buffer[position + 1] = (byte)(value >> 8);
            buffer[position + 2] = (byte)(value >> 16);
            buffer[position + 3] = (byte)(value >> 24);
            return position + 4;
        }

        // A save asked for in play that waits for its map.
        private sealed class Save
        {
            internal readonly bool SetLogoutPoint, IsFromRpc;
            internal readonly Snapshot Snapshot;
            internal readonly ManualResetEventSlim Done = new ManualResetEventSlim();
            internal byte[] Data;
            internal bool Started;
            internal volatile bool Failed;

            internal Save(bool setLogoutPoint, bool isFromRpc, Snapshot snapshot)
            {
                SetLogoutPoint = setLogoutPoint;
                IsFromRpc = isFromRpc;
                Snapshot = snapshot;
            }
        }

        // What the game packs a map from: the texture size, both exploration layers' bits in whole words and the rest of the
        // package as the game writes it.
        private sealed class Snapshot
        {
            internal int TextureSize, Flags;
            internal int[] Explored = new int[0], ExploredOthers = new int[0];
            internal byte[] Rest = new byte[0];

            internal int Size => 4 + 2 * Flags + Rest.Length;

            // The minimap's values, taken into the given snapshot's arrays where they fit, or null where the game's own packing
            // fails on them, which then stays with the game's code.
            internal static Snapshot Take(Minimap minimap, Snapshot into)
            {
                var explored = exploredOf(minimap);
                var exploredOthers = exploredOthersOf(minimap);
                var pins = pinsOf(minimap);
                if (explored == null || exploredOthers == null || pins == null || exploredOthers.Length != explored.Length || !ZNet.instance) return null;
                try
                {
                    var rest = new ZPackage();
                    int saved = 0;
                    foreach (var pin in pins) if (pin.m_save) saved++;
                    rest.Write(saved);
                    foreach (var pin in pins)
                    {
                        if (!pin.m_save) continue;
                        rest.Write(pin.m_name);
                        rest.Write(pin.m_pos);
                        rest.Write((int)pin.m_type);
                        rest.Write(pin.m_checked);
                        rest.Write(pin.m_ownerID);
                        rest.Write(pin.m_author.ToString());
                    }
                    rest.Write(ZNet.instance.IsReferencePositionPublic());
                    var snapshot = into ?? new Snapshot();
                    int words = (explored.Length + 31) / 32;
                    if (snapshot.Explored.Length != words)
                    {
                        snapshot.Explored = new int[words];
                        snapshot.ExploredOthers = new int[words];
                    }
                    explored.CopyTo(snapshot.Explored, 0);
                    exploredOthers.CopyTo(snapshot.ExploredOthers, 0);
                    snapshot.TextureSize = minimap.m_textureSize;
                    snapshot.Flags = explored.Length;
                    snapshot.Rest = rest.GetArray();
                    return snapshot;
                }
                catch (Exception) { return null; }
            }

            internal bool Same(Snapshot other)
            {
                if (other == null || TextureSize != other.TextureSize || Flags != other.Flags || Explored.Length != other.Explored.Length || Rest.Length != other.Rest.Length) return false;
                for (int word = 0; word < Explored.Length; word++)
                    if (Explored[word] != other.Explored[word] || ExploredOthers[word] != other.ExploredOthers[word]) return false;
                for (int index = 0; index < Rest.Length; index++)
                    if (Rest[index] != other.Rest[index]) return false;
                return true;
            }

            internal Snapshot Copy() => new Snapshot { TextureSize = TextureSize, Flags = Flags, Explored = (int[])Explored.Clone(),
                ExploredOthers = (int[])ExploredOthers.Clone(), Rest = (byte[])Rest.Clone() };
        }
    }
}
