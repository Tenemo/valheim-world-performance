using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WorldPerformance
{
    // Unity's own comparison of two live objects is a reference comparison reached through a virtual call and its null
    // checks, and its hash is the runtime's; keyed by reference directly, a table gives the same answers with less work.
    internal sealed class Identity<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly Identity<T> Instance = new Identity<T>();
        public bool Equals(T first, T second) => ReferenceEquals(first, second);
        public int GetHashCode(T item) => RuntimeHelpers.GetHashCode(item);
    }
}
