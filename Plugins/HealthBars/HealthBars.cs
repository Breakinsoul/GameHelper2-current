// <copyright file="HealthBars.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace HealthBars
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using Coroutine;
    using GameHelper;
    using GameHelper.CoroutineEvents;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     <see cref="HealthBars" /> plugin.
    /// </summary>
    public sealed class HealthBars : PCore<HealthBarsSettings>
    {
        private readonly List<string> textureToValidate = new()
        {
            "full_bar.png",
            "hollow_bar.png"
        };

        private int poiMonsterConfigToDelete = 0;
        private int poiMonsterConfigToAdd = 0;
        private float graduationsThickness = 0f;
        private Vector2 fontSize = Vector2.Zero;
        private readonly Dictionary<string, HealthBarDebugRecord> debugRecords = new(StringComparer.Ordinal);
        private string debugFilter = string.Empty;
        private string debugSkipReason = "Not evaluated";
        private int debugEntitiesSeen;
        private int debugCandidates;
        private int debugDrawAttempts;
        private int debugDrawn;
        private int debugFiltered;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        private string TexturesPath => Path.Join(this.DllDirectory, "Textures");

        private readonly TextureLoader textures = new();

        private readonly Dictionary<uint, Vector2> bPositions = new();
        private readonly HashSet<uint> activeEntityIdsScratch = new();
        private readonly List<uint> cachedEntityIdsScratch = new();

        private ActiveCoroutine? onAreaChange = null;
        private int entityDrawExceptionCount;
        private string lastEntityDrawException = string.Empty;

        private bool IsDebugEnabled => this.Settings.ShowDebugTable || this.Settings.ShowDebugOverlay;

        private bool IsTelemetryEnabled => this.IsDebugEnabled || this.Settings.ShowStatusOverlay;

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Text($"Textures loaded: {this.textures.TotalTexturesLoaded}");
            if (!this.Settings.UseModernBars)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted("Legacy texture mode");
            }

            if (!ImGui.BeginTabBar("HealthBarsSettingsTabs"))
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

            if (ImGui.BeginTabItem("Monsters"))
            {
                this.DrawConfigTabs("monster_config", this.Settings.Monster);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("POI"))
            {
                this.DrawPoiSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Players"))
            {
                this.DrawConfigTabs("player_config", this.Settings.Player);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Debug"))
            {
                this.DrawDebugSettings();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        /// <inheritdoc />
        public override void DrawUI()
        {
            this.ResetDebugState();
            if (Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState))
            {
                this.debugSkipReason = "Game state is not InGameState/EscapeState";
                this.DrawHealthBarStatusOverlay();
                this.DrawHealthBarDebugOverlay();
                return;
            }

            var cAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            var cWorldInstance = Core.States.InGameStateObject.CurrentWorldInstance;
            if ((!this.Settings.DrawInTown && cWorldInstance.AreaDetails.IsTown) ||
                (!this.Settings.DrawInHideout && cWorldInstance.AreaDetails.IsHideout))
            {
                this.debugSkipReason = "Hidden in town/hideout by settings";
                this.DrawHealthBarStatusOverlay();
                this.DrawHealthBarDebugOverlay();
                return;
            }

            if (!this.Settings.DrawWhenGameInBackground && !Core.Process.Foreground)
            {
                this.debugSkipReason = "Game is in background";
                this.DrawHealthBarStatusOverlay();
                this.DrawHealthBarDebugOverlay();
                return;
            }

            if (Core.States.GameCurrentState == GameStateTypes.InGameState &&
                Core.States.InGameStateObject.GameUi.IsAnyLargePanelOpen)
            {
                this.debugSkipReason = "Large panel is open";
                this.DrawHealthBarStatusOverlay();
                this.DrawHealthBarDebugOverlay();
                return;
            }

            this.debugSkipReason = "Drawing";
            this.UpdateOncePerDraw();
            this.SanitizeRuntimeSettings();
            this.PruneInterpolationCache(cAreaInstance);
            var debugEnabled = this.IsDebugEnabled;
            foreach (var entity in cAreaInstance.AwakeEntities)
            {
                var entityValue = entity.Value;
                if (debugEnabled)
                {
                    this.debugEntitiesSeen++;
                }

                var shouldTrack = debugEnabled && this.ShouldTrackDebugEntity(entityValue);
                if (shouldTrack)
                {
                    this.debugCandidates++;
                }

                if (this.Settings.HideOutsideNetworkBubble && !entityValue.IsValid)
                {
                    this.RecordDebug(entityValue, "filtered", "outside network bubble", shouldTrack);
                    continue;
                }

                var isMonsterEntity = entityValue.EntityType == EntityTypes.Monster;
                var isConfirmedDeadMonster = isMonsterEntity && IsDeadEntity(entityValue);
                if (entityValue.EntityState == EntityStates.Useless &&
                    (!isMonsterEntity || isConfirmedDeadMonster))
                {
                    this.RecordDebug(entityValue, "filtered", "useless state", shouldTrack);
                    continue;
                }

                if (entityValue.EntityState == EntityStates.PinnacleBossHidden)
                {
                    this.RecordDebug(entityValue, "filtered", "hidden pinnacle boss state", shouldTrack);
                    continue;
                }

                if (IsDeadEntity(entityValue))
                {
                    this.RecordDebug(entityValue, "filtered", "is_dead stat", shouldTrack);
                    continue;
                }

                if (entityValue.EntityType == EntityTypes.Player)
                {
                    if (entityValue.EntitySubtype == EntitySubtypes.PlayerOther)
                    {
                        if (entityValue.EntityState == EntityStates.PlayerLeader)
                        {
                            this.DrawHealthbar(entityValue, this.Settings.Player["leader"], (int)Rarity.Rare, false, "player leader");
                        }
                        else
                        {
                            this.DrawHealthbar(entityValue, this.Settings.Player["member"], (int)Rarity.Rare, false, "player member");
                        }
                    }
                    else
                    {
                        this.DrawHealthbar(entityValue, this.Settings.Player["self"], (int)Rarity.Rare, true, "self");
                    }

                    continue;
                }

                if (!this.IsMonsterLikeEntity(entityValue))
                {
                    this.RecordDebug(entityValue, "filtered", "not monster-like", shouldTrack);
                    continue;
                }

                if (entityValue.EntitySubtype == EntitySubtypes.POIMonster)
                {
                    if (!this.Settings.ShowPoiMonsters)
                    {
                        this.RecordDebug(entityValue, "filtered", "POI monsters hidden by settings", true);
                        continue;
                    }

                    if (!this.Settings.POIMonster.TryGetValue(entityValue.EntityCustomGroup, out var poiConfig))
                    {
                        poiConfig = this.Settings.POIMonster[-1];
                    }

                    this.DrawHealthbar(entityValue, poiConfig,
                        entityValue.TryGetComponent<ObjectMagicProperties>(out var poiMagicProps) ?
                        (int)poiMagicProps.Rarity :
                        (int)Rarity.Rare,
                        false,
                        "POI monster");
                }
                else if (entityValue.EntityState == EntityStates.MonsterFriendly)
                {
                    if (!this.Settings.ShowFriendlyMonsters)
                    {
                        this.RecordDebug(entityValue, "filtered", "friendly monsters hidden by settings", true);
                        continue;
                    }

                    this.DrawHealthbar(entityValue, this.Settings.Monster["friendly"], (int)Rarity.Rare, false, "friendly monster");
                }
                else if (entityValue.TryGetComponent<ObjectMagicProperties>(out var magicProps))
                {
                    if (!this.ShouldDrawRarity(magicProps.Rarity))
                    {
                        this.RecordDebug(entityValue, "filtered", $"{magicProps.Rarity} monsters hidden by settings", true, (int)magicProps.Rarity);
                        continue;
                    }

                    switch (magicProps.Rarity)
                    {
                        case Rarity.Normal:
                            this.DrawHealthbar(entityValue, this.Settings.Monster["white"], (int)Rarity.Normal, false, "normal monster");
                            break;
                        case Rarity.Magic:
                            this.DrawHealthbar(entityValue, this.Settings.Monster["magic"], (int)Rarity.Magic, false, "magic monster");
                            break;
                        case Rarity.Rare:
                            this.DrawHealthbar(entityValue, this.Settings.Monster["rare"], (int)Rarity.Rare, false, "rare monster");
                            break;
                        case Rarity.Unique:
                            this.DrawHealthbar(entityValue, this.Settings.Monster["unique"], (int)Rarity.Unique, false, "unique monster");
                            break;
                    }
                }
                else
                {
                    if (!this.Settings.ShowNormalMonsters)
                    {
                        this.RecordDebug(entityValue, "filtered", "fallback normal monsters hidden by settings", true, (int)Rarity.Normal);
                        continue;
                    }

                    this.DrawHealthbar(entityValue, this.Settings.Monster["white"], (int)Rarity.Normal, false, "fallback monster");
                }
            }

            this.DrawHealthBarStatusOverlay();
            this.DrawHealthBarDebugOverlay();
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
            this.textures.cleanup(this.TexturesPath);
            this.onAreaChange?.Cancel();
            this.onAreaChange = null;
        }

        /// <inheritdoc />
        public override void OnEnable(bool isGameOpened)
        {
            this.textures.Load(this.TexturesPath);
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<HealthBarsSettings>(content) ?? new HealthBarsSettings();
            }

            this.NormalizeSettings();

            for (var i = 0; i < this.textureToValidate.Count; i++)
            {
                if (!this.textures.TextureKeys.Contains(this.textureToValidate[i]))
                {
                    throw new Exception($"Missing texture file {this.textureToValidate[i]} in {this.TexturesPath} folder.");
                }
            }

            this.onAreaChange = CoroutineHandler.Start(this.OnAreaChange());
        }

        /// <inheritdoc />
        public override void SaveSettings()
        {
            JsonHelper.SafeToFile(this.Settings, new FileInfo(this.SettingPathname));
        }

        private void DrawGeneralSettings()
        {
            if (ImGui.BeginTable("HealthBarsGeneral", 2, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGui.Checkbox("Draw in town", ref this.Settings.DrawInTown);
                ImGui.Checkbox("Draw in hideout", ref this.Settings.DrawInHideout);
                ImGui.Checkbox("Draw when game is in background", ref this.Settings.DrawWhenGameInBackground);
                ImGui.Checkbox("Show mana rather than ES on self", ref this.Settings.ShowManaRatherThanESOnSelf);
                ImGui.Checkbox("Draw monsters with unknown HP", ref this.Settings.DrawUnknownHealthMonsters);
                ImGuiHelper.ToolTip("Use this when the Life component exists but the current game build returns 0/0 HP for monsters.");
                ImGui.Checkbox("Use Radar monster detection", ref this.Settings.UseRadarMonsterDetection);
                ImGuiHelper.ToolTip("Also treats renderable/unidentified entities as monsters when their path/components match Radar's monster fallback logic.");

                ImGui.TableNextColumn();
                ImGui.Checkbox("Hide outside network bubble", ref this.Settings.HideOutsideNetworkBubble);
                ImGui.Checkbox("Cull bars outside screen", ref this.Settings.CullOutsideScreen);
                ImGui.Checkbox("Interpolate position", ref this.Settings.InterpolatePosition);
                ImGuiHelper.ToolTip("Enable this if healthbars stutter while entities move.");
                if (this.Settings.InterpolatePosition)
                {
                    if (ImGui.SliderInt("Interpolation rate", ref this.Settings.InterpolationRate, 1, 1000))
                    {
                        this.Settings.InterpolationRate = Math.Clamp(this.Settings.InterpolationRate, 1, 1000);
                    }
                }

                ImGui.EndTable();
            }

            ImGui.SeparatorText("Visible monster classes");
            if (ImGui.BeginTable("HealthBarsVisibleClasses", 3, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGui.Checkbox("Normal", ref this.Settings.ShowNormalMonsters);
                ImGui.Checkbox("Magic", ref this.Settings.ShowMagicMonsters);
                ImGui.TableNextColumn();
                ImGui.Checkbox("Rare", ref this.Settings.ShowRareMonsters);
                ImGui.Checkbox("Unique", ref this.Settings.ShowUniqueMonsters);
                ImGui.TableNextColumn();
                ImGui.Checkbox("Friendly", ref this.Settings.ShowFriendlyMonsters);
                ImGui.Checkbox("POI", ref this.Settings.ShowPoiMonsters);
                ImGui.EndTable();
            }

            if (ImGui.Button("Show all"))
            {
                this.SetMonsterVisibility(normal: true, magic: true, rare: true, unique: true, friendly: true, poi: true);
            }

            ImGui.SameLine();
            if (ImGui.Button("Rares + uniques"))
            {
                this.SetMonsterVisibility(normal: false, magic: false, rare: true, unique: true, friendly: false, poi: true);
            }

            ImGui.SameLine();
            if (ImGui.Button("Uniques only"))
            {
                this.SetMonsterVisibility(normal: false, magic: false, rare: false, unique: true, friendly: false, poi: true);
            }

            ImGui.SeparatorText("Cull strike thresholds");
            ImGui.TextUnformatted("white       magic       rare        unique");
            ImGui.DragInt4("Percent health", ref this.Settings.CullingStrikeRangePerRarity[0], 1, 0, 100);
        }

        private void DrawAppearanceSettings()
        {
            ImGui.SeparatorText("Presets");
            if (ImGui.Button("Compact"))
            {
                this.ApplyStylePreset(HealthBarsStylePreset.Compact);
            }

            ImGui.SameLine();
            if (ImGui.Button("High contrast"))
            {
                this.ApplyStylePreset(HealthBarsStylePreset.HighContrast);
            }

            ImGui.SameLine();
            if (ImGui.Button("Boss focus"))
            {
                this.ApplyStylePreset(HealthBarsStylePreset.BossFocus);
            }

            ImGui.SeparatorText("Rendering");
            ImGui.Checkbox("Use modern bars", ref this.Settings.UseModernBars);
            if (!this.Settings.UseModernBars)
            {
                ImGui.TextWrapped("Legacy mode uses full_bar.png and hollow_bar.png textures. Modern mode draws vector bars with shadow, rounded corners and crisp borders.");
                return;
            }

            ImGui.SliderFloat("Corner rounding", ref this.Settings.ModernBarRounding, 0f, 12f);
            ImGui.SliderFloat("Border thickness", ref this.Settings.ModernBarBorderThickness, 0f, 4f);
            ImGui.SliderInt("Shadow alpha", ref this.Settings.ModernBarShadowAlpha, 0, 255);
            ImGui.ColorEdit4("Border color", ref this.Settings.ModernBarBorderColor);

            ImGui.SeparatorText("Text");
            ImGui.Checkbox("Show current HP text", ref this.Settings.ShowCurrentHealthText);
            ImGui.Checkbox("Use global HP text color", ref this.Settings.UseGlobalCurrentHealthTextColor);
            ImGui.ColorEdit4("Current HP text color", ref this.Settings.CurrentHealthTextColor);
            ImGui.ColorEdit4("Current HP shadow color", ref this.Settings.CurrentHealthTextShadowColor);

            ImGui.SeparatorText("Runtime summary");
            ImGui.Checkbox("Show status overlay", ref this.Settings.ShowStatusOverlay);
            ImGui.SliderFloat2("Status overlay position", ref this.Settings.StatusOverlayPosition, 0f, 4000f);
        }

        private void DrawDebugSettings()
        {
            ImGui.Checkbox("Show diagnostics table", ref this.Settings.ShowDebugTable);
            ImGui.Checkbox("Show floating diagnostics window", ref this.Settings.ShowDebugOverlay);
            ImGui.Checkbox("Show object name under healthbar", ref this.Settings.ShowDebugObjectNameUnderBar);
            ImGui.SameLine();
            ImGui.Checkbox("Full path", ref this.Settings.ShowDebugObjectFullPath);
            ImGui.ColorEdit4("Object name color", ref this.Settings.DebugObjectNameTextColor);
            ImGui.InputText("Filter", ref this.debugFilter, 256);
            this.DrawHealthBarDebugSummary();
            this.DrawHealthBarDebugTable();
        }

        private void DrawConfigTabs(string id, Dictionary<string, Config> configs)
        {
            if (!ImGui.BeginTabBar(id))
            {
                return;
            }

            foreach (var item in configs)
            {
                if (ImGui.BeginTabItem(item.Key))
                {
                    item.Value.Draw();
                    ImGui.EndTabItem();
                }
            }

            ImGui.EndTabBar();
        }

        private void DrawPoiSettings()
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
            if (ImGui.InputInt("Group Number##poimonsterconfig", ref this.poiMonsterConfigToAdd) && this.poiMonsterConfigToAdd < 0)
            {
                this.poiMonsterConfigToAdd = 0;
            }

            ImGui.SameLine();
            if (ImGui.Button("Add group"))
            {
                this.Settings.POIMonster.TryAdd(this.poiMonsterConfigToAdd, new());
            }

            if (ImGui.BeginTabBar("poimonster_config", ImGuiTabBarFlags.AutoSelectNewTabs))
            {
                foreach (var conf in this.Settings.POIMonster)
                {
                    var text = conf.Key < 0 ? "Default" : $"Group {conf.Key}";
                    var shouldNotDelete = true;
                    if (ImGui.BeginTabItem(text, ref shouldNotDelete, ImGuiTabItemFlags.NoAssumedClosure))
                    {
                        conf.Value.Draw();
                        ImGui.EndTabItem();
                    }

                    if (conf.Key >= 0 && !shouldNotDelete)
                    {
                        this.poiMonsterConfigToDelete = conf.Key;
                        ImGui.OpenPopup("POIConfigHealthbarDeleteConfirmation");
                    }
                }

                this.DrawConfirmationPopup();
                ImGui.EndTabBar();
            }
        }

        private void NormalizeSettings()
        {
            var defaults = new HealthBarsSettings();
            if (this.Settings.CullingStrikeRangePerRarity == null ||
                this.Settings.CullingStrikeRangePerRarity.Length < 4)
            {
                var normalized = new int[4];
                for (var i = 0; i < normalized.Length; i++)
                {
                    normalized[i] = this.Settings.CullingStrikeRangePerRarity != null &&
                        i < this.Settings.CullingStrikeRangePerRarity.Length
                            ? this.Settings.CullingStrikeRangePerRarity[i]
                            : defaults.CullingStrikeRangePerRarity[i];
                }

                this.Settings.CullingStrikeRangePerRarity = normalized;
            }

            this.Settings.Monster ??= new Dictionary<string, Config>();
            foreach (var (key, value) in defaults.Monster)
            {
                this.Settings.Monster.TryAdd(key, value);
            }

            this.Settings.POIMonster ??= new Dictionary<int, Config>();
            foreach (var (key, value) in defaults.POIMonster)
            {
                this.Settings.POIMonster.TryAdd(key, value);
            }

            this.Settings.Player ??= new Dictionary<string, Config>();
            foreach (var (key, value) in defaults.Player)
            {
                this.Settings.Player.TryAdd(key, value);
            }

            foreach (var config in this.Settings.Monster.Values)
            {
                config.Normalize();
            }

            foreach (var config in this.Settings.POIMonster.Values)
            {
                config.Normalize();
            }

            foreach (var config in this.Settings.Player.Values)
            {
                config.Normalize();
            }

            this.Settings.InterpolationRate = Math.Clamp(this.Settings.InterpolationRate, 1, 1000);
            this.SanitizeRuntimeSettings();
        }

        private void ApplyStylePreset(HealthBarsStylePreset preset)
        {
            this.Settings.UseModernBars = true;
            switch (preset)
            {
                case HealthBarsStylePreset.Compact:
                    this.Settings.ModernBarRounding = 2f;
                    this.Settings.ModernBarBorderThickness = 0f;
                    this.Settings.ModernBarShadowAlpha = 34;
                    this.ApplyScale("white", new Vector2(86f, 4f), false);
                    this.ApplyScale("magic", new Vector2(92f, 5f), false);
                    this.ApplyScale("rare", new Vector2(112f, 7f), true);
                    this.ApplyScale("unique", new Vector2(138f, 10f), true);
                    break;
                case HealthBarsStylePreset.HighContrast:
                    this.Settings.ModernBarRounding = 3f;
                    this.Settings.ModernBarBorderThickness = 1.25f;
                    this.Settings.ModernBarShadowAlpha = 78;
                    this.Settings.CurrentHealthTextColor = new(0.95f, 0.98f, 1f, 1f);
                    this.ApplyScale("white", new Vector2(104f, 6f), false);
                    this.ApplyScale("magic", new Vector2(112f, 7f), false);
                    this.ApplyScale("rare", new Vector2(132f, 9f), true);
                    this.ApplyScale("unique", new Vector2(160f, 12f), true);
                    break;
                case HealthBarsStylePreset.BossFocus:
                    this.Settings.ModernBarRounding = 3f;
                    this.Settings.ModernBarBorderThickness = 1f;
                    this.Settings.ModernBarShadowAlpha = 64;
                    this.Settings.ShowNormalMonsters = false;
                    this.Settings.ShowMagicMonsters = false;
                    this.Settings.ShowRareMonsters = true;
                    this.Settings.ShowUniqueMonsters = true;
                    this.ApplyScale("rare", new Vector2(138f, 9f), true);
                    this.ApplyScale("unique", new Vector2(190f, 15f), true);
                    break;
            }

            foreach (var config in this.Settings.Monster.Values)
            {
                config.Normalize();
            }
        }

        private void SetMonsterVisibility(bool normal, bool magic, bool rare, bool unique, bool friendly, bool poi)
        {
            this.Settings.ShowNormalMonsters = normal;
            this.Settings.ShowMagicMonsters = magic;
            this.Settings.ShowRareMonsters = rare;
            this.Settings.ShowUniqueMonsters = unique;
            this.Settings.ShowFriendlyMonsters = friendly;
            this.Settings.ShowPoiMonsters = poi;
        }

        private void ApplyScale(string key, Vector2 scale, bool showText)
        {
            if (!this.Settings.Monster.TryGetValue(key, out var config))
            {
                return;
            }

            config.Scale = scale;
            config.ShowText = showText;
            config.Normalize();
        }

        private void DrawHealthbar(Entity entity, Config healthbarConfig, int rarity, bool isSelf = false, string source = "")
        {
            try
            {
                this.DrawHealthbarUnsafe(entity, healthbarConfig, rarity, isSelf, source);
            }
            catch (Exception ex)
            {
                this.bPositions.Remove(entity.Id);
                this.entityDrawExceptionCount++;
                this.lastEntityDrawException = ex.Message;
                if (this.entityDrawExceptionCount <= 25 || this.entityDrawExceptionCount % 100 == 0)
                {
                    Console.WriteLine($"[HealthBars.DrawHealthbar] entity {entity.Id} ({source}) threw: {ex}");
                }

                try
                {
                    this.RecordDebug(entity, "filtered", $"{source}: exception {ex.GetType().Name}", true, rarity);
                }
                catch
                {
                    // Debug recording must not turn a bad entity into a repeated render failure.
                }
            }
        }

        private void DrawHealthbarUnsafe(Entity entity, Config healthbarConfig, int rarity, bool isSelf = false, string source = "")
        {
            if (this.IsDebugEnabled)
            {
                this.debugDrawAttempts++;
            }

            if (!healthbarConfig.Enable)
            {
                this.RecordDebug(entity, "filtered", $"{source}: config disabled", true, rarity);
                return;
            }

            if (!entity.TryGetComponent<Render>(out var rComp))
            {
                this.RecordDebug(entity, "filtered", $"{source}: missing Render", true, rarity);
                return;
            }

            var curPos = rComp.WorldPosition;
            if (!IsFinite(curPos) || !IsFinite(rComp.ModelBounds))
            {
                this.bPositions.Remove(entity.Id);
                this.RecordDebug(entity, "filtered", $"{source}: invalid render position", true, rarity);
                return;
            }

            curPos.Z -= rComp.ModelBounds.Z + healthbarConfig.Shift.Y;
            var location = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(curPos, curPos.Z);
            if (!IsSafeScreenPosition(location))
            {
                this.bPositions.Remove(entity.Id);
                this.RecordDebug(entity, "filtered", $"{source}: invalid screen projection", true, rarity, location);
                return;
            }

            location.X += healthbarConfig.Shift.X;
            if (!IsSafeScreenPosition(location))
            {
                this.bPositions.Remove(entity.Id);
                this.RecordDebug(entity, "filtered", $"{source}: invalid shifted projection", true, rarity, location);
                return;
            }

            var screenCullMargin = Vector2.One * (MathF.Max(healthbarConfig.HalfOfScale.X, healthbarConfig.HalfOfScale.Y) + 8f);
            var isProjectedOutsideScreen = PluginRuntimeHelper.IsOutsideScreen(location, screenCullMargin);
            if (isProjectedOutsideScreen && this.Settings.CullOutsideScreen)
            {
                this.bPositions.Remove(entity.Id);
                this.RecordDebug(entity, "filtered", $"{source}: outside screen", true, rarity, location);
                return;
            }

            if (!entity.TryGetComponent<Life>(out var hComp))
            {
                this.RecordDebug(entity, "filtered", $"{source}: missing Life", true, rarity, location);
                return;
            }

            var hasKnownHealth = hComp.Health.Total > 0 && hComp.Health.Current > 0;
            var shouldDrawUnknownHealth = !isSelf &&
                this.Settings.DrawUnknownHealthMonsters &&
                !IsDeadEntity(entity);
            if (hComp.Health.Total <= 0 || hComp.Health.Current <= 0)
            {
                if (!shouldDrawUnknownHealth)
                {
                    this.bPositions.Remove(entity.Id);
                    this.RecordDebug(entity, "filtered", $"{source}: zero hp ({hComp.Health.Current}/{hComp.Health.Total})", true, rarity, location, hComp, "Life component");
                    return;
                }
            }

            if (this.Settings.InterpolatePosition)
            {
                if (this.bPositions.TryGetValue(entity.Id, out var prevLocation))
                {
                    location = IsSafeScreenPosition(prevLocation)
                        ? MathHelper.Lerp(prevLocation, location, this.Settings.InterpolationRate / 1000f)
                        : location;
                    if (!IsSafeScreenPosition(location))
                    {
                        this.bPositions.Remove(entity.Id);
                        this.RecordDebug(entity, "filtered", $"{source}: invalid interpolated projection", true, rarity, location, hComp, "Life component");
                        return;
                    }
                }

                this.bPositions[entity.Id] = location;
            }

            var ptr = ImGui.GetBackgroundDrawList();
            var start = location - healthbarConfig.HalfOfScale;
            var end = location + healthbarConfig.HalfOfScale;
            if (!IsSafeScreenPosition(start) || !IsSafeScreenPosition(end) || end.X <= start.X || end.Y <= start.Y)
            {
                this.bPositions.Remove(entity.Id);
                this.RecordDebug(entity, "filtered", $"{source}: invalid draw rectangle", true, rarity, location, hComp, "Life component");
                return;
            }

            var hPercent = hasKnownHealth ? ClampPercent(hComp.Health.CurrentInPercent()) : 100;
            rarity = Math.Clamp(rarity, 0, this.Settings.CullingStrikeRangePerRarity.Length - 1);
            var isCullable = hasKnownHealth &&
                hPercent <= this.Settings.CullingStrikeRangePerRarity[rarity] &&
                healthbarConfig.ShowCullStrike;
            var secondaryPercent = 0;
            var hasSecondary = false;
            if (hasKnownHealth && isSelf && this.Settings.ShowManaRatherThanESOnSelf && hComp.Mana.Total > 0)
            {
                secondaryPercent = ClampPercent(hComp.Mana.CurrentInPercent());
                hasSecondary = true;
            }
            else if (hasKnownHealth && hComp.EnergyShield.Total > 0)
            {
                secondaryPercent = ClampPercent(hComp.EnergyShield.CurrentInPercent());
                hasSecondary = true;
            }

            if (this.Settings.UseModernBars)
            {
                this.DrawModernHealthbar(ptr, start, end, healthbarConfig, hPercent, secondaryPercent, hasSecondary, isCullable);
            }
            else
            {
                this.DrawLegacyHealthbar(ptr, start, end, healthbarConfig, hPercent, secondaryPercent, hasSecondary, isCullable);
            }

            var tmp = start - Vector2.UnitY;
            for (var i = 0; i < healthbarConfig.Graduations; i++)
            {
                tmp.X += healthbarConfig.GraduationsLocationStart;
                ptr.AddLine(tmp, tmp + healthbarConfig.GraduationsLocationEnd, 0xFF000000, this.graduationsThickness);
            }

            if (healthbarConfig.ShowText && this.Settings.ShowCurrentHealthText)
            {
                var secondaryValue = isSelf && this.Settings.ShowManaRatherThanESOnSelf
                    ? hComp.Mana.Current
                    : hComp.EnergyShield.Current;
                var textPos = start - this.fontSize;
                var healthText = hasKnownHealth
                    ? this.healthToHumanReadable((long)hComp.Health.Current + secondaryValue)
                    : "?";
                var shadowColor = ImGuiHelper.Color(this.Settings.CurrentHealthTextShadowColor);
                if (HasAlpha(shadowColor))
                {
                    ptr.AddText(textPos + Vector2.One, shadowColor, healthText);
                }

                var textColor = ImGuiHelper.Color(this.Settings.UseGlobalCurrentHealthTextColor
                    ? this.Settings.CurrentHealthTextColor
                    : healthbarConfig.TextColor);
                if (HasAlpha(textColor))
                {
                    ptr.AddText(textPos, textColor, healthText);
                }
            }

            if (this.Settings.ShowDebugObjectNameUnderBar)
            {
                this.DrawObjectName(ptr, entity, start, end);
            }

            if (this.IsDebugEnabled)
            {
                this.debugDrawn++;
            }

            var drawReason = isProjectedOutsideScreen
                ? $"{source}: projected outside"
                : source;
            this.RecordDebug(entity, "drawn", drawReason, true, rarity, location, hComp, hasKnownHealth ? "Life component" : "unknown -> full fallback");
        }

        private void UpdateOncePerDraw()
        {
            this.graduationsThickness = ImGui.GetFontSize() / 9f;
            this.fontSize = new(0f, ImGui.GetFontSize());
        }

        private void SanitizeRuntimeSettings()
        {
            this.Settings.InterpolationRate = Math.Clamp(this.Settings.InterpolationRate, 1, 1000);
            this.Settings.ModernBarRounding = SanitizeFloat(this.Settings.ModernBarRounding, 0f, 12f, 2f);
            this.Settings.ModernBarBorderThickness = SanitizeFloat(this.Settings.ModernBarBorderThickness, 0f, 4f, 0f);
            this.Settings.ModernBarShadowAlpha = Math.Clamp(this.Settings.ModernBarShadowAlpha, 0, 255);
            this.Settings.StatusOverlayPosition = SanitizeVector2(this.Settings.StatusOverlayPosition, new(20f, 120f));
            this.Settings.CurrentHealthTextColor = SanitizeColor(this.Settings.CurrentHealthTextColor, new(0.92156863f, 0.9607843f, 1f, 1f));
            this.Settings.CurrentHealthTextShadowColor = SanitizeColor(this.Settings.CurrentHealthTextShadowColor, new(0f, 0f, 0f, 0.85f));
            this.Settings.ModernBarBorderColor = SanitizeColor(this.Settings.ModernBarBorderColor, new(0.92156863f, 0.9607843f, 1f, 1f));

            foreach (var config in this.Settings.Monster.Values)
            {
                config.Normalize();
            }

            foreach (var config in this.Settings.POIMonster.Values)
            {
                config.Normalize();
            }

            foreach (var config in this.Settings.Player.Values)
            {
                config.Normalize();
            }
        }

        private void DrawModernHealthbar(
            ImDrawListPtr drawList,
            Vector2 start,
            Vector2 end,
            Config config,
            int healthPercent,
            int secondaryPercent,
            bool hasSecondary,
            bool isCullable)
        {
            var rounding = MathF.Min(this.Settings.ModernBarRounding, MathF.Min(config.Scale.X, config.Scale.Y) * 0.5f);
            var shadowAlpha = (uint)Math.Clamp(this.Settings.ModernBarShadowAlpha, 0, 255);
            var shadowOffset = new Vector2(1.5f, 2f);
            var shadowColor = ImGuiHelper.Color(0, 0, 0, shadowAlpha);
            var backgroundColor = ImGuiHelper.Color(config.BackgroundColor);
            var borderColor = isCullable
                ? ImGuiHelper.Color(255, 255, 255, 255)
                : ImGuiHelper.Color(this.Settings.ModernBarBorderColor);
            var healthColor = isCullable
                ? ImGuiHelper.Color(255, 255, 255, 255)
                : ImGuiHelper.Color(config.HealthbarColor);
            var width = end.X - start.X;

            if (HasAlpha(shadowColor))
            {
                drawList.AddRectFilled(start + shadowOffset, end + shadowOffset, shadowColor, rounding);
            }

            if (HasAlpha(backgroundColor))
            {
                drawList.AddRectFilled(start, end, backgroundColor, rounding);
            }

            var healthEnd = new Vector2(start.X + (width * healthPercent / 100f), end.Y);
            if (healthEnd.X > start.X && HasAlpha(healthColor))
            {
                drawList.AddRectFilled(start, healthEnd, healthColor, rounding);
            }

            if (hasSecondary && secondaryPercent > 0)
            {
                var secondaryColor = ImGuiHelper.Color(config.ESColor);
                var stripHeight = config.Scale.Y * 0.32f;
                var stripStart = new Vector2(start.X, end.Y - stripHeight);
                var stripEnd = new Vector2(start.X + (width * secondaryPercent / 100f), end.Y);
                if (stripEnd.X > stripStart.X && HasAlpha(secondaryColor))
                {
                    drawList.AddRectFilled(stripStart, stripEnd, secondaryColor, rounding);
                }
            }

            if (this.Settings.ModernBarBorderThickness > 0f && HasAlpha(borderColor))
            {
                drawList.AddRect(start, end, borderColor, rounding, ImDrawFlags.None, this.Settings.ModernBarBorderThickness);
            }
        }

        private void DrawLegacyHealthbar(
            ImDrawListPtr drawList,
            Vector2 start,
            Vector2 end,
            Config config,
            int healthPercent,
            int secondaryPercent,
            bool hasSecondary,
            bool isCullable)
        {
            var backgroundColor = ImGuiHelper.Color(config.BackgroundColor);
            if (HasAlpha(backgroundColor))
            {
                drawList.AddRectFilled(start, end, backgroundColor);
            }

            var (hbPtr, _, _) = this.textures.GetTexture(this.textureToValidate[0]);
            var healthColor = isCullable ? 0xFFFFFFFF : ImGuiHelper.Color(config.HealthbarColor);
            var healthEnd = end - (Vector2.UnitX * config.Scale * (100 - healthPercent) / 100f);
            if (healthEnd.X > start.X && HasAlpha(healthColor))
            {
                drawList.AddImage(
                    hbPtr,
                    start,
                    healthEnd,
                    Vector2.Zero,
                    Vector2.One,
                    healthColor);
            }

            if (hasSecondary)
            {
                var (secondaryPtr, _, _) = this.textures.GetTexture(this.textureToValidate[1]);
                var secondaryColor = ImGuiHelper.Color(config.ESColor);
                var secondaryEnd = end - (Vector2.UnitX * config.Scale * (100 - secondaryPercent) / 100f);
                if (secondaryEnd.X > start.X && HasAlpha(secondaryColor))
                {
                    drawList.AddImage(
                        secondaryPtr,
                        start,
                        secondaryEnd,
                        Vector2.Zero,
                        Vector2.One,
                        secondaryColor);
                }
            }
        }

        private void PruneInterpolationCache(AreaInstance area)
        {
            if (!this.Settings.InterpolatePosition || this.bPositions.Count == 0)
            {
                return;
            }

            PluginRuntimeHelper.PrunePositionCache(
                area,
                this.bPositions,
                this.activeEntityIdsScratch,
                this.cachedEntityIdsScratch);
        }

        private static int ClampPercent(int value)
        {
            return Math.Clamp(value, 0, 100);
        }

        private static bool IsFinite(Vector2 value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y);
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        }

        private static bool IsFinite(StdTuple3D<float> value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        }

        private static bool IsSafeScreenPosition(Vector2 value)
        {
            const float MaxDrawCoordinate = 100000f;
            return IsFinite(value) &&
                MathF.Abs(value.X) <= MaxDrawCoordinate &&
                MathF.Abs(value.Y) <= MaxDrawCoordinate;
        }

        private static float SanitizeFloat(float value, float min, float max, float fallback)
        {
            return float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        }

        private static Vector2 SanitizeVector2(Vector2 value, Vector2 fallback)
        {
            return new(
                SanitizeFloat(value.X, -4000f, 4000f, fallback.X),
                SanitizeFloat(value.Y, -4000f, 4000f, fallback.Y));
        }

        private static Vector4 SanitizeColor(Vector4 color, Vector4 fallback)
        {
            return new(
                SanitizeFloat(color.X, 0f, 1f, fallback.X),
                SanitizeFloat(color.Y, 0f, 1f, fallback.Y),
                SanitizeFloat(color.Z, 0f, 1f, fallback.Z),
                SanitizeFloat(color.W, 0f, 1f, fallback.W));
        }

        private static bool HasAlpha(uint color)
        {
            return (color & 0xFF000000) != 0;
        }

        private static bool IsDeadEntity(Entity entity)
        {
            if (entity.TryGetStatValue(GameStats.is_dead, out var isDead) && isDead > 0)
            {
                return true;
            }

            return false;
        }

        private void ResetDebugState()
        {
            if (!this.IsTelemetryEnabled)
            {
                this.debugRecords.Clear();
                return;
            }

            this.debugSkipReason = "Drawing";
            this.debugEntitiesSeen = 0;
            this.debugCandidates = 0;
            this.debugDrawAttempts = 0;
            this.debugDrawn = 0;
            this.debugFiltered = 0;
            this.debugRecords.Clear();
        }

        private bool ShouldTrackDebugEntity(Entity entity)
        {
            return entity.EntityType == EntityTypes.Player ||
                entity.EntityType == EntityTypes.Monster ||
                this.HasMonsterEvidence(entity);
        }

        private bool ShouldDrawRarity(Rarity rarity) => rarity switch
        {
            Rarity.Normal => this.Settings.ShowNormalMonsters,
            Rarity.Magic => this.Settings.ShowMagicMonsters,
            Rarity.Rare => this.Settings.ShowRareMonsters,
            Rarity.Unique => this.Settings.ShowUniqueMonsters,
            _ => true,
        };

        private bool HasMonsterEvidence(Entity entity)
        {
            return (!string.IsNullOrWhiteSpace(entity.Path) &&
                    entity.Path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal)) ||
                entity.HasComponent("Monster") ||
                entity.HasComponent(nameof(Life)) ||
                entity.HasComponent(nameof(Targetable)) ||
                entity.HasComponent(nameof(ObjectMagicProperties)) ||
                entity.HasComponent(nameof(Buffs)) ||
                entity.HasComponent(nameof(Stats)) ||
                entity.HasComponent("BaseEvents");
        }

        private void RecordDebug(
            Entity entity,
            string decision,
            string reason,
            bool force,
            int rarity = -1,
            Vector2? screenPosition = null,
            Life? life = null,
            string healthSource = "")
        {
            if (!this.IsDebugEnabled)
            {
                return;
            }

            if (!force && !this.ShouldTrackDebugEntity(entity))
            {
                return;
            }

            if (decision == "filtered")
            {
                this.debugFiltered++;
            }

            var hasRender = entity.TryGetComponent<Render>(out _, false);
            var hasLife = life != null || entity.TryGetComponent<Life>(out life, false);
            var hasTargetable = entity.TryGetComponent<Targetable>(out var targetable, false);
            var hasMagicProps = entity.TryGetComponent<ObjectMagicProperties>(out var magicProps, false);
            var isDead = entity.TryGetStatValue(GameStats.is_dead, out var deadValue) && deadValue > 0;
            var key = $"{entity.Id}:{decision}:{reason}";
            this.debugRecords[key] = new HealthBarDebugRecord
            {
                Id = entity.Id,
                Decision = decision,
                Reason = reason,
                Type = entity.EntityType.ToString(),
                Subtype = entity.EntitySubtype.ToString(),
                State = entity.EntityState.ToString(),
                Rarity = rarity >= 0
                    ? ((Rarity)Math.Clamp(rarity, 0, 3)).ToString()
                    : hasMagicProps && magicProps != null ? magicProps.Rarity.ToString() : string.Empty,
                Health = hasLife ? $"{life!.Health.Current}/{life.Health.Total}" : "n/a",
                HealthSource = healthSource,
                HasRender = hasRender,
                HasLife = hasLife,
                HasTargetable = hasTargetable,
                IsTargetable = hasTargetable && targetable!.IsTargetable,
                IsDead = isDead,
                DeadValue = isDead ? deadValue : 0,
                Screen = screenPosition.HasValue
                    ? $"{screenPosition.Value.X:0.0}, {screenPosition.Value.Y:0.0}"
                    : string.Empty,
                Components = string.Join(", ", entity.GetComponentNames()),
                Path = entity.Path,
            };
        }

        private bool IsMonsterLikeEntity(Entity entity)
        {
            if (entity.EntityType == EntityTypes.Monster)
            {
                return true;
            }

            if (!this.Settings.UseRadarMonsterDetection)
            {
                return false;
            }

            if (entity.EntityType is EntityTypes.Player or EntityTypes.NPC or EntityTypes.Chest or EntityTypes.OtherImportantObjects)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(entity.Path) ||
                !entity.Path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal))
            {
                return false;
            }

            if (Core.GHSettings.MonstersPathsToIgnore.Any(entity.Path.StartsWith))
            {
                return false;
            }

            return this.HasMonsterEvidence(entity) ||
                entity.TryGetComponent<Life>(out _, false) ||
                entity.TryGetComponent<ObjectMagicProperties>(out _, false) ||
                entity.TryGetComponent<Buffs>(out _, false);
        }

        private void DrawObjectName(ImDrawListPtr drawList, Entity entity, Vector2 start, Vector2 end)
        {
            var objectName = this.Settings.ShowDebugObjectFullPath
                ? entity.Path
                : GetShortObjectName(entity.Path);
            if (string.IsNullOrWhiteSpace(objectName))
            {
                objectName = entity.Id.ToString();
            }

            var textSize = ImGui.CalcTextSize(objectName);
            var textPos = new Vector2(
                start.X + ((end.X - start.X - textSize.X) * 0.5f),
                end.Y + 2f);
            var shadowColor = ImGuiHelper.Color(0, 0, 0, 220);
            var textColor = ImGuiHelper.Color(this.Settings.DebugObjectNameTextColor);
            if (HasAlpha(shadowColor))
            {
                drawList.AddText(textPos + Vector2.One, shadowColor, objectName);
            }

            if (HasAlpha(textColor))
            {
                drawList.AddText(textPos, textColor, objectName);
            }
        }

        private static string GetShortObjectName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var slash = path.LastIndexOf('/');
            var name = slash >= 0 && slash + 1 < path.Length
                ? path[(slash + 1)..]
                : path;
            var levelSuffix = name.IndexOf('@', StringComparison.Ordinal);
            return levelSuffix > 0 ? name[..levelSuffix] : name;
        }

        private void DrawHealthBarDebugOverlay()
        {
            if (!this.Settings.ShowDebugOverlay)
            {
                return;
            }

            ImGui.SetNextWindowSize(new Vector2(760f, 420f), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("HealthBars Diagnostics", ref this.Settings.ShowDebugOverlay))
            {
                ImGui.End();
                return;
            }

            this.DrawHealthBarDebugSummary();
            this.DrawHealthBarDebugTable();
            ImGui.End();
        }

        private void DrawHealthBarStatusOverlay()
        {
            if (!this.Settings.ShowStatusOverlay)
            {
                return;
            }

            ImGui.SetNextWindowPos(this.Settings.StatusOverlayPosition, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(300f, 118f), ImGuiCond.FirstUseEver);
            var show = this.Settings.ShowStatusOverlay;
            if (!ImGui.Begin("HealthBars Status", ref show, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar))
            {
                this.Settings.ShowStatusOverlay = show;
                ImGui.End();
                return;
            }

            this.Settings.ShowStatusOverlay = show;
            this.Settings.StatusOverlayPosition = ImGui.GetWindowPos();
            ImGui.TextUnformatted("HealthBars");
            ImGui.SameLine();
            ImGui.TextDisabled(this.debugSkipReason);
            ImGui.Separator();
            ImGui.TextUnformatted($"Seen {this.debugEntitiesSeen}  candidates {this.debugCandidates}");
            ImGui.TextUnformatted($"Attempts {this.debugDrawAttempts}  drawn {this.debugDrawn}  filtered {this.debugFiltered}");
            ImGui.TextUnformatted($"Visible: N={this.Settings.ShowNormalMonsters} M={this.Settings.ShowMagicMonsters} R={this.Settings.ShowRareMonsters} U={this.Settings.ShowUniqueMonsters}");
            ImGui.End();
        }

        private void DrawHealthBarDebugSummary()
        {
            ImGui.TextUnformatted($"Reason: {this.debugSkipReason}");
            ImGui.TextUnformatted($"State: {Core.States.GameCurrentState}, foreground: {Core.Process.Foreground}");
            ImGui.TextUnformatted($"Entities: seen={this.debugEntitiesSeen}, candidates={this.debugCandidates}, attempts={this.debugDrawAttempts}, drawn={this.debugDrawn}, filtered={this.debugFiltered}");
            ImGui.TextUnformatted($"Entity draw exceptions: {this.entityDrawExceptionCount}");
            if (!string.IsNullOrWhiteSpace(this.lastEntityDrawException))
            {
                ImGui.TextWrapped(this.lastEntityDrawException);
            }

            ImGui.TextUnformatted($"Records: {this.debugRecords.Count}");
            ImGui.Separator();
        }

        private void DrawHealthBarDebugTable()
        {
            if (!this.Settings.ShowDebugTable)
            {
                return;
            }

            var records = this.debugRecords.Values
                .Where(record => string.IsNullOrWhiteSpace(this.debugFilter) ||
                    record.Path.Contains(this.debugFilter, StringComparison.OrdinalIgnoreCase) ||
                    record.Reason.Contains(this.debugFilter, StringComparison.OrdinalIgnoreCase) ||
                    record.Type.Contains(this.debugFilter, StringComparison.OrdinalIgnoreCase) ||
                    record.Decision.Contains(this.debugFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(record => record.Decision == "drawn" ? 0 : 1)
                .ThenBy(record => record.Type)
                .ThenBy(record => record.Id)
                .Take(250)
                .ToArray();

            if (ImGui.BeginTable("HealthBarsDiagnosticsTable", 13, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0f, 300f)))
            {
                ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Decision", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.WidthFixed, 190f);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableSetupColumn("Rarity", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("HP", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableSetupColumn("HP source", ImGuiTableColumnFlags.WidthFixed, 150f);
                ImGui.TableSetupColumn("Render", ImGuiTableColumnFlags.WidthFixed, 55f);
                ImGui.TableSetupColumn("Life", ImGuiTableColumnFlags.WidthFixed, 45f);
                ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("Screen", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableSetupColumn("Path");
                ImGui.TableHeadersRow();

                foreach (var record in records)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.Id.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.Decision);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.Reason);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.Type);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.State);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.Rarity);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.Health);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.HealthSource);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.HasRender ? "yes" : "no");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.HasLife ? "yes" : "no");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.HasTargetable ? (record.IsTargetable ? "yes" : "no") : "n/a");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.Screen);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(record.Path);
                    if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(record.Components))
                    {
                        ImGui.SetTooltip(record.Components);
                    }
                }

                ImGui.EndTable();
            }
        }

        private string healthToHumanReadable(long value)
        {
            if (value >= 100000)
            {
                return $"{(value / 1000000d):0.00}M";

            }
            else if (value >= 100)
            {
                return $"{(value / 1000d):0.00}K";
            }
            else
            {
                return $"{value}";
            }
        }

        private void DrawConfirmationPopup()
        {
            ImGui.SetNextWindowPos(new Vector2(Core.Overlay.Size.Width / 3f, Core.Overlay.Size.Height / 3f));
            if (ImGui.BeginPopup("POIConfigHealthbarDeleteConfirmation"))
            {
                ImGui.Text($"Do you want to delete group {this.poiMonsterConfigToDelete} POI Monster healthbar config?");
                ImGui.Separator();
                if (ImGui.Button("Yes",
                    new Vector2(ImGui.GetContentRegionAvail().X / 2f, ImGui.GetTextLineHeight() * 2)))
                {
                    _ = this.Settings.POIMonster.Remove(poiMonsterConfigToDelete);
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
                if (ImGui.Button("No", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 2)))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }

        private sealed class HealthBarDebugRecord
        {
            public uint Id { get; init; }

            public string Decision { get; init; } = string.Empty;

            public string Reason { get; init; } = string.Empty;

            public string Type { get; init; } = string.Empty;

            public string Subtype { get; init; } = string.Empty;

            public string State { get; init; } = string.Empty;

            public string Rarity { get; init; } = string.Empty;

            public string Health { get; init; } = string.Empty;

            public string HealthSource { get; init; } = string.Empty;

            public bool HasRender { get; init; }

            public bool HasLife { get; init; }

            public bool HasTargetable { get; init; }

            public bool IsTargetable { get; init; }

            public bool IsDead { get; init; }

            public int DeadValue { get; init; }

            public string Screen { get; init; } = string.Empty;

            public string Components { get; init; } = string.Empty;

            public string Path { get; init; } = string.Empty;
        }

        private IEnumerator<Wait> OnAreaChange()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.bPositions.Clear();
            }
        }
    }
}
