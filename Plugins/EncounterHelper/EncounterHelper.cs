namespace EncounterHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class EncounterHelper : PCore<EncounterHelperSettings>
    {
        private readonly Dictionary<string, int> matchCounts = new(StringComparer.Ordinal);
        private int seen;
        private int drawn;
        private int skippedDead;
        private int skippedNoRender;
        private int skippedDistance;
        private int skippedOutsideScreen;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        public override void OnEnable(bool isGameOpened)
        {
            if (!File.Exists(this.SettingPathname))
            {
                this.NormalizeSettings();
                return;
            }

            var content = File.ReadAllText(this.SettingPathname);
            this.Settings = JsonConvert.DeserializeObject<EncounterHelperSettings>(content) ?? new EncounterHelperSettings();
            this.NormalizeSettings();
        }

        public override void OnDisable()
        {
        }

        public override void SaveSettings()
        {
            foreach (var rule in this.Settings.Rules)
            {
                rule.Color = ImGuiHelper.Color(rule.MarkerColor);
            }

            JsonHelper.SafeToFile(this.Settings, new FileInfo(this.SettingPathname));
        }

        public override void DrawSettings()
        {
            if (!ImGui.BeginTabBar("EncounterHelperSettingsTabs"))
            {
                return;
            }

            if (ImGui.BeginTabItem("General"))
            {
                this.DrawGeneralSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Appearance"))
            {
                this.DrawAppearanceSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Rules"))
            {
                this.DrawRulesSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Debug"))
            {
                this.DrawDebugSettings();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        public override void DrawUI()
        {
            this.ResetCounters();

            if (Core.Process.Pid == 0 || Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            if (this.Settings.HideWhenGameInBackground && !Core.Process.Foreground)
            {
                return;
            }

            var world = Core.States.InGameStateObject.CurrentWorldInstance;
            if ((this.Settings.HideInTown && world.AreaDetails.IsTown) ||
                (this.Settings.HideInHideout && world.AreaDetails.IsHideout))
            {
                return;
            }

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!area.Player.TryGetComponent<Render>(out var playerRender, false))
            {
                return;
            }

            if (this.Settings.EnableWorldMarkers)
            {
                this.DrawMarkers(area, playerRender);
            }

            if (this.Settings.ShowSummaryWindow)
            {
                this.DrawSummary(area);
            }
        }

        private void DrawMarkers(AreaInstance area, Render playerRender)
        {
            var drawList = ImGui.GetBackgroundDrawList();
            var playerGrid = playerRender.GridPosition;
            var width = Core.Process.WindowArea.Width;
            var height = Core.Process.WindowArea.Height;

            foreach (var entity in area.AwakeEntities.Values)
            {
                var rule = this.GetMatchingRule(entity);
                if (rule == null)
                {
                    continue;
                }

                this.seen++;
                this.matchCounts[rule.Name] = this.matchCounts.TryGetValue(rule.Name, out var count) ? count + 1 : 1;

                if (this.Settings.HideDead && IsDead(entity))
                {
                    this.skippedDead++;
                    continue;
                }

                if (!entity.TryGetComponent<Render>(out var render, false))
                {
                    this.skippedNoRender++;
                    continue;
                }

                var grid = render.GridPosition;
                var dx = grid.X - playerGrid.X;
                var dy = grid.Y - playerGrid.Y;
                if (MathF.Sqrt(dx * dx + dy * dy) > this.Settings.MaxGridDistance)
                {
                    this.skippedDistance++;
                    continue;
                }

                var screen = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(render.WorldPosition, render.WorldPosition.Z);
                if (screen.X < 0 || screen.Y < 0 || screen.X > width || screen.Y > height)
                {
                    this.skippedOutsideScreen++;
                    continue;
                }

                var markerRadius = MathF.Max(1f, this.Settings.MarkerRadius * MathF.Max(0.1f, rule.SizeMultiplier));
                this.DrawMarker(drawList, screen, rule, markerRadius);

                if (this.Settings.ShowLabels && rule.ShowLabel)
                {
                    this.DrawLabel(drawList, screen, rule, markerRadius);
                }

                this.drawn++;
            }
        }

        private void DrawSummary(AreaInstance area)
        {
            if (this.Settings.SummaryWindowPos != Vector2.Zero)
            {
                ImGui.SetNextWindowPos(this.Settings.SummaryWindowPos, ImGuiCond.FirstUseEver);
            }

            ImGui.SetNextWindowSize(this.Settings.SummaryWindowSize, ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Encounter Helper"))
            {
                this.Settings.SummaryWindowPos = ImGui.GetWindowPos();
                this.Settings.SummaryWindowSize = ImGui.GetWindowSize();
                ImGui.End();
                return;
            }

            this.Settings.SummaryWindowPos = ImGui.GetWindowPos();
            this.Settings.SummaryWindowSize = ImGui.GetWindowSize();

            ImGui.Text($"Area: {area.AreaHash}");
            ImGui.Text($"Matched: {this.seen}");
            ImGui.Text($"Drawn: {this.drawn}");
            ImGui.Text($"Dead: {this.skippedDead}");
            ImGui.Text($"No render: {this.skippedNoRender}");
            ImGui.Text($"Out of range: {this.skippedDistance}");
            ImGui.Text($"Outside screen: {this.skippedOutsideScreen}");
            ImGui.Separator();

            var counts = this.Settings.ShowOnlyMatchedSummary
                ? this.matchCounts.OrderByDescending(x => x.Value)
                : this.Settings.Rules
                    .Where(rule => rule.Enabled)
                    .Select(rule => new KeyValuePair<string, int>(rule.Name, this.matchCounts.GetValueOrDefault(rule.Name)))
                    .OrderByDescending(x => x.Value);
            foreach (var item in counts)
            {
                ImGui.Text($"{item.Key}: {item.Value}");
            }

            ImGui.End();
        }

        private EncounterRule? GetMatchingRule(Entity entity)
        {
            for (var i = 0; i < this.Settings.Rules.Count; i++)
            {
                var rule = this.Settings.Rules[i];
                if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.PathContains))
                {
                    continue;
                }

                if (entity.Path.Contains(rule.PathContains, StringComparison.OrdinalIgnoreCase))
                {
                    return rule;
                }
            }

            if (entity.EntityType == EntityTypes.Shrine)
            {
                return this.Settings.Rules.FirstOrDefault(x => x.Enabled && x.Name.Equals("Shrine", StringComparison.OrdinalIgnoreCase));
            }

            if (entity.EntitySubtype == EntitySubtypes.Strongbox)
            {
                return this.Settings.Rules.FirstOrDefault(x => x.Enabled && x.Name.Equals("Strongbox", StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private void ResetCounters()
        {
            this.matchCounts.Clear();
            this.seen = 0;
            this.drawn = 0;
            this.skippedDead = 0;
            this.skippedNoRender = 0;
            this.skippedDistance = 0;
            this.skippedOutsideScreen = 0;
        }

        private static bool IsDead(Entity entity)
        {
            return entity.TryGetStatValue(GameStats.is_dead, out var isDead) && isDead > 0;
        }

        private void DrawGeneralSettings()
        {
            if (ImGui.BeginTable("EncounterGeneral", 2, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGui.Checkbox("World markers", ref this.Settings.EnableWorldMarkers);
                ImGui.Checkbox("Summary window", ref this.Settings.ShowSummaryWindow);
                ImGui.Checkbox("Labels", ref this.Settings.ShowLabels);
                ImGui.Checkbox("Label background", ref this.Settings.ShowLabelBackground);

                ImGui.TableNextColumn();
                ImGui.Checkbox("Hide dead", ref this.Settings.HideDead);
                ImGui.Checkbox("Hide when game is in background", ref this.Settings.HideWhenGameInBackground);
                ImGui.Checkbox("Hide in town", ref this.Settings.HideInTown);
                ImGui.Checkbox("Hide in hideout", ref this.Settings.HideInHideout);
                ImGui.Checkbox("Only matched in summary", ref this.Settings.ShowOnlyMatchedSummary);
                ImGui.SliderFloat("Max grid distance", ref this.Settings.MaxGridDistance, 50f, 1200f);
                ImGui.EndTable();
            }
        }

        private void DrawAppearanceSettings()
        {
            ImGui.SliderFloat("Marker radius", ref this.Settings.MarkerRadius, 4f, 40f);
            ImGui.SliderFloat("Marker thickness", ref this.Settings.MarkerThickness, 1f, 10f);
            ImGui.SliderFloat("Outline padding", ref this.Settings.MarkerOutlinePadding, 0f, 12f);
            ImGui.SliderInt("Circle segments", ref this.Settings.MarkerSegments, 8, 64);
            ImGui.ColorEdit4("Outline color", ref this.Settings.MarkerOutlineColor);
            ImGui.ColorEdit4("Label background", ref this.Settings.LabelBackgroundColor);
            ImGui.DragFloat2("Label offset", ref this.Settings.LabelOffset, 0.25f, -80f, 80f);
        }

        private void DrawRulesSettings()
        {
            if (ImGui.BeginTable("EncounterRules", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0f, 420f)))
            {
                ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 38f);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 120f);
                ImGui.TableSetupColumn("Path contains", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Shape", ImGuiTableColumnFlags.WidthFixed, 105f);
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Fill/Dot", ImGuiTableColumnFlags.WidthFixed, 88f);
                ImGui.TableSetupColumn("Marker", ImGuiTableColumnFlags.WidthFixed, 160f);
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 160f);
                ImGui.TableHeadersRow();

                for (var i = 0; i < this.Settings.Rules.Count; i++)
                {
                    var rule = this.Settings.Rules[i];
                    ImGui.PushID(i);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.Checkbox("##enabled", ref rule.Enabled);

                    ImGui.TableNextColumn();
                    var name = rule.Name;
                    if (ImGui.InputText("##name", ref name, 96))
                    {
                        rule.Name = name;
                    }

                    ImGui.TableNextColumn();
                    var path = rule.PathContains;
                    if (ImGui.InputText("##path", ref path, 256))
                    {
                        rule.PathContains = path;
                    }

                    ImGui.TableNextColumn();
                    var shape = rule.Shape;
                    if (ImGuiHelper.EnumComboBox("##shape", ref shape))
                    {
                        rule.Shape = shape;
                    }

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1f);
                    ImGui.DragFloat("##size", ref rule.SizeMultiplier, 0.01f, 0.1f, 4f, "x%.2f");

                    ImGui.TableNextColumn();
                    ImGui.Checkbox("F##filled", ref rule.Filled);
                    ImGui.SameLine();
                    ImGui.Checkbox("D##dot", ref rule.ShowCenterDot);

                    ImGui.TableNextColumn();
                    ImGui.ColorEdit4("##marker", ref rule.MarkerColor, ImGuiColorEditFlags.AlphaBar);

                    ImGui.TableNextColumn();
                    ImGui.Checkbox("L##label", ref rule.ShowLabel);
                    ImGui.SameLine();
                    ImGui.ColorEdit4("##labelColor", ref rule.LabelColor, ImGuiColorEditFlags.AlphaBar);
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        }

        private void DrawDebugSettings()
        {
            ImGui.Text($"Matched: {this.seen}, drawn: {this.drawn}");
            ImGui.Text($"Skipped: dead={this.skippedDead}, noRender={this.skippedNoRender}, distance={this.skippedDistance}, screen={this.skippedOutsideScreen}");
            ImGui.Separator();
            foreach (var rule in this.Settings.Rules)
            {
                ImGui.Text($"{rule.Name}: {this.matchCounts.GetValueOrDefault(rule.Name)}");
            }
        }

        private void DrawMarker(ImDrawListPtr drawList, Vector2 center, EncounterRule rule, float radius)
        {
            var markerColor = ImGuiHelper.Color(rule.MarkerColor);
            var outlineColor = ImGuiHelper.Color(this.Settings.MarkerOutlineColor);
            var outlineRadius = radius + this.Settings.MarkerOutlinePadding;
            var thickness = MathF.Max(1f, this.Settings.MarkerThickness);
            var segments = Math.Clamp(this.Settings.MarkerSegments, 8, 64);

            if (HasAlpha(outlineColor))
            {
                this.DrawMarkerShape(drawList, center, rule.Shape, outlineRadius, outlineColor, thickness + 1.5f, false, segments);
            }

            if (HasAlpha(markerColor))
            {
                this.DrawMarkerShape(drawList, center, rule.Shape, radius, markerColor, thickness, rule.Filled, segments);
                if (rule.ShowCenterDot)
                {
                    drawList.AddCircleFilled(center, MathF.Max(2f, radius * 0.22f), markerColor, segments);
                }
            }
        }

        private void DrawMarkerShape(ImDrawListPtr drawList, Vector2 center, EncounterMarkerShape shape, float radius, uint color, float thickness, bool filled, int segments)
        {
            switch (shape)
            {
                case EncounterMarkerShape.Circle:
                    if (filled)
                    {
                        drawList.AddCircleFilled(center, radius, color, segments);
                    }
                    else
                    {
                        drawList.AddCircle(center, radius, color, segments, thickness);
                    }

                    break;
                case EncounterMarkerShape.Diamond:
                    this.DrawDiamond(drawList, center, radius, color, thickness);
                    break;
                case EncounterMarkerShape.Square:
                    var squareMin = center - new Vector2(radius);
                    var squareMax = center + new Vector2(radius);
                    if (filled)
                    {
                        drawList.AddRectFilled(squareMin, squareMax, color, 2f);
                    }
                    else
                    {
                        drawList.AddRect(squareMin, squareMax, color, 2f, ImDrawFlags.None, thickness);
                    }

                    break;
                case EncounterMarkerShape.Triangle:
                    var top = center + new Vector2(0f, -radius);
                    var left = center + new Vector2(-radius * 0.9f, radius * 0.75f);
                    var right = center + new Vector2(radius * 0.9f, radius * 0.75f);
                    if (filled)
                    {
                        drawList.AddTriangleFilled(top, right, left, color);
                    }
                    else
                    {
                        drawList.AddTriangle(top, right, left, color, thickness);
                    }

                    break;
                case EncounterMarkerShape.Cross:
                    drawList.AddLine(center + new Vector2(-radius, -radius), center + new Vector2(radius, radius), color, thickness);
                    drawList.AddLine(center + new Vector2(radius, -radius), center + new Vector2(-radius, radius), color, thickness);
                    break;
                case EncounterMarkerShape.Dot:
                    drawList.AddCircleFilled(center, radius * 0.55f, color, segments);
                    break;
                case EncounterMarkerShape.Ring:
                default:
                    drawList.AddCircle(center, radius, color, segments, thickness);
                    break;
            }
        }

        private void DrawDiamond(ImDrawListPtr drawList, Vector2 center, float radius, uint color, float thickness)
        {
            var top = center + new Vector2(0f, -radius);
            var right = center + new Vector2(radius, 0f);
            var bottom = center + new Vector2(0f, radius);
            var left = center + new Vector2(-radius, 0f);
            drawList.AddLine(top, right, color, thickness);
            drawList.AddLine(right, bottom, color, thickness);
            drawList.AddLine(bottom, left, color, thickness);
            drawList.AddLine(left, top, color, thickness);
        }

        private void DrawLabel(ImDrawListPtr drawList, Vector2 center, EncounterRule rule, float markerRadius)
        {
            var labelColor = ImGuiHelper.Color(rule.LabelColor);
            if (!HasAlpha(labelColor) || string.IsNullOrWhiteSpace(rule.Name))
            {
                return;
            }

            var pos = center + new Vector2(markerRadius, 0f) + this.Settings.LabelOffset;
            if (this.Settings.ShowLabelBackground)
            {
                var bgColor = ImGuiHelper.Color(this.Settings.LabelBackgroundColor);
                if (HasAlpha(bgColor))
                {
                    var textSize = ImGui.CalcTextSize(rule.Name);
                    drawList.AddRectFilled(pos - new Vector2(4f, 2f), pos + textSize + new Vector2(4f, 2f), bgColor, 3f);
                }
            }

            drawList.AddText(pos, labelColor, rule.Name);
        }

        private void NormalizeSettings()
        {
            var defaults = new EncounterHelperSettings();
            this.Settings.Rules ??= [];

            foreach (var rule in this.Settings.Rules)
            {
                NormalizeRule(rule);
            }

            this.Settings.Rules = this.Settings.Rules
                .Where(rule => !string.IsNullOrWhiteSpace(rule.Name) || !string.IsNullOrWhiteSpace(rule.PathContains))
                .GroupBy(rule => GetRuleKey(rule), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            foreach (var defaultRule in defaults.Rules)
            {
                if (!this.Settings.Rules.Any(rule => AreSameRule(rule, defaultRule)))
                {
                    this.Settings.Rules.Add(defaultRule);
                }
            }

            this.Settings.MarkerRadius = Math.Clamp(this.Settings.MarkerRadius, 1f, 80f);
            this.Settings.MarkerThickness = Math.Clamp(this.Settings.MarkerThickness, 1f, 20f);
            this.Settings.MarkerSegments = Math.Clamp(this.Settings.MarkerSegments, 8, 64);
            this.Settings.MaxGridDistance = Math.Clamp(this.Settings.MaxGridDistance, 1f, 5000f);
        }

        private static void NormalizeRule(EncounterRule rule)
        {
            if (rule.Color != ImGuiHelper.Color(120, 220, 255, 245))
            {
                rule.MarkerColor = PackedColorToVector4(rule.Color);
            }

            rule.Name = rule.Name?.Trim() ?? string.Empty;
            rule.PathContains = rule.PathContains?.Trim() ?? string.Empty;
            rule.SizeMultiplier = Math.Clamp(rule.SizeMultiplier, 0.1f, 4f);
            if (!Enum.IsDefined(rule.Shape))
            {
                rule.Shape = EncounterMarkerShape.Ring;
            }
        }

        private static bool AreSameRule(EncounterRule left, EncounterRule right)
        {
            return GetRuleKey(left).Equals(GetRuleKey(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRuleKey(EncounterRule rule)
        {
            return $"{rule.Name.Trim()}|{rule.PathContains.Trim()}";
        }

        private static Vector4 PackedColorToVector4(uint color)
        {
            return new Vector4(
                (color & 0xFF) / 255f,
                ((color >> 8) & 0xFF) / 255f,
                ((color >> 16) & 0xFF) / 255f,
                ((color >> 24) & 0xFF) / 255f);
        }

        private static bool HasAlpha(uint color)
        {
            return (color & 0xFF000000) != 0;
        }
    }
}
