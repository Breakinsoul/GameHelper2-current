namespace MonsterRadarPro
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

    public sealed class MonsterRadarPro : PCore<MonsterRadarProSettings>
    {
        private int seen;
        private int drawn;
        private int dead;
        private int skippedDistance;
        private int skippedNoRender;
        private int normal;
        private int magic;
        private int rare;
        private int unique;
        private int unknown;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        public override void OnEnable(bool isGameOpened)
        {
            if (!File.Exists(this.SettingPathname))
            {
                return;
            }

            var content = File.ReadAllText(this.SettingPathname);
            this.Settings = JsonConvert.DeserializeObject<MonsterRadarProSettings>(content) ?? new MonsterRadarProSettings();
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
            var enableMarkers = this.Settings.EnableWorldMarkers;
            if (ImGui.Checkbox("World markers", ref enableMarkers))
            {
                this.Settings.EnableWorldMarkers = enableMarkers;
            }

            var showSummary = this.Settings.ShowSummaryWindow;
            if (ImGui.Checkbox("Summary window", ref showSummary))
            {
                this.Settings.ShowSummaryWindow = showSummary;
            }

            var hideDead = this.Settings.HideDeadMonsters;
            if (ImGui.Checkbox("Hide dead monsters", ref hideDead))
            {
                this.Settings.HideDeadMonsters = hideDead;
            }

            var hideFriendly = this.Settings.HideFriendlyMonsters;
            if (ImGui.Checkbox("Hide friendly monsters", ref hideFriendly))
            {
                this.Settings.HideFriendlyMonsters = hideFriendly;
            }

            var showLabels = this.Settings.ShowLabels;
            if (ImGui.Checkbox("Labels", ref showLabels))
            {
                this.Settings.ShowLabels = showLabels;
            }

            var radius = this.Settings.MarkerRadius;
            if (ImGui.SliderFloat("Marker radius", ref radius, 2f, 24f))
            {
                this.Settings.MarkerRadius = radius;
            }

            var distance = this.Settings.MaxGridDistance;
            if (ImGui.SliderFloat("Max grid distance", ref distance, 25f, 600f))
            {
                this.Settings.MaxGridDistance = distance;
            }

            this.DrawRarityToggles();
        }

        public override void DrawUI()
        {
            this.ResetCounters();

            if (Core.Process.Pid == 0 || Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            var player = area.Player;
            if (!player.TryGetComponent<Render>(out var playerRender, false))
            {
                return;
            }

            if (this.Settings.EnableWorldMarkers)
            {
                this.DrawMonsterMarkers(area, playerRender);
            }

            if (this.Settings.ShowSummaryWindow)
            {
                this.DrawSummaryWindow(area);
            }
        }

        private void DrawRarityToggles()
        {
            var showNormal = this.Settings.ShowNormal;
            if (ImGui.Checkbox("Normal", ref showNormal))
            {
                this.Settings.ShowNormal = showNormal;
            }

            ImGui.SameLine();
            var showMagic = this.Settings.ShowMagic;
            if (ImGui.Checkbox("Magic", ref showMagic))
            {
                this.Settings.ShowMagic = showMagic;
            }

            ImGui.SameLine();
            var showRare = this.Settings.ShowRare;
            if (ImGui.Checkbox("Rare", ref showRare))
            {
                this.Settings.ShowRare = showRare;
            }

            ImGui.SameLine();
            var showUnique = this.Settings.ShowUnique;
            if (ImGui.Checkbox("Unique", ref showUnique))
            {
                this.Settings.ShowUnique = showUnique;
            }
        }

        private void DrawMonsterMarkers(AreaInstance area, Render playerRender)
        {
            var drawList = ImGui.GetBackgroundDrawList();
            var playerGrid = playerRender.GridPosition;
            var windowWidth = Core.Process.WindowArea.Width;
            var windowHeight = Core.Process.WindowArea.Height;

            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!this.IsMonsterCandidate(entity))
                {
                    continue;
                }

                this.seen++;

                if (this.Settings.HideFriendlyMonsters && entity.EntityState == EntityStates.MonsterFriendly)
                {
                    continue;
                }

                var isDead = IsDead(entity);
                if (isDead)
                {
                    this.dead++;
                    if (this.Settings.HideDeadMonsters)
                    {
                        continue;
                    }
                }

                if (!entity.TryGetComponent<Render>(out var render, false))
                {
                    this.skippedNoRender++;
                    continue;
                }

                var grid = render.GridPosition;
                var dx = grid.X - playerGrid.X;
                var dy = grid.Y - playerGrid.Y;
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance > this.Settings.MaxGridDistance)
                {
                    this.skippedDistance++;
                    continue;
                }

                var rarity = this.GetRarity(entity);
                if (!this.ShouldDrawRarity(rarity))
                {
                    continue;
                }

                var screen = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(render.WorldPosition, render.WorldPosition.Z);
                if (screen.X < 0 || screen.Y < 0 || screen.X > windowWidth || screen.Y > windowHeight)
                {
                    continue;
                }

                var color = this.GetRarityColor(rarity);
                drawList.AddCircleFilled(screen, this.Settings.MarkerRadius, color, 20);
                drawList.AddCircle(screen, this.Settings.MarkerRadius + 2f, ImGuiHelper.Color(0, 0, 0, 230), 20, 2f);

                if (this.Settings.ShowLabels)
                {
                    var label = rarity?.ToString() ?? "Monster";
                    drawList.AddText(screen + new Vector2(this.Settings.MarkerRadius + 4f, -8f), color, label);
                }

                this.CountRarity(rarity);
                this.drawn++;
            }
        }

        private void DrawSummaryWindow(AreaInstance area)
        {
            ImGui.SetNextWindowSize(new Vector2(300f, 210f), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Monster Radar Pro"))
            {
                ImGui.End();
                return;
            }

            ImGui.Text($"Area: {area.AreaHash}");
            ImGui.Text($"Seen: {this.seen}");
            ImGui.Text($"Drawn: {this.drawn}");
            ImGui.Text($"Dead: {this.dead}");
            ImGui.Text($"No render: {this.skippedNoRender}");
            ImGui.Text($"Out of range: {this.skippedDistance}");
            ImGui.Separator();
            ImGui.Text($"Normal: {this.normal}");
            ImGui.Text($"Magic: {this.magic}");
            ImGui.Text($"Rare: {this.rare}");
            ImGui.Text($"Unique: {this.unique}");
            ImGui.Text($"Unknown: {this.unknown}");

            ImGui.End();
        }

        private bool IsMonsterCandidate(Entity entity)
        {
            return entity.EntityType == EntityTypes.Monster ||
                   entity.Path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal) ||
                   entity.HasComponent(nameof(Life)) ||
                   entity.HasComponent(nameof(Targetable)) ||
                   entity.HasComponent(nameof(Buffs)) ||
                   entity.HasComponent(nameof(Stats)) ||
                   entity.HasComponent("BaseEvents");
        }

        private Rarity? GetRarity(Entity entity)
        {
            return entity.TryGetComponent<ObjectMagicProperties>(out var omp, false)
                ? omp.Rarity
                : null;
        }

        private bool ShouldDrawRarity(Rarity? rarity)
        {
            return rarity switch
            {
                Rarity.Normal => this.Settings.ShowNormal,
                Rarity.Magic => this.Settings.ShowMagic,
                Rarity.Rare => this.Settings.ShowRare,
                Rarity.Unique => this.Settings.ShowUnique,
                _ => true,
            };
        }

        private uint GetRarityColor(Rarity? rarity)
        {
            return rarity switch
            {
                Rarity.Normal => this.Settings.NormalColor,
                Rarity.Magic => this.Settings.MagicColor,
                Rarity.Rare => this.Settings.RareColor,
                Rarity.Unique => this.Settings.UniqueColor,
                _ => this.Settings.UnknownColor,
            };
        }

        private void CountRarity(Rarity? rarity)
        {
            switch (rarity)
            {
                case Rarity.Normal:
                    this.normal++;
                    break;
                case Rarity.Magic:
                    this.magic++;
                    break;
                case Rarity.Rare:
                    this.rare++;
                    break;
                case Rarity.Unique:
                    this.unique++;
                    break;
                default:
                    this.unknown++;
                    break;
            }
        }

        private void ResetCounters()
        {
            this.seen = 0;
            this.drawn = 0;
            this.dead = 0;
            this.skippedDistance = 0;
            this.skippedNoRender = 0;
            this.normal = 0;
            this.magic = 0;
            this.rare = 0;
            this.unique = 0;
            this.unknown = 0;
        }

        private static bool IsDead(Entity entity)
        {
            return entity.TryGetStatValue(GameStats.is_dead, out var isDead) && isDead > 0;
        }
    }
}
