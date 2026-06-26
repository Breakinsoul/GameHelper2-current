// <copyright file="SettingsWindow.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Settings
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text.Json;
    using ClickableTransparentOverlay;
    using ClickableTransparentOverlay.Win32;
    using Coroutine;
    using CoroutineEvents;
    using ImGuiNET;
    using Plugin;
    using Utils;
    using GameOffsets.Objects.States.InGameState;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;

    /// <summary>
    ///     Creates the MainMenu on the UI.
    /// </summary>
    internal static class SettingsWindow
    {
        private enum SettingsPage
        {
            Overview,
            Core,
            Theme,
            Diagnostics,
            Plugins,
            Plugin,
        }

        private static readonly Vector4 AccentCoral = new(1.00f, 0.35f, 0.25f, 1f);
        private static readonly Vector4 AccentPink = new(1.00f, 0.24f, 0.58f, 1f);
        private static readonly Vector4 AccentMint = new(0.25f, 0.95f, 0.72f, 1f);
        private static readonly Vector4 TextStrong = new(0.95f, 0.96f, 0.98f, 1f);
        private static bool isOverlayRunningLocal = true;
        private static bool isSettingsWindowVisible = true;
        private static SettingsPage selectedPage = SettingsPage.Overview;
        private static string selectedPlugin = string.Empty;
        private static string diagnosticsExportStatus = string.Empty;
        private static string pluginManagerStatus = string.Empty;

        private static EntityFilterType efilterType = EntityFilterType.PATH;
        private static string filterText = string.Empty;
        private static Rarity erarity = Rarity.Normal;
        private static GameStats eStats = 0;
        private static int filterGroup = 0;

        private static string specialNpcPath = string.Empty;

        private static string specialMiscObjPath = string.Empty;

        private static string monterPathToIgnore = string.Empty;

#if DEBUG
        private static string pluginForHotReload = string.Empty;
        private static bool pluginLoaded = true;
        private static bool showImGuiDemo = false;
#endif

        /// <summary>
        ///     Initializes the Main Menu.
        /// </summary>
        internal static void InitializeCoroutines()
        {
            HideOnStartCheck();
            CoroutineHandler.Start(SaveCoroutine());
            Core.CoroutinesRegistrar.Add(CoroutineHandler.Start(
                RenderCoroutine(),
                "[Settings] Draw Core/Plugin settings",
                int.MaxValue));
        }

        private static void TogglePlugin(PluginContainer container, bool isEnabled)
        {
            if (container.Metadata.Enable == isEnabled)
            {
                return;
            }

            container.Metadata.Enable = isEnabled;
            if (container.Metadata.Enable)
            {
                container.Plugin.OnEnable(Core.Process.Address != IntPtr.Zero);
            }
            else
            {
                container.Plugin.SaveSettings();
                container.Plugin.OnDisable();
                if (selectedPage == SettingsPage.Plugin && selectedPlugin == container.Name)
                {
                    selectedPage = SettingsPage.Plugins;
                    selectedPlugin = string.Empty;
                }
            }
        }

        private static void DrawModernSettings()
        {
            var plugins = PManager.GetPluginsSnapshot();
            DrawHeader(plugins);
            ImGui.Spacing();

            var contentHeight = ImGui.GetContentRegionAvail().Y;
            ImGui.BeginChild("SettingsSidebar", new Vector2(235f, contentHeight), ImGuiChildFlags.Borders);
            DrawSidebar(plugins);
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("SettingsContent", new Vector2(0f, contentHeight), ImGuiChildFlags.None);
            DrawSelectedPage(plugins);
            ImGui.EndChild();
        }

        private static void DrawHeader(PluginContainer[] plugins)
        {
            var enabledPlugins = 0;
            foreach (var plugin in plugins)
            {
                if (plugin.Metadata.Enable)
                {
                    enabledPlugins++;
                }
            }

            ImGui.TextColored(AccentMint, "GAMEHELPER2");
            ImGui.SameLine();
            ImGui.TextDisabled(Core.GetVersion());

            ImGui.TextColored(AccentCoral, "Overlay");
            ImGui.SameLine();
            ImGui.TextColored(AccentPink, "Control Center");
            ImGui.SameLine();
            ImGui.TextDisabled($"  {Core.States.GameCurrentState}");

            ImGui.Spacing();
            DrawPill($"Plugins {enabledPlugins}/{plugins.Length}", AccentMint);
            ImGui.SameLine();
            DrawPill(Core.Process.Pid == 0 ? "Detached" : $"PID {Core.Process.Pid}", AccentPink);
            ImGui.SameLine();
            DrawPill($"FPS {ImGui.GetIO().Framerate:0}", AccentCoral);
            ImGui.SameLine();
            DrawPill(Core.Process.Foreground ? "Foreground" : "Background", new Vector4(0.47f, 0.56f, 1f, 1f));
        }

        private static void DrawSidebar(PluginContainer[] plugins)
        {
            ImGui.TextDisabled("MAIN");
            DrawSidebarButton("Overview", SettingsPage.Overview);
            DrawSidebarButton("Settings", SettingsPage.Core);
            DrawSidebarButton("Theme", SettingsPage.Theme);
            DrawSidebarButton("Plugins", SettingsPage.Plugins);

            ImGui.Separator();
            ImGui.TextDisabled("MODULES");
            foreach (var container in plugins)
            {
                var enabled = container.Metadata.Enable;
                if (ImGui.Checkbox($"##enable-{container.Name}", ref enabled))
                {
                    TogglePlugin(container, enabled);
                }

                ImGui.SameLine();
                ImGui.BeginDisabled(!container.Metadata.Enable);
                if (ImGui.Selectable(container.Name, selectedPage == SettingsPage.Plugin && selectedPlugin == container.Name))
                {
                    selectedPage = SettingsPage.Plugin;
                    selectedPlugin = container.Name;
                }

                ImGui.EndDisabled();
            }

#if DEBUG
            ImGui.Separator();
            ImGui.TextDisabled("DEV");
            if (ImGui.SmallButton("ImGui Demo"))
            {
                showImGuiDemo = !showImGuiDemo;
            }

            if (showImGuiDemo)
            {
                ImGui.ShowDemoWindow(ref showImGuiDemo);
            }
#endif
        }

        private static void DrawSidebarButton(string label, SettingsPage page)
        {
            if (ImGui.Selectable(label, selectedPage == page))
            {
                selectedPage = page;
                if (page != SettingsPage.Plugin)
                {
                    selectedPlugin = string.Empty;
                }
            }
        }

        private static void DrawSelectedPage(PluginContainer[] plugins)
        {
            switch (selectedPage)
            {
                case SettingsPage.Core:
                    DrawSectionHeader("Settings", "Runtime options without debug clutter.");
                    DrawCoreSettings();
                    break;
                case SettingsPage.Theme:
                    DrawSectionHeader("Theme", "Choose the overlay skin.");
                    DrawThemeSettings();
                    break;
                case SettingsPage.Plugins:
                    DrawSectionHeader("Plugins", "Enable modules and open their settings.");
                    DrawPluginManager(plugins);
                    break;
                case SettingsPage.Diagnostics:
                    DrawSectionHeader("Diagnostics", "Offsets, projection inputs and exportable runtime snapshots.");
                    DrawDiagnostics();
                    break;
                case SettingsPage.Plugin:
                    DrawPluginSettings(plugins);
                    break;
                default:
                    DrawOverview(plugins);
                    break;
            }
        }

        private static void DrawSectionHeader(string title, string subtitle)
        {
            ImGui.TextColored(TextStrong, title);
            ImGui.TextDisabled(subtitle);
            ImGui.Spacing();
        }

        private static void DrawOverview(PluginContainer[] plugins)
        {
            DrawSectionHeader("Overview", "Current overlay state.");

            if (ImGui.BeginTable("OverviewMetrics", 4, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
            {
                DrawMetric("Game State", Core.States.GameCurrentState.ToString());
                DrawMetric("Foreground", Core.Process.Foreground ? "Yes" : "No");
                DrawMetric("FPS", $"{ImGui.GetIO().Framerate:0}");
                DrawMetric("Process", Core.Process.Pid == 0 ? "Detached" : $"PID {Core.Process.Pid}");
                ImGui.EndTable();
            }

            ImGui.Spacing();
            if (ImGui.Button("Theme", new Vector2(150f, ImGui.GetTextLineHeight() * 1.9f)))
            {
                selectedPage = SettingsPage.Theme;
            }

            ImGui.SameLine();
            if (ImGui.Button("Plugins", new Vector2(150f, ImGui.GetTextLineHeight() * 1.9f)))
            {
                selectedPage = SettingsPage.Plugins;
            }

            ImGui.SameLine();
            if (ImGui.Button("Save", new Vector2(120f, ImGui.GetTextLineHeight() * 1.9f)))
            {
                CoroutineHandler.RaiseEvent(GameHelperEvents.TimeToSaveAllSettings);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Advanced"))
            {
                selectedPage = SettingsPage.Diagnostics;
            }

            ImGui.Spacing();
            DrawPluginManager(plugins);
        }

        private static void DrawMetric(string label, string value)
        {
            ImGui.TableNextColumn();
            ImGui.TextDisabled(label);
            ImGui.TextWrapped(value);
        }

        private static void DrawPill(string label, Vector4 dotColor)
        {
            ImGui.TextColored(dotColor, "*");
            ImGui.SameLine();
            ImGui.TextUnformatted(label);
        }

        private static void DrawPluginManager(PluginContainer[] plugins)
        {
            if (ImGui.Button("Open plugins folder"))
            {
                OpenPath(State.PluginsDirectory.FullName);
            }

            ImGui.SameLine();
            if (ImGui.Button("Open configs folder"))
            {
                OpenPath(State.CoreSettingFile.DirectoryName ?? "configs");
            }

            ImGui.SameLine();
            if (ImGui.Button("Backup all plugin configs"))
            {
                pluginManagerStatus = BackupAllPluginConfigs(plugins);
            }

            if (!string.IsNullOrEmpty(pluginManagerStatus))
            {
                ImGui.TextDisabled(pluginManagerStatus);
            }

            if (ImGui.BeginTable("PluginManager", 7, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupColumn("Plugin");
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 120f);
                ImGui.TableSetupColumn("Draw ms", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableSetupColumn("Avg ms", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 120f);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 310f);
                ImGui.TableHeadersRow();

                foreach (var container in plugins)
                {
                    var pluginDir = Path.GetFullPath(container.Directory);
                    var pluginDll = Path.Combine(pluginDir, $"{container.Name}.dll");
                    var configDir = Path.Combine(pluginDir, "config");
                    var settingsPath = Path.Combine(configDir, "settings.txt");
                    var status = PManager.GetRuntimeStatus(container.Name);

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var enabled = container.Metadata.Enable;
                    if (ImGui.Checkbox($"##pm-enable-{container.Name}", ref enabled))
                    {
                        TogglePlugin(container, enabled);
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(container.Name);
                    ImGuiHelper.ToolTip(pluginDir);

                    ImGui.TableNextColumn();
                    ImGui.Text(status.ExceptionCount == 0 ? "Ready" : $"Errors {status.ExceptionCount}");
                    if (!string.IsNullOrEmpty(status.LastException))
                    {
                        ImGuiHelper.ToolTip(status.LastException);
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text($"{status.LastDrawUiMs:0.00}");

                    ImGui.TableNextColumn();
                    ImGui.Text($"{status.AverageDrawUiMs:0.00}");

                    ImGui.TableNextColumn();
                    ImGui.Text(GetPluginDllVersion(pluginDll));

                    ImGui.TableNextColumn();
                    ImGui.BeginDisabled(!container.Metadata.Enable);
                    if (ImGui.SmallButton($"Settings##pm-open-{container.Name}"))
                    {
                        selectedPage = SettingsPage.Plugin;
                        selectedPlugin = container.Name;
                    }

                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Folder##pm-folder-{container.Name}"))
                    {
                        OpenPath(pluginDir);
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Config##pm-config-{container.Name}"))
                    {
                        OpenPath(Directory.Exists(configDir) ? configDir : pluginDir);
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Backup##pm-backup-{container.Name}"))
                    {
                        pluginManagerStatus = BackupPluginConfig(container.Name, configDir);
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Restore##pm-restore-{container.Name}"))
                    {
                        pluginManagerStatus = RestoreLatestPluginConfig(container.Name, configDir);
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Reset##pm-reset-{container.Name}"))
                    {
                        pluginManagerStatus = ResetPluginConfig(container, settingsPath);
                    }
                }

                ImGui.EndTable();
            }
        }

        private static string GetPluginDllVersion(string pluginDll)
        {
            if (!File.Exists(pluginDll))
            {
                return "-";
            }

            try
            {
                var info = FileVersionInfo.GetVersionInfo(pluginDll);
                return string.IsNullOrWhiteSpace(info.FileVersion) ? "-" : info.FileVersion;
            }
            catch
            {
                return "-";
            }
        }

        private static void OpenPath(string path)
        {
            try
            {
                if (Directory.Exists(path) || File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                pluginManagerStatus = $"Open failed: {ex.Message}";
            }
        }

        private static string BackupAllPluginConfigs(PluginContainer[] plugins)
        {
            var ok = 0;
            foreach (var plugin in plugins)
            {
                var configDir = Path.Combine(Path.GetFullPath(plugin.Directory), "config");
                if (BackupPluginConfig(plugin.Name, configDir).StartsWith("Backup:", StringComparison.Ordinal))
                {
                    ok++;
                }
            }

            return $"Backed up {ok}/{plugins.Length} plugin config folders.";
        }

        private static string BackupPluginConfig(string pluginName, string configDir)
        {
            if (!Directory.Exists(configDir))
            {
                return $"{pluginName}: no config folder.";
            }

            var backupDir = Path.Combine("configs", "plugin-backups", pluginName, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            CopyDirectory(configDir, backupDir, overwrite: true);
            return $"Backup: {pluginName}";
        }

        private static string RestoreLatestPluginConfig(string pluginName, string configDir)
        {
            var pluginBackupRoot = Path.Combine("configs", "plugin-backups", pluginName);
            if (!Directory.Exists(pluginBackupRoot))
            {
                return $"{pluginName}: no backups.";
            }

            var latest = Directory.GetDirectories(pluginBackupRoot).OrderByDescending(x => x).FirstOrDefault();
            if (latest == null)
            {
                return $"{pluginName}: no backups.";
            }

            CopyDirectory(latest, configDir, overwrite: true);
            return $"Restored: {pluginName}";
        }

        private static string ResetPluginConfig(PluginContainer container, string settingsPath)
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    File.Delete(settingsPath);
                }

                if (container.Metadata.Enable)
                {
                    container.Plugin.OnDisable();
                    container.Plugin.OnEnable(Core.Process.Address != IntPtr.Zero);
                }

                return $"Reset: {container.Name}";
            }
            catch (Exception ex)
            {
                return $"Reset failed: {container.Name}: {ex.Message}";
            }
        }

        private static void CopyDirectory(string sourceDir, string destinationDir, bool overwrite)
        {
            Directory.CreateDirectory(destinationDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)), overwrite);
            }
        }

        private static void DrawPluginSettings(PluginContainer[] plugins)
        {
            PluginContainer? activePlugin = null;
            foreach (var plugin in plugins)
            {
                if (plugin.Name == selectedPlugin)
                {
                    activePlugin = plugin;
                    break;
                }
            }

            if (activePlugin == null || !activePlugin.Metadata.Enable)
            {
                selectedPage = SettingsPage.Plugins;
                selectedPlugin = string.Empty;
                DrawSelectedPage(plugins);
                return;
            }

            DrawSectionHeader(activePlugin.Name, "Plugin-specific controls.");
            activePlugin.Plugin.DrawSettings();
        }

        private static void DrawDiagnostics()
        {
            DrawAdvancedTools();
            ImGui.Separator();

            ImGui.Text($"Process: {(Core.Process.Pid == 0 ? "Detached" : $"PID {Core.Process.Pid}")}");
            ImGui.Text($"Game State: {Core.States.GameCurrentState}");
            ImGui.Text($"Static Base: 0x{Core.Process.Address.ToInt64():X}");
            ImGui.Separator();

            if (ImGui.TreeNode("Static Offsets"))
            {
                foreach (var item in Core.Process.StaticAddresses)
                {
                    ImGuiHelper.IntPtrToImGui(item.Key, item.Value);
                }

                ImGui.TreePop();
            }

            if (ImGui.TreeNode("Render / TerrainHeight"))
            {
                if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
                {
                    ImGui.Text($"Selected TerrainHeight offset: 0x{Render.TerrainHeightOffset:X}");
                    ImGui.Text($"Player grid: {playerRender.GridPosition}");
                    ImGui.Text($"Player world: {playerRender.WorldPosition}");
                    ImGui.Text($"Player terrain height: {playerRender.TerrainHeight:0.####}");

                    if (ImGui.BeginTable("TerrainHeightCandidates", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                    {
                        ImGui.TableSetupColumn("Offset");
                        ImGui.TableSetupColumn("Value");
                        ImGui.TableSetupColumn("Expected");
                        ImGui.TableSetupColumn("Delta");
                        ImGui.TableSetupColumn("OK");
                        ImGui.TableHeadersRow();
                        foreach (var candidate in playerRender.GetTerrainHeightDiagnostics())
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text($"0x{candidate.Offset:X}");
                            ImGui.TableNextColumn();
                            ImGui.Text(float.IsFinite(candidate.Value) ? $"{candidate.Value:0.####}" : "NaN");
                            ImGui.TableNextColumn();
                            ImGui.Text(candidate.Expected?.ToString("0.####") ?? "n/a");
                            ImGui.TableNextColumn();
                            ImGui.Text(float.IsFinite(candidate.Delta) ? $"{candidate.Delta:0.####}" : "n/a");
                            ImGui.TableNextColumn();
                            ImGui.Text(candidate.Plausible ? "yes" : "no");
                        }

                        ImGui.EndTable();
                    }
                }
                else
                {
                    ImGui.Text("Player Render component is not available.");
                }

                ImGui.TreePop();
            }

            if (ImGui.Button("Export Snapshot", new Vector2(180f, ImGui.GetTextLineHeight() * 1.9f)))
            {
                ExportDebugSnapshot();
            }

            if (!string.IsNullOrEmpty(diagnosticsExportStatus))
            {
                ImGui.SameLine();
                ImGui.TextWrapped(diagnosticsExportStatus);
            }
        }

        private static void ExportDebugSnapshot()
        {
            try
            {
                Directory.CreateDirectory("diagnostics");
                object? renderSnapshot = null;
                if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
                {
                    renderSnapshot = new
                    {
                        TerrainHeightOffset = $"0x{Render.TerrainHeightOffset:X}",
                        GridPosition = ToObject(playerRender.GridPosition),
                        WorldPosition = ToObject(playerRender.WorldPosition),
                        TerrainHeight = playerRender.TerrainHeight,
                        Candidates = playerRender.GetTerrainHeightDiagnostics().Select(x => new
                        {
                            Offset = $"0x{x.Offset:X}",
                            Value = SanitizeFloat(x.Value),
                            Expected = SanitizeFloat(x.Expected),
                            Delta = SanitizeFloat(x.Delta),
                            x.Plausible,
                        }).ToArray(),
                    };
                }

                var currentArea = Core.States.InGameStateObject.CurrentAreaInstance;
                var snapshot = new
                {
                    TimeUtc = DateTime.UtcNow,
                    ProcessId = Core.Process.Pid,
                    BaseAddress = $"0x{Core.Process.Address.ToInt64():X}",
                    GameState = Core.States.GameCurrentState.ToString(),
                    StaticAddresses = Core.Process.StaticAddresses.ToDictionary(
                        x => x.Key,
                        x => $"0x{x.Value.ToInt64():X}"),
                    Render = renderSnapshot,
                    Area = new
                    {
                        currentArea.AreaHash,
                        currentArea.CurrentAreaLevel,
                        EntityCount = currentArea.AwakeEntities.Count,
                        currentArea.NetworkBubbleEntityCount,
                    },
                };

                var path = Path.Join("diagnostics", $"snapshot-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                var fullPath = Path.GetFullPath(path);
                ImGui.SetClipboardText(fullPath);
                diagnosticsExportStatus = $"Exported: {fullPath}";
            }
            catch (Exception ex)
            {
                diagnosticsExportStatus = $"Export failed: {ex.Message}";
                AppLogger.Error("Diagnostics export", ex);
            }
        }

        private static object ToObject(GameOffsets.Natives.StdTuple3D<float> value)
        {
            return new
            {
                X = SanitizeFloat(value.X),
                Y = SanitizeFloat(value.Y),
                Z = SanitizeFloat(value.Z),
            };
        }

        private static float? SanitizeFloat(float value)
        {
            return float.IsFinite(value) ? value : null;
        }

        private static float? SanitizeFloat(float? value)
        {
            return value != null && float.IsFinite(value.Value) ? value.Value : null;
        }

        private static void DrawThemeSettings()
        {
            var availableWidth = ImGui.GetContentRegionAvail().X;
            var columns = availableWidth >= 980f ? 3 : availableWidth >= 620f ? 2 : 1;
            if (ImGui.BeginTable("ThemeGallery", columns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
            {
                foreach (var theme in ThemeManager.Themes)
                {
                    ImGui.TableNextColumn();
                    var isSelected = Core.GHSettings.Theme == theme.Theme;
                    var accent = GetThemeAccent(theme.Theme);
                    var muted = GetThemeMuted(theme.Theme);
                    if (isSelected)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Border, accent);
                        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
                    }

                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(accent.X, accent.Y, accent.Z, isSelected ? 0.65f : 0.24f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(accent.X, accent.Y, accent.Z, 0.82f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, accent);
                    if (ImGui.Button($"{theme.Name}##theme-{theme.Theme}", new Vector2(-1f, ImGui.GetTextLineHeight() * 2.3f)))
                    {
                        Core.GHSettings.Theme = theme.Theme;
                        ThemeManager.Apply(theme.Theme);
                    }

                    ImGui.PopStyleColor(3);

                    if (isSelected)
                    {
                        ImGui.PopStyleVar();
                        ImGui.PopStyleColor();
                    }

                    DrawThemeSwatches(accent, muted, isSelected);
                    ImGui.Spacing();
                }

                ImGui.EndTable();
            }
        }

        private static void DrawThemeSwatches(Vector4 accent, Vector4 muted, bool isSelected)
        {
            ImGui.TextColored(accent, "*");
            ImGui.SameLine();
            ImGui.TextColored(muted, "*");
            ImGui.SameLine();
            ImGui.TextColored(TextStrong, isSelected ? "Selected" : "Available");
        }

        private static Vector4 GetThemeAccent(AppTheme theme)
        {
            return theme switch
            {
                AppTheme.CarbonMint => new Vector4(0.48f, 0.93f, 0.76f, 1f),
                AppTheme.MidnightAmber => new Vector4(1.00f, 0.66f, 0.24f, 1f),
                AppTheme.ArcticGlass => new Vector4(0.14f, 0.36f, 0.58f, 1f),
                AppTheme.SolarSlate => new Vector4(0.95f, 0.72f, 0.22f, 1f),
                AppTheme.CrimsonSteel => new Vector4(0.89f, 0.20f, 0.25f, 1f),
                AppTheme.ForestCircuit => new Vector4(0.45f, 0.82f, 0.46f, 1f),
                AppTheme.Oceanic => new Vector4(0.27f, 0.79f, 0.89f, 1f),
                AppTheme.VioletTerminal => new Vector4(0.67f, 0.48f, 0.95f, 1f),
                AppTheme.PaperDark => new Vector4(0.74f, 0.65f, 0.48f, 1f),
                AppTheme.NeonGlass => AccentPink,
                _ => new Vector4(0.35f, 0.57f, 0.95f, 1f),
            };
        }

        private static Vector4 GetThemeMuted(AppTheme theme)
        {
            return theme switch
            {
                AppTheme.CarbonMint => new Vector4(0.20f, 0.50f, 0.43f, 1f),
                AppTheme.MidnightAmber => new Vector4(0.58f, 0.34f, 0.12f, 1f),
                AppTheme.ArcticGlass => new Vector4(0.40f, 0.56f, 0.70f, 1f),
                AppTheme.SolarSlate => new Vector4(0.50f, 0.42f, 0.20f, 1f),
                AppTheme.CrimsonSteel => new Vector4(0.48f, 0.13f, 0.16f, 1f),
                AppTheme.ForestCircuit => new Vector4(0.23f, 0.43f, 0.27f, 1f),
                AppTheme.Oceanic => new Vector4(0.15f, 0.46f, 0.58f, 1f),
                AppTheme.VioletTerminal => new Vector4(0.36f, 0.25f, 0.60f, 1f),
                AppTheme.PaperDark => new Vector4(0.40f, 0.34f, 0.25f, 1f),
                AppTheme.NeonGlass => AccentMint,
                _ => new Vector4(0.22f, 0.34f, 0.58f, 1f),
            };
        }

        /// <summary>
        ///     Draws the currently selected settings on ImGui.
        /// </summary>
        private static void DrawCoreSettings()
        {
            if (ImGui.BeginTable("QuickSettings", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
            {
                ImGui.TableNextColumn();
                ImGui.TextDisabled("Profile");
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputText("Party Leader##leader", ref Core.GHSettings.LeaderName, 200);

                ImGui.TableNextColumn();
                ImGui.TextDisabled("Visibility");
                ImGui.Checkbox("Hide settings on start", ref Core.GHSettings.HideSettingWindowOnStart);
                ImGui.Checkbox("Close with game", ref Core.GHSettings.CloseWhenGameExit);
                ImGui.EndTable();
            }

            ImGui.Spacing();
            DrawNearbyWidget();
            DrawInputConfigWidget();
            DrawToolsConfig();
            DrawMiscConfig();
            ChangeFontWidget();

            if (ImGui.CollapsingHeader("Trackers"))
            {
                DrawPoiWidget();
                DrawMonstersToIgnore();
                DrawNPCWidget();
                DrawMiscObjWidget();
            }
        }

        private static void DrawNearbyWidget()
        {
            if (ImGui.CollapsingHeader("Nearby Ranges"))
            {
                ImGui.DragInt($"Small Range", ref Core.GHSettings.InnerCircle.Meaning,
                    1f, 0, Core.GHSettings.OuterCircle.Meaning);
                ImGui.SameLine();
                ImGui.Checkbox($"Visible##small", ref Core.GHSettings.InnerCircle.IsVisible);

                ImGui.DragInt($"Large Range", ref Core.GHSettings.OuterCircle.Meaning,
                    1f, Core.GHSettings.InnerCircle.Meaning, AreaInstanceConstants.NETWORK_BUBBLE_RADIUS);
                ImGui.SameLine();
                ImGui.Checkbox($"Visible##large", ref Core.GHSettings.OuterCircle.IsVisible);

                // ImGui.SameLine(0f, 30f);
                // ImGui.Checkbox($"Follow Mouse##{name}", ref value.FollowMouse);
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for changing fonts.
        /// </summary>
        private static void ChangeFontWidget()
        {
            if (ImGui.CollapsingHeader("Fonts"))
            {
                ImGui.InputText("Pathname", ref Core.GHSettings.FontPathName, 300);
                ImGui.DragInt("Size", ref Core.GHSettings.FontSize, 0.1f, 13, 40);
                var languageChanged = ImGuiHelper.EnumComboBox("Language", ref Core.GHSettings.FontLanguage);
                var customLanguage = ImGui.InputText("Custom Glyph Ranges", ref Core.GHSettings.FontCustomGlyphRange, 100);
                ImGuiHelper.ToolTip("This is advance level feature. Do not modify this if you don't know what you are doing. " +
                    "Example usage:- If you have downloaded and pointed to the ArialUnicodeMS.ttf font, you can use " +
                    "0x0020, 0xFFFF, 0x00 text in this field to load all of the font texture in ImGui. Note the 0x00" +
                    " as the last item in the range.");
                if (languageChanged)
                {
                    Core.GHSettings.FontCustomGlyphRange = string.Empty;
                }

                if (customLanguage)
                {
                    Core.GHSettings.FontLanguage = FontGlyphRangeType.English;
                }

                if (ImGui.Button("Apply Changes"))
                {
                    if (MiscHelper.TryConvertStringToImGuiGlyphRanges(Core.GHSettings.FontCustomGlyphRange, out var glyphranges))
                    {
                        Core.Overlay.ReplaceFont(
                            Core.GHSettings.FontPathName,
                            Core.GHSettings.FontSize,
                            glyphranges);
                    }
                    else
                    {
                        Core.Overlay.ReplaceFont(
                            Core.GHSettings.FontPathName,
                            Core.GHSettings.FontSize,
                            Core.GHSettings.FontLanguage);
                    }
                }
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for changing POI monsters.
        /// </summary>
        private static void DrawPoiWidget()
        {
            var isOpened = ImGui.CollapsingHeader("Special Monster Tracker (A.K.A Monster POI)");
            ImGuiHelper.ToolTip("In order to figure out the path/mod to add " +
                "please open DV -> States -> InGameState -> CurrentAreaInstance -> " +
                "Awake Entities -> click dump button against the entity you want to add. " +
                "This will create a new file in entity_dumps folder with all mod names and " +
                "path of that entity.");
            if (isOpened)
            {
                ImGui.TextWrapped("Please restart gamehelper or change area/zone if you make any changes over here.");
                for (var i = Core.GHSettings.PoiMonstersCategories2.Count - 1; i >= 0; i--)
                {
                    var (filtertype, filter, rarity, stat, group) = Core.GHSettings.PoiMonstersCategories2[i];
                    var isChanged = false;
                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                    if (ImGuiHelper.EnumComboBox($"Filter type     ##{i}MonsterPoiWidget", ref filtertype))
                    {
                        isChanged = true;
                    }

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 27);
                    if (ImGui.InputText($"Filter     ##{i}MonsterPoiWidget", ref filter, 200))
                    {
                        isChanged = true;
                    }

                    ImGuiHelper.ToolTip(filtertype == EntityFilterType.PATH ||
                        filtertype == EntityFilterType.PATHANDRARITY ||
                        filtertype == EntityFilterType.PATHANDSTAT ?
                        "Path is going to be checked from left to right (i.e. String.StartsWith), up till the filter length." :
                        "Mod name is fully checked, it need to be 100% match.");
                    ImGui.SameLine();
                    if (filtertype == EntityFilterType.PATHANDRARITY || filtertype == EntityFilterType.MODANDRARITY)
                    {
                        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                        if (ImGuiHelper.EnumComboBox($"Rarity     ##{i}MonsterPoiWidget", ref rarity))
                        {
                            isChanged = true;
                        }

                        ImGui.SameLine();
                    }

                    if (filtertype == EntityFilterType.PATHANDSTAT)
                    {
                        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                        if (ImGuiHelper.NonContinuousEnumComboBox($"Stat        ##{i}MonsterPoiWidget", ref stat))
                        {
                            isChanged = true;
                        }

                        ImGui.SameLine();
                    }

                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                    if (ImGui.InputInt($"Group Number##{i}MonsterPoiWidget", ref group))
                    {
                        if (group < 0)
                        {
                            group = 0;
                        }

                        isChanged = true;
                    }

                    if (isChanged)
                    {
                        Core.GHSettings.PoiMonstersCategories2[i] = new(filtertype, filter, rarity, stat, group);
                    }

                    ImGui.SameLine();
                    if (ImGui.Button($"delete##{i}MonsterPoiWidget"))
                    {
                        Core.GHSettings.PoiMonstersCategories2.RemoveAt(i);
                    }
                }

                ImGui.Separator();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                ImGuiHelper.EnumComboBox($"Filter type     ##addMonsterPoiWidget", ref efilterType);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 17);
                ImGui.InputText($"Filter     ##addMonsterPoiWidget", ref filterText, 200);
                ImGuiHelper.ToolTip(efilterType == EntityFilterType.PATH ||
                    efilterType == EntityFilterType.PATHANDRARITY ||
                    efilterType == EntityFilterType.PATHANDSTAT ?
                    "Path is going to be checked from left to right (i.e. String.StartsWith), up till the filter length." :
                    "Mod name is fully checked, it need to be 100% match.");
                ImGui.SameLine();
                if (efilterType == EntityFilterType.PATHANDRARITY || efilterType == EntityFilterType.MODANDRARITY)
                {
                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                    ImGuiHelper.EnumComboBox($"Rarity     ##addMonsterPoiWidget", ref erarity);
                    ImGui.SameLine();
                }

                if (efilterType == EntityFilterType.PATHANDSTAT)
                {
                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                    ImGuiHelper.NonContinuousEnumComboBox($"Stat        ##addMonsterPoiWidget", ref eStats);
                    ImGui.SameLine();
                }

                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                if (ImGui.InputInt($"Group Number##addMonsterPoiWidget", ref filterGroup) && filterGroup < 0)
                {
                    filterGroup = 0;
                }

                ImGui.SameLine();
                if(ImGui.Button("add##MonsterPoiWidget"))
                {
                    Core.GHSettings.PoiMonstersCategories2.Add(new(efilterType, filterText, erarity, eStats, filterGroup));
                    efilterType = EntityFilterType.PATH;
                    eStats = GameStats.is_capturable_monster;
                    filterText = string.Empty;
                    filterGroup = 0;
                }
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for ignoring monsters.
        /// </summary>
        private static void DrawMonstersToIgnore()
        {
            var isOpened = ImGui.CollapsingHeader("Ignore Monsters");
            ImGuiHelper.ToolTip("In order to figure out the path, please open " +
                "DV -> States -> InGameState -> CurrentAreaInstance -> Awake Entities -> " +
                "Click Path -> see NPC path in the game world");
            if (isOpened)
            {
                ImGui.TextWrapped("Please restart gamehelper or change area/zone if you make any changes over here.");
                ImGui.InputText("Monster metadata path##ToRemove", ref monterPathToIgnore, 200);
                ImGuiHelper.ToolTip("Path is going to be checked from left to right (i.e. String.StartsWith), up till the filter length.");
                ImGui.SameLine();
                if (ImGui.Button("Add##monsterPathToRemove") && !string.IsNullOrEmpty(monterPathToIgnore))
                {
                    Core.GHSettings.MonstersPathsToIgnore.Add(monterPathToIgnore);
                    monterPathToIgnore = string.Empty;
                }

                for (var i = Core.GHSettings.MonstersPathsToIgnore.Count - 1; i >= 0; i--)
                {
                    ImGui.Text($"Path: {Core.GHSettings.MonstersPathsToIgnore[i]}");
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete##{i}monsterPathToRemove"))
                    {
                        Core.GHSettings.MonstersPathsToIgnore.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for defining important NPCs.
        /// </summary>
        private static void DrawNPCWidget()
        {
            var isOpened = ImGui.CollapsingHeader("Special NPC Metadata Paths");
            ImGuiHelper.ToolTip("In order to figure out the path, please open " +
                "DV -> States -> InGameState -> CurrentAreaInstance -> Awake Entities -> " +
                "Click Path -> see NPC path in the game world");
            if (isOpened)
            {
                ImGui.TextWrapped("Please restart gamehelper or change area/zone if you make any changes over here.");
                ImGui.InputText("NPC Path##specialNPCPath", ref specialNpcPath, 200);
                ImGuiHelper.ToolTip("Path is going to be checked from left to right (i.e. String.StartsWith), up till the filter length.");
                ImGui.SameLine();
                if (ImGui.Button("Add##specialNPCPath") && !string.IsNullOrEmpty(specialNpcPath))
                {
                    Core.GHSettings.SpecialNPCPaths.Add(specialNpcPath);
                    specialNpcPath = string.Empty;
                }

                for (var i = Core.GHSettings.SpecialNPCPaths.Count - 1; i >= 0; i--)
                {
                    ImGui.Text($"Path: {Core.GHSettings.SpecialNPCPaths[i]}");
                    ImGui.SameLine();
                    if(ImGui.Button($"Delete##{i}specialNPCPath"))
                    {
                        Core.GHSettings.SpecialNPCPaths.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for defining important MiscellaneousObjects.
        /// </summary>
        private static void DrawMiscObjWidget()
        {
            var isOpened = ImGui.CollapsingHeader("Special Objects Metadata Paths");
            ImGuiHelper.ToolTip("In order to figure out the path, please open " +
                "DV -> States -> InGameState -> CurrentAreaInstance -> Awake Entities -> " +
                "Click Path -> see objects path in the game world");
            if (isOpened)
            {
                ImGui.TextWrapped("Please restart gamehelper or change area/zone if you make any changes over here.");
                ImGui.InputText("Object Path##MiscObjWidget", ref specialMiscObjPath, 200);
                ImGuiHelper.ToolTip("Path is going to be checked from left to right (i.e. String.StartsWith), up till the filter length.");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                if (ImGui.InputInt($"Group Number##MiscObjgroup", ref filterGroup) && filterGroup < 0)
                {
                    filterGroup = 0;
                }

                ImGui.SameLine();
                if (ImGui.Button("add##MiscObjadd"))
                {
                    Core.GHSettings.SpecialMiscObjPaths.Add(new(specialMiscObjPath, filterGroup));
                    specialMiscObjPath = string.Empty;
                    filterGroup = 0;
                }

                for (var i = Core.GHSettings.SpecialMiscObjPaths.Count - 1; i >= 0; i--)
                {
                    ImGui.Text($"Path: {Core.GHSettings.SpecialMiscObjPaths[i].path}, GroupId: {Core.GHSettings.SpecialMiscObjPaths[i].group}");
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete##MiscObjDel{i}"))
                    {
                        Core.GHSettings.SpecialMiscObjPaths.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for changing keyboard related settings
        /// </summary>
        private static void DrawInputConfigWidget()
        {
            if (ImGui.CollapsingHeader("Input Config"))
            {
                ImGui.DragInt("Key Timeout", ref Core.GHSettings.KeyPressTimeout, 0.2f, 60, 300);
                ImGuiHelper.ToolTip("When GameOverlay press a key in the game, the key " +
                    "has to go to the GGG server for it to work. This process takes " +
                    "time equal to your latency x 3. During this time GameOverlay might " +
                    "press that key again. Set the key timeout value to latency x 3 so " +
                    "this doesn't happen. e.g. for 30ms latency, set it to 90ms. Also, " +
                    "do not go below 60 (due to server ticks), no matter how good your latency is.");
                ImGuiHelper.NonContinuousEnumComboBox("Settings Window Key", ref Core.GHSettings.MainMenuHotKey);
                ImGuiHelper.NonContinuousEnumComboBox("Disable Rendering Key", ref Core.GHSettings.DisableAllRenderingKey);
            }
        }

        /// <summary>
        ///     Draws the imgui widget for enabling/disabling tools.
        /// </summary>
        private static void DrawToolsConfig()
        {
            if (ImGui.CollapsingHeader("Overlay Tools"))
            {
                ImGui.Checkbox("Performance Stats", ref Core.GHSettings.ShowPerfStats);
                if (Core.GHSettings.ShowPerfStats)
                {
                    ImGui.Spacing();
                    ImGui.SameLine();
                    ImGui.Spacing();
                    ImGui.SameLine();
                    ImGui.Checkbox("Hide when game is in background", ref Core.GHSettings.HidePerfStatsWhenBg);
                    ImGui.Spacing();
                    ImGui.SameLine();
                    ImGui.Spacing();
                    ImGui.SameLine();
                    ImGui.Checkbox("Show minimum stats", ref Core.GHSettings.MinimumPerfStats);
                }
            }
        }

        /// <summary>
        ///     Draws the imgui widget for showing misc config
        /// </summary>
        private static void DrawMiscConfig()
        {
            if (ImGui.CollapsingHeader("Runtime"))
            {
                if (ImGui.Checkbox("Fix Taskbar not showing", ref Core.GHSettings.FixTaskbarNotShowing))
                {
                    if (Core.States.GameCurrentState != GameStateTypes.GameNotLoaded)
                    {
                        CoroutineHandler.RaiseEvent(GameHelperEvents.OnMoved);
                    }
                }

                ImGui.Checkbox("Disable entity processing when in town or hideout",
                    ref Core.GHSettings.DisableEntityProcessingInTownOrHideout);
                if (ImGui.Checkbox("V-Sync", ref Core.Overlay.VSync))
                {
                    Core.GHSettings.Vsync = Core.Overlay.VSync;
                }

                ImGui.BeginDisabled(Core.Overlay.VSync);
                if (ImGui.InputInt("FPS Limiter (0 to disable)", ref Core.GHSettings.FPSLimit))
                {
                    Core.Overlay.FPSLimit = Core.GHSettings.FPSLimit;
                }

                ImGui.EndDisabled();

                ImGui.Checkbox("Enable NPC entity cleanup", ref Core.GHSettings.EnableNpcEntityCleanup);
                ImGui.Checkbox("Enable stale entity cleanup", ref Core.GHSettings.EnableStaleEntityCleanup);
                if (Core.GHSettings.EnableStaleEntityCleanup)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80);
                    ImGui.InputInt("threshold (frames)", ref Core.GHSettings.StaleEntityFrameThreshold);
                    if (Core.GHSettings.StaleEntityFrameThreshold < 10)
                        Core.GHSettings.StaleEntityFrameThreshold = 10;
                }
            }
        }

        private static void DrawAdvancedTools()
        {
            if (ImGui.CollapsingHeader("Developer Tools"))
            {
                ImGui.Checkbox("Game UiExplorer", ref Core.GHSettings.ShowGameUiExplorer);
                ImGui.Checkbox("Data Visualization", ref Core.GHSettings.ShowDataVisualization);
                ImGui.Checkbox("Performance Profiler", ref Core.GHSettings.ShowPerfProfiler);
#if DEBUG
                ImGui.Checkbox("Krangled Passive Detector", ref Core.GHSettings.ShowKrangledPassiveDetector);
#endif
            }

            if (ImGui.CollapsingHeader("Entity Processing"))
            {
                ImGui.Checkbox("Process all renderable entities", ref Core.GHSettings.ProcessAllRenderableEntities);
                ImGui.Checkbox("Disable counters for heavy maps", ref Core.GHSettings.DisableAllCounters);
                ImGui.Checkbox("Taiwan client", ref Core.GHSettings.IsTaiwanClient);
                ImGui.Text("Entity parallelism");
                ImGui.SameLine();
                if (ImGui.RadioButton("Auto", Core.GHSettings.EntityReaderMaxDegreeOfParallelism == -1))
                {
                    Core.GHSettings.EntityReaderMaxDegreeOfParallelism = -1;
                }

                ImGui.SameLine();
                for (var i = 2; i < 128; i *= 2)
                {
                    if (ImGui.RadioButton(i.ToString(), Core.GHSettings.EntityReaderMaxDegreeOfParallelism == i))
                    {
                        Core.GHSettings.EntityReaderMaxDegreeOfParallelism = i;
                    }

                    if (i * 2 < 128)
                    {
                        ImGui.SameLine();
                    }
                }
            }
        }

        /// <summary>
        ///     Draws the imgui widget for reloading plugins
        /// </summary>
        private static void DrawReloadPluginWidget()
        {
#if DEBUG
            if (ImGui.CollapsingHeader("Reload Plugin"))
            {
                ImGuiHelper.IEnumerableComboBox<string>("Plugins", PManager.PluginNames, ref pluginForHotReload);
                ImGui.BeginDisabled(!pluginLoaded || string.IsNullOrEmpty(pluginForHotReload));
                if (ImGui.Button("Unload Plugin"))
                {
                    if (PManager.UnloadPlugin(pluginForHotReload))
                    {
                        pluginLoaded = false;
                    }
                }

                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(pluginLoaded || string.IsNullOrEmpty(pluginForHotReload));
                if (ImGui.Button("Load Plugin"))
                {
                    if (PManager.LoadPlugin(pluginForHotReload))
                    {
                        pluginLoaded = true;
                    }
                }

                ImGui.EndDisabled();
            }
#endif
        }

        /// <summary>
        ///     Draws the closing confirmation popup on ImGui.
        /// </summary>
        private static void DrawConfirmationPopup()
        {
            ImGui.SetNextWindowPos(new Vector2(Core.Overlay.Size.Width / 3f, Core.Overlay.Size.Height / 3f));
            if (ImGui.BeginPopup("GameHelperCloseConfirmation"))
            {
                ImGui.Text("Do you want to quit the GameHelper overlay?");
                ImGui.Separator();
                if (ImGui.Button("Yes", new Vector2(ImGui.GetContentRegionAvail().X / 2f, ImGui.GetTextLineHeight() * 2)))
                {
                    Core.GHSettings.IsOverlayRunning = false;
                    ImGui.CloseCurrentPopup();
                    isOverlayRunningLocal = true;
                }

                ImGui.SameLine();
                if (ImGui.Button("No", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 2)))
                {
                    ImGui.CloseCurrentPopup();
                    isOverlayRunningLocal = true;
                }

                ImGui.EndPopup();
            }
        }

        /// <summary>
        ///     Hides the overlay on startup.
        /// </summary>
        private static void HideOnStartCheck()
        {
            if (Core.GHSettings.HideSettingWindowOnStart)
            {
                isSettingsWindowVisible = false;
            }
        }

        /// <summary>
        ///     Draws the Settings Window.
        /// </summary>
        /// <returns>co-routine IWait.</returns>
        private static IEnumerator<Wait> RenderCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnRender);
                if (Utils.IsKeyPressedAndNotTimeout(Core.GHSettings.MainMenuHotKey))
                {
                    isSettingsWindowVisible = !isSettingsWindowVisible;
                    ImGui.GetIO().WantCaptureMouse = true;
                    if (!isSettingsWindowVisible)
                    {
                        CoroutineHandler.RaiseEvent(GameHelperEvents.TimeToSaveAllSettings);
                    }
                }

                if (!isSettingsWindowVisible)
                {
                    continue;
                }

                if (Core.GHSettings.SettingsWindowPosition != Vector2.Zero)
                {
                    ImGui.SetNextWindowPos(Core.GHSettings.SettingsWindowPosition, ImGuiCond.FirstUseEver);
                }

                ImGui.SetNextWindowSize(Core.GHSettings.SettingsWindowSize, ImGuiCond.FirstUseEver);
                ImGui.SetNextWindowSizeConstraints(new Vector2(800, 600), Vector2.One * float.MaxValue);
                var isMainMenuExpanded = ImGui.Begin(
                    $"Game Overlay Settings [ {Core.GetVersion()} ]",
                    ref isOverlayRunningLocal);

                Core.GHSettings.SettingsWindowPosition = ImGui.GetWindowPos();
                Core.GHSettings.SettingsWindowSize = ImGui.GetWindowSize();

                if (!isOverlayRunningLocal)
                {
                    ImGui.OpenPopup("GameHelperCloseConfirmation");
                }

                DrawConfirmationPopup();
                if (!Core.GHSettings.IsOverlayRunning)
                {
                    CoroutineHandler.RaiseEvent(GameHelperEvents.TimeToSaveAllSettings);
                }

                if (!isMainMenuExpanded)
                {
                    ImGui.End();
                    continue;
                }

                DrawModernSettings();
                ImGui.End();
            }
        }

        /// <summary>
        ///     Saves the GameHelper settings to disk.
        /// </summary>
        /// <returns>co-routine IWait.</returns>
        private static IEnumerator<Wait> SaveCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.TimeToSaveAllSettings);
                JsonHelper.SafeToFile(Core.GHSettings, State.CoreSettingFile);
            }
        }
    }
}
