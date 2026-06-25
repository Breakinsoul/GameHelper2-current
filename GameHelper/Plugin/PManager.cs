// <copyright file="PManager.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Plugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using Coroutine;
    using CoroutineEvents;
    using CTOUtils = ClickableTransparentOverlay.Win32.Utils;
    using Settings;
    using Ui;
    using Utils;

    internal record PluginWithName(string Name, IPCore Plugin, PluginAssemblyLoadContext Alc, string Directory);

    internal record PluginContainer(string Name, IPCore Plugin, PluginMetadata Metadata, PluginAssemblyLoadContext Alc, string Directory);

    internal sealed class PluginRuntimeStatus
    {
        public double LastDrawUiMs { get; set; }

        public double AverageDrawUiMs { get; set; }

        public int DrawUiCount { get; set; }

        public int ExceptionCount { get; set; }

        public string LastException { get; set; } = string.Empty;
    }

    /// <summary>
    ///     Finds, loads and unloads the plugins.
    /// </summary>
    internal static class PManager
    {
        private static bool disableRendering = false;
        private static bool isShuttingDown = false;
        private static readonly Dictionary<string, PluginRuntimeStatus> RuntimeStatuses = new();
#if DEBUG
        internal static readonly List<string> PluginNames = new();
#endif
        internal static readonly List<PluginContainer> Plugins = new();

        internal static PluginContainer[] GetPluginsSnapshot()
        {
            lock (Plugins)
            {
                return Plugins.ToArray();
            }
        }

        internal static PluginRuntimeStatus GetRuntimeStatus(string pluginName)
        {
            lock (RuntimeStatuses)
            {
                if (!RuntimeStatuses.TryGetValue(pluginName, out var status))
                {
                    status = new PluginRuntimeStatus();
                    RuntimeStatuses[pluginName] = status;
                }

                return new PluginRuntimeStatus
                {
                    LastDrawUiMs = status.LastDrawUiMs,
                    AverageDrawUiMs = status.AverageDrawUiMs,
                    DrawUiCount = status.DrawUiCount,
                    ExceptionCount = status.ExceptionCount,
                    LastException = status.LastException,
                };
            }
        }

        /// <summary>
        ///     Initlizes the plugin manager by loading all the plugins and their Metadata.
        /// </summary>
        internal static void InitializePlugins()
        {
            State.PluginsDirectory.Create(); // doesn't do anything if already exists.
            LoadPluginMetadata(LoadPlugins());
#if DEBUG
            GetAllPluginNames();
#endif
            // F-079: replaced Parallel.ForEach with foreach. Plugin OnEnable is rare
            // (once at startup), should be single-threaded for plugin authors who
            // assume ImGui / coroutine-registration semantics work on the render thread.
            foreach (var container in GetPluginsSnapshot())
            {
                EnablePluginIfRequired(container);
            }
            CoroutineHandler.Start(SavePluginSettingsCoroutine());
            CoroutineHandler.Start(SavePluginMetadataCoroutine());
            Core.CoroutinesRegistrar.Add(CoroutineHandler.Start(
                DrawPluginUiRenderCoroutine(), "[PManager] Draw Plugins UI"));
        }

        private static List<PluginWithName> LoadPlugins()
        {
            return GetPluginsDirectories()
                  .AsParallel()
                  .Select(LoadPlugin)
                  .Where(x => x != null)
                  .Select(x => x!)
                  .OrderBy(x => x.Name)
                  .ToList();
        }

#if DEBUG
        private static void GetAllPluginNames()
        {
            foreach (var plugin in GetPluginsSnapshot())
            {
                PluginNames.Add(plugin.Name);
            }
        }

        /// <summary>
        ///     Cleans up the already loaded plugins.
        /// </summary>
        internal static bool UnloadPlugin(string name)
        {
            PluginContainer? target;
            lock (Plugins)
            {
                target = Plugins.FirstOrDefault(p => p.Name == name);
            }

            if (target == null)
            {
                return false;
            }

            target.Plugin.SaveSettings();
            target.Plugin.OnDisable();

            lock (Plugins)
            {
                Plugins.Remove(target);
            }

            // F-075: actually unload the assembly via the collectible ALC tracked
            // in the PluginContainer (F-074 made the ALC collectible).
            var alcRef = new WeakReference(target.Alc);
            target.Alc.Unload();

            // Release the strong reference to the PluginContainer (and its Alc field)
            // BEFORE the GC loop. The .NET docs require this — without it, the JIT
            // can keep `target` rooted across the loop and alcRef.IsAlive stays true
            // forever (spurious warning log). See:
            // https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability
            target = null;

            // Run GC repeatedly until the ALC is unreachable (or we give up after
            // 10 attempts).
            for (var i = 0; i < 10 && alcRef.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            if (alcRef.IsAlive)
            {
                Console.WriteLine($"[PManager.UnloadPlugin] {name}: ALC still alive after 10 GC cycles - likely a static reflection cache pinning a type. Plugin removed from manager but assembly remains loaded.");
            }

            return true;
        }

        internal static bool LoadPlugin(string name)
        {
            try
            {
                var container = GetPluginsDirectories()
                                .Where(x => x.Name.Contains(name))
                                .Select(LoadPlugin)
                                .Where(y => y != null)
                                .Select(y => y!)
                                .ToList();
                if (container.Count > 0)
                {
                    LoadPluginMetadata(container);
                    container[0].Plugin.OnEnable(Core.Process.Address != IntPtr.Zero);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
#endif

        private static List<DirectoryInfo> GetPluginsDirectories()
        {
            return State.PluginsDirectory.GetDirectories().Where(
                x => (x.Attributes & FileAttributes.Hidden) == 0).ToList();
        }

        private static (Assembly assembly, PluginAssemblyLoadContext alc)? ReadPluginFiles(DirectoryInfo pluginDirectory)
        {
            try
            {
                var dllFile = pluginDirectory.GetFiles(
                    $"{pluginDirectory.Name}*.dll",
                    SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (dllFile == null)
                {
                    Console.WriteLine($"Couldn't find plugin dll with name {pluginDirectory.Name}" +
                                      $" in directory {pluginDirectory.FullName}." +
                                      " Please make sure DLL & the plugin got same name.");
                    return null;
                }

                var alc = new PluginAssemblyLoadContext(dllFile.FullName);
                var assembly = alc.LoadFromAssemblyPath(dllFile.FullName);
                return (assembly, alc);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to load plugin {pluginDirectory.FullName} due to {e}");
                return null;
            }
        }


        private static PluginWithName? LoadPlugin(DirectoryInfo pluginDirectory)
        {
            var loaded = ReadPluginFiles(pluginDirectory);
            if (loaded != null)
            {
                var relativePluginDir = pluginDirectory.FullName.Replace(
                    State.PluginsDirectory.FullName, State.PluginsDirectory.Name);
                return LoadPlugin(loaded.Value.assembly, loaded.Value.alc, relativePluginDir);
            }

            return null;
        }

        private static PluginWithName? LoadPlugin(Assembly assembly, PluginAssemblyLoadContext alc, string pluginRootDirectory)
        {
            try
            {
                var types = assembly.GetTypes();
                if (types.Length <= 0)
                {
                    Console.WriteLine($"Plugin (in {pluginRootDirectory}) {assembly} doesn't " +
                                      "contain any types (i.e. classes/stuctures).");
                    return null;
                }

                var iPluginClasses = types.Where(
                    type => typeof(IPCore).IsAssignableFrom(type) &&
                            type.IsSealed).ToList();
                if (iPluginClasses.Count != 1)
                {
                    Console.WriteLine($"Plugin (in {pluginRootDirectory}) {assembly} contains" +
                                      $" {iPluginClasses.Count} sealed classes derived from CoreBase<TSettings>." +
                                      " It should have one sealed class derived from IPlugin.");
                    return null;
                }

                var pluginCore = Activator.CreateInstance(iPluginClasses[0]) as IPCore;
                if (pluginCore == null)
                {
                    Console.WriteLine($"Plugin (in {pluginRootDirectory}) {assembly} failed to instantiate IPCore-derived class.");
                    return null;
                }

                pluginCore.SetPluginDllLocation(pluginRootDirectory);
                return new PluginWithName(assembly.GetName().Name ?? string.Empty, pluginCore, alc, pluginRootDirectory);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error loading plugin {assembly.FullName} due to {e}");
                return null;
            }
        }

        private static void LoadPluginMetadata(IEnumerable<PluginWithName> plugins)
        {
            var metadata = JsonHelper.CreateOrLoadJsonFile<Dictionary<string, PluginMetadata>>(State.PluginsMetadataFile);
            var newContainers = plugins.Select(
                x => new PluginContainer(
                    x.Name,
                    x.Plugin,
                    metadata.GetValueOrDefault(
                        x.Name,
                        new PluginMetadata { Enable = IsEnabledByDefault(x.Name) }),
                    x.Alc,
                    x.Directory)).ToList();

            lock (Plugins)
            {
                Plugins.AddRange(newContainers);
            }

            SavePluginMetadata();
        }

        private static bool IsEnabledByDefault(string pluginName)
        {
            return pluginName is "EncounterHelper" or "HealthBars" or "Radar";
        }

        private static void EnablePluginIfRequired(PluginContainer container)
        {
            if (container.Metadata.Enable)
            {
                container.Plugin.OnEnable(Core.Process.Address != IntPtr.Zero);
            }
        }

        private static void SavePluginMetadata()
        {
            Dictionary<string, PluginMetadata> snapshot;
            lock (Plugins)
            {
                snapshot = Plugins.ToDictionary(x => x.Name, x => x.Metadata);
            }

            JsonHelper.SafeToFile(snapshot, State.PluginsMetadataFile);
        }

        internal static void SaveAllSettings()
        {
            SavePluginMetadata();
            SaveAllPluginSettings();
        }

        internal static void ShutdownPlugins()
        {
            if (isShuttingDown)
            {
                return;
            }

            isShuttingDown = true;
            SaveAllSettings();
            foreach (var container in GetPluginsSnapshot())
            {
                if (!container.Metadata.Enable)
                {
                    continue;
                }

                try
                {
                    container.Plugin.OnDisable();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PManager.ShutdownPlugins] {container.Name} threw on disable: {ex}");
                }
            }
        }

        private static IEnumerator<Wait> SavePluginMetadataCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.TimeToSaveAllSettings);
                SavePluginMetadata();
            }
        }

        private static IEnumerator<Wait> SavePluginSettingsCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.TimeToSaveAllSettings);
                SaveAllPluginSettings();
            }
        }

        private static void SaveAllPluginSettings()
        {
            foreach (var container in GetPluginsSnapshot())
            {
                try
                {
                    container.Plugin.SaveSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PManager.SaveAllPluginSettings] {container.Name} threw on save: {ex}");
                }
            }
        }

        private static IEnumerator<Wait> DrawPluginUiRenderCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnRender);
                if (CTOUtils.IsKeyPressedAndNotTimeout(Core.GHSettings.DisableAllRenderingKey))
                {
                    disableRendering = !disableRendering;
                }

                if (disableRendering)
                {
                    continue;
                }

                foreach (var container in GetPluginsSnapshot())
                {
                    if (container.Metadata.Enable)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            using var _ = PerformanceProfiler.Profile(container.Plugin.GetType().FullName ?? string.Empty, "DrawUI");
                            container.Plugin.DrawUI();
                            sw.Stop();
                            UpdateRuntimeStatus(container.Name, sw.Elapsed.TotalMilliseconds, null);
                        }
                        catch (Exception ex)
                        {
                            sw.Stop();
                            UpdateRuntimeStatus(container.Name, sw.Elapsed.TotalMilliseconds, ex);
                            AppLogger.Error($"Plugin {container.Name} DrawUI", ex);
                            Console.WriteLine($"[PManager.DrawPluginUiRenderCoroutine] {container.Name} threw: {ex}");
                        }
                    }
                }
            }
        }

        private static void UpdateRuntimeStatus(string pluginName, double drawMs, Exception? ex)
        {
            lock (RuntimeStatuses)
            {
                if (!RuntimeStatuses.TryGetValue(pluginName, out var status))
                {
                    status = new PluginRuntimeStatus();
                    RuntimeStatuses[pluginName] = status;
                }

                status.LastDrawUiMs = drawMs;
                status.DrawUiCount++;
                status.AverageDrawUiMs = status.AverageDrawUiMs == 0
                    ? drawMs
                    : (status.AverageDrawUiMs * 0.95) + (drawMs * 0.05);
                if (ex != null)
                {
                    status.ExceptionCount++;
                    status.LastException = ex.Message;
                }
            }
        }
    }
}
