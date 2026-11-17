using System;
using System.Collections.Generic;
using UnityEngine;

namespace WorldPerformance
{
    // Members' detail-level checks, kept by when the camera's travel makes them due.
    internal static partial class SpatialBuildings
    {
        // A member's detail-level check, due once the camera has travelled this far in total, or, parked, once the camera is
        // this far from the anchor; with where the camera was when the check was made and how far from there it may go before
        // the member can reach a change.
        private struct Check { internal double Due; internal Member Member; internal int Stamp; internal Vector3 From; internal float Distance; }

        // A min-heap of checks by when they are due, in an array the checks are read in place from.
        private sealed class CheckHeap
        {
            private Check[] items = new Check[1024];
            internal int Count;
            // Set while the top check is being made: the next check queued takes its place and sinks only as far as it is due
            // later, instead of the last entry taking it and sinking through the whole heap before the next one is added.
            internal bool Replacing;

            internal Check Top => items[0];
            internal double TopDue => items[0].Due;

            internal void Clear()
            {
                Array.Clear(items, 0, Count);
                Count = 0;
                Replacing = false;
            }

            internal void Push(Check entry)
            {
                if (Replacing)
                {
                    Replacing = false;
                    SiftDown(0, entry);
                    return;
                }
                if (Count == items.Length) Array.Resize(ref items, Count * 2);
                int index = Count++;
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (items[parent].Due <= entry.Due) break;
                    items[index] = items[parent];
                    index = parent;
                }
                items[index] = entry;
            }

            internal Check Pop()
            {
                var top = items[0];
                var last = items[--Count];
                items[Count] = default;
                if (Count > 0) SiftDown(0, last);
                return top;
            }

            private void SiftDown(int index, Check entry)
            {
                while (true)
                {
                    int child = index * 2 + 1;
                    if (child >= Count) break;
                    if (child + 1 < Count && items[child + 1].Due < items[child].Due) child++;
                    if (entry.Due <= items[child].Due) break;
                    items[index] = items[child];
                    index = child;
                }
                items[index] = entry;
            }

            // Gives each entry the due time the function returns, drops those it gives a negative one and orders the rest again.
            internal void Keep(Func<Check, double> due)
            {
                int kept = 0;
                for (int index = 0; index < Count; index++)
                {
                    var entry = items[index];
                    double value = due(entry);
                    if (value < 0) continue;
                    entry.Due = value;
                    items[kept++] = entry;
                }
                Array.Clear(items, kept, Count - kept);
                Count = kept;
                for (int index = Count / 2 - 1; index >= 0; index--) SiftDown(index, items[index]);
            }
        }
    }
}
