using BepInEx.Configuration;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using System;
using System.Collections;
using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace COM3D2.ResourceUnloadOptimizations.Plugin
{
    /// <summary>
    /// Improves loading times and reduces or eliminates stutter in games that abuse Resources.UnloadUnusedAssets and/or GC.Collect.
    /// </summary>
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInIncompatibility("BepInEx.ResourceUnloadOptimizations")]
    [BepInIncompatibility("COM3D2.DCMMemoryOptimization")]
    public class ResourceUnloadOptimizations : BaseUnityPlugin
    {
        public const string GUID = "COM3D2.ResourceUnloadOptimizations.Plugin.InoryS";
        public const string PluginName = "COM3D2.ResourceUnloadOptimizations.Plugin";
        public const string Version = "1.0.0";

        private static new ManualLogSource Logger;

        private static AsyncOperation _currentOperation;
        private static Func<AsyncOperation> _originalUnload;

        private static int _garbageCollect;
        private float _waitTime;

        public static ConfigEntry<bool> FullGCOnSceneUnload { get; private set; }
        public static ConfigEntry<bool> GCAfterDance { get; private set; }
        public static ConfigEntry<long> MaxHeapSize { get; private set; }  // GC.GetTotalMemory return long
        public static ConfigEntry<bool> MaximizeMemoryUsage { get; private set; }
        public static ConfigEntry<int> PercentMemoryThreshold { get; private set; }
        public static ConfigEntry<float> PageFileFreeThreshold { get; private set; }
        public static ConfigEntry<ulong> MinAvailPageFileBytes { get; private set; }
        public static ConfigEntry<bool> DanceMaximizeMemoryUsage { get; private set; }

        public static bool DanceMaximizeMemoryUsageFlag = false;

        public static ConfigEntry<int> DancePercentMemoryThreshold { get; private set; }
        public static ConfigEntry<float> DancePageFileFreeThreshold { get; private set; }
        public static ConfigEntry<ulong> DanceMinAvailPageFileBytes { get; private set; }

        public static ConfigEntry<bool> EnablePeriodicGC { get; private set; }
        public static ConfigEntry<int> PeriodicGCInterval { get; private set; }

        public static ConfigEntry<bool> DisableUnload { get; private set; }


        private static bool _monoGCInited = false;
        private static ResourceUnloadOptimizations Instance { get; set; }

        private Coroutine _memoryCheckCoroutine;

        private static Harmony _harmony;

        void Awake()
        {
            Instance = this;
            Logger = base.Logger;
            _harmony = new Harmony(GUID);

            FullGCOnSceneUnload = Config.Bind("Garbage Collection", "Enable Full GC OnScene Unload", true, "If true, will run a full GC after a scene is unloaded. It will increase loading times, but it can save memory for the next scene.");
            GCAfterDance = Config.Bind("Garbage Collection", "GC After Dance", true, "If true, will run a full GC after a dance finished, include DCM dance");
            MaxHeapSize =  Config.Bind("Garbage Collection", "Max Heap Size", 20000L * 1024L * 1024L, "The maximum amount of heap memory the game can use, there seems to be a hardcoded limit, the game will crash with a \"Too many heap sections\" message when heap size exceeds around 23500 MB. (default value mean 20000 MB, exceeding this value will trigger a full GC)");

            MaximizeMemoryUsage = Config.Bind("Unload Throttling", "Maximize Memory Usage", true, "If true, allows the game to use as much memory as possible, up to the limit set below, to reduce random stuttering caused by garbage collection.");
            PercentMemoryThreshold = Config.Bind("Unload Throttling", "Memory Threshold", 75, "Allow games and other programs to occupy up to x% of physical memory without garbage collecting the game (default value mean: 75%).");
            PageFileFreeThreshold = Config.Bind("Unload Throttling", "Page File Free Threshold", 0.4f, "Minimum ratio (0~1) of page file free. If the free page file ratio is above this threshold, skip garbage collecting (default value mean: 40% free page file).");
            MinAvailPageFileBytes = Config.Bind("Unload Throttling", "Min Avail Page File Bytes", 2UL * 1024UL * 1024UL * 1024UL, "Minimum bytes of available page file. If above this threshold, skip garbage collecting (default value mean: 2GB).");

            DanceMaximizeMemoryUsage = Config.Bind("Dance Unload Throttling", "Dance Maximize Memory Usage", true, "If true ,allows the game to use as much memory as possible during dance(include DCM), up to the limit set below, to reduce random stuttering caused by garbage collection.");
            DancePercentMemoryThreshold = Config.Bind("Dance Unload Throttling", "Dance Memory Threshold", 90, "(Only in dance scene) Allow games and other programs to occupy up to x% of physical memory without garbage collecting the game (default value mean: 90%).");
            DancePageFileFreeThreshold = Config.Bind("Dance Unload Throttling", "Dance Page File Free Threshold", 0.1f, "(Only in dance scene) Minimum ratio (0~1) of page file free. If the free page file ratio is above this threshold, skip garbage collecting (default value mean: 10% free page file).");
            DanceMinAvailPageFileBytes = Config.Bind("Dance Unload Throttling", "Dance Min Avail Page File Bytes", 2UL * 1024UL * 1024UL * 1024UL, "(Only in dance scene) Minimum bytes of available page file. If above this threshold, skip garbage collecting (default value mean: 2GB).");

            EnablePeriodicGC = Config.Bind("Periodic GC", "Enable Periodic GC", false, "Enable periodic garbage collection.");
            PeriodicGCInterval = Config.Bind("Periodic GC", "Periodic GC Interval", 300, "Interval (in seconds) for periodic Gen 0 garbage collection.");

            DisableUnload = Config.Bind("TEST", "Disable Resource Unload", false, "ONLY USE IN TEST. Disables all resource unloading. Requires large amounts of RAM or will likely crash your game.");

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            InstallHooks();
            StartCoroutine(CleanupCo());

            if (EnablePeriodicGC.Value)
            {
                StartCoroutine(PeriodicGCCo());
            }
        }

        void Start()
        {
            Type danceCameraMotionType = Type.GetType("COM3D2.DanceCameraMotion.Plugin.DanceCameraMotion, COM3D2.DanceCameraMotion.Plugin");
            if (danceCameraMotionType != null)
            {
                Logger.LogDebug("COM3D2.DanceCameraMotion.Plugin detected. Applying hooks...");
                _harmony.PatchAll(typeof(DanceCameraMotionHooks));
            }
            else
            {
                Logger.LogWarning("COM3D2.DanceCameraMotion.Plugin not detected. Skipping related hooks.");
            }
        }

        void OnDestroy()
        {
            StopAllCoroutines();
        }


        /// <summary>
        /// Use MonoMod.RuntimeDetour + Harmony to replace and intercept Resources.UnloadUnusedAssets and GC.Collect
        /// </summary>
        private static void InstallHooks()
        {
            Logger.LogDebug("Installing Hooks");
            var target = AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets));
            var replacement = AccessTools.Method(typeof(GCHooks), nameof(GCHooks.UnloadUnusedAssetsHook));

            var detour = new NativeDetour(target, replacement);

            _originalUnload = detour.GenerateTrampoline<Func<AsyncOperation>>();

            detour.Apply();

            _harmony.PatchAll(typeof(GCHooks));
            //harmony.PatchAll(typeof(GameMainUnloadSceneHooks));
        }

        /// <summary>
        /// Initialize monoPatcherHooks, skip if already initialized
        /// </summary>
        private static void TryInitMonoPatcher()
        {
            if (!_monoGCInited)
            {
                _monoGCInited = MonoPatcherHooks.gcOpInit();
                if (_monoGCInited)
                {
                    Logger.LogDebug("MonoPatcherHooks initialized successfully");
                }
                else
                {
                    Logger.LogWarning("MonoPatcherHooks initialization failed, may not disable GC!");
                }
            }
        }

        /// <summary>
        /// The main timed cleanup logic checks whether GC needs to be executed at regular intervals
        /// </summary>
        private IEnumerator CleanupCo()
        {
            while (true)
            {
                // Simple 1 second delay
                while (Time.realtimeSinceStartup < _waitTime)
                    yield return null;
                _waitTime = Time.realtimeSinceStartup + 1;

                // If GC requests accumulate during this period
                if (_garbageCollect > 0)
                {
                    // Decrement frame by frame until it reaches zero before performing real GC
                    if (--_garbageCollect == 0)
                    {
                        RunFullGarbageCollect(false);
                    }
                }
                yield return null;
            }
        }

        /// <summary>
        /// Trigger resource unloading, using the original trampoline call
        /// </summary>
        private static AsyncOperation RunUnloadAssets()
        {
            // Only allow a single unload operation to run at one time
            if (_currentOperation == null || _currentOperation.isDone && !PlentyOfMemory(false))
            {
                Logger.LogDebug("Starting unused asset cleanup");
                _currentOperation = _originalUnload();
            }
            return _currentOperation;
        }

        /// <summary>
        /// Perform a full GC immediately
        /// </summary>
        private static void RunFullGarbageCollect(bool ignorePlentyOfMemory)
        {
            if (!ignorePlentyOfMemory && PlentyOfMemory(false))
            {
                return;
            }


            Stopwatch stopwatch = Stopwatch.StartNew();
            Logger.LogDebug($"Starting full garbage collection (GC.Collect({GC.MaxGeneration}))");

            // Use different overload since we disable the parameterless one
            GC.Collect(GC.MaxGeneration);  // unity only got 0
            GC.WaitForPendingFinalizers();

            stopwatch.Stop();
            Logger.LogInfo($"Full garbage collection completed in {stopwatch.ElapsedMilliseconds} ms");
        }

        private IEnumerator PeriodicGCCo()
        {
            while (true)
            {
                yield return new WaitForSeconds(PeriodicGCInterval.Value);

                if (EnablePeriodicGC.Value)
                {
                    RunFullGarbageCollect(true);
                }
            }
        }


        /// <summary>
        /// Determine whether there is enough memory and there is no need to clean it up immediately
        /// false mean do GC, true mean do not GC
        /// </summary>
        private static bool PlentyOfMemory(bool ignoreMaximizeMemoryUsage)
        {
            // If we are not ignoring the memory usage configuration and memory usage is not maximized, return false.
            if (!ignoreMaximizeMemoryUsage && !MaximizeMemoryUsage.Value){
                    return false;
            }

            var mem = MemoryInfo.GetCurrentStatus();
            if (mem == null)
            {
                Logger.LogDebug($"get memory info failed");
                return false;
            }


            var pageFileFree = mem.ullAvailPageFile / (float)mem.ullTotalPageFile; // Clean up more aggressively during loading, less aggressively during gameplay
            var heapSize = GC.GetTotalMemory(false);  // there seems to be a hardcoded limit, the game will crash with a "Too many heap sections" message when heap size exceeds around 23500 MB.
            bool plentyOfMemory;

            if (DanceMaximizeMemoryUsageFlag)
            {
#if DEBUG
                Logger.LogDebug($"we are in dance memory threshold");
#endif
                plentyOfMemory = mem.dwMemoryLoad < DancePercentMemoryThreshold.Value // < 90% by default
                                     && pageFileFree >  DancePageFileFreeThreshold.Value  // page file free %, default 10%
                                     && mem.ullAvailPageFile >  DanceMinAvailPageFileBytes.Value // at least 2GB of page file free
                                     && heapSize < MaxHeapSize.Value; // max heap size, default 20000MB

            }
            else
            {
#if DEBUG
                Logger.LogDebug($"we are in normal memory threshold");
#endif
                plentyOfMemory = mem.dwMemoryLoad < PercentMemoryThreshold.Value // < 75% by default
                                     && pageFileFree > PageFileFreeThreshold.Value  // page file free %, default 40%
                                     && mem.ullAvailPageFile > MinAvailPageFileBytes.Value // at least 2GB of page file free
                                     && heapSize < MaxHeapSize.Value; // max heap size, default 20000MB
            }

            if (!plentyOfMemory){
                Logger.LogDebug("GC called and cleaning, because the memory usage reaches the threshold.");
                Logger.LogDebug($"Physical RAM Load: {mem.dwMemoryLoad}% (Threshold: {(DanceMaximizeMemoryUsageFlag ? DancePercentMemoryThreshold.Value : PercentMemoryThreshold.Value)}%), PageFile Free: {pageFileFree:P} (Threshold: {(DanceMaximizeMemoryUsageFlag ? DancePageFileFreeThreshold.Value : PageFileFreeThreshold.Value):P}), Available PageFile: {mem.ullAvailPageFile / 1024 / 1024}MB (Threshold: {(DanceMaximizeMemoryUsageFlag ? DanceMinAvailPageFileBytes.Value : MinAvailPageFileBytes.Value) / 1024 / 1024}MB), Heap Size: {heapSize / 1024.0 / 1024.0:P}MB (Threshold: {MaxHeapSize.Value / 1024.0 / 1024.0:P}MB)");

                return false;
            }


            Logger.LogDebug("GC called, but skipping cleanup because of low memory load.");
            Logger.LogDebug($"Physical RAM Load: {mem.dwMemoryLoad}% (Threshold: {(DanceMaximizeMemoryUsageFlag ? DancePercentMemoryThreshold.Value : PercentMemoryThreshold.Value)}%), PageFile Free: {pageFileFree:P} (Threshold: {(DanceMaximizeMemoryUsageFlag ? DancePageFileFreeThreshold.Value : PageFileFreeThreshold.Value):P}), Available PageFile: {mem.ullAvailPageFile / 1024 / 1024}MB (Threshold: {(DanceMaximizeMemoryUsageFlag ? DanceMinAvailPageFileBytes.Value : MinAvailPageFileBytes.Value) / 1024 / 1024}MB), Heap Size: {heapSize / 1024.0 / 1024.0:P}MB (Threshold: {MaxHeapSize.Value / 1024.0 / 1024.0:P}MB)");
            return true;
        }


        public void OnSceneUnloaded(Scene scene)
        {
            if (GCAfterDance.Value && scene.name.ToLower().Contains("dance"))
            {
                Logger.LogDebug("We are exit dance scene, running a full GC...");
                RunFullGarbageCollect(true);
                RunFullGarbageCollect(true);
                return;
            }
            if (FullGCOnSceneUnload.Value)
            {
                Logger.LogDebug($"scene {scene.name} unloading, running a full GC...");
                RunFullGarbageCollect(true);
                RunFullGarbageCollect(true);
            }
        }

        public void OnSceneLoaded(Scene scene, LoadSceneMode sceneMode)
        {
            if (!scene.name.ToLower().Contains("danceselect") && scene.name.ToLower().Contains("dance"))
            {
                if (DanceMaximizeMemoryUsage.Value)
                {
                    Logger.LogDebug("We are in dane scene, stopping GC~");
                    DisableMonoGC();
                    DanceMaximizeMemoryUsageFlag = true;
                }
            }
            else
            {
                EnableMonoGC();
                DanceMaximizeMemoryUsageFlag = false;
            }
        }

        internal static void DisableMonoGC()
        {
            if (!_monoGCInited)
            {
                TryInitMonoPatcher();
            }
            else
            {
                MonoPatcherHooks.gcSetStatusX(false);
                Logger.LogDebug("The mono GC has been disabled.");


                if (Instance._memoryCheckCoroutine == null)
                {
                    Instance._memoryCheckCoroutine = Instance.StartCoroutine(Instance.MemoryCheckCo());
                    Logger.LogDebug("Memory check coroutine started.");
                }

                return;
            }

            if (!_monoGCInited)
            {
                Logger.LogDebug("DisableMonoGC is called but MonoPatcherHooks is not initialized successfully.");
            }
        }

        internal static void EnableMonoGC()
        {
            if (!_monoGCInited)
            {
                TryInitMonoPatcher();
            }
            else
            {
                MonoPatcherHooks.gcSetStatusX(true);
                Logger.LogDebug("The mono GC has been resumed.");

                if (Instance._memoryCheckCoroutine != null)
                {
                    Instance.StopCoroutine(Instance._memoryCheckCoroutine);
                    Instance._memoryCheckCoroutine = null;
                    Logger.LogDebug("Memory check coroutine stopped.");
                }

                return;
            }

            if (!_monoGCInited)
            {
                Logger.LogDebug("EnableMonoGC is called but MonoPatcherHooks is not initialized successfully.");
            }
        }

        private IEnumerator MemoryCheckCo()
        {
            while (true)
            {
                yield return new WaitForSeconds(5);

                if (!PlentyOfMemory(true))
                {
                    Logger.LogInfo("Memory usage exceeds threshold, triggering garbage collection...");
                    EnableMonoGC();
                    RunFullGarbageCollect(true);
                    DisableMonoGC(); // Disable again after cleaning
                }
                else
                {
                    #if DEBUG
                    Logger.LogDebug("Memory usage is within acceptable limits. No action required.");
                    #endif
                }
            }
        }


        private static class GCHooks
        {
            [HarmonyPrefix]
            [HarmonyPatch(typeof(GC), nameof(GC.Collect), new Type[0])]
            public static bool GCCollectHook()
            {
                // Throttle down the calls. Keep resetting the timer until things calm down since it's usually fairly low memory usage
                _garbageCollect = 3;
                // Disable the original method, Invoke will call it later
                return false;
            }

            // Replacement method needs to be inside a static class to be used in NativeDetour
            public static AsyncOperation UnloadUnusedAssetsHook()
            {
                if (DisableUnload.Value)
                    return null;
                else
                    return RunUnloadAssets();
            }
        }

        public static class DanceCameraMotionHooks
        {
            [HarmonyPatch(typeof(COM3D2.DanceCameraMotion.Plugin.DanceCameraMotion), "StartFreeDance")]
            [HarmonyPostfix]
            private static void StartFreeDancePostfix()
            {
                if (DanceMaximizeMemoryUsage.Value)
                {
                    Logger.LogDebug("We are in DCM dancing, stopping GC~");
                    DisableMonoGC();
                    DanceMaximizeMemoryUsageFlag = true;
                }
            }

            [HarmonyPatch(typeof(COM3D2.DanceCameraMotion.Plugin.DanceCameraMotion), "EndFreeDance")]
            [HarmonyPostfix]
            private static void EndFreeDancePostfix()
            {
                Logger.LogDebug("We are exit DCM dancing, end stopping GC~");
                EnableMonoGC();
                DanceMaximizeMemoryUsageFlag = false;
                if (GCAfterDance.Value)
                {
                    Logger.LogDebug("We are exit DCM dancing, running a full GC...");
                    RunFullGarbageCollect(true);
                }
            }
        }
    }
}
