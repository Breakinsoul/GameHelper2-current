// <copyright file="OffsetSearchCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace OffsetSearch
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using GameHelper;
    using GameHelper.Plugin;
    using GameOffsets;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     Plugin that scans an executable or dump for GameHelper static-offset patterns.
    /// </summary>
    public sealed class OffsetSearchCore : PCore<OffsetSearchSettings>
    {
        private readonly object stateLock = new();
        private Task? searchTask;
        private string status = "Idle";
        private string error = string.Empty;
        private string cacheInfo = string.Empty;
        private string lastTargetPath = string.Empty;
        private DateTime lastSearchTime;
        private Dictionary<string, int> results = new(StringComparer.Ordinal);

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        private string ResultsTextPathname => Path.Join(AppContext.BaseDirectory, "offset_search_results.txt");

        private string ResultsJsonPathname => Path.Join(AppContext.BaseDirectory, "offset_search_results.json");

        private string LegacyResultsTextPathname => Path.Join(this.DllDirectory, "offset_search_results.txt");

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.TextWrapped("Scan PathOfExile.exe or a binary dump for GameHelper static-offset patterns. Results are cached in the GameHelper folder and loaded on next start.");
            ImGui.SetNextItemWidth(Math.Max(250f, ImGui.GetContentRegionAvail().X - 180f));
            ImGui.InputText("##OffsetSearchTargetPath", ref this.Settings.TargetFilePath, 1024);
            ImGui.SameLine();
            ImGui.Text("Target EXE or dump");
            ImGui.Checkbox("Auto search on plugin enable", ref this.Settings.AutoSearchOnEnable);
            ImGui.SameLine();
            ImGui.Checkbox("Load cached results", ref this.Settings.LoadCachedResultsOnEnable);

            var isRunning = this.searchTask is { IsCompleted: false };
            string localStatus;
            string localError;
            string localCacheInfo;
            string localTargetPath;
            DateTime localLastSearchTime;
            KeyValuePair<string, int>[] localResults;
            lock (this.stateLock)
            {
                localStatus = this.status;
                localError = this.error;
                localCacheInfo = this.cacheInfo;
                localTargetPath = this.lastTargetPath;
                localLastSearchTime = this.lastSearchTime;
                localResults = this.results.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
            }

            if (ImGui.Button("Use attached game EXE"))
            {
                this.UseAttachedGameExecutable();
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(isRunning || string.IsNullOrWhiteSpace(this.Settings.TargetFilePath));
            if (ImGui.Button("Search offsets"))
            {
                this.StartSearch();
            }

            ImGui.EndDisabled();
            ImGui.SameLine();

            ImGui.BeginDisabled(localResults.Length == 0);
            if (ImGui.Button("Copy results"))
            {
                ImGui.SetClipboardText(this.FormatResults());
            }

            ImGui.SameLine();
            if (ImGui.Button("Save results"))
            {
                this.SaveResults(autoSaved: false);
            }

            ImGui.SameLine();
            if (ImGui.Button("Reload cached"))
            {
                this.LoadCachedResults();
            }

            ImGui.EndDisabled();

            ImGui.Separator();
            ImGui.TextColored(isRunning ? new System.Numerics.Vector4(0.35f, 0.70f, 0.95f, 1f) : new System.Numerics.Vector4(0.30f, 0.80f, 0.70f, 1f), $"Status: {localStatus}");
            if (!string.IsNullOrEmpty(localCacheInfo))
            {
                ImGui.TextWrapped(localCacheInfo);
            }

            if (!string.IsNullOrEmpty(localTargetPath))
            {
                ImGui.TextWrapped($"Last target: {localTargetPath}");
            }

            if (localLastSearchTime != default)
            {
                ImGui.Text($"Last search: {localLastSearchTime:g}");
            }

            if (!string.IsNullOrEmpty(localError))
            {
                ImGui.TextWrapped($"Error: {localError}");
            }

            if (localResults.Length > 0 &&
                ImGui.BeginTable("OffsetSearchResults", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Pattern");
                ImGui.TableSetupColumn("Offset");
                ImGui.TableSetupColumn("Copy", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableHeadersRow();

                foreach (var (name, offset) in localResults)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(name);
                    ImGui.TableNextColumn();
                    ImGui.Text($"0x{offset:X}");
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"Copy##{name}"))
                    {
                        ImGui.SetClipboardText($"0x{offset:X}");
                    }
                }

                ImGui.EndTable();
            }
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
        }

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<OffsetSearchSettings>(content) ?? new OffsetSearchSettings();
            }

            if (this.Settings.LoadCachedResultsOnEnable)
            {
                this.LoadCachedResults();
            }

            if (this.Settings.AutoSearchOnEnable && File.Exists(this.Settings.TargetFilePath))
            {
                this.StartSearch();
            }
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname)!);
            var settingsData = JsonConvert.SerializeObject(this.Settings, Formatting.Indented);
            File.WriteAllText(this.SettingPathname, settingsData);
        }

        private void StartSearch()
        {
            if (this.searchTask is { IsCompleted: false })
            {
                return;
            }

            var targetPath = this.Settings.TargetFilePath.Trim('"', ' ');
            lock (this.stateLock)
            {
                this.status = $"Searching {targetPath}";
                this.error = string.Empty;
                this.results.Clear();
            }

            this.searchTask = Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(targetPath))
                    {
                        throw new FileNotFoundException("Target file was not found.", targetPath);
                    }

                    var found = PatternSearchEngine.FindStaticOffsetsInFile(targetPath);
                    lock (this.stateLock)
                    {
                        this.results = found;
                        this.status = $"Found {found.Count} offsets";
                        this.error = string.Empty;
                        this.lastTargetPath = targetPath;
                        this.lastSearchTime = DateTime.Now;
                    }

                    this.SaveResults(autoSaved: true);
                }
                catch (Exception ex)
                {
                    lock (this.stateLock)
                    {
                        this.results.Clear();
                        this.status = "Failed";
                        this.error = ex.Message;
                    }
                }
            });
        }

        private void UseAttachedGameExecutable()
        {
            try
            {
                var modulePath = Core.Process.MainModuleFileName;
                if (string.IsNullOrWhiteSpace(modulePath))
                {
                    lock (this.stateLock)
                    {
                        this.status = "Game process is not attached";
                        this.error = string.Empty;
                    }

                    return;
                }

                this.Settings.TargetFilePath = modulePath;
                lock (this.stateLock)
                {
                    this.status = $"Target set to {modulePath}";
                    this.error = string.Empty;
                }
            }
            catch (Exception ex)
            {
                lock (this.stateLock)
                {
                    this.status = "Failed to read attached game path";
                    this.error = ex.Message;
                }
            }
        }

        private string FormatResults()
        {
            KeyValuePair<string, int>[] snapshot;
            lock (this.stateLock)
            {
                snapshot = this.results.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
            }

            var builder = new StringBuilder();
            foreach (var (name, offset) in snapshot)
            {
                builder.AppendLine($"{name}: 0x{offset:X}");
            }

            return builder.ToString();
        }

        private void SaveResults(bool autoSaved)
        {
            var cache = this.CreateCacheSnapshot();
            File.WriteAllText(this.ResultsTextPathname, this.FormatResults());
            File.WriteAllText(this.ResultsJsonPathname, JsonConvert.SerializeObject(cache, Formatting.Indented));
            lock (this.stateLock)
            {
                this.cacheInfo = $"Cache file: {this.ResultsJsonPathname}";
                this.status = autoSaved
                    ? $"Found {cache.Results.Count} offsets and saved cache"
                    : $"Saved to {this.ResultsJsonPathname}";
            }
        }

        private OffsetSearchCache CreateCacheSnapshot()
        {
            lock (this.stateLock)
            {
                return new OffsetSearchCache
                {
                    TargetFilePath = this.lastTargetPath,
                    SearchTimeUtc = this.lastSearchTime == default ? DateTime.UtcNow : this.lastSearchTime.ToUniversalTime(),
                    Results = new Dictionary<string, int>(this.results, StringComparer.Ordinal),
                };
            }
        }

        private void LoadCachedResults()
        {
            try
            {
                if (!File.Exists(this.ResultsJsonPathname))
                {
                    if (this.TryLoadTextResults(this.ResultsTextPathname, out var textResults) ||
                        this.TryLoadTextResults(this.LegacyResultsTextPathname, out textResults))
                    {
                        lock (this.stateLock)
                        {
                            this.results = textResults;
                            this.lastTargetPath = this.Settings.TargetFilePath;
                            this.lastSearchTime = File.GetLastWriteTime(
                                File.Exists(this.ResultsTextPathname)
                                    ? this.ResultsTextPathname
                                    : this.LegacyResultsTextPathname);
                            this.status = $"Loaded {this.results.Count} cached offsets";
                            this.error = string.Empty;
                        }

                        this.SaveResults(autoSaved: true);
                        return;
                    }

                    lock (this.stateLock)
                    {
                        this.cacheInfo = $"No cache file found: {this.ResultsJsonPathname}";
                    }

                    return;
                }

                var cache = JsonConvert.DeserializeObject<OffsetSearchCache>(File.ReadAllText(this.ResultsJsonPathname));
                if (cache?.Results == null || cache.Results.Count == 0)
                {
                    lock (this.stateLock)
                    {
                        this.cacheInfo = $"Cache file is empty: {this.ResultsJsonPathname}";
                    }

                    return;
                }

                lock (this.stateLock)
                {
                    this.results = new Dictionary<string, int>(cache.Results, StringComparer.Ordinal);
                    this.lastTargetPath = cache.TargetFilePath;
                    this.lastSearchTime = cache.SearchTimeUtc.ToLocalTime();
                    this.status = $"Loaded {this.results.Count} cached offsets";
                    this.error = string.Empty;
                    this.cacheInfo = $"Cache file: {this.ResultsJsonPathname}";
                }
            }
            catch (Exception ex)
            {
                lock (this.stateLock)
                {
                    this.status = "Failed to load cached offsets";
                    this.error = ex.Message;
                }
            }
        }

        private bool TryLoadTextResults(string path, out Dictionary<string, int> loadedResults)
        {
            loadedResults = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!File.Exists(path))
            {
                return false;
            }

            foreach (var line in File.ReadLines(path))
            {
                var separatorIndex = line.LastIndexOf(':');
                if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
                {
                    continue;
                }

                var name = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim();
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    value = value[2..];
                }

                if (int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset))
                {
                    loadedResults[name] = offset;
                }
            }

            return loadedResults.Count > 0;
        }

        private sealed class OffsetSearchCache
        {
            public string TargetFilePath { get; set; } = string.Empty;

            public DateTime SearchTimeUtc { get; set; }

            public Dictionary<string, int> Results { get; set; } = new(StringComparer.Ordinal);
        }
    }
}
