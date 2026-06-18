namespace PlayerStatusPanel
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class PlayerStatusPanel : PCore<PlayerStatusPanelSettings>
    {
        private string exportStatus = string.Empty;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        public override void OnEnable(bool isGameOpened)
        {
            if (!File.Exists(this.SettingPathname))
            {
                return;
            }

            var content = File.ReadAllText(this.SettingPathname);
            this.Settings = JsonConvert.DeserializeObject<PlayerStatusPanelSettings>(content) ?? new PlayerStatusPanelSettings();
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
            var show = this.Settings.ShowWindow;
            if (ImGui.Checkbox("Show status panel", ref show))
            {
                this.Settings.ShowWindow = show;
            }

            var hide = this.Settings.HideWhenGameInBackground;
            if (ImGui.Checkbox("Hide when game is in background", ref hide))
            {
                this.Settings.HideWhenGameInBackground = hide;
            }

            var buffs = this.Settings.ShowBuffs;
            if (ImGui.Checkbox("Show buffs", ref buffs))
            {
                this.Settings.ShowBuffs = buffs;
            }

            var timed = this.Settings.ShowOnlyTimedBuffs;
            if (ImGui.Checkbox("Only timed buffs", ref timed))
            {
                this.Settings.ShowOnlyTimedBuffs = timed;
            }

            var maxRows = this.Settings.MaxBuffRows;
            if (ImGui.SliderInt("Max buff rows", ref maxRows, 5, 100))
            {
                this.Settings.MaxBuffRows = maxRows;
            }
        }

        public override void DrawUI()
        {
            if (!this.Settings.ShowWindow)
            {
                return;
            }

            if (this.Settings.HideWhenGameInBackground && !Core.Process.Foreground)
            {
                return;
            }

            if (Core.Process.Pid == 0 || Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            ImGui.SetNextWindowSize(new Vector2(420f, 360f), ImGuiCond.FirstUseEver);
            var show = this.Settings.ShowWindow;
            if (!ImGui.Begin("Player Status Panel", ref show))
            {
                this.Settings.ShowWindow = show;
                ImGui.End();
                return;
            }

            this.Settings.ShowWindow = show;
            this.DrawPanel();
            ImGui.End();
        }

        private void DrawPanel()
        {
            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;

            if (player.TryGetComponent<Life>(out var life, false))
            {
                this.DrawVital("Life", life.Health.Current, life.Health.Total, new Vector4(0.95f, 0.22f, 0.22f, 1f));
                this.DrawVital("Energy Shield", life.EnergyShield.Current, life.EnergyShield.Total, new Vector4(0.25f, 0.55f, 1f, 1f));
                this.DrawVital("Mana", life.Mana.Current, life.Mana.Total, new Vector4(0.25f, 0.42f, 1f, 1f));
                ImGui.Text($"Alive: {life.IsAlive}");
            }
            else
            {
                ImGui.TextUnformatted("Life component is unavailable.");
            }

            if (ImGui.Button("Export player snapshot"))
            {
                this.ExportPlayerSnapshot(player);
            }

            if (!string.IsNullOrEmpty(this.exportStatus))
            {
                ImGui.SameLine();
                ImGui.TextWrapped(this.exportStatus);
            }

            if (this.Settings.ShowBuffs)
            {
                this.DrawBuffs(player);
            }
        }

        private void DrawVital(string label, int current, int total, Vector4 color)
        {
            var percent = total <= 0 ? 0f : Math.Clamp((float)current / total, 0f, 1f);
            ImGui.Text($"{label}: {current}/{total}");
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
            ImGui.ProgressBar(percent, new Vector2(-1f, 16f), $"{percent * 100f:0}%");
            ImGui.PopStyleColor();
        }

        private void DrawBuffs(GameHelper.RemoteObjects.States.InGameStateObjects.Entity player)
        {
            ImGui.SeparatorText("Buffs");
            if (!player.TryGetComponent<Buffs>(out var buffs, false))
            {
                ImGui.TextUnformatted("Buffs component is unavailable.");
                return;
            }

            var rows = buffs.StatusEffects
                .Where(x => !this.Settings.ShowOnlyTimedBuffs || x.Value.TimeLeft > 0)
                .OrderBy(x => x.Value.TimeLeft < 0 ? float.MaxValue : x.Value.TimeLeft)
                .Take(this.Settings.MaxBuffRows)
                .ToArray();

            if (!ImGui.BeginTable("PlayerStatusBuffs", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
            {
                return;
            }

            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Charges", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Flask", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Key);
                ImGui.TableNextColumn();
                ImGui.Text(row.Value.TimeLeft < 0 ? "inf" : $"{row.Value.TimeLeft:0.0}");
                ImGui.TableNextColumn();
                ImGui.Text($"{row.Value.Charges}");
                ImGui.TableNextColumn();
                ImGui.Text(row.Value.FlaskSlot >= 0 ? $"{row.Value.FlaskSlot + 1}" : "-");
            }

            ImGui.EndTable();
        }

        private void ExportPlayerSnapshot(GameHelper.RemoteObjects.States.InGameStateObjects.Entity player)
        {
            var exportDir = Path.Join(this.DllDirectory, "exports");
            Directory.CreateDirectory(exportDir);
            var file = Path.Join(exportDir, $"player-status-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            player.TryGetComponent<Life>(out var life, false);
            player.TryGetComponent<Buffs>(out var buffs, false);

            var payload = new
            {
                Timestamp = DateTimeOffset.Now,
                player.Id,
                State = Core.States.GameCurrentState.ToString(),
                AreaHash = Core.States.InGameStateObject.CurrentAreaInstance.AreaHash,
                Life = life == null ? null : new
                {
                    Health = new { life.Health.Current, life.Health.Total, life.Health.ReservedFlat, life.Health.ReservedPercent },
                    EnergyShield = new { life.EnergyShield.Current, life.EnergyShield.Total },
                    Mana = new { life.Mana.Current, life.Mana.Total, life.Mana.ReservedFlat, life.Mana.ReservedPercent },
                    life.IsAlive,
                },
                Buffs = buffs?.StatusEffects.Select(x => new
                {
                    Name = x.Key,
                    x.Value.TimeLeft,
                    x.Value.TotalTime,
                    x.Value.Charges,
                    x.Value.FlaskSlot,
                    x.Value.SourceEntityId,
                }).ToArray(),
            };

            File.WriteAllText(file, JsonConvert.SerializeObject(payload, Formatting.Indented));
            this.exportStatus = $"Exported: {file}";
        }
    }
}
