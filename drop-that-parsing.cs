using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // A joining player's game reads Drop That 3.1.7's drop tables, locations and settings from the server as compressed
    // YAML, about 3.9 million characters, on the main thread inside the frame that receives them: 2.4-2.5 s of a rejoin at
    // Tenemo's base on a typical player's client. Each message is now unpacked on the main thread and read on a worker thread, one after
    // another, with Drop That's own reader, while the main thread goes on with the server's other join messages. The
    // drop conditions whose first reading registers a handler are set up on the main thread first, in the same order. Drop
    // That applies one received message per network update; that update now waits for the message's result if it is not
    // ready yet and applies it then, so every message takes effect in the same frame as before.
    internal static class DropThatParsing
    {
        // The isolated test client sets false to compare Drop That's own reading on the main thread.
        internal static bool Parallel = true;
        internal static readonly Dictionary<Type, byte[]> LastBytes = new Dictionary<Type, byte[]>();
        private static readonly object gate = new object();
        private static readonly List<Task<object>> pending = new List<Task<object>>();
        private static Task chain = Task.FromResult(0);
        private static MethodInfo getSerializer, getDeserializer, deserialize, afterUnpack, dequeue;
        private static PropertyInfo content;
        private static Type message;
        private static Type[] conditions;
        private static Func<object> receivedMessages;
        private static ManualLogSource log;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource logSource)
        {
            if (!PluginIds.TryGet(PluginIds.DropThat, out var plugin)) return;
            log = logSource;
            var assembly = plugin.Instance.GetType().Assembly;
            var split = assembly.GetType("ThatCore.Network.SplitMessage");
            var manager = assembly.GetType("ThatCore.Network.SplitMessageManager");
            var service = assembly.GetType("ThatCore.Valheim.Network.IncomingMessageService");
            var serializer = assembly.GetType("ThatCore.Network.Serializer");
            var reader = assembly.GetType("YamlDotNet.Serialization.IDeserializer");
            message = assembly.GetType("ThatCore.Network.IMessage");
            var combine = split == null || manager == null ? null
                : AccessTools.Method(manager, "CombineAndUnpackSplits", new[] { typeof(Type), typeof(Dictionary<,>).MakeGenericType(typeof(int), split) });
            var dispatch = service == null ? null : AccessTools.Method(service, "DispatchMessage");
            var received = service == null ? null : AccessTools.PropertyGetter(service, "ReceivedMessages");
            content = split == null ? null : AccessTools.Property(split, "Content");
            getSerializer = AccessTools.Method(assembly.GetType("ThatCore.Network.SerializerManager"), "GetSerializer", new[] { typeof(Type) });
            getDeserializer = serializer == null ? null : AccessTools.Method(serializer, "GetDeserializer");
            deserialize = reader == null ? null : AccessTools.Method(reader, "Deserialize", new[] { typeof(TextReader), typeof(Type) });
            afterUnpack = message == null ? null : AccessTools.Method(message, "AfterUnpack");
            dequeue = received == null ? null : AccessTools.Method(received.ReturnType, "Dequeue");
            if (combine == null || dispatch == null || received == null || content == null || getSerializer == null || getDeserializer == null || deserialize == null ||
                afterUnpack == null || dequeue == null || !typeof(ICollection).IsAssignableFrom(received.ReturnType))
                throw new InvalidOperationException("Drop That's message receiving changed.");
            conditions = new[] { "ConditionBiome", "ConditionNotBiome", "ConditionLocation", "ConditionNotLocation" }
                .Select(name => assembly.GetType("DropThat.Drop.CharacterDropSystem.Conditions." + name)).ToArray();
            if (conditions.Any(condition => condition == null)) throw new InvalidOperationException("Drop That's drop conditions changed.");
            receivedMessages = () => received.Invoke(null, null);
            harmony.Patch(combine, prefix: new HarmonyMethod(typeof(DropThatParsing), nameof(Combine)));
            harmony.Patch(dispatch, prefix: new HarmonyMethod(typeof(DropThatParsing), nameof(Dispatch)));
            log.LogInfo("Drop That's received drop tables are read on a worker thread.");
        }

        // The message's bytes, joined in split order as Drop That joins them.
        internal static byte[] Join(IDictionary splits)
        {
            var parts = new List<KeyValuePair<int, byte[]>>();
            foreach (DictionaryEntry entry in splits) parts.Add(new KeyValuePair<int, byte[]>((int)entry.Key, (byte[])content.GetValue(entry.Value, null)));
            return parts.OrderBy(part => part.Key).SelectMany(part => part.Value).ToArray();
        }

        // The message's text, unpacked as Drop That unpacks it.
        internal static string Unpack(byte[] bytes)
        {
            using (var packed = new MemoryStream(bytes))
            using (var text = new MemoryStream())
            {
                using (var unpacking = new GZipStream(packed, CompressionMode.Decompress, true)) unpacking.CopyTo(text);
                return Encoding.Unicode.GetString(text.GetBuffer(), 0, (int)text.Length);
            }
        }

        // Drop That's own reader for the type, built on the main thread, read from the unpacked text.
        internal static object Read(object reader, Type type, string text)
        {
            using (TextReader input = new StringReader(text))
            {
                var result = deserialize.Invoke(reader, new object[] { input, type });
                return message.IsInstanceOfType(result) ? result : null;
            }
        }

        // Four drop conditions register a drop table handler in their static constructors, which reading their first entry
        // runs. Those that the text names run here on the main thread, in the order their first entries come, before reading.
        private static void RunConditionConstructors(string text)
        {
            var named = new List<KeyValuePair<int, Type>>();
            foreach (var condition in conditions)
            {
                int at = text.IndexOf(condition.FullName, StringComparison.Ordinal);
                if (at >= 0) named.Add(new KeyValuePair<int, Type>(at, condition));
            }
            foreach (var pair in named.OrderBy(pair => pair.Key)) System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(pair.Value.TypeHandle);
        }

        internal static object Reader(Type type) => getDeserializer.Invoke(getSerializer.Invoke(null, new object[] { type }), null);

        // Drop That queues what this returns; a null stands in for the result, which the network update picks up in order.
        private static bool Combine(Type type, IDictionary splits, ref object __result)
        {
            if (!Parallel) return true;
            byte[] bytes = Join(splits);
            LastBytes[type] = bytes;
            string text = Unpack(bytes);
            RunConditionConstructors(text);
            var reader = Reader(type);
            var done = new TaskCompletionSource<object>();
            lock (gate)
            {
                chain = chain.ContinueWith(_ =>
                {
                    try { done.SetResult(Read(reader, type, text)); }
                    catch (Exception error) { done.SetException(error is TargetInvocationException && error.InnerException != null ? error.InnerException : error); }
                }, TaskScheduler.Default);
                pending.Add(done.Task);
            }
            __result = null;
            return false;
        }

        private static bool Dispatch()
        {
            Task<object> next;
            var queue = (ICollection)receivedMessages();
            lock (queue)
            {
                int waiting = 0;
                object head = null;
                bool first = true;
                foreach (object item in queue)
                {
                    if (first) { head = item; first = false; }
                    if (item == null) waiting++;
                }
                lock (gate)
                {
                    // A new world clears Drop That's queue; results still pending for it are dropped too.
                    if (pending.Count > waiting) pending.RemoveRange(0, pending.Count - waiting);
                    if (queue.Count == 0 || head != null || pending.Count == 0) return true;
                    next = pending[0];
                    pending.RemoveAt(0);
                }
                dequeue.Invoke(queue, null);
            }
            object result;
            try { result = next.Result; }
            catch (AggregateException error)
            {
                log.LogError("Drop That could not read a received message: " + error.InnerException);
                return false;
            }
            if (result == null) return false;
            try { afterUnpack.Invoke(result, null); }
            catch (TargetInvocationException error) { log.LogError("Drop That could not apply a received message: " + error.InnerException); }
            return false;
        }
    }
}
