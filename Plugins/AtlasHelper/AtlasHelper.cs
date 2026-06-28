namespace AtlasHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteObjects.States;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class AtlasHelper : PCore<AtlasHelperSettings>
    {
        private readonly List<AtlasLiveNode> liveNodes = [];
        private string liveNodeSource = "none";
        private string captureStatus = string.Empty;
        private string liveNodeFilter = string.Empty;
        private DateTimeOffset lastCaptureAt = DateTimeOffset.MinValue;
        private DateTimeOffset lastLiveNodeRefreshAt = DateTimeOffset.MinValue;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        private string DumpDirectory => Path.Join(this.DllDirectory, "dumps");

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<AtlasHelperSettings>(content) ?? new AtlasHelperSettings();
            }

            this.NormalizeSettings();
        }

        public override void OnDisable()
        {
        }

        public override void SaveSettings()
        {
            JsonHelper.SafeToFile(this.Settings, new FileInfo(this.SettingPathname));
        }

        public override void DrawSettings()
        {
            this.DrawControls();
        }

        public override void DrawUI()
        {
            this.UpdateLiveAtlasNodes();

            if (this.Settings.HideWhenGameInBackground && !Core.Process.Foreground)
            {
                return;
            }

            if (this.Settings.DrawLiveAtlasNodes)
            {
                this.DrawLiveAtlasNodes();
            }

            if (this.Settings.ShowWindow)
            {
                this.DrawNodeWindow();
            }
        }

        private void DrawNodeWindow()
        {
            if (this.Settings.WindowPos != Vector2.Zero)
            {
                ImGui.SetNextWindowPos(this.Settings.WindowPos, ImGuiCond.FirstUseEver);
            }

            ImGui.SetNextWindowSize(this.Settings.WindowSize, ImGuiCond.FirstUseEver);
            var show = this.Settings.ShowWindow;
            if (!ImGui.Begin("Atlas Nodes##AtlasHelper", ref show))
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
            this.DrawControls();
            ImGui.End();
        }

        private void DrawControls()
        {
            ImGui.Checkbox("Show node window", ref this.Settings.ShowWindow);
            ImGui.Checkbox("Hide when game is in background", ref this.Settings.HideWhenGameInBackground);
            ImGui.Checkbox("Draw live atlas nodes", ref this.Settings.DrawLiveAtlasNodes);
            var labelModes = new[] { "Hover only", "Map names", "Node ids", "Map + id", "Raw keys" };
            this.Settings.LabelMode = Math.Clamp(this.Settings.LabelMode, 0, labelModes.Length - 1);
            ImGui.Combo("Label mode", ref this.Settings.LabelMode, labelModes, labelModes.Length);
            ImGui.Checkbox("Draw raw keys", ref this.Settings.DrawRawKeys);
            ImGui.Checkbox("Draw hidden nodes", ref this.Settings.DrawHiddenNodes);
            ImGui.Checkbox("Capture UI tree with node dump", ref this.Settings.CaptureUiTree);
            ImGui.DragFloat("Refresh interval ms", ref this.Settings.RefreshIntervalMs, 25f, 100f, 3000f);
            ImGui.DragFloat("Live node radius", ref this.Settings.LiveNodeRadius, 0.25f, 1f, 24f);
            ImGui.DragFloat("Capture radius", ref this.Settings.CaptureRadius, 1f, 20f, 400f);
            ImGui.SliderFloat2("Anchor nudge", ref this.Settings.AnchorNudge, -500f, 500f);
            ImGui.ColorEdit4("Accessible node", ref this.Settings.AccessibleNodeColor);
            ImGui.ColorEdit4("Hidden node", ref this.Settings.HiddenNodeColor);
            ImGui.ColorEdit4("Label", ref this.Settings.LabelColor);

            ImGui.SeparatorText("Live atlas nodes");
            ImGui.TextUnformatted($"Current area: {this.GetCurrentAreaLabel()}");
            ImGui.TextUnformatted($"Live nodes: {this.liveNodes.Count} ({this.liveNodeSource})");
            ImGui.TextUnformatted("Hover node: inspect raw fields. Ctrl+Click: capture nearest node.");
            if (ImGui.Button("Capture nearest live node"))
            {
                this.CaptureNearestAtlasNodeSnapshot(force: true);
            }

            if (!string.IsNullOrWhiteSpace(this.captureStatus))
            {
                ImGui.TextWrapped(this.captureStatus);
            }

            this.DrawLiveNodeTable();
        }

        private void DrawLiveNodeTable()
        {
            if (!ImGui.CollapsingHeader("Live node table"))
            {
                return;
            }

            ImGui.InputText("Search##atlas_live_node_filter", ref this.liveNodeFilter, 128);
            var rows = this.liveNodes
                .Where(node => string.IsNullOrWhiteSpace(this.liveNodeFilter) ||
                    node.Id.ToString().Contains(this.liveNodeFilter, StringComparison.OrdinalIgnoreCase) ||
                    node.MapName.Contains(this.liveNodeFilter, StringComparison.OrdinalIgnoreCase) ||
                    node.BiomeName.Contains(this.liveNodeFilter, StringComparison.OrdinalIgnoreCase) ||
                    node.RawKey.ToString("X").Contains(this.liveNodeFilter, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (!ImGui.BeginTable("AtlasHelperLiveNodeTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0f, 240f)))
            {
                return;
            }

            ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 76f);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 72f);
            ImGui.TableSetupColumn("Map");
            ImGui.TableSetupColumn("Biome", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Raw key", ImGuiTableColumnFlags.WidthFixed, 136f);
            ImGui.TableSetupColumn("Screen", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableHeadersRow();

            foreach (var node in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(node.Id.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(node.IsAccessible ? "access" : "hidden");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Fallback(node.MapName, "-"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Fallback(node.BiomeName, "-"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"0x{node.RawKey:X}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{node.Center.X:0},{node.Center.Y:0}");
            }

            ImGui.EndTable();
        }

        private void UpdateLiveAtlasNodes()
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - this.lastLiveNodeRefreshAt).TotalMilliseconds < this.Settings.RefreshIntervalMs)
            {
                return;
            }

            this.lastLiveNodeRefreshAt = now;
            this.liveNodes.Clear();
            this.liveNodeSource = "none";

            var inGameState = Core.States.InGameStateObject;
            if (Core.Process.Pid == 0 || inGameState == null || inGameState.Address == IntPtr.Zero)
            {
                return;
            }

            var items = inGameState.ReadAtlasUiGraphItems(readTooltips: this.Settings.DrawMapNames)
                .Where(item => item.Kind == AtlasUiGraphItemKind.MapNode)
                .ToArray();
            if (items.Length == 0)
            {
                return;
            }

            foreach (var item in items)
            {
                if (!item.IsVisible && !this.Settings.DrawHiddenNodes)
                {
                    continue;
                }

                if (!PluginRuntimeHelper.IsSafeScreenPosition(item.ScreenPosition) ||
                    !PluginRuntimeHelper.IsFinite(item.ScreenSize))
                {
                    continue;
                }

                this.liveNodes.Add(new AtlasLiveNode(
                    item.Id,
                    item.ScreenPosition,
                    new Vector2(Math.Max(4f, item.ScreenSize.X), Math.Max(4f, item.ScreenSize.Y)),
                    item.IsAccessible || item.IsVisible,
                    true,
                    item.Address,
                    item.ChildAddress,
                    item.RawX,
                    item.RawY,
                    item.RawState,
                    item.RawKey,
                    item.MapName,
                    item.BiomeName,
                    item.TooltipText));
            }

            this.liveNodeSource = $"live atlas graph ({this.liveNodes.Count}/{items.Length})";
        }

        private void DrawLiveAtlasNodes()
        {
            if (this.liveNodes.Count == 0)
            {
                return;
            }

            var draw = ImGui.GetForegroundDrawList();
            var accessibleColor = ImGui.ColorConvertFloat4ToU32(this.Settings.AccessibleNodeColor);
            var hiddenColor = ImGui.ColorConvertFloat4ToU32(this.Settings.HiddenNodeColor);
            var labelColor = ImGui.ColorConvertFloat4ToU32(this.Settings.LabelColor);

            foreach (var node in this.liveNodes)
            {
                var center = node.Center + this.Settings.AnchorNudge;
                if (!PluginRuntimeHelper.IsSafeScreenPosition(center))
                {
                    continue;
                }

                var radius = Math.Clamp(this.Settings.LiveNodeRadius, 1f, 24f);
                var color = node.IsAccessible ? accessibleColor : hiddenColor;
                draw.AddCircleFilled(center, radius, color, 16);

                if (this.IsMouseOverLiveNode(node, center))
                {
                    ImGui.SetTooltip(
                        $"Atlas node\nmap: {Fallback(node.MapName, "(unknown)")}\nbiome: {Fallback(node.BiomeName, "(unknown)")}\nid: {node.Id}\naddr: 0x{node.Address.ToInt64():X}\nchild: 0x{node.ChildAddress.ToInt64():X}\nrawX/rawY: {node.RawX}/{node.RawY}\nrawState: 0x{node.RawState:X}\nrawKey: 0x{node.RawKey:X16}\nCtrl+Click: dump nearest node data");
                }

                var drawMapName = this.Settings.LabelMode is 1 or 3 && !string.IsNullOrWhiteSpace(node.MapName);
                var drawNodeId = this.Settings.LabelMode is 2 or 3;
                var drawRawKey = this.Settings.LabelMode == 4 || this.Settings.DrawRawKeys;
                if (drawMapName)
                {
                    draw.AddText(center + new Vector2(7f, -7f), labelColor, node.MapName);
                }

                if (drawNodeId)
                {
                    draw.AddText(center + new Vector2(7f, drawMapName ? 8f : -7f), labelColor, node.Id.ToString());
                }

                if (drawRawKey)
                {
                    draw.AddText(center + new Vector2(7f, 8f), labelColor, $"0x{node.RawKey:X}");
                }
            }

            if (IsCtrlLeftMousePressed())
            {
                this.CaptureNearestAtlasNodeSnapshot(force: false);
            }
        }

        private bool IsMouseOverLiveNode(AtlasLiveNode node, Vector2 center)
        {
            var mouse = ImGui.GetIO().MousePos;
            if (!PluginRuntimeHelper.IsSafeScreenPosition(mouse))
            {
                return false;
            }

            var radius = MathF.Max(this.Settings.LiveNodeRadius + 8f, MathF.Max(node.Size.X, node.Size.Y) * 0.55f);
            if (!float.IsFinite(radius))
            {
                return false;
            }

            return Vector2.Distance(mouse, center) <= radius;
        }

        private void CaptureNearestAtlasNodeSnapshot(bool force)
        {
            if (this.liveNodes.Count == 0)
            {
                this.captureStatus = "Atlas node capture skipped: no live nodes.";
                return;
            }

            var now = DateTimeOffset.Now;
            if (!force && (now - this.lastCaptureAt).TotalMilliseconds < 700)
            {
                return;
            }

            var mouse = ImGui.GetIO().MousePos;
            if (!PluginRuntimeHelper.IsSafeScreenPosition(mouse))
            {
                this.captureStatus = "Atlas node capture skipped: mouse position is invalid.";
                return;
            }

            var validNodes = this.liveNodes
                .Where(node => PluginRuntimeHelper.IsSafeScreenPosition(node.Center + this.Settings.AnchorNudge))
                .ToArray();
            if (validNodes.Length == 0)
            {
                this.captureStatus = "Atlas node capture skipped: no valid live node positions.";
                return;
            }

            var nearest = validNodes
                .Select(node => (Node: node, Distance: Vector2.Distance(node.Center + this.Settings.AnchorNudge, mouse)))
                .OrderBy(pair => pair.Distance)
                .First();

            if (!force && nearest.Distance > this.Settings.CaptureRadius)
            {
                this.captureStatus = $"Atlas node capture skipped: nearest node is {nearest.Distance:0.#} px away.";
                this.lastCaptureAt = now;
                return;
            }

            this.lastCaptureAt = now;
            this.CaptureAtlasNodeSnapshot(nearest.Node, nearest.Distance);
        }

        private void CaptureAtlasNodeSnapshot(AtlasLiveNode selected, float distance)
        {
            try
            {
                Directory.CreateDirectory(this.DumpDirectory);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var path = Path.Join(this.DumpDirectory, $"atlas-node-hover-{stamp}.txt");
                var mouse = ImGui.GetIO().MousePos;
                var builder = new StringBuilder();
                builder.AppendLine($"Atlas node hover capture {DateTimeOffset.Now:O}");
                builder.AppendLine($"Current area: {this.GetCurrentAreaLabel()}");
                builder.AppendLine($"Live source: {this.liveNodeSource}");
                builder.AppendLine($"Mouse: {mouse.X:0.###},{mouse.Y:0.###}");
                builder.AppendLine($"Selected distance: {distance:0.###}");
                builder.AppendLine();
                builder.AppendLine("Selected node:");
                AppendNodeSnapshot(builder, selected);
                AppendPointerProbe(builder, "selected", selected.Address);
                AppendPointerProbe(builder, "selected child", selected.ChildAddress);
                builder.AppendLine();
                builder.AppendLine("Nearby nodes:");
                foreach (var node in this.liveNodes
                             .OrderBy(node => Vector2.Distance(node.Center + this.Settings.AnchorNudge, mouse))
                             .Take(20))
                {
                    AppendNodeSnapshot(builder, node);
                }

                File.WriteAllText(path, builder.ToString());

                var uiResult = string.Empty;
                if (this.Settings.CaptureUiTree)
                {
                    var uiPath = Path.Join(this.DumpDirectory, $"ui-tree-after-atlas-node-hover-{stamp}.txt");
                    uiResult = " " + Core.States.InGameStateObject.DumpVisibleUiTree(uiPath, maxDepth: 12, maxNodes: 40000);
                }

                this.captureStatus = $"Captured atlas node {selected.Id} to {path}.{uiResult}";
            }
            catch (Exception ex)
            {
                this.captureStatus = $"Atlas node capture failed: {ex.Message}";
            }
        }

        private static void AppendNodeSnapshot(StringBuilder builder, AtlasLiveNode node)
        {
            builder
                .Append("id=").Append(node.Id)
                .Append(" addr=0x").Append(node.Address.ToInt64().ToString("X"))
                .Append(" child=0x").Append(node.ChildAddress.ToInt64().ToString("X"))
                .Append(" pos=(").Append(node.Position.X.ToString("0.###")).Append(',').Append(node.Position.Y.ToString("0.###")).Append(')')
                .Append(" size=(").Append(node.Size.X.ToString("0.###")).Append(',').Append(node.Size.Y.ToString("0.###")).Append(')')
                .Append(" accessible=").Append(node.IsAccessible)
                .Append(" rawX=").Append(node.RawX)
                .Append(" rawY=").Append(node.RawY)
                .Append(" rawState=0x").Append(node.RawState.ToString("X"))
                .Append(" rawKey=0x").Append(node.RawKey.ToString("X16"))
                .Append(" map=\"").Append(node.MapName.Replace("\"", "'"))
                .Append("\" biome=\"").Append(node.BiomeName.Replace("\"", "'"))
                .Append("\" tooltip=\"").Append(node.TooltipText.Replace("\"", "'").Replace("\r", "\\r").Replace("\n", "\\n"))
                .Append('"')
                .AppendLine();
        }

        private static string Fallback(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static void AppendPointerProbe(StringBuilder builder, string label, IntPtr address)
        {
            if (address == IntPtr.Zero)
            {
                return;
            }

            builder.Append(Core.States.InGameStateObject.ProbeAtlasUiElementPointers(address, label));
        }

        private string GetCurrentAreaLabel()
        {
            if (Core.Process.Pid == 0)
            {
                return "Game is not attached.";
            }

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            var world = Core.States.InGameStateObject.CurrentWorldInstance;
            var areaName = world.AreaDetails?.Name ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(areaName))
            {
                return $"{areaName}  level {area.CurrentAreaLevel}  hash {area.AreaHash}";
            }

            return string.IsNullOrWhiteSpace(area.AreaHash)
                ? "Area is not available yet."
                : $"Area hash {area.AreaHash}  level {area.CurrentAreaLevel}";
        }

        private void NormalizeSettings()
        {
            this.Settings.LiveNodeRadius = Math.Clamp(this.Settings.LiveNodeRadius, 1f, 24f);
            this.Settings.CaptureRadius = Math.Clamp(this.Settings.CaptureRadius, 20f, 400f);
            this.Settings.RefreshIntervalMs = Math.Clamp(this.Settings.RefreshIntervalMs, 100f, 3000f);
        }

        private static bool IsCtrlLeftMousePressed()
        {
            const int vkLButton = 0x01;
            const int vkControl = 0x11;
            return (GetAsyncKeyState(vkLButton) & 0x8000) != 0 &&
                (GetAsyncKeyState(vkControl) & 0x8000) != 0;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKeyCode);
    }
}
