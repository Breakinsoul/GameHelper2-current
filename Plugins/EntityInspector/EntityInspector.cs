namespace EntityInspector
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class EntityInspector : PCore<EntityInspectorSettings>
    {
        private readonly Dictionary<string, int> componentCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<EntityTypes, int> typeCounts = new();
        private readonly Dictionary<EntityStates, int> stateCounts = new();
        private string idFilter = string.Empty;
        private string pathFilter = string.Empty;
        private string componentFilter = string.Empty;
        private string exportStatus = string.Empty;
        private EntityTypes typeFilter = EntityTypes.Unidentified;
        private EntityStates stateFilter = EntityStates.None;
        private bool filterByType;
        private bool filterByState;
        private uint selectedEntityId;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        public override void OnEnable(bool isGameOpened)
        {
            if (!File.Exists(this.SettingPathname))
            {
                return;
            }

            var content = File.ReadAllText(this.SettingPathname);
            this.Settings = JsonConvert.DeserializeObject<EntityInspectorSettings>(content) ?? new EntityInspectorSettings();
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
            if (ImGui.Checkbox("Show inspector window", ref show))
            {
                this.Settings.ShowWindow = show;
            }

            var includeInvalid = this.Settings.IncludeInvalidEntities;
            if (ImGui.Checkbox("Include invalid entities", ref includeInvalid))
            {
                this.Settings.IncludeInvalidEntities = includeInvalid;
            }

            var includeUseless = this.Settings.IncludeUselessEntities;
            if (ImGui.Checkbox("Include useless entities", ref includeUseless))
            {
                this.Settings.IncludeUselessEntities = includeUseless;
            }

            var maxRows = this.Settings.MaxRows;
            if (ImGui.InputInt("Max rows", ref maxRows))
            {
                this.Settings.MaxRows = Math.Clamp(maxRows, 10, 5000);
            }
        }

        public override void DrawUI()
        {
            if (!this.Settings.ShowWindow || Core.States.GameCurrentState == GameStateTypes.AreaLoadingState)
            {
                return;
            }

            if (Core.Process.Pid == 0 || Core.States.InGameStateObject.CurrentAreaInstance == null)
            {
                return;
            }

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            var entities = this.GetFilteredEntities(area).ToArray();
            this.RebuildSummary(area.AwakeEntities.Values);

            ImGui.SetNextWindowSize(new System.Numerics.Vector2(920f, 620f), ImGuiCond.FirstUseEver);
            var showWindow = this.Settings.ShowWindow;
            if (!ImGui.Begin("Entity Inspector", ref showWindow))
            {
                this.Settings.ShowWindow = showWindow;
                ImGui.End();
                return;
            }

            this.Settings.ShowWindow = showWindow;

            this.DrawHeader(area, entities.Length);
            ImGui.Separator();
            this.DrawFilters();
            ImGui.Separator();

            if (ImGui.BeginTabBar("EntityInspectorTabs"))
            {
                if (ImGui.BeginTabItem("Entities"))
                {
                    this.DrawEntitiesTable(entities);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Selected"))
                {
                    this.DrawSelectedEntity(area);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Summary"))
                {
                    this.DrawSummary();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.End();
        }

        private void DrawHeader(AreaInstance area, int filteredCount)
        {
            ImGui.Text($"Area: {area.AreaHash}  Level: {area.CurrentAreaLevel}  Network: {area.NetworkBubbleEntityCount}  Awake: {area.AwakeEntities.Count}  Filtered: {filteredCount}");
            if (ImGui.Button("Export selected"))
            {
                this.ExportSelected(area);
            }

            ImGui.SameLine();
            if (ImGui.Button("Export filtered"))
            {
                this.ExportEntities(this.GetFilteredEntities(area), "filtered");
            }

            if (!string.IsNullOrEmpty(this.exportStatus))
            {
                ImGui.SameLine();
                ImGui.TextWrapped(this.exportStatus);
            }
        }

        private void DrawFilters()
        {
            ImGui.InputText("Id", ref this.idFilter, 32);
            ImGui.SameLine();
            ImGui.InputText("Path", ref this.pathFilter, 160);
            ImGui.SameLine();
            ImGui.InputText("Component", ref this.componentFilter, 64);

            ImGui.Checkbox("Type", ref this.filterByType);
            ImGui.SameLine();
            this.EnumCombo("##type", ref this.typeFilter);
            ImGui.SameLine();
            ImGui.Checkbox("State", ref this.filterByState);
            ImGui.SameLine();
            this.EnumCombo("##state", ref this.stateFilter);
        }

        private void DrawEntitiesTable(IReadOnlyList<Entity> entities)
        {
            var rows = entities.Take(this.Settings.MaxRows).ToArray();
            if (!ImGui.BeginTable("EntityInspectorEntities", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
            {
                return;
            }

            ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Valid", ImGuiTableColumnFlags.WidthFixed, 52f);
            ImGui.TableSetupColumn("Dead", ImGuiTableColumnFlags.WidthFixed, 52f);
            ImGui.TableSetupColumn("Grid", ImGuiTableColumnFlags.WidthFixed, 130f);
            ImGui.TableSetupColumn("Components", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Path");
            ImGui.TableHeadersRow();

            foreach (var entity in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = this.selectedEntityId == entity.Id;
                if (ImGui.Selectable($"{entity.Id}", selected, ImGuiSelectableFlags.SpanAllColumns))
                {
                    this.selectedEntityId = entity.Id;
                }

                ImGui.TableNextColumn();
                ImGui.Text(entity.EntityType.ToString());
                ImGui.TableNextColumn();
                ImGui.Text(entity.EntityState.ToString());
                ImGui.TableNextColumn();
                ImGui.Text(entity.IsValid ? "yes" : "no");
                ImGui.TableNextColumn();
                ImGui.Text(IsDead(entity) ? "yes" : "no");
                ImGui.TableNextColumn();
                ImGui.Text(GetGridText(entity));
                ImGui.TableNextColumn();
                ImGui.Text($"{entity.GetComponentNames().Count()}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entity.Path);
            }

            ImGui.EndTable();
        }

        private void DrawSelectedEntity(AreaInstance area)
        {
            var entity = area.AwakeEntities.Values.FirstOrDefault(x => x.Id == this.selectedEntityId);
            if (entity == null)
            {
                ImGui.Text("No entity selected.");
                return;
            }

            ImGui.Text($"Id: {entity.Id}");
            ImGui.Text($"Path: {entity.Path}");
            ImGui.Text($"Type: {entity.EntityType} / {entity.EntitySubtype}");
            ImGui.Text($"State: {entity.EntityState}");
            ImGui.Text($"Valid: {entity.IsValid}");
            ImGui.Text($"Dead: {IsDead(entity)}");
            ImGui.Text($"Zones: {entity.Zones}");

            if (entity.TryGetComponent<Render>(out var render))
            {
                ImGui.SeparatorText("Render");
                ImGui.Text($"Grid: {render.GridPosition}");
                ImGui.Text($"World: {render.WorldPosition}");
                ImGui.Text($"Terrain: {render.TerrainHeight:0.####}");
            }

            if (entity.TryGetComponent<ObjectMagicProperties>(out var omp))
            {
                ImGui.SeparatorText("ObjectMagicProperties");
                ImGui.Text($"Rarity: {omp.Rarity}");
                ImGui.TextWrapped($"Mods: {string.Join(", ", omp.ModNames)}");
            }

            if (entity.TryGetComponent<Stats>(out var stats))
            {
                ImGui.SeparatorText("Stats");
                this.DrawStatsMap("Items", stats.StatsChangedByItems);
                this.DrawStatsMap("Buffs/Actions", stats.StatsChangedByBuffAndActions);
            }

            ImGui.SeparatorText("Components");
            foreach (var component in entity.GetComponentNames().OrderBy(x => x, StringComparer.Ordinal))
            {
                ImGui.TextUnformatted(component);
            }
        }

        private void DrawSummary()
        {
            ImGui.SeparatorText("Types");
            foreach (var item in this.typeCounts.OrderByDescending(x => x.Value))
            {
                ImGui.Text($"{item.Key}: {item.Value}");
            }

            ImGui.SeparatorText("States");
            foreach (var item in this.stateCounts.OrderByDescending(x => x.Value))
            {
                ImGui.Text($"{item.Key}: {item.Value}");
            }

            ImGui.SeparatorText("Components");
            foreach (var item in this.componentCounts.OrderByDescending(x => x.Value).Take(40))
            {
                ImGui.Text($"{item.Key}: {item.Value}");
            }
        }

        private IEnumerable<Entity> GetFilteredEntities(AreaInstance area)
        {
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!this.Settings.IncludeInvalidEntities && !entity.IsValid)
                {
                    continue;
                }

                if (!this.Settings.IncludeUselessEntities && entity.EntityState == EntityStates.Useless)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(this.idFilter) &&
                    !entity.Id.ToString().Contains(this.idFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(this.pathFilter) &&
                    !entity.Path.Contains(this.pathFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(this.componentFilter) &&
                    !entity.GetComponentNames().Any(x => x.Contains(this.componentFilter, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (this.filterByType && entity.EntityType != this.typeFilter)
                {
                    continue;
                }

                if (this.filterByState && entity.EntityState != this.stateFilter)
                {
                    continue;
                }

                yield return entity;
            }
        }

        private void RebuildSummary(IEnumerable<Entity> entities)
        {
            this.componentCounts.Clear();
            this.typeCounts.Clear();
            this.stateCounts.Clear();

            foreach (var entity in entities)
            {
                this.typeCounts[entity.EntityType] = this.typeCounts.TryGetValue(entity.EntityType, out var typeCount) ? typeCount + 1 : 1;
                this.stateCounts[entity.EntityState] = this.stateCounts.TryGetValue(entity.EntityState, out var stateCount) ? stateCount + 1 : 1;
                foreach (var component in entity.GetComponentNames())
                {
                    this.componentCounts[component] = this.componentCounts.TryGetValue(component, out var componentCount) ? componentCount + 1 : 1;
                }
            }
        }

        private void ExportSelected(AreaInstance area)
        {
            var entity = area.AwakeEntities.Values.FirstOrDefault(x => x.Id == this.selectedEntityId);
            if (entity == null)
            {
                this.exportStatus = "No entity selected.";
                return;
            }

            this.ExportEntities(new[] { entity }, $"entity-{entity.Id}");
        }

        private void ExportEntities(IEnumerable<Entity> entities, string label)
        {
            try
            {
                var dir = Path.Join(this.DllDirectory, "exports");
                Directory.CreateDirectory(dir);
                var path = Path.Join(dir, $"{label}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                var payload = entities.Select(ToExportObject).ToArray();
                File.WriteAllText(path, JsonConvert.SerializeObject(payload, Formatting.Indented));
                ImGui.SetClipboardText(path);
                this.exportStatus = $"Exported: {path}";
            }
            catch (Exception ex)
            {
                this.exportStatus = $"Export failed: {ex.Message}";
            }
        }

        private static object ToExportObject(Entity entity)
        {
            entity.TryGetComponent<Render>(out var render, false);
            entity.TryGetComponent<ObjectMagicProperties>(out var omp, false);
            entity.TryGetComponent<Stats>(out var stats, false);

            return new
            {
                entity.Id,
                entity.Path,
                Type = entity.EntityType.ToString(),
                Subtype = entity.EntitySubtype.ToString(),
                State = entity.EntityState.ToString(),
                entity.IsValid,
                Dead = IsDead(entity),
                Zones = entity.Zones.ToString(),
                Components = entity.GetComponentNames().OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                Render = render == null ? null : new
                {
                    Grid = new { render.GridPosition.X, render.GridPosition.Y, render.GridPosition.Z },
                    World = new { render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z },
                    render.TerrainHeight,
                },
                ObjectMagicProperties = omp == null ? null : new
                {
                    Rarity = omp.Rarity.ToString(),
                    Mods = omp.ModNames.ToArray(),
                },
                Stats = stats == null ? null : new
                {
                    Items = stats.StatsChangedByItems?.ToDictionary(x => x.Key.ToString(), x => x.Value),
                    BuffsAndActions = stats.StatsChangedByBuffAndActions?.ToDictionary(x => x.Key.ToString(), x => x.Value),
                },
            };
        }

        private static bool IsDead(Entity entity)
        {
            return entity.TryGetStatValue(GameStats.is_dead, out var isDead) && isDead > 0;
        }

        private static string GetGridText(Entity entity)
        {
            if (!entity.TryGetComponent<Render>(out var render, false))
            {
                return "-";
            }

            var grid = render.GridPosition;
            return $"{grid.X:0.##}, {grid.Y:0.##}, {grid.Z:0.##}";
        }

        private void DrawStatsMap(string label, IReadOnlyDictionary<GameStats, int>? stats)
        {
            if (stats == null || stats.Count == 0)
            {
                ImGui.Text($"{label}: none");
                return;
            }

            if (!ImGui.TreeNode($"{label} ({stats.Count})"))
            {
                return;
            }

            foreach (var item in stats.OrderBy(x => x.Key.ToString()))
            {
                ImGui.Text($"{item.Key}: {item.Value}");
            }

            ImGui.TreePop();
        }

        private void EnumCombo<TEnum>(string label, ref TEnum value)
            where TEnum : struct, Enum
        {
            var names = Enum.GetNames<TEnum>();
            var current = Array.IndexOf(names, value.ToString());
            if (ImGui.Combo(label, ref current, names, names.Length) &&
                current >= 0 &&
                current < names.Length &&
                Enum.TryParse<TEnum>(names[current], out var parsed))
            {
                value = parsed;
            }
        }
    }
}
