namespace WorldPerformance
{
    // The game's runtime starts a garbage collection once the program has allocated a share of the memory a collection has to
    // scan, and it weighs live memory that can hold references eight times as much as memory that cannot. At the base the game
    // allocates 3.6–5.7 MB a second and collects about four times a minute, each collection one frame of 34–65 ms. The mod
    // keeps 256 MB of reference arrays that stay empty, which the collector counts as live memory with references, so it lets
    // more garbage build up before each collection; it is allocated once and never written.
    internal static class CollectionBallast
    {
        // 256 arrays of 1 MB.
        private const int Arrays = 256, Length = 131072;
        private static object[][] arrays;
        internal static bool Running { get; private set; }
        // Off lets the collector reclaim the ballast; the isolated client switches it for paired measurements.
        internal static bool Holding
        {
            get => arrays != null;
            set
            {
                if (!value) arrays = null;
                else if (arrays == null)
                {
                    var made = new object[Arrays][];
                    for (int index = 0; index < Arrays; index++) made[index] = new object[Length];
                    arrays = made;
                }
            }
        }
        internal static int Megabytes => arrays == null ? 0 : Arrays * Length * System.IntPtr.Size / 1048576;

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            Holding = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            Holding = false;
        }
    }
}
