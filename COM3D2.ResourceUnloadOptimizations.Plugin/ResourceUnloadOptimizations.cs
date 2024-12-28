using BepInEx.Configuration;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using System;
using System.Collections;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BepInEx
{
    /// <summary>
    /// Improves loading times and reduces or eliminates stutter in games that abuse Resources.UnloadUnusedAssets and/or GC.Collect.
    /// </summary>
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInIncompatibility("BepInEx.ResourceUnloadOptimizations")]
    public class ResourceUnloadOptimizations : BaseUnityPlugin
    {
        public const string GUID = "COM3D2.ResourceUnloadOptimizations.Plugin.InoryS";
        public const string PluginName = "COM3D2.ResourceUnloadOptimizations.Plugin";
        public const string Version = "1.0";

        private static new ManualLogSource Logger;

        private static AsyncOperation _currentOperation;
        private static Func<AsyncOperation> _originalUnload;

        private static int _garbageCollect;
        private float _waitTime;

        private Coroutine _incrementalGCCoroutine; // Coroutine used to mark whether step-by-step GC is being performed

        public static ConfigEntry<bool> FullGCOnSceneUnload { get; private set; }
        public static ConfigEntry<bool> EnablePseudoIncrementalGC { get; private set; }
        public static ConfigEntry<bool> DisableUnload { get; private set; }
        public static ConfigEntry<bool> MaximizeMemoryUsage { get; private set; }
        public static ConfigEntry<int> PercentMemoryThreshold { get; private set; }
        public static ConfigEntry<float> PageFileFreeThreshold { get; private set; }
        public static ConfigEntry<ulong> MinAvailPageFileBytes { get; private set; }
        public static ConfigEntry<bool> DanceMaximizeMemoryUsage { get; private set; }

        public static bool DanceMaximizeMemoryUsageFlag;

        public static ConfigEntry<int> DancePercentMemoryThreshold { get; private set; }
        public static ConfigEntry<float> DancePageFileFreeThreshold { get; private set; }
        public static ConfigEntry<ulong> DanceMinAvailPageFileBytes { get; private set; }

        private Coroutine _periodicGCCoroutine;
        public static ConfigEntry<bool> EnablePeriodicGC { get; private set; }
        public static ConfigEntry<int> PeriodicGCInterval { get; private set; }

        internal void Awake()
        {
            Logger = base.Logger;

            FullGCOnSceneUnload = Config.Bind("Garbage Collection", "EnableFullGCOnSceneUnload", false, "If true, will run a full GC after a scene is unloaded. It will increase loading times, but it can save memory for the next scene.");
            EnablePseudoIncrementalGC = Config.Bind("Garbage Collection", "EnableIncrementalGC", true, "Enable pseudo incremental GC that splits GC.Collect() calls across multiple frames.");

            MaximizeMemoryUsage = Config.Bind("Unload Throttling", "MaximizeMemoryUsage", true, "If true, allows the game to use as much memory as possible, up to the limit set below, to reduce random stuttering caused by garbage collection.");
            PercentMemoryThreshold = Config.Bind("Unload Throttling", "MemoryThreshold", 75, "Allow games and other programs to occupy up to x% of physical memory without garbage collecting the game (default value mean: 75%).");
            PageFileFreeThreshold = Config.Bind("Unload Throttling", "PageFileFreeThreshold", 0.3f, "Minimum ratio (0~1) of page file free. If the free page file ratio is above this threshold, skip garbage collecting (default value mean: 30%).");
            MinAvailPageFileBytes = Config.Bind("Unload Throttling", "MinAvailPageFileBytes", 2UL * 1024UL * 1024UL * 1024UL, "Minimum bytes of available page file. If above this threshold, skip garbage collecting (default value mean: 2GB).");

            DanceMaximizeMemoryUsage = Config.Bind("Dance Unload Throttling", "StopGCDuringDance", true, "If true ,allows the game to use as much memory as possible during dance(include DCM), up to the limit set below, to reduce random stuttering caused by garbage collection.");
            DancePercentMemoryThreshold = Config.Bind("Dance Unload Throttling", "DanceMemoryThreshold", 90, "(Only in dance scene) Allow games and other programs to occupy up to x% of physical memory without garbage collecting the game (default value mean: 90%).");
            DancePageFileFreeThreshold = Config.Bind("Dance Unload Throttling", "DancePageFileFreeThreshold", 0.7f, "(Only in dance scene) Minimum ratio (0~1) of page file free. If the free page file ratio is above this threshold, skip garbage collecting (default value mean: 70%).");
            DanceMinAvailPageFileBytes = Config.Bind("Dance Unload Throttling", "DanceMinAvailPageFile Bytes", 2UL * 1024UL * 1024UL * 1024UL, "(Only in dance scene) Minimum bytes of available page file. If above this threshold, skip garbage collecting (default value mean: 2GB).");

            EnablePeriodicGC = Config.Bind("Periodic GC", "EnablePeriodicGC", false, "Enable periodic Gen 0 garbage collection.");
            PeriodicGCInterval = Config.Bind("Periodic GC", "PeriodicGCInterval", 120, "Interval (in seconds) for periodic Gen 0 garbage collection.");

            DisableUnload = Config.Bind("TEST", "DisableResourceUnload", false, "ONLY USE IN TEST. Disables all resource unloading. Requires large amounts of RAM or will likely crash your game.");

            SceneManager.sceneLoaded += OnSceneLoaded;

            DanceMaximizeMemoryUsageFlag = DanceMaximizeMemoryUsage.Value;

            InstallHooks();
            StartCoroutine(CleanupCo());

            if (EnablePeriodicGC.Value)
            {
                _periodicGCCoroutine = StartCoroutine(PeriodicGCCo());
            }
        }

        /// <summary>
        /// Use MonoMod.RuntimeDetour + Harmony to replace and intercept Resources.UnloadUnusedAssets and GC.Collect
        /// </summary>
        private static void InstallHooks()
        {
            var target = AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets));
            var replacement = AccessTools.Method(typeof(GCHooks), nameof(GCHooks.UnloadUnusedAssetsHook));

            var detour = new NativeDetour(target, replacement);

            _originalUnload = detour.GenerateTrampoline<Func<AsyncOperation>>();

            detour.Apply();

            var harmony = new Harmony(GUID);

            harmony.PatchAll(typeof(GCHooks));
            harmony.PatchAll(typeof(GameMainUnloadSceneHooks));
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
                        if (EnablePseudoIncrementalGC.Value)
                        {
                            if (_incrementalGCCoroutine == null)
                                _incrementalGCCoroutine = StartCoroutine(RunPseudoIncrementalCollect());
                        }
                        else
                        {
                            RunFullGarbageCollect();
                        }
                    }
                }
                yield return null;
            }
        }

        /// <summary>
        /// Pseudo-incremental GC coroutine: split into 3 frames to complete a full collection
        /// </summary>
        private IEnumerator RunPseudoIncrementalCollect()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Logger.LogDebug("Pseudo Incremental GC started");

            int maxGen = GC.MaxGeneration; // Usually 2, but we just be safe

            // Collect Generation 0. Multiple calls can allow short-lived objects to be recycled first.
            // Then execute WaitForPendingFinalizers() to allow the destructor to complete as soon as possible.
            for (int i = 0; i < 2; i++)
            {
#if DEBUG
                Stopwatch stopwatch0 = Stopwatch.StartNew();
#endif

                GC.Collect(0);
                GC.WaitForPendingFinalizers();

#if DEBUG
                stopwatch0.Stop();
                Logger.LogInfo($"GC.Collect(0) completed in {stopwatch0.ElapsedMilliseconds} ms");
#endif
                yield return new WaitForEndOfFrame();
            }

            for (int gen = 1; gen <= maxGen; gen++)
            {
#if DEBUG
                Stopwatch stopwatch1 = Stopwatch.StartNew();
#endif

                GC.Collect(gen);
                GC.WaitForPendingFinalizers();

#if DEBUG
                stopwatch1.Stop();
                Logger.LogInfo($"GC.Collect(0) completed in {stopwatch1.ElapsedMilliseconds} ms");
#endif
                yield return new WaitForEndOfFrame();
            }

            stopwatch.Stop();
            Logger.LogDebug($"Pseudo Incremental GC finished in {stopwatch.ElapsedMilliseconds} ms");
            _incrementalGCCoroutine = null;
        }


        /// <summary>
        /// Trigger resource unloading, using the original trampoline call
        /// </summary>
        private static AsyncOperation RunUnloadAssets()
        {
            // Only allow a single unload operation to run at one time
            if (_currentOperation == null || _currentOperation.isDone && !PlentyOfMemory())
            {
                Logger.LogDebug("Starting unused asset cleanup");
                _currentOperation = _originalUnload();
            }
            return _currentOperation;
        }

        /// <summary>
        /// Perform a full GC immediately
        /// </summary>
        private static void RunFullGarbageCollect()
        {
            if (PlentyOfMemory()) return;

            Stopwatch stopwatch = Stopwatch.StartNew();
            Logger.LogDebug("Starting full garbage collection");

            // Use different overload since we disable the parameterless one
            GC.Collect(GC.MaxGeneration);
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
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    GC.Collect(0);
                    stopwatch.Stop();

                    Logger.LogInfo($"Periodic GC.Collect(0) completed in {stopwatch.ElapsedMilliseconds} ms");
                }
            }
        }


        /// <summary>
        /// Determine whether there is enough memory and there is no need to clean it up immediately
        /// </summary>
        private static bool PlentyOfMemory()
        {
            if (!MaximizeMemoryUsage.Value) return false;

            var mem = MemoryInfo.GetCurrentStatus();
            if (mem == null) return false;

            // Clean up more aggressively during loading, less aggressively during gameplay
            var pageFileFree = mem.ullAvailPageFile / (float)mem.ullTotalPageFile;
            bool plentyOfMemory;

            if (DanceMaximizeMemoryUsageFlag)
            {
                plentyOfMemory = mem.dwMemoryLoad < DancePercentMemoryThreshold.Value // < 90% by default
                                     && pageFileFree >  DancePageFileFreeThreshold.Value  // page file free %, default 70%
                                     && mem.ullAvailPageFile >  DanceMinAvailPageFileBytes.Value; // at least 2GB of page file free
            }
            else
            {
                plentyOfMemory = mem.dwMemoryLoad < PercentMemoryThreshold.Value // < 75% by default
                                     && pageFileFree > PageFileFreeThreshold.Value  // page file free %, default 30%
                                     && mem.ullAvailPageFile > MinAvailPageFileBytes.Value; // at least 2GB of page file free
            }

            if (!plentyOfMemory)
                return false;

            Logger.LogDebug($"GC called, but skipping cleanup because of low memory load ({mem.dwMemoryLoad}% RAM, {100 - (int)(pageFileFree * 100)}% Page file, {mem.ullAvailPageFile / 1024 / 1024}MB available in Page file)");
            return true;
        }


        public void OnSceneLoaded(Scene scene, LoadSceneMode sceneMode)
        {
            if (scene.name.ToLower().Contains("dance"))
            {
                if (DanceMaximizeMemoryUsage.Value)
                {
                    Logger.LogDebug("We are in dane scene, stopping GC~");
                    DanceMaximizeMemoryUsageFlag = true;
                }
            }
            else
            {
                DanceMaximizeMemoryUsageFlag = true;
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

        [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.UnloadScene), new Type[] { typeof(string) })]
        public static class GameMainUnloadSceneHooks
        {
            static void Postfix(string f_strSceneName)
            {
                if (!FullGCOnSceneUnload.Value)
                {
                    return;
                }

                Logger.LogDebug($"GameMain.UnloadScene({f_strSceneName}) finished, running a full GC...");

                GC.Collect(GC.MaxGeneration);
                GC.WaitForPendingFinalizers();
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
                    Logger.LogDebug("We are in dancing, stopping GC~");
                    DanceMaximizeMemoryUsageFlag = true;
                }
            }

            [HarmonyPatch(typeof(COM3D2.DanceCameraMotion.Plugin.DanceCameraMotion), "EndFreeDance")]
            [HarmonyPostfix]
            private static void EndFreeDancePostfix()
            {
                Logger.LogDebug("We are exit dancing, end stopping GC~");
                DanceMaximizeMemoryUsageFlag = false;
            }
        }
    }
}
