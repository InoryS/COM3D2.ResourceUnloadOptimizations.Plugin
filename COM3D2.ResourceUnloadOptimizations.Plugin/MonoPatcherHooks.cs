using System;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;

namespace COM3D2.ResourceUnloadOptimizations.Plugin
{
    internal static class MonoPatcherHooks
    {
        static string LibPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "monoPatcher.dll");

        [DllImport("Kernel32.dll")]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("Kernel32.dll")]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        public static Delegate LoadFunction<T>(string dllPath, string functionName)
        {
            var hModule = LoadLibrary(dllPath);
            var functionAddress = GetProcAddress(hModule, functionName);
            return Marshal.GetDelegateForFunctionPointer(functionAddress, typeof(T));
        }

        private delegate bool GcInitType();
        static private GcInitType GcInit;
        private delegate bool GcSetStatusType(bool bEnable);
        static private GcSetStatusType GcSetStatus;

        public static bool gcOpInit()
        {
            if (LoadLibrary(LibPath) == null)
            {
                return false;
            }
            GcInit = (GcInitType)LoadFunction<GcInitType>(LibPath, "monoPatchInit");
            GcSetStatus = (GcSetStatusType)LoadFunction<GcSetStatusType>(LibPath, "monoSetGCStatus");
            if (GcInit != null && GcSetStatus != null)
            {
                return GcInit();
            }
            return false;
        }

        public static bool gcSetStatusX(bool bEnable)
        {
            if (GcSetStatus != null)
            {
                return GcSetStatus(bEnable);
            }
            return false;
        }
        public static MEMORYSTATUSEX QuerySystemMemStatus()
        {
            bool flag = GlobalMemoryStatusEx(_memorystatusex);
            if (flag)
            {
                return _memorystatusex;
            }
            throw new Exception("GlobalMemoryStatusEx returned false. Error Code is " + Marshal.GetLastWin32Error());
        }

        public static PROCESS_MEMORY_COUNTERS QueryProcessMemStatus()
        {
            bool processMemoryInfo = GetProcessMemoryInfo(_currentProcessHandle, _memoryCounters, _memoryCounters.cb);
            if (processMemoryInfo)
            {
                return _memoryCounters;
            }
            throw new Exception("GetProcessMemoryInfo returned false. Error Code is " + Marshal.GetLastWin32Error());
        }

        public static void SetMemoryToDisk()
        {
            SetProcessWorkingSetSize(-1, -1, -1);
        }

        public static void StartLowMode()
        {
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareConstrainedRegions();
            GCSettings.LatencyMode = GCLatencyMode.LowLatency;
        }

        public static void EndLowMod()
        {
            GCSettings.LatencyMode = GCLatencyMode.Interactive;
        }

        [DllImport("kernel32.dll")]
        public static extern int SetProcessWorkingSetSize(int process, int minSize, int maxSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In][Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess, [In][Out] PROCESS_MEMORY_COUNTERS counters, uint size);

        private static readonly IntPtr _currentProcessHandle = GetCurrentProcess();
        private static readonly MEMORYSTATUSEX _memorystatusex = new MEMORYSTATUSEX();
        private static readonly PROCESS_MEMORY_COUNTERS _memoryCounters = new PROCESS_MEMORY_COUNTERS();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }

            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential, Size = 72)]
        public class PROCESS_MEMORY_COUNTERS
        {
            public PROCESS_MEMORY_COUNTERS()
            {
                cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS));
            }

            public uint cb;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
            public ulong QuotaPeakPagedPoolUsage;
            public ulong QuotaPagedPoolUsage;
            public ulong QuotaPeakNonPagedPoolUsage;
            public ulong QuotaNonPagedPoolUsage;
            public ulong PagefileUsage;
            public ulong PeakPagefileUsage;
        }
    }
}