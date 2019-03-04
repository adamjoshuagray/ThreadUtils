using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThreadUtils
{
    public static class CoreBinder
    {
        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        /// <summary>
        /// This map contains a mapping from core to allocation type.
        /// Allocation types are:
        ///     0: Nothing allocated
        ///     1: Non exclusive thread allocated
        ///     2: Unique thread allocated.
        ///    -1: Reserved for exclusive allocation.
        /// </summary>
        private static Dictionary<int, int> _ExclusiveAllocationMap = new Dictionary<int, int>();

        private static Random _RandomAllocator = new Random();

        private const int _RESERVED_EXCLUSIVE = 8;

        static CoreBinder()
        {
            // We setup the exclusive allocation map such that core 1 is already allocated
            // because important operating system stuff tends to happen on that core.
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                if (i == 0)
                {
                    _ExclusiveAllocationMap[i + 1] = 1;
                }
                else if (i <= _RESERVED_EXCLUSIVE)
                {
                    _ExclusiveAllocationMap[i + 1] = -1;
                }
                else
                {
                    _ExclusiveAllocationMap[i + 1] = 0;
                }
            }
        }

        public static void FreeExclusive(int core)
        {
            lock (_ExclusiveAllocationMap)
            {
                if (_ExclusiveAllocationMap.TryGetValue(core, out var alloc) && alloc == 2)
                {
                    _ExclusiveAllocationMap[core] = core <= _RESERVED_EXCLUSIVE ? -1 : 0;
                }
            }
        }

        public static int Allocate(bool exclusive = false)
        {
            lock (_ExclusiveAllocationMap)
            {
                if (exclusive)
                {
                    // We allocate sequentially to the reserved cores first.
                    var avail = _ExclusiveAllocationMap.Where(kvp => kvp.Value <= 0).OrderBy(kvp => kvp.Value)
                        .ToList();
                    if (avail.Count == 0)
                    {
                        throw new Exception("All cores already allocated.");
                    }
                    else
                    {
                        int core = avail.First().Key;
                        _ExclusiveAllocationMap[core] = 2;
                        return core;
                    }
                }
                else
                {
                    // We allocate randomlly among available cores.
                    var avail = _ExclusiveAllocationMap.Where(kvp => kvp.Value == 0 || kvp.Value == 1).ToList();
                    if (avail.Count == 0)
                    {
                        throw new Exception("All cores already allocated.");
                    }
                    else
                    {
                        int core = avail[_RandomAllocator.Next(1, avail.Count)].Key;
                        _ExclusiveAllocationMap[core] = exclusive ? 2 : 1;
                        return core;
                    }
                }
            }
        }

        private static ProcessThread _CurrentThread
        {
            get
            {
                int id = GetCurrentThreadId();
                var pt = (from ProcessThread th in Process.GetCurrentProcess().Threads where th.Id == id select th)
                    .Single();
                return pt;
            }
        }

        public static void Bind(int core, ThreadPriorityLevel priority = ThreadPriorityLevel.Normal)
        {

            if (core <= 0 || core > 64)
            {
                // That isn't gonna work.
                throw new Exception("Core must be between 1 and 64 inclusive");
            }

            IntPtr core_bitmask = new IntPtr(1 << (core - 1));
            Thread.BeginThreadAffinity();
            _CurrentThread.ProcessorAffinity = core_bitmask;
            _CurrentThread.PriorityLevel = priority;
        }
    }
}
