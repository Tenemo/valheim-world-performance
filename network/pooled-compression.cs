using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace WorldPerformance
{
    // Better Networking compresses every Steam send between two players who both run it and decompresses every receive.
    // A send copies the package, compresses it into a new buffer of the compression bound, copies the result into a new array,
    // builds the frame in another and wraps it in a new package, about four times the packet in garbage: 24 KB a send, 0.5 MB a
    // second at the base and 1 MB while traveling. A receive copies the frame, then the payload, then the decompressed bytes
    // twice. The mod patches Better Networking's own two methods to do the same work from the package's buffer into buffers it
    // keeps, with compression contexts of its own at the same level and with the same dictionary, so the frames on the wire and
    // the payloads read stay byte for byte Better Networking's. Better Networking's own method takes the rest: a package already
    // framed, an unframed packet, a frame before the start message, an unknown frame type, a decoding error, a packet over the
    // size limit and any thread but the main one. Better Networking is a soft dependency: without it, in any other version, with
    // its compression off or with its per-packet logging on, nothing is patched and it works as before.
    internal static class PooledCompression
    {
        internal const string BetterNetworking = "DIT.BetterNetworking10";
        private const string NotInstalled = "Better Networking is not installed";
        private static readonly System.Version[] Checked = { new System.Version(1, 2, 0), new System.Version(1, 3, 1) };
        // Larger packets go to Better Networking's own methods, so the kept buffers stay within twice this.
        private const int Largest = 1 << 20;
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".pooled-compression");
        private static readonly AccessTools.FieldRef<ZPackage, MemoryStream> streamOf = AccessTools.FieldRefAccess<ZPackage, MemoryStream>("m_stream");
        private static readonly MethodInfo gameSend = AccessTools.DeclaredMethod(typeof(ZSteamSocket), nameof(ZSteamSocket.Send), new[] { typeof(ZPackage) });
        private static readonly MethodInfo gameReceive = AccessTools.DeclaredMethod(typeof(ZSteamSocket), nameof(ZSteamSocket.Recv));
        private static MethodInfo sendPrefix, receivePostfix;
        private static IDictionary peers;
        private static AccessTools.FieldRef<object, bool> sending, receiving, warnedUnframed;
        private static object compressor, decompressor;
        private static Codec wrap, unwrap;
        private static Bound bound;
        private static ContentSize contentSize;
        private static int mainThread;
        private static bool sendPatchesChecked, keepSendPackage;
        private static readonly ZPackage sendPackage = new ZPackage();
        private static byte[] frame = new byte[4096], payload = new byte[4096];

        private delegate int Codec(byte[] source, int sourceOffset, int sourceLength, byte[] destination, int destinationOffset, int destinationLength);
        private delegate int Bound(int length);
        private delegate ulong ContentSize(byte[] source, int offset, int length);

        internal static bool Running { get; private set; }
        // Off leaves every packet to Better Networking's own methods; the isolated client switches it for paired measurements.
        internal static bool Pooling { get; set; } = true;
        // Why Better Networking keeps its own compression, or null.
        internal static string Failure { get; private set; }
        internal static long Sends { get; private set; }
        internal static long Receives { get; private set; }
        internal static long Fallbacks { get; private set; }
        internal static bool KeepsSendPackage => keepSendPackage;

        internal static void Start()
        {
            if (Running) return;
            try { Failure = Bind(); }
            catch (Exception error) { Failure = "binding Better Networking failed: " + error; }
            if (Failure == null)
            {
                try
                {
                    mainThread = Thread.CurrentThread.ManagedThreadId;
                    patches.Patch(sendPrefix, prefix: new HarmonyMethod(typeof(PooledCompression), nameof(PooledSend)));
                    patches.Patch(receivePostfix, prefix: new HarmonyMethod(typeof(PooledCompression), nameof(PooledReceive)));
                    Running = true;
                    Plugin.Log.LogInfo("Pooled Better Networking compression is on: sends and receives use kept buffers and put Better Networking's own frames on the wire.");
                    return;
                }
                catch (Exception error)
                {
                    Failure = "patching Better Networking failed: " + error;
                    patches.UnpatchSelf();
                }
            }
            Release();
            if (Failure == NotInstalled) Plugin.Log.LogInfo("Pooled Better Networking compression is off: " + Failure + ".");
            else Plugin.Log.LogWarning("Pooled Better Networking compression is off, so Better Networking compresses as it does itself: " + Failure + ".");
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            Release();
        }

        private static void Release()
        {
            (compressor as IDisposable)?.Dispose();
            (decompressor as IDisposable)?.Dispose();
            compressor = decompressor = null;
        }

        // Finds Better Networking and binds what the patches use by name, with no compile-time reference to it or to the
        // compression library it carries, and returns why it cannot, or null.
        private static string Bind()
        {
            if (!Chainloader.PluginInfos.TryGetValue(BetterNetworking, out var plugin)) return NotInstalled;
            if (!Checked.Contains(plugin.Metadata.Version)) return $"Better Networking {plugin.Metadata.Version} is not a checked version ({string.Join(", ", Checked.Select(version => version.ToString()))})";
            var type = plugin.Instance.GetType();
            var assembly = type.Assembly;
            Type Named(string name) => assembly.GetType("DIT.BetterNetworking10." + name, true);
            var steam = Named("SteamCompressionPatch");
            sendPrefix = AccessTools.DeclaredMethod(steam, "SendPrefix", new[] { typeof(ZSteamSocket), typeof(ZPackage).MakeByRefType() });
            receivePostfix = AccessTools.DeclaredMethod(steam, "ReceivePostfix", new[] { typeof(ZSteamSocket), typeof(ZPackage).MakeByRefType() });
            // Better Networking patches the game's Steam send and receive only while its compression is on and passed its self-test.
            if (sendPrefix == null || receivePostfix == null || Harmony.GetPatchInfo(gameSend)?.Prefixes.Any(patch => patch.PatchMethod == sendPrefix) != true
                || Harmony.GetPatchInfo(gameReceive)?.Postfixes.Any(patch => patch.PatchMethod == receivePostfix) != true)
                return "Better Networking's Steam compression is not patched in";
            // Its Info level logs each packet, which only its own methods do.
            var log = AccessTools.Property(type, "Log").GetValue(null);
            if ((bool)AccessTools.Property(log.GetType(), "IsInfo").GetValue(log)) return "Better Networking logs every packet at its Info level";
            peers = (IDictionary)AccessTools.Field(Named("CompressionState"), "Peers").GetValue(null);
            var peer = Named("CompressionState+PeerState");
            sending = AccessTools.FieldRefAccess<bool>(peer, "Sending");
            receiving = AccessTools.FieldRefAccess<bool>(peer, "Receiving");
            warnedUnframed = AccessTools.FieldRefAccess<bool>(peer, "WarnedUnframed");
            var codec = Named("CompressionCodec");
            var compressorType = AccessTools.Field(codec, "_compressor").FieldType;
            var decompressorType = AccessTools.Field(codec, "_decompressor").FieldType;
            byte[] dictionary;
            using (var stream = assembly.GetManifestResourceStream("DIT.BetterNetworking10.dict.small"))
            {
                if (stream == null) return "Better Networking's dictionary is missing";
                dictionary = new byte[stream.Length];
                for (int read = 0, step; read < dictionary.Length; read += step)
                    if ((step = stream.Read(dictionary, read, dictionary.Length - read)) == 0) return "Better Networking's dictionary ended early";
            }
            // The level and dictionary of Better Networking's own contexts.
            compressor = Activator.CreateInstance(compressorType, 1);
            compressorType.GetMethod("LoadDictionary", new[] { typeof(byte[]) }).Invoke(compressor, new object[] { dictionary });
            decompressor = Activator.CreateInstance(decompressorType);
            decompressorType.GetMethod("LoadDictionary", new[] { typeof(byte[]) }).Invoke(decompressor, new object[] { dictionary });
            var arrays = new[] { typeof(byte[]), typeof(int), typeof(int), typeof(byte[]), typeof(int), typeof(int) };
            wrap = Bind<Codec>(compressorType, compressor, "Wrap", arrays);
            unwrap = Bind<Codec>(decompressorType, decompressor, "Unwrap", arrays);
            bound = Bind<Bound>(compressorType, compressor, "GetCompressBound", typeof(int));
            contentSize = Bind<ContentSize>(decompressorType, decompressor, "GetDecompressedSize", typeof(byte[]), typeof(int), typeof(int));
            return null;
        }

        private static T Bind<T>(Type type, object instance, string name, params Type[] parameters) where T : Delegate
        {
            var method = type.GetMethod(name, parameters) ?? throw new MissingMethodException(type.FullName, name);
            return (T)(method.IsStatic ? Delegate.CreateDelegate(typeof(T), method) : Delegate.CreateDelegate(typeof(T), instance, method));
        }

        // The game's send copies the package into its queue at once, so one kept package can carry every frame while no other
        // patch on the send could keep it: every other patch must be a prefix that runs before Better Networking's, which sees
        // the caller's own package. Decided at the first send, once every mod has patched.
        private static bool KeepSendPackage()
        {
            var info = Harmony.GetPatchInfo(gameSend);
            if (info == null || info.Postfixes.Count + info.Transpilers.Count + info.Finalizers.Count + info.ILManipulators.Count > 0) return false;
            var order = PatchProcessor.GetSortedPatchMethods(gameSend, info.Prefixes.ToArray());
            return order.Count > 0 && order[order.Count - 1] == sendPrefix;
        }

        // Better Networking's state for the peer, while pooling and on the main thread, or null.
        private static object Peer(ZSteamSocket socket, ZPackage package) =>
            Pooling && package != null && socket != null && Thread.CurrentThread.ManagedThreadId == mainThread ? peers[socket] : null;

        private static bool Framed(byte[] data, int offset, int length) =>
            length >= 5 && data[offset] == 66 && data[offset + 1] == 78 && data[offset + 2] == 49 && data[offset + 3] == 48;

        // Prefix on SteamCompressionPatch.SendPrefix(ZSteamSocket __instance, ref ZPackage pkg), which frames the package while
        // the peer's state is sending: compressed when that is strictly smaller, raw otherwise, behind the same five-byte header.
        // Harmony fills parameters named __instance by itself, so a patch on it takes its arguments by position.
        private static bool PooledSend(ZSteamSocket __0, ref ZPackage __1)
        {
            object state = Peer(__0, __1);
            if (state == null || !sending(state)) return true;
            // Size flushes the package's writer, as the copy Better Networking compresses does.
            int length = __1.Size();
            if (length > Largest || !streamOf(__1).TryGetBuffer(out var input) || Framed(input.Array, input.Offset, length)) { Fallbacks++; return true; }
            int framed;
            try
            {
                int capacity = 5 + Math.Max(bound(length), length);
                if (frame.Length < capacity) frame = new byte[capacity * 2];
                int written = wrap(input.Array, input.Offset, length, frame, 5, frame.Length - 5);
                bool smaller = written < length;
                if (!smaller) Buffer.BlockCopy(input.Array, input.Offset, frame, 5, length);
                frame[0] = 66; frame[1] = 78; frame[2] = 49; frame[3] = 48;
                frame[4] = (byte)(smaller ? 1 : 0);
                framed = 5 + (smaller ? written : length);
            }
            catch (Exception) { Fallbacks++; return true; }
            if (!sendPatchesChecked)
            {
                keepSendPackage = KeepSendPackage();
                sendPatchesChecked = true;
                if (!keepSendPackage) Plugin.Log.LogInfo("Better Networking compression: each send gets a new package, since another mod patches the game's send after Better Networking.");
            }
            if (keepSendPackage)
            {
                sendPackage.Clear();
                var stream = streamOf(sendPackage);
                stream.Write(frame, 0, framed);
                stream.Position = 0;
                __1 = sendPackage;
            }
            else __1 = new ZPackage(frame, framed);
            Sends++;
            return false;
        }

        // Prefix on SteamCompressionPatch.ReceivePostfix(ZSteamSocket __instance, ref ZPackage __result), for frames from a peer
        // that has started compressing; the game keeps the package it gets, so that one is new.
        private static bool PooledReceive(ZSteamSocket __0, ref ZPackage __1)
        {
            object state = Peer(__0, __1);
            if (state == null || !receiving(state)) return true;
            int length = __1.Size();
            if (length > Largest || !streamOf(__1).TryGetBuffer(out var input) || !Framed(input.Array, input.Offset, length) || input.Array[input.Offset + 4] > 1)
            {
                Fallbacks++;
                return true;
            }
            ZPackage result;
            try
            {
                if (input.Array[input.Offset + 4] == 0) result = Package(input.Array, input.Offset + 5, length - 5);
                else
                {
                    ulong content = contentSize(input.Array, input.Offset + 5, length - 5);
                    if (content > Largest) { Fallbacks++; return true; }
                    if (payload.Length < (int)content) payload = new byte[(int)content * 2];
                    result = Package(payload, 0, unwrap(input.Array, input.Offset + 5, length - 5, payload, 0, payload.Length));
                }
            }
            catch (Exception) { Fallbacks++; return true; }
            warnedUnframed(state) = false;
            __1 = result;
            Receives++;
            return false;
        }

        private static ZPackage Package(byte[] data, int offset, int count)
        {
            var package = new ZPackage();
            var stream = streamOf(package);
            stream.Write(data, offset, count);
            stream.Position = 0;
            return package;
        }
    }
}
