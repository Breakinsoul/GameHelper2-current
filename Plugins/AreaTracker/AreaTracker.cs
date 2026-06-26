namespace AreaTracker
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class AreaTracker : PCore<AreaTrackerSettings>
    {
        private readonly List<AreaVisit> visits = [];
        private string currentAreaHash = string.Empty;
        private string exportStatus = string.Empty;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        public override void OnEnable(bool isGameOpened)
        {
            if (!File.Exists(this.SettingPathname))
            {
                return;
            }

            var content = File.ReadAllText(this.SettingPathname);
            this.Settings = JsonConvert.DeserializeObject<AreaTrackerSettings>(content) ?? new AreaTrackerSettings();
        }

        public override void OnDisable()
        {
        }

        public override void SaveSettings()
        {
            GameHelper.Utils.JsonHelper.SafeToFile(this.Settings, new FileInfo(this.SettingPathname));
        }

        public override void DrawSettings()
        {
            if (!ImGui.BeginTabBar("AreaTrackerSettingsTabs"))
            {
                return;
            }

            if (ImGui.BeginTabItem("General"))
            {
                this.DrawGeneralSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("History"))
            {
                this.DrawHistorySettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Debug"))
            {
                this.DrawDebugSettings();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        private void DrawGeneralSettings()
        {
            var show = this.Settings.ShowWindow;
            if (ImGui.Checkbox("Show area tracker", ref show))
            {
                this.Settings.ShowWindow = show;
            }

            var hide = this.Settings.HideWhenGameInBackground;
            if (ImGui.Checkbox("Hide when game is in background", ref hide))
            {
                this.Settings.HideWhenGameInBackground = hide;
            }
        }

        private void DrawHistorySettings()
        {
            var autoExport = this.Settings.AutoExportOnAreaChange;
            if (ImGui.Checkbox("Auto export on area change", ref autoExport))
            {
                this.Settings.AutoExportOnAreaChange = autoExport;
            }

            var maxVisits = Math.Clamp(this.Settings.MaxVisits, 10, 1000);
            if (ImGui.SliderInt("Max history rows", ref maxVisits, 10, 1000))
            {
                this.Settings.MaxVisits = maxVisits;
                this.TrimVisits();
            }

            if (this.Settings.MaxVisits != maxVisits)
            {
                this.Settings.MaxVisits = maxVisits;
                this.TrimVisits();
            }
        }

        private void DrawDebugSettings()
        {
            var showDebug = this.Settings.ShowDebug;
            if (ImGui.Checkbox("Show debug details in tracker window", ref showDebug))
            {
                this.Settings.ShowDebug = showDebug;
            }

            ImGui.Separator();
            ImGui.Text($"Tracked visits: {this.visits.Count}");
            ImGui.Text($"Current area hash: {(string.IsNullOrEmpty(this.currentAreaHash) ? "-" : this.currentAreaHash)}");
            ImGui.Text($"Export status: {(string.IsNullOrEmpty(this.exportStatus) ? "-" : this.exportStatus)}");

            var current = this.visits.FirstOrDefault();
            if (current == null)
            {
                return;
            }

            ImGui.Text($"Awake entities: {current.AwakeEntities}");
            ImGui.Text($"Network bubble entities: {current.NetworkBubbleEntities}");
            ImGui.Text($"Useless awake entities: {current.UselessAwakeEntities}");
        }

        public override void DrawUI()
        {
            this.TrackCurrentArea();

            if (!this.Settings.ShowWindow)
            {
                return;
            }

            if (this.Settings.HideWhenGameInBackground && !Core.Process.Foreground)
            {
                return;
            }

            if (this.Settings.WindowPos != Vector2.Zero)
            {
                ImGui.SetNextWindowPos(this.Settings.WindowPos, ImGuiCond.FirstUseEver);
            }

            ImGui.SetNextWindowSize(this.Settings.WindowSize, ImGuiCond.FirstUseEver);
            var show = this.Settings.ShowWindow;
            if (!ImGui.Begin("Area Tracker", ref show))
            {
                this.Settings.ShowWindow = show;
                this.Settings.WindowPos = ImGui.GetWindowPos();
                this.Settings.WindowSize = ImGui.GetWindowSize();
                ImGui.End();
                return;
            }

            this.Settings.ShowWindow = show;
            this.Settings.WindowPos = ImGui.GetWindowPos();
            this.Settings.WindowSize = ImGui.GetWindowSize();
            this.DrawWindow();
            ImGui.End();
        }

        private void TrackCurrentArea()
        {
            if (Core.Process.Pid == 0 || Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (string.IsNullOrWhiteSpace(area.AreaHash))
            {
                return;
            }

            var now = DateTimeOffset.Now;
            if (!this.currentAreaHash.Equals(area.AreaHash, StringComparison.Ordinal))
            {
                if (this.Settings.AutoExportOnAreaChange && this.visits.Count > 0)
                {
                    this.ExportHistory();
                }

                this.currentAreaHash = area.AreaHash;
                this.visits.Insert(0, new AreaVisit
                {
                    EnteredAt = now,
                    LastSeenAt = now,
                    AreaHash = area.AreaHash,
                    AreaLevel = area.CurrentAreaLevel,
                });

                this.TrimVisits();
            }

            var current = this.visits.FirstOrDefault(x => x.AreaHash.Equals(area.AreaHash, StringComparison.Ordinal));
            if (current == null)
            {
                return;
            }

            current.LastSeenAt = now;
            current.AreaLevel = area.CurrentAreaLevel;
            current.AwakeEntities = area.AwakeEntities.Count;
            current.NetworkBubbleEntities = area.NetworkBubbleEntityCount;
            current.UselessAwakeEntities = area.UselessAwakeEntities;
        }

        private void DrawWindow()
        {
            var current = this.visits.FirstOrDefault();
            if (current != null)
            {
                ImGui.Text($"Current: {current.AreaHash}  level {current.AreaLevel}  time {FormatDuration(current.LastSeenAt - current.EnteredAt)}");
                if (this.Settings.ShowDebug)
                {
                    ImGui.TextDisabled($"Awake {current.AwakeEntities}, network {current.NetworkBubbleEntities}, useless {current.UselessAwakeEntities}");
                }
            }
            else
            {
                ImGui.TextUnformatted("No area recorded yet.");
            }

            if (ImGui.Button("Export history"))
            {
                this.ExportHistory();
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear history"))
            {
                this.visits.Clear();
                this.currentAreaHash = string.Empty;
            }

            if (!string.IsNullOrEmpty(this.exportStatus))
            {
                ImGui.SameLine();
                ImGui.TextWrapped(this.exportStatus);
            }

            if (!ImGui.BeginTable("AreaTrackerHistory", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
            {
                return;
            }

            ImGui.TableSetupColumn("Hash", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupColumn("Duration", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Awake", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Network", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Entered");
            ImGui.TableHeadersRow();

            foreach (var visit in this.visits)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(visit.AreaHash);
                ImGui.TableNextColumn();
                ImGui.Text($"{visit.AreaLevel}");
                ImGui.TableNextColumn();
                ImGui.Text(FormatDuration(visit.LastSeenAt - visit.EnteredAt));
                ImGui.TableNextColumn();
                ImGui.Text($"{visit.AwakeEntities}");
                ImGui.TableNextColumn();
                ImGui.Text($"{visit.NetworkBubbleEntities}");
                ImGui.TableNextColumn();
                ImGui.Text($"{visit.EnteredAt:HH:mm:ss}");
            }

            ImGui.EndTable();
        }

        private void TrimVisits()
        {
            while (this.visits.Count > this.Settings.MaxVisits)
            {
                this.visits.RemoveAt(this.visits.Count - 1);
            }
        }

        private void ExportHistory()
        {
            var exportDir = Path.Join(this.DllDirectory, "exports");
            Directory.CreateDirectory(exportDir);
            var file = Path.Join(exportDir, $"area-history-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(file, JsonConvert.SerializeObject(this.visits, Formatting.Indented));
            this.exportStatus = $"Exported: {file}";
        }

        private static string FormatDuration(TimeSpan value)
        {
            return value.TotalHours >= 1d ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
        }
    }
}
