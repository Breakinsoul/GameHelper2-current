// <copyright file="Radar.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Radar
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Threading.Tasks;
    using Coroutine;
    using GameHelper;
    using GameHelper.CoroutineEvents;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;
    using SixLabors.ImageSharp;
    using SixLabors.ImageSharp.PixelFormats;
    using SixLabors.ImageSharp.Processing.Processors.Transforms;
    using SixLabors.ImageSharp.Processing;

    /// <summary>
    /// <see cref="Radar"/> plugin.
    /// </summary>
    public sealed class Radar : PCore<RadarSettings>
    {
        private const string TempleTgtPrefix = "Metadata/Terrain/Leagues/Incursion/Tiles/Features/Waygates/WaygateDevice";
        private const string TormentedSpiritPathPrefix = "Metadata/Monsters/TormentedSpirits/";

        private readonly string delveChestStarting = "Metadata/Chests/DelveChests/";
        private readonly Dictionary<uint, string> delveChestCache = new();

        /// <summary>
        /// If we don't do this, user will be asked to
        /// setup the culling window everytime they open the game.
        /// </summary>
        private bool skipOneSettingChange = false;
        private bool isAddNewPOIHeaderOpened = false;
        private ActiveCoroutine? onMove;
        private ActiveCoroutine? onForegroundChange;
        private ActiveCoroutine? onGameClose;
        private ActiveCoroutine? onAreaChange;

        private string currentAreaName = string.Empty;
        private string tmpTileName = string.Empty;
        private string tmpDisplayName = string.Empty;
        private int tmpTgtSelectionCounter = 0;
        private string tmpTileFilter = string.Empty;
        private bool addTileForAllAreas = false;

        private double miniMapDiagonalLength = 0x00;

        private double largeMapDiagonalLength = 0x00;

        private IntPtr walkableMapTexture = IntPtr.Zero;
        private Vector2 walkableMapDimension = Vector2.Zero;
        private readonly Dictionary<string, Vector2> textHalfSizeCache = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Vector2> poiIndexHalfSizeCache = new();
        private readonly Dictionary<uint, Vector2> largeMapInterpolatedPositions = new();
        private readonly Dictionary<uint, Vector2> miniMapInterpolatedPositions = new();
        private readonly HashSet<uint> activeEntityIdsScratch = new();
        private readonly List<uint> cachedEntityIdsScratch = new();
        private string debugSkipReason = "Not evaluated";
        private int debugEntitiesSeen;
        private int debugEntitiesValid;
        private int debugEntitiesUseless;
        private int debugEntitiesWithRender;
        private int debugMonsterEntities;
        private int debugMonsterEntitiesWithoutRender;
        private int debugMonsterUseless;
        private int debugMonsterTargetableTrue;
        private int debugMonsterLifeAlive;
        private int debugMonsterDeadStat;
        private int debugLifeEntitiesWithoutRender;
        private int debugEntitiesClipped;
        private int debugEntitiesInBounds;
        private int debugMonsterLikeRenderables;
        private int debugIconsProjected;
        private int debugPrimitivesProjected;
        private Vector2 debugLastProjectedPosition;
        private Vector2 debugLastDrawnPosition;
        private string debugTopComponents = string.Empty;
        private string debugTypeCounts = string.Empty;
        private readonly Dictionary<string, DrawnEntityRecord> drawnEntityRecords = new(StringComparer.Ordinal);
        private string drawnEntityFilter = string.Empty;
        private string newDisabledPathFilter = string.Empty;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        private string ImportantTgtPathName => Path.Join(this.DllDirectory, "important_tgt_files.txt");

        private string BossArenaTgtPathName => Path.Join(this.DllDirectory, "boss_arena_tgt_files.txt");

        private string StairsTgtPathName => Path.Join(this.DllDirectory, "stairs_tgt_files.txt");

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            if (!ImGui.BeginTabBar("RadarSettingsTabs"))
            {
                return;
            }

            if (ImGui.BeginTabItem("General"))
            {
                this.DrawGeneralSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Markers"))
            {
                this.DrawMarkerSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Icons"))
            {
                this.DrawIconSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("On-death"))
            {
                this.DrawOnDeathEffectSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Debug / Advanced"))
            {
                this.DrawDebugSettings();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            var largeMap = Core.States.InGameStateObject.GameUi.LargeMap;
            var miniMap = Core.States.InGameStateObject.GameUi.MiniMap;
            var areaDetails = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails;
            this.debugSkipReason = "Drawing";
            this.debugEntitiesSeen = 0;
            this.debugEntitiesValid = 0;
            this.debugEntitiesUseless = 0;
            this.debugEntitiesWithRender = 0;
            this.debugMonsterEntities = 0;
            this.debugMonsterEntitiesWithoutRender = 0;
            this.debugMonsterUseless = 0;
            this.debugMonsterTargetableTrue = 0;
            this.debugMonsterLifeAlive = 0;
            this.debugMonsterDeadStat = 0;
            this.debugLifeEntitiesWithoutRender = 0;
            this.debugEntitiesClipped = 0;
            this.debugEntitiesInBounds = 0;
            this.debugMonsterLikeRenderables = 0;
            this.debugIconsProjected = 0;
            this.debugPrimitivesProjected = 0;
            this.debugTopComponents = string.Empty;
            this.debugTypeCounts = string.Empty;
            this.debugLastProjectedPosition = Vector2.Zero;
            this.debugLastDrawnPosition = Vector2.Zero;
            this.drawnEntityRecords.Clear();
            if (this.Settings.ModifyCullWindow)
            {
                ImGui.SetNextWindowPos(largeMap.Center, ImGuiCond.Appearing);
                ImGui.SetNextWindowSize(new Vector2(400f), ImGuiCond.Appearing);
                ImGui.Begin("Large Map Culling Window");
                ImGui.TextWrapped("This is a culling window for the large map icons. " +
                                  "Any large map icons outside of this window will be hidden automatically. " +
                                  "Feel free to change the position/size of this window. " +
                                  "Once you are happy with the dimensions, double click this window. " +
                                  "You can bring this window back from the settings menu.");
                this.Settings.CullWindowPos = ImGui.GetWindowPos();
                this.Settings.CullWindowSize = ImGui.GetWindowSize();
                if (ImGui.IsWindowHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    this.Settings.ModifyCullWindow = false;
                }

                ImGui.End();
            }
            
            if (this.Settings.DrawWhenNotPaused && Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                this.debugSkipReason = "DrawWhenNotPaused: state is not InGameState";
                this.DrawRadarDebugOverlay(largeMap.IsVisible, miniMap.IsVisible);
                return;
            }

            if (Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState))
            {
                this.debugSkipReason = "Game state is not InGameState/EscapeState";
                this.DrawRadarDebugOverlay(largeMap.IsVisible, miniMap.IsVisible);
                return;
            }

            var allowBackgroundDebugDraw = this.Settings.ShowDebugOverlay ||
                this.Settings.DrawDebugPrimitives ||
                this.Settings.DrawTestIcons;
            if (this.Settings.DrawWhenForeground && !Core.Process.Foreground && !allowBackgroundDebugDraw)
            {
                this.debugSkipReason = "Game is in background";
                this.DrawRadarDebugOverlay(largeMap.IsVisible, miniMap.IsVisible);
                return;
            }

            if (this.Settings.DrawWhenNotInHideoutOrTown &&
                (areaDetails.IsHideout || areaDetails.IsTown))
            {
                this.debugSkipReason = "Area is hideout/town";
                this.DrawRadarDebugOverlay(largeMap.IsVisible, miniMap.IsVisible);
                return;
            }

            if (Core.States.InGameStateObject.GameUi.IsPassiveSkillTreeOpen)
            {
                this.debugSkipReason = "Passive tree is open";
                this.DrawRadarDebugOverlay(largeMap.IsVisible, miniMap.IsVisible);
                return;
            }

            if (this.Settings.MakeCullWindowFullScreen)
            {
                this.Settings.CullWindowPos = Vector2.Zero;
                this.Settings.CullWindowSize.X = Core.Process.WindowArea.Size.Width;
                this.Settings.CullWindowSize.Y = Core.Process.WindowArea.Size.Height;
            }

            if (largeMap.IsVisible && !Core.States.InGameStateObject.GameUi.WorldMapPanel.IsVisible)
            {
                if (this.largeMapDiagonalLength <= 0)
                {
                    this.UpdateLargeMapDetails();
                }

                var largeMapRealCenter = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
                var largeMapModifiedZoom = this.Settings.LargeMapScaleMultiplier * largeMap.Zoom;
                Helper.DiagonalLength = this.largeMapDiagonalLength;
                Helper.Scale = largeMapModifiedZoom;
                ImGui.SetNextWindowPos(this.Settings.CullWindowPos);
                ImGui.SetNextWindowSize(this.Settings.CullWindowSize);
                ImGui.SetNextWindowBgAlpha(0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin("Large Map Culling Window", ImGuiHelper.TransparentWindowFlags);
                ImGui.PopStyleVar();
                this.DrawLargeMap(largeMapRealCenter);
                this.DrawTgtFiles(largeMapRealCenter);
                this.DrawTgtIcons(largeMapRealCenter, largeMapModifiedZoom * 5f);
                this.DrawMapIcons(largeMapRealCenter, largeMapModifiedZoom * 5f, isLargeMap: true);
                this.DrawRadarTestIcons(largeMapRealCenter, largeMapModifiedZoom * 5f);
                ImGui.End();
            }

            if (miniMap.IsVisible)
            {
                if (this.miniMapDiagonalLength <= 0)
                {
                    this.UpdateMiniMapDetails();
                }

                Helper.DiagonalLength = this.miniMapDiagonalLength;
                Helper.Scale = miniMap.Zoom;
                var miniMapCenter = miniMap.Position +
                    (miniMap.Size / 2) +
                    miniMap.DefaultShift +
                    miniMap.Shift;
                ImGui.SetNextWindowPos(miniMap.Position);
                ImGui.SetNextWindowSize(miniMap.Size);
                ImGui.SetNextWindowBgAlpha(0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin("###minimapRadar", ImGuiHelper.TransparentWindowFlags);
                ImGui.PopStyleVar();
                this.DrawTgtIcons(miniMapCenter, miniMap.Zoom);
                this.DrawMapIcons(miniMapCenter, miniMap.Zoom, isLargeMap: false);
                this.DrawRadarTestIcons(miniMapCenter, miniMap.Zoom);
                ImGui.End();
            }

            this.DrawRadarDebugOverlay(largeMap.IsVisible, miniMap.IsVisible);
            this.DrawDrawnEntitiesTable();
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            this.onMove?.Cancel();
            this.onForegroundChange?.Cancel();
            this.onGameClose?.Cancel();
            this.onAreaChange?.Cancel();
            this.onMove = null;
            this.onForegroundChange = null;
            this.onGameClose = null;
            this.onAreaChange = null;
            this.CleanUpRadarPluginCaches();
        }

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (!isGameOpened)
            {
                this.skipOneSettingChange = true;
            }

            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<RadarSettings>(content) ?? new RadarSettings();
            }

            if (File.Exists(this.ImportantTgtPathName))
            {
                var tgtfiles = File.ReadAllText(this.ImportantTgtPathName);
                this.Settings.ImportantTgts = JsonConvert.DeserializeObject
                    <Dictionary<string, Dictionary<string, string>>>(tgtfiles)
                    ?? new Dictionary<string, Dictionary<string, string>>();
            }

            if (File.Exists(this.BossArenaTgtPathName))
            {
                var bossfiles = File.ReadAllText(this.BossArenaTgtPathName);
                this.Settings.BossArenaTgts = JsonConvert.DeserializeObject
                    <Dictionary<string, string>>(bossfiles) ?? new Dictionary<string, string>();
            }

            if (File.Exists(this.StairsTgtPathName))
            {
                var stairsfiles = File.ReadAllText(this.StairsTgtPathName);
                this.Settings.StairsTgts = JsonConvert.DeserializeObject
                    <Dictionary<string, string>>(stairsfiles) ?? new Dictionary<string, string>();
            }

            this.Settings.AddDefaultIcons(this.DllDirectory);
            this.Settings.DisabledEntityPathFilters ??= new List<string>();
            this.MigrateOnDeathFiltersFromDisabledList();

            this.onMove = CoroutineHandler.Start(this.OnMove());
            this.onForegroundChange = CoroutineHandler.Start(this.OnForegroundChange());
            this.onGameClose = CoroutineHandler.Start(this.OnClose());
            this.onAreaChange = CoroutineHandler.Start(this.ClearCachesAndUpdateAreaInfo());
            this.GenerateMapTexture();
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            JsonHelper.SafeToFile(this.Settings, new FileInfo(this.SettingPathname));

            if (this.Settings.ImportantTgts.Count > 0)
            {
                JsonHelper.SafeToFile(this.Settings.ImportantTgts, new FileInfo(this.ImportantTgtPathName));
            }

            if (this.Settings.BossArenaTgts.Count > 0)
            {
                JsonHelper.SafeToFile(this.Settings.BossArenaTgts, new FileInfo(this.BossArenaTgtPathName));
            }

            if (this.Settings.StairsTgts.Count > 0)
            {
                JsonHelper.SafeToFile(this.Settings.StairsTgts, new FileInfo(this.StairsTgtPathName));
            }
        }

        private void DrawLargeMap(Vector2 mapCenter)
        {
            if (!this.Settings.DrawWalkableMap)
            {
                return;
            }

            if (this.walkableMapTexture == IntPtr.Zero)
            {
                return;
            }

            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.TryGetComponent<Render>(out var pRender))
            {
                return;
            }

            var rectf = new RectangleF(
                -pRender.GridPosition.X,
                -pRender.GridPosition.Y,
                this.walkableMapDimension.X,
                this.walkableMapDimension.Y);

            var p1 = Helper.DeltaInWorldToMapDelta(
                new Vector2(rectf.Left, rectf.Top), -pRender.TerrainHeight);
            var p2 = Helper.DeltaInWorldToMapDelta(
                new Vector2(rectf.Right, rectf.Top), -pRender.TerrainHeight);
            var p3 = Helper.DeltaInWorldToMapDelta(
                new Vector2(rectf.Right, rectf.Bottom), -pRender.TerrainHeight);
            var p4 = Helper.DeltaInWorldToMapDelta(
                new Vector2(rectf.Left, rectf.Bottom), -pRender.TerrainHeight);
            p1 += mapCenter;
            p2 += mapCenter;
            p3 += mapCenter;
            p4 += mapCenter;

            if (this.Settings.DrawMapInCull)
            {
                ImGui.GetWindowDrawList().AddImageQuad(this.walkableMapTexture, p1, p2, p3, p4);
            }
            else
            {
                ImGui.GetBackgroundDrawList().AddImageQuad(this.walkableMapTexture, p1, p2, p3, p4);
            }
        }

        private void DrawTgtFiles(Vector2 mapCenter)
        {
            var col = ImGuiHelper.Color(
                (uint)(this.Settings.POIColor.X * 255),
                (uint)(this.Settings.POIColor.Y * 255),
                (uint)(this.Settings.POIColor.Z * 255),
                (uint)(this.Settings.POIColor.W * 255));

            ImDrawListPtr fgDraw;
            if (this.Settings.DrawPOIInCull)
            {
                fgDraw = ImGui.GetWindowDrawList();
            }
            else
            {
                fgDraw = ImGui.GetBackgroundDrawList();
            }

            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!currentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
            {
                return;
            }

            var pPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            var clipMin = ImGui.GetWindowPos();
            var clipMax = clipMin + ImGui.GetWindowSize();

            void drawString(string text, Vector2 location, Vector2 stringImGuiSize, bool drawBackground)
            {
                float height = 0;
                if (location.X < currentAreaInstance.GridHeightData[0].Length &&
                    location.Y < currentAreaInstance.GridHeightData.Length)
                {
                    height = currentAreaInstance.GridHeightData[(int)location.Y][(int)location.X];
                }

                var fpos = Helper.DeltaInWorldToMapDelta(
                    location - pPos, -playerRender.TerrainHeight + height);
                var textMin = mapCenter + fpos - stringImGuiSize;
                var textMax = mapCenter + fpos + stringImGuiSize;
                if (textMax.X < clipMin.X || textMin.X > clipMax.X || textMax.Y < clipMin.Y || textMin.Y > clipMax.Y)
                {
                    return;
                }

                if (drawBackground)
                {
                    fgDraw.AddRectFilled(
                        textMin,
                        textMax,
                        ImGuiHelper.Color(0, 0, 0, 200));
                }

                fgDraw.AddText(
                    ImGui.GetFont(),
                    ImGui.GetFontSize(),
                    textMin,
                    col,
                    text);
            }

            if (this.isAddNewPOIHeaderOpened)
            {
                var counter = 0;
                foreach (var tgtKV in currentAreaInstance.TgtTilesLocations)
                {
                    if (!(this.Settings.POIFrequencyFilter > 0 &&
                        tgtKV.Value.Count > this.Settings.POIFrequencyFilter))
                    {
                        if (!this.poiIndexHalfSizeCache.TryGetValue(counter, out var tgtKImGuiSize))
                        {
                            tgtKImGuiSize = ImGui.CalcTextSize(counter.ToString()) / 2;
                            this.poiIndexHalfSizeCache[counter] = tgtKImGuiSize;
                        }

                        for (var i = 0; i < tgtKV.Value.Count; i++)
                        {
                            drawString(counter.ToString(), tgtKV.Value[i], tgtKImGuiSize, false);
                        }
                    }

                    counter++;
                }
            }
            else if (this.Settings.ShowImportantPOI)
            {
                if (this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var importantTgtsOfCurrentArea))
                {
                    foreach (var tile in importantTgtsOfCurrentArea)
                    {
                        if (currentAreaInstance.TgtTilesLocations.TryGetValue(tile.Key, out var locations))
                        {
                            var strSize = this.GetTextHalfSize(tile.Value);
                            for (var i = 0; i < locations.Count; i++)
                            {
                                drawString(tile.Value, locations[i], strSize, this.Settings.EnablePOIBackground);
                            }
                        }
                    }
                }

                if (this.Settings.ImportantTgts.TryGetValue("common", out var importantTgtsOfAllAreas))
                {
                    foreach (var tile in importantTgtsOfAllAreas)
                    {
                        if (currentAreaInstance.TgtTilesLocations.TryGetValue(tile.Key, out var locations))
                        {
                            var strSize = this.GetTextHalfSize(tile.Value);
                            for (var i = 0; i < locations.Count; i++)
                            {
                                drawString(tile.Value, locations[i], strSize, this.Settings.EnablePOIBackground);
                            }
                        }
                    }
                }
            }
        }

        private void DrawTgtIcons(Vector2 mapCenter, float iconSizeMultiplier)
        {
            var fgDraw = ImGui.GetWindowDrawList();
            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!currentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
            {
                return;
            }

            var pPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);

            foreach (var tgtKV in currentAreaInstance.TgtTilesLocations)
            {
                if (tgtKV.Key.StartsWith(TempleTgtPrefix) && tgtKV.Key.EndsWith(":1-y:1"))
                {
                    if (!this.Settings.TempleIcons.TryGetValue("Vaal Ruins", out var templeIcon))
                    {
                        continue;
                    }

                    this.DrawIconAtTgtLocations(fgDraw, mapCenter, pPos, playerRender, tgtKV.Value, templeIcon, iconSizeMultiplier, shiftUp: true);
                }
                else if (this.Settings.BossArenaTgts.ContainsKey(tgtKV.Key))
                {
                    if (!this.Settings.BossIcons.TryGetValue("Boss Arena", out var bossIcon))
                    {
                        continue;
                    }

                    this.DrawIconAtTgtLocations(fgDraw, mapCenter, pPos, playerRender, tgtKV.Value, bossIcon, iconSizeMultiplier);
                }
                else if (this.Settings.StairsTgts.ContainsKey(tgtKV.Key))
                {
                    if (!this.Settings.BaseIcons.TryGetValue("Stairs", out var stairsIcon))
                    {
                        continue;
                    }

                    this.DrawIconAtTgtLocations(fgDraw, mapCenter, pPos, playerRender, tgtKV.Value, stairsIcon, iconSizeMultiplier);
                }
            }
        }

        private void DrawIconAtTgtLocations(
            ImDrawListPtr fgDraw,
            Vector2 mapCenter,
            Vector2 pPos,
            Render playerRender,
            List<Vector2> locations,
            IconPicker icon,
            float iconSizeMultiplier,
            bool shiftUp = false)
        {
            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            for (var i = 0; i < locations.Count; i++)
            {
                var location = locations[i];
                float height = 0;
                if (location.X < currentAreaInstance.GridHeightData[0].Length &&
                    location.Y < currentAreaInstance.GridHeightData.Length)
                {
                    height = currentAreaInstance.GridHeightData[(int)location.Y][(int)location.X];
                }

                var fpos = Helper.DeltaInWorldToMapDelta(
                    location - pPos, -playerRender.TerrainHeight + height);
                var iconSizeMultiplierVector = new Vector2(iconSizeMultiplier);
                iconSizeMultiplierVector *= icon.IconScale;
                var offset = shiftUp ? new Vector2(0, iconSizeMultiplierVector.Y) : Vector2.Zero;
                fgDraw.AddImage(
                    icon.TexturePtr,
                    mapCenter + fpos - iconSizeMultiplierVector - offset,
                    mapCenter + fpos + iconSizeMultiplierVector - offset,
                    icon.UV0,
                    icon.UV1);
            }
        }

        private void DrawMapIcons(Vector2 mapCenter, float iconSizeMultiplier, bool isLargeMap)
        {
            var fgDraw = ImGui.GetWindowDrawList();
            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!currentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
            {
                return;
            }

            var interpolatedPositions = isLargeMap
                ? this.largeMapInterpolatedPositions
                : this.miniMapInterpolatedPositions;
            this.PruneInterpolatedPositionCache(currentAreaInstance, interpolatedPositions);

            var clipMin = ImGui.GetWindowPos();
            var clipMax = clipMin + ImGui.GetWindowSize();
            var clipPadding = iconSizeMultiplier * 4f;
            var pPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);

            var baseIcons = this.Settings.BaseIcons;
            var expeditionIcons = this.Settings.ExpeditionIcons;
            var breachIcons = this.Settings.BreachIcons;
            var deliriumIcons = this.Settings.DeliriumIcons;
            var poiMonsterIcons = this.Settings.POIMonsters;
            var otherImportantObjects = this.Settings.OtherImportantObjects;

            var npcIcon = baseIcons["NPC"];
            var specialNpcIcon = baseIcons["Special NPC"];
            var leaderIcon = baseIcons["Leader"];
            var playerIcon = baseIcons["Player"];
            var selfIcon = baseIcons["Self"];
            var allOtherChestIcon = baseIcons["All Other Chest"];
            var rareChestIcon = baseIcons["Rare Chests"];
            var magicChestIcon = baseIcons["Magic Chests"];
            var expeditionChestIcon = expeditionIcons["Generic Expedition Chests"];
            var breachChestIcon = breachIcons["Breach Chest"];
            var strongboxIcon = baseIcons["Strongbox"];
            var shrineIcon = baseIcons["Shrine"];
            var pinnacleBossHiddenIcon = baseIcons["Pinnacle Boss Not Attackable"];
            var friendlyIcon = baseIcons["Friendly"];
            var deliriumBombIcon = deliriumIcons["Delirium Bomb"];
            var deliriumSpawnerIcon = deliriumIcons["Delirium Spawner"];
            var normalMonsterIcon = baseIcons["Normal Monster"];
            var magicMonsterIcon = baseIcons["Magic Monster"];
            var rareMonsterIcon = baseIcons["Rare Monster"];
            var uniqueMonsterIcon = baseIcons["Unique Monster"];
            var tormentedSpiritIcon = this.Settings.TormentedSpiritIcons["Tormented Spirit"];
            Dictionary<string, int>? componentCounts = this.Settings.ShowDebugOverlay
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : null;
            Dictionary<EntityTypes, int>? typeCounts = this.Settings.ShowDebugOverlay
                ? new Dictionary<EntityTypes, int>()
                : null;
            var iconDrawCommands = new List<RadarIconDrawCommand>();
            var iconDrawSequence = 0;

            foreach (var entity in currentAreaInstance.AwakeEntities)
            {
                this.debugEntitiesSeen++;
                var entityValue = entity.Value;
                if (typeCounts != null)
                {
                    typeCounts[entityValue.EntityType] = typeCounts.TryGetValue(entityValue.EntityType, out var typeCount)
                        ? typeCount + 1
                        : 1;
                }

                if (entityValue.IsValid)
                {
                    this.debugEntitiesValid++;
                }

                if (componentCounts != null)
                {
                    foreach (var componentName in entityValue.GetComponentNames())
                    {
                        componentCounts[componentName] = componentCounts.TryGetValue(componentName, out var count)
                            ? count + 1
                            : 1;
                    }
                }

                if (this.Settings.HideOutsideNetworkBubble && !entityValue.IsValid)
                {
                    continue;
                }

                var isOnDeathEffect = this.IsOnDeathEffectPath(entityValue.Path);
                if (isOnDeathEffect && !this.Settings.ShowOnDeathEffectMarkers)
                {
                    continue;
                }

                if (!isOnDeathEffect && this.IsEntityPathDisabled(entityValue.Path))
                {
                    continue;
                }

                var isMonsterEntity = entityValue.EntityType == EntityTypes.Monster;
                if (isMonsterEntity)
                {
                    if (entityValue.EntityState == EntityStates.Useless)
                    {
                        this.debugMonsterUseless++;
                    }

                    if (entityValue.TryGetComponent<Targetable>(out var targetableComp, false) &&
                        targetableComp.IsTargetable)
                    {
                        this.debugMonsterTargetableTrue++;
                    }

                    if (entityValue.TryGetComponent<Life>(out var lifeComp, false) &&
                        lifeComp.IsAlive)
                    {
                        this.debugMonsterLifeAlive++;
                    }

                    if (entityValue.TryGetStatValue(GameStats.is_dead, out var isDead) && isDead > 0)
                    {
                        this.debugMonsterDeadStat++;
                    }
                }

                var isConfirmedDeadMonster = isMonsterEntity &&
                    entityValue.TryGetStatValue(GameStats.is_dead, out var radarIsDead) &&
                    radarIsDead > 0;

                if (entityValue.EntityState == EntityStates.Useless &&
                    (!isMonsterEntity || isConfirmedDeadMonster))
                {
                    this.debugEntitiesUseless++;
                    continue;
                }

                if (!entityValue.TryGetComponent<Render>(out var entityRender))
                {
                    if (entityValue.EntityType == EntityTypes.Monster)
                    {
                        this.debugMonsterEntities++;
                        this.debugMonsterEntitiesWithoutRender++;
                    }

                    if (entityValue.TryGetComponent<Life>(out _))
                    {
                        this.debugLifeEntitiesWithoutRender++;
                    }

                    continue;
                }

                this.debugEntitiesWithRender++;
                if (entityValue.EntityType == EntityTypes.Monster)
                {
                    this.debugMonsterEntities++;
                }

                var ePos = new Vector2(entityRender.GridPosition.X, entityRender.GridPosition.Y);
                var fpos = Helper.DeltaInWorldToMapDelta(ePos - pPos, entityRender.TerrainHeight - playerRender.TerrainHeight);
                var screenPos = mapCenter + fpos;
                screenPos = this.InterpolateMapPosition(entityValue.Id, screenPos, interpolatedPositions);
                this.debugLastProjectedPosition = screenPos;
                if (screenPos.X < clipMin.X - clipPadding || screenPos.X > clipMax.X + clipPadding ||
                    screenPos.Y < clipMin.Y - clipPadding || screenPos.Y > clipMax.Y + clipPadding)
                {
                    this.debugEntitiesClipped++;
                    continue;
                }

                this.debugEntitiesInBounds++;
                this.debugLastDrawnPosition = screenPos;

                if (isOnDeathEffect)
                {
                    var (elementName, elementColor) = this.GetOnDeathEffectElement(entityValue.Path);
                    this.RecordDrawnEntity(entityValue, $"OnDeath {elementName}");
                    this.DrawOnDeathEffectMarker(fgDraw, screenPos, elementColor);
                    continue;
                }

                if (this.Settings.DrawDebugPrimitives)
                {
                    var primitiveColor = entityValue.EntityType switch
                    {
                        EntityTypes.Monster => ImGuiHelper.Color(255, 64, 64, 255),
                        EntityTypes.Player => ImGuiHelper.Color(64, 180, 255, 255),
                        EntityTypes.NPC => ImGuiHelper.Color(255, 220, 80, 255),
                        EntityTypes.Chest => ImGuiHelper.Color(80, 255, 150, 255),
                        _ => ImGuiHelper.Color(255, 255, 255, 255),
                    };
                    fgDraw.AddCircleFilled(screenPos, 7f, primitiveColor, 16);
                    fgDraw.AddCircle(screenPos, 10f, ImGuiHelper.Color(0, 0, 0, 220), 16, 2f);
                    this.debugPrimitivesProjected++;
                }

                var iconSizeMultiplierVector = Vector2.One * iconSizeMultiplier;

                void DrawIcon(IconPicker icon, string iconName)
                {
                    if (icon.IconScale <= 0)
                    {
                        return;
                    }

                    this.debugIconsProjected++;
                    this.RecordDrawnEntity(entityValue, iconName);
                    var scaled = iconSizeMultiplierVector * icon.IconScale;
                    iconDrawCommands.Add(new RadarIconDrawCommand
                    {
                        Center = screenPos,
                        HalfSize = scaled,
                        Icon = icon,
                        IconName = iconName,
                        EntityType = entityValue.EntityType,
                        Priority = this.GetIconDrawPriority(iconName, entityValue.EntityType),
                        Sequence = iconDrawSequence++,
                    });
                }

                switch (entityValue.EntityType)
                {
                    case EntityTypes.NPC:
                        DrawIcon(
                            entityValue.EntitySubtype == EntitySubtypes.SpecialNPC ? specialNpcIcon : npcIcon,
                            entityValue.EntitySubtype == EntitySubtypes.SpecialNPC ? "Special NPC" : "NPC");
                        break;
                    case EntityTypes.Player:
                        if (entityValue.EntitySubtype == EntitySubtypes.PlayerOther)
                        {
                            if (this.Settings.ShowPlayersNames && entityValue.TryGetComponent<Player>(out var playerComp))
                            {
                                var pNameSizeH = this.GetTextHalfSize(playerComp.Name);
                                fgDraw.AddRectFilled(screenPos - pNameSizeH, screenPos + pNameSizeH,
                                    ImGuiHelper.Color(0, 0, 0, 200));
                                fgDraw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), screenPos - pNameSizeH,
                                    ImGuiHelper.Color(255, 128, 128, 255), playerComp.Name);
                            }
                            else
                            {
                                DrawIcon(
                                    entityValue.EntityState == EntityStates.PlayerLeader ? leaderIcon : playerIcon,
                                    entityValue.EntityState == EntityStates.PlayerLeader ? "Leader" : "Player");
                            }
                        }
                        else
                        {
                            DrawIcon(selfIcon, "Self");
                        }

                        break;
                    case EntityTypes.Chest:
                        switch (entityValue.EntitySubtype)
                        {
                            case EntitySubtypes.None:
                                DrawIcon(allOtherChestIcon, "All Other Chest");
                                break;
                            case EntitySubtypes.ChestWithRareRarity:
                                DrawIcon(rareChestIcon, "Rare Chests");
                                break;
                            case EntitySubtypes.ChestWithMagicRarity:
                                DrawIcon(magicChestIcon, "Magic Chests");
                                break;
                            case EntitySubtypes.ExpeditionChest:
                                if (entityValue.Path.Contains("LeagueFaction") &&
                                    this.Settings.ExpeditionMarkerIcons.TryGetValue("Logbook", out var logbookIcon) &&
                                    logbookIcon.IconScale > 0)
                                {
                                    DrawIcon(logbookIcon, "Logbook");
                                }
                                else
                                {
                                    DrawIcon(expeditionChestIcon, "Generic Expedition Chests");
                                }

                                break;
                            case EntitySubtypes.BreachChest:
                                DrawIcon(breachChestIcon, "Breach Chest");
                                break;
                            case EntitySubtypes.Strongbox:
                                DrawIcon(strongboxIcon, "Strongbox");
                                break;
                        }

                        break;
                    case EntityTypes.Shrine:
                        if ((entityValue.TryGetComponent<Shrine>(out var shrineComp) && shrineComp.IsUsed) ||
                            (entityValue.TryGetComponent<Targetable>(out var targ) && !targ.IsTargetable))
                        {
                            break;
                        }

                        DrawIcon(shrineIcon, "Shrine");
                        break;
                    case EntityTypes.Monster:
                        switch (entityValue.EntityState)
                        {
                            case EntityStates.None:
                                if (IsTormentedSpirit(entityValue.Path))
                                {
                                    DrawIcon(tormentedSpiritIcon, "Tormented Spirit");
                                }
                                else if (entityValue.EntitySubtype == EntitySubtypes.POIMonster)
                                {
                                    if (!poiMonsterIcons.TryGetValue(entityValue.EntityCustomGroup, out var poiIcon))
                                    {
                                        poiIcon = poiMonsterIcons[-1];
                                    }

                                    DrawIcon(poiIcon, $"POI Monster {entityValue.EntityCustomGroup}");
                                }
                                else if (entityValue.TryGetComponent<ObjectMagicProperties>(out var omp))
                                {
                                    DrawIcon(
                                        this.RarityToIconMapping(omp.Rarity, normalMonsterIcon, magicMonsterIcon, rareMonsterIcon, uniqueMonsterIcon),
                                        $"{omp.Rarity} Monster");
                                }
                                else
                                {
                                    DrawIcon(normalMonsterIcon, "Normal Monster");
                                }

                                break;
                            case EntityStates.PinnacleBossHidden:
                                DrawIcon(pinnacleBossHiddenIcon, "Pinnacle Boss Not Attackable");
                                break;
                            case EntityStates.MonsterFriendly:
                                DrawIcon(friendlyIcon, "Friendly");
                                break;
                            default:
                                break;
                        }

                        break;
                    case EntityTypes.DeliriumBomb:
                        DrawIcon(deliriumBombIcon, "Delirium Bomb");
                        break;
                    case EntityTypes.DeliriumSpawner:
                        DrawIcon(deliriumSpawnerIcon, "Delirium Spawner");
                        break;
                    case EntityTypes.OtherImportantObjects:
                        if (entityValue.EntityCustomGroup == RadarSettings.ExpeditionMarkerGroup)
                        {
                            if (entityValue.TryGetComponent<MinimapIcon>(out var minimapIcon) &&
                                !string.IsNullOrEmpty(minimapIcon.IconName) &&
                                RadarSettings.ExpeditionMarkerIconNameMap.TryGetValue(minimapIcon.IconName, out var displayName) &&
                                this.Settings.ExpeditionMarkerIcons.TryGetValue(displayName, out var expMarkerIcon) &&
                                expMarkerIcon.IconScale > 0)
                            {
                                DrawIcon(expMarkerIcon, displayName);
                            }
                        }
                        else if (entityValue.EntityCustomGroup == RadarSettings.ExpeditionRemnantGroup)
                        {
                            if (entityValue.TryGetComponent<ObjectMagicProperties>(out var remnantOmp))
                            {
                                foreach (var modName in remnantOmp.ModNames)
                                {
                                    foreach (var (modSubstring, remnantDisplayName) in RadarSettings.ExpeditionRemnantModMap)
                                    {
                                        if (modName.Contains(modSubstring) &&
                                            this.Settings.ExpeditionRemnantIcons.TryGetValue(remnantDisplayName, out var remnantIcon) &&
                                            remnantIcon.IconScale > 0)
                                        {
                                            DrawIcon(remnantIcon, remnantDisplayName);
                                            goto doneRemnant;
                                        }
                                    }
                                }
                                doneRemnant:;
                            }
                        }
                        else
                        {
                            if (!otherImportantObjects.TryGetValue(entityValue.EntityCustomGroup, out var mopoiIcon))
                            {
                                mopoiIcon = otherImportantObjects[-1];
                            }

                            DrawIcon(mopoiIcon, $"Special Object {entityValue.EntityCustomGroup}");
                        }

                        break;
                    case EntityTypes.Renderable:
                    case EntityTypes.Unidentified:
                        if (this.TryDrawMonsterLikeFallback(entityValue, DrawIcon, normalMonsterIcon, magicMonsterIcon, rareMonsterIcon, uniqueMonsterIcon))
                        {
                            this.debugMonsterLikeRenderables++;
                        }
                        else
                        {
                            this.RecordDrawnEntity(entityValue, "Unidentified primitive");
                            fgDraw.AddCircleFilled(screenPos, 3f, 0xFFFFFFFF);
                        }

                        break;
                }
            }

            foreach (var command in iconDrawCommands.OrderBy(x => x.Priority).ThenBy(x => x.Sequence))
            {
                this.DrawEnhancedIconMarker(
                    fgDraw,
                    command.Center,
                    command.HalfSize,
                    command.Icon,
                    command.IconName,
                    command.EntityType);
            }

            if (componentCounts != null)
            {
                this.debugTopComponents = string.Join(", ",
                    componentCounts
                        .OrderByDescending(x => x.Value)
                        .ThenBy(x => x.Key)
                        .Take(8)
                        .Select(x => $"{x.Key}:{x.Value}"));
            }

            if (typeCounts != null)
            {
                this.debugTypeCounts = string.Join(", ",
                    typeCounts
                        .OrderByDescending(x => x.Value)
                        .ThenBy(x => x.Key.ToString())
                        .Select(x => $"{x.Key}:{x.Value}"));
            }
        }

        private void DrawGeneralSettings()
        {
            ImGui.Checkbox("Hide Radar when in Hideout/Town", ref this.Settings.DrawWhenNotInHideoutOrTown);
            ImGui.Checkbox("Hide Radar when game is in the background", ref this.Settings.DrawWhenForeground);
            ImGui.Checkbox("Hide Radar when game is paused", ref this.Settings.DrawWhenNotPaused);
            ImGui.Checkbox("Hide Entities outside the network bubble", ref this.Settings.HideOutsideNetworkBubble);
            ImGui.Checkbox("Show Player Names", ref this.Settings.ShowPlayersNames);
            ImGuiHelper.ToolTip("This button will not work while Player is in the Scourge.");
            if (ImGui.Checkbox("Interpolate icon positions", ref this.Settings.InterpolatePositions) &&
                !this.Settings.InterpolatePositions)
            {
                this.ClearInterpolatedPositionCaches();
            }

            ImGuiHelper.ToolTip("Smooths moving entity icons on the minimap and large map, similar to HealthBars position interpolation.");
            if (this.Settings.InterpolatePositions)
            {
                if (ImGui.SliderInt("Interpolation rate", ref this.Settings.InterpolationRate, 1, 1000))
                {
                    this.Settings.InterpolationRate = Math.Clamp(this.Settings.InterpolationRate, 1, 1000);
                }

                ImGuiHelper.ToolTip("Lower values are smoother. Higher values follow the raw position faster.");
            }

            if (ImGui.Checkbox("Draw Area/Zone Map (maphack)", ref this.Settings.DrawWalkableMap))
            {
                if (this.Settings.DrawWalkableMap)
                {
                    if (this.walkableMapTexture == IntPtr.Zero)
                    {
                        this.ReloadMapTexture();
                    }
                }
                else
                {
                    this.RemoveMapTexture();
                }
            }

            if (ImGui.ColorEdit4("Drawn Map Color", ref this.Settings.WalkableMapColor) &&
                this.walkableMapTexture != IntPtr.Zero)
            {
                this.ReloadMapTexture();
            }
        }

        private void DrawMarkerSettings()
        {
            this.DrawEnhancedMarkerSettings();
            this.DrawDisabledEntityFiltersSettings();
        }

        private void DrawIconSettings()
        {
            this.Settings.DrawIconsSettingToImGui(
                "BaseGame Icons",
                this.Settings.BaseIcons,
                "Blockages icon can be set from Delve Icons category i.e. 'Blockage OR DelveWall'");

            this.Settings.DrawPOIMonsterSettingToImGui(this.DllDirectory);
            this.Settings.OtherImportantObjectsSettingToImGui(this.DllDirectory);
            this.Settings.DrawIconsSettingToImGui(
                "Breach Icons",
                this.Settings.BreachIcons,
                "Breach bosses are same as BaseGame Icons -> Unique Monsters.");

            this.Settings.DrawIconsSettingToImGui(
                "Delirium Icons",
                this.Settings.DeliriumIcons,
                string.Empty);

            this.Settings.DrawIconsSettingToImGui(
                "Tormented Spirit Icons",
                this.Settings.TormentedSpiritIcons,
                "Icons for Metadata/Monsters/TormentedSpirits/*. Set size to 0 to disable.");

            this.Settings.DrawIconsSettingToImGui(
                "Expedition Icons",
                this.Settings.ExpeditionIcons,
                string.Empty);

            this.Settings.DrawIconsSettingToImGui(
                "Temple Icons",
                this.Settings.TempleIcons,
                "Icons for Incursion Waygate devices (Vaal Ruins).");

            this.Settings.DrawIconsSettingToImGui(
                "Expedition Marker Icons",
                this.Settings.ExpeditionMarkerIcons,
                "Icons for expedition markers, keyed by MinimapIcon name. Set size to 0 to disable.");

            this.Settings.DrawIconsSettingToImGui(
                "Expedition Remnant Icons",
                this.Settings.ExpeditionRemnantIcons,
                "Icons for expedition remnants with specific mods. Set size to 0 to disable.");

            this.Settings.DrawIconsSettingToImGui(
                "Boss Icons",
                this.Settings.BossIcons,
                "Icons for map boss arenas.");
        }

        private void DrawDebugSettings()
        {
            ImGui.Checkbox("Show Radar debug overlay", ref this.Settings.ShowDebugOverlay);
            ImGui.Checkbox("Show drawn entities table", ref this.Settings.ShowDrawnEntitiesTable);
            ImGui.Checkbox("Draw test icons around player", ref this.Settings.DrawTestIcons);
            ImGui.Checkbox("Draw debug projection circles", ref this.Settings.DrawDebugPrimitives);
            if (ImGui.Button("Performance mode"))
            {
                this.Settings.ShowDebugOverlay = false;
                this.Settings.ShowDrawnEntitiesTable = false;
                this.Settings.DrawTestIcons = false;
                this.Settings.DrawDebugPrimitives = false;
                this.Settings.ShowPlayersNames = false;
                this.Settings.ShowOriginalIconTexture = false;
                this.Settings.EnablePOIBackground = false;
            }

            ImGui.Separator();
            ImGui.TextWrapped("If your mini/large map icons are not working or visible, open this settings window, click anywhere on it, then hide it.");
            ImGui.DragFloat("Large Map Fix", ref this.Settings.LargeMapScaleMultiplier, 0.001f, 0.1f, 2.0f);
            ImGuiHelper.ToolTip("This slider is for fixing large map icon offset. It should only be changed when icons drift after a resolution/window mode change.");

            if (ImGui.Checkbox("Modify Large Map Culling Window", ref this.Settings.ModifyCullWindow) &&
                this.Settings.ModifyCullWindow)
            {
                this.Settings.MakeCullWindowFullScreen = false;
            }

            if (ImGui.Checkbox("Make Culling Window Cover Whole Game", ref this.Settings.MakeCullWindowFullScreen))
            {
                this.Settings.ModifyCullWindow = !this.Settings.MakeCullWindowFullScreen;
                this.Settings.CullWindowPos = Vector2.Zero;
                this.Settings.CullWindowSize.X = Core.Process.WindowArea.Width;
                this.Settings.CullWindowSize.Y = Core.Process.WindowArea.Height;
            }

            if (ImGui.TreeNode("Culling window advance options"))
            {
                ImGui.Checkbox("Draw maphack in culling window", ref this.Settings.DrawMapInCull);
                ImGui.Checkbox("Draw POIs in culling window", ref this.Settings.DrawPOIInCull);
                ImGui.TreePop();
            }

            ImGui.Separator();
            ImGui.Checkbox("Show terrain points of interest (A.K.A Terrain POI)", ref this.Settings.ShowImportantPOI);
            ImGui.ColorEdit4("Terrain POI text color", ref this.Settings.POIColor);
            ImGui.Checkbox("Add black background to Terrain POI text", ref this.Settings.EnablePOIBackground);
            this.isAddNewPOIHeaderOpened = ImGui.CollapsingHeader("Add or Modify Terrain POI");
            if (this.isAddNewPOIHeaderOpened)
            {
                this.AddNewPOIWidget();
                this.ShowPOIWidget();
            }
        }

        private void DrawDisabledEntityFiltersSettings()
        {
            if (!ImGui.CollapsingHeader("Disabled drawn entity path filters"))
            {
                return;
            }

            ImGui.TextWrapped("Any entity whose Path contains one of these filters will not be drawn by Radar icons or debug primitives.");
            ImGui.InputText("Path filter", ref this.newDisabledPathFilter, 512);
            ImGui.SameLine();
            if (ImGui.Button("Add filter"))
            {
                this.AddDisabledPathFilter(this.newDisabledPathFilter);
                this.newDisabledPathFilter = string.Empty;
            }

            if (this.Settings.DisabledEntityPathFilters.Count == 0)
            {
                ImGui.TextUnformatted("No disabled entity path filters.");
                return;
            }

            if (ImGui.BeginTable("RadarDisabledEntityPathFilters", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Filter");
                ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 85f);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableHeadersRow();

                foreach (var filter in this.Settings.DisabledEntityPathFilters.ToArray())
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(filter);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(filter.EndsWith("/*", StringComparison.Ordinal) ? "prefix" : "contains");
                    ImGui.TableNextColumn();
                    if (ImGui.Button($"Remove##disabled_{filter.GetHashCode()}"))
                    {
                        this.Settings.DisabledEntityPathFilters.Remove(filter);
                    }
                }

                ImGui.EndTable();
            }
        }

        private void DrawEnhancedMarkerSettings()
        {
            if (!ImGui.CollapsingHeader("Enhanced minimap icon markers"))
            {
                return;
            }

            ImGui.Checkbox("Use enhanced icon markers", ref this.Settings.UseEnhancedIconMarkers);
            ImGui.Checkbox("Show original icon texture inside marker", ref this.Settings.ShowOriginalIconTexture);

            var markerShape = (int)this.Settings.EnhancedMarkerShape;
            var markerShapeNames = Enum.GetNames<RadarMarkerShape>();
            if (ImGui.Combo("Marker shape", ref markerShape, markerShapeNames, markerShapeNames.Length))
            {
                this.Settings.EnhancedMarkerShape = (RadarMarkerShape)markerShape;
            }

            ImGui.SliderFloat("Marker scale", ref this.Settings.EnhancedMarkerScale, 0.75f, 2.5f);
            ImGui.SliderFloat("Border thickness", ref this.Settings.EnhancedMarkerBorderThickness, 1f, 6f);
            ImGui.SliderInt("Background alpha", ref this.Settings.EnhancedMarkerBackgroundAlpha, 0, 255);
        }

        private void DrawOnDeathEffectSettings()
        {
            ImGui.Checkbox("Show on-death effect markers", ref this.Settings.ShowOnDeathEffectMarkers);
            ImGui.SliderFloat("On-death marker radius", ref this.Settings.OnDeathEffectMarkerRadius, 1f, 10f);
            ImGui.TextWrapped("OnDeath and GroundOnDeath entities are drawn separately as tiny elemental markers instead of normal monster markers.");
        }

        private bool IsOnDeathEffectPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                (path.Contains("OnDeath", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains("GroundOnDeath", StringComparison.OrdinalIgnoreCase));
        }

        private void MigrateOnDeathFiltersFromDisabledList()
        {
            if (this.Settings.DisabledEntityPathFilters.Count == 0)
            {
                return;
            }

            this.Settings.DisabledEntityPathFilters = this.Settings.DisabledEntityPathFilters
                .Where(filter => !this.IsOnDeathEffectPath(filter))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private (string Name, uint Color) GetOnDeathEffectElement(string path)
        {
            if (path.Contains("Fire", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Burn", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Ignite", StringComparison.OrdinalIgnoreCase))
            {
                return ("Fire", ImGuiHelper.Color(255, 94, 48, 255));
            }

            if (path.Contains("Cold", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Frost", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Ice", StringComparison.OrdinalIgnoreCase))
            {
                return ("Cold", ImGuiHelper.Color(94, 210, 255, 255));
            }

            if (path.Contains("Lightning", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Shock", StringComparison.OrdinalIgnoreCase))
            {
                return ("Lightning", ImGuiHelper.Color(250, 235, 80, 255));
            }

            if (path.Contains("Chaos", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Poison", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Caustic", StringComparison.OrdinalIgnoreCase))
            {
                return ("Chaos", ImGuiHelper.Color(156, 92, 255, 255));
            }

            if (path.Contains("Physical", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Bleed", StringComparison.OrdinalIgnoreCase))
            {
                return ("Physical", ImGuiHelper.Color(230, 230, 230, 255));
            }

            return ("Generic", ImGuiHelper.Color(255, 160, 72, 255));
        }

        private void DrawOnDeathEffectMarker(ImDrawListPtr drawList, Vector2 center, uint color)
        {
            var radius = MathF.Max(1f, this.Settings.OnDeathEffectMarkerRadius);
            var shadow = ImGuiHelper.Color(0, 0, 0, 245);
            drawList.AddCircleFilled(center + new Vector2(1f, 1f), radius + 1.5f, shadow, 12);
            drawList.AddCircleFilled(center, radius, color, 12);
            drawList.AddCircle(center, radius + 1.5f, shadow, 12, 1.5f);
        }

        private int GetIconDrawPriority(string iconName, EntityTypes entityType)
        {
            if (iconName.Contains("Unique", StringComparison.OrdinalIgnoreCase) ||
                iconName.Contains("Boss", StringComparison.OrdinalIgnoreCase) ||
                iconName.Contains("Pinnacle", StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if (iconName.Contains("Rare", StringComparison.OrdinalIgnoreCase))
            {
                return 80;
            }

            if (iconName.Contains("Magic", StringComparison.OrdinalIgnoreCase))
            {
                return 70;
            }

            if (entityType == EntityTypes.Monster ||
                iconName.Contains("Monster", StringComparison.OrdinalIgnoreCase) ||
                iconName.Contains("Fallback", StringComparison.OrdinalIgnoreCase))
            {
                return 60;
            }

            if (entityType == EntityTypes.Player ||
                iconName.Contains("Self", StringComparison.OrdinalIgnoreCase) ||
                iconName.Contains("Player", StringComparison.OrdinalIgnoreCase) ||
                iconName.Contains("Leader", StringComparison.OrdinalIgnoreCase))
            {
                return 90;
            }

            return 50;
        }

        private void DrawEnhancedIconMarker(
            ImDrawListPtr drawList,
            Vector2 center,
            Vector2 iconHalfSize,
            IconPicker icon,
            string iconName,
            EntityTypes entityType)
        {
            if (!this.Settings.UseEnhancedIconMarkers)
            {
                drawList.AddImage(
                    icon.TexturePtr,
                    center - iconHalfSize,
                    center + iconHalfSize,
                    icon.UV0,
                    icon.UV1);
                return;
            }

            var markerHalfSize = iconHalfSize * MathF.Max(0.1f, this.Settings.EnhancedMarkerScale);
            var borderColor = this.GetEnhancedMarkerColor(iconName, entityType);
            var backgroundAlpha = (uint)Math.Clamp(this.Settings.EnhancedMarkerBackgroundAlpha, 0, 255);
            var backgroundColor = ImGuiHelper.Color(4, 8, 12, backgroundAlpha);
            var borderThickness = MathF.Max(1f, this.Settings.EnhancedMarkerBorderThickness);

            if (backgroundAlpha > 0)
            {
                var shadowColor = ImGuiHelper.Color(0, 0, 0, Math.Min(backgroundAlpha, 245u));
                this.DrawMarkerShape(drawList, center + new Vector2(1.5f, 1.5f), markerHalfSize + new Vector2(2f), shadowColor, shadowColor, borderThickness);
            }

            this.DrawMarkerShape(drawList, center, markerHalfSize, backgroundColor, borderColor, borderThickness);

            if (!this.Settings.ShowOriginalIconTexture || icon.IconScale <= 0)
            {
                drawList.AddCircleFilled(center, MathF.Max(2f, MathF.Min(markerHalfSize.X, markerHalfSize.Y) * 0.22f), borderColor, 16);
                return;
            }

            var textureHalfSize = iconHalfSize * 0.72f;
            drawList.AddImage(
                icon.TexturePtr,
                center - textureHalfSize,
                center + textureHalfSize,
                icon.UV0,
                icon.UV1);
        }

        private void DrawMarkerShape(
            ImDrawListPtr drawList,
            Vector2 center,
            Vector2 halfSize,
            uint fillColor,
            uint borderColor,
            float borderThickness)
        {
            switch (this.Settings.EnhancedMarkerShape)
            {
                case RadarMarkerShape.Square:
                    if (HasAlpha(fillColor))
                    {
                        drawList.AddRectFilled(center - halfSize, center + halfSize, fillColor);
                    }

                    if (HasAlpha(borderColor))
                    {
                        drawList.AddRect(center - halfSize, center + halfSize, borderColor, 0f, ImDrawFlags.None, borderThickness);
                    }

                    break;
                case RadarMarkerShape.Circle:
                    var radius = MathF.Max(halfSize.X, halfSize.Y);
                    if (HasAlpha(fillColor))
                    {
                        drawList.AddCircleFilled(center, radius, fillColor, 24);
                    }

                    if (HasAlpha(borderColor))
                    {
                        drawList.AddCircle(center, radius, borderColor, 24, borderThickness);
                    }

                    break;
                case RadarMarkerShape.Diamond:
                default:
                    var top = center + new Vector2(0f, -halfSize.Y);
                    var right = center + new Vector2(halfSize.X, 0f);
                    var bottom = center + new Vector2(0f, halfSize.Y);
                    var left = center + new Vector2(-halfSize.X, 0f);
                    if (HasAlpha(fillColor))
                    {
                        drawList.AddQuadFilled(top, right, bottom, left, fillColor);
                    }

                    if (HasAlpha(borderColor))
                    {
                        drawList.AddQuad(top, right, bottom, left, borderColor, borderThickness);
                    }

                    break;
            }
        }

        private static bool HasAlpha(uint color)
        {
            return (color & 0xFF000000) != 0;
        }

        private static bool IsTormentedSpirit(string path)
        {
            return path.StartsWith(TormentedSpiritPathPrefix, StringComparison.Ordinal);
        }

        private uint GetEnhancedMarkerColor(string iconName, EntityTypes entityType)
        {
            if (iconName.Contains("Unique", StringComparison.OrdinalIgnoreCase) ||
                iconName.Contains("Boss", StringComparison.OrdinalIgnoreCase))
            {
                return ImGuiHelper.Color(255, 92, 64, 255);
            }

            if (iconName.Contains("Rare", StringComparison.OrdinalIgnoreCase))
            {
                return ImGuiHelper.Color(255, 214, 62, 255);
            }

            if (iconName.Contains("Magic", StringComparison.OrdinalIgnoreCase))
            {
                return ImGuiHelper.Color(92, 150, 255, 255);
            }

            if (iconName.Contains("Chest", StringComparison.OrdinalIgnoreCase) ||
                iconName.Contains("Strongbox", StringComparison.OrdinalIgnoreCase) ||
                iconName.Contains("Logbook", StringComparison.OrdinalIgnoreCase))
            {
                return ImGuiHelper.Color(79, 232, 159, 255);
            }

            if (iconName.Contains("Shrine", StringComparison.OrdinalIgnoreCase) ||
                iconName.Contains("Splinter", StringComparison.OrdinalIgnoreCase) ||
                iconName.Contains("Currency", StringComparison.OrdinalIgnoreCase))
            {
                return ImGuiHelper.Color(255, 229, 96, 255);
            }

            if (iconName.Contains("Delirium", StringComparison.OrdinalIgnoreCase))
            {
                return ImGuiHelper.Color(184, 154, 255, 255);
            }

            if (iconName.Contains("Friendly", StringComparison.OrdinalIgnoreCase) ||
                entityType == EntityTypes.Player ||
                entityType == EntityTypes.NPC)
            {
                return ImGuiHelper.Color(82, 208, 255, 255);
            }

            return entityType switch
            {
                EntityTypes.Monster => ImGuiHelper.Color(255, 74, 74, 255),
                EntityTypes.Chest => ImGuiHelper.Color(79, 232, 159, 255),
                EntityTypes.Shrine => ImGuiHelper.Color(255, 229, 96, 255),
                EntityTypes.OtherImportantObjects => ImGuiHelper.Color(255, 168, 72, 255),
                _ => ImGuiHelper.Color(236, 238, 244, 255),
            };
        }

        private void DrawDrawnEntitiesTable()
        {
            if (!this.Settings.ShowDrawnEntitiesTable)
            {
                return;
            }

            ImGui.SetNextWindowSize(new Vector2(980f, 520f), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Radar Drawn Entities"))
            {
                ImGui.End();
                return;
            }

            ImGui.Text($"Drawn rows: {this.drawnEntityRecords.Count}");
            ImGui.SameLine();
            ImGui.InputText("Filter", ref this.drawnEntityFilter, 160);
            ImGui.SameLine();
            if (ImGui.Button("Performance mode##drawn_entities"))
            {
                this.Settings.ShowDrawnEntitiesTable = false;
                this.Settings.ShowDebugOverlay = false;
                this.Settings.DrawDebugPrimitives = false;
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear disabled filters"))
            {
                this.Settings.DisabledEntityPathFilters.Clear();
            }

            var rows = this.drawnEntityRecords.Values
                .Where(x => string.IsNullOrWhiteSpace(this.drawnEntityFilter) ||
                    x.Path.Contains(this.drawnEntityFilter, StringComparison.OrdinalIgnoreCase) ||
                    x.Icon.Contains(this.drawnEntityFilter, StringComparison.OrdinalIgnoreCase) ||
                    x.Type.Contains(this.drawnEntityFilter, StringComparison.OrdinalIgnoreCase) ||
                    x.Components.Contains(this.drawnEntityFilter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Icon)
                .ThenBy(x => x.Path)
                .ToArray();

            if (ImGui.BeginTable("RadarDrawnEntitiesTable", 11, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 55f);
                ImGui.TableSetupColumn("Icon", ImGuiTableColumnFlags.WidthFixed, 150f);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableSetupColumn("Subtype", ImGuiTableColumnFlags.WidthFixed, 120f);
                ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 100f);
                ImGui.TableSetupColumn("Dead", ImGuiTableColumnFlags.WidthFixed, 48f);
                ImGui.TableSetupColumn("Tgt", ImGuiTableColumnFlags.WidthFixed, 42f);
                ImGui.TableSetupColumn("Life", ImGuiTableColumnFlags.WidthFixed, 42f);
                ImGui.TableSetupColumn("Disable", ImGuiTableColumnFlags.WidthFixed, 158f);
                ImGui.TableSetupColumn("Prefix", ImGuiTableColumnFlags.WidthFixed, 105f);
                ImGui.TableSetupColumn("Path");
                ImGui.TableHeadersRow();

                foreach (var row in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text($"{row.Count}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Icon);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Type);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Subtype);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.State);
                    ImGui.TableNextColumn();
                    ImGui.Text(row.IsDead ? "yes" : "no");
                    ImGui.TableNextColumn();
                    ImGui.Text(row.IsTargetable ? "yes" : "no");
                    ImGui.TableNextColumn();
                    ImGui.Text(row.IsAliveByLife ? "yes" : "no");
                    ImGui.TableNextColumn();
                    if (ImGui.Button($"Disable##drawn_{row.Id}_{row.Icon}"))
                    {
                        this.AddDisabledPathFilter(row.Path);
                    }

                    ImGui.TableNextColumn();
                    if (ImGui.Button($"Similar##drawn_prefix_{row.Id}_{row.Icon}"))
                    {
                        this.AddDisabledPathFilter(ToPathPrefixFilter(row.Path));
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Path);
                }

                ImGui.EndTable();
            }

            ImGui.End();
        }

        private void AddDisabledPathFilter(string pathFilter)
        {
            var normalized = pathFilter.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            PluginRuntimeHelper.AddUniquePathFilter(this.Settings.DisabledEntityPathFilters, normalized);
        }

        private static string ToPathPrefixFilter(string path)
        {
            var normalized = path.Trim();
            var atIndex = normalized.IndexOf('@', StringComparison.Ordinal);
            if (atIndex > 0)
            {
                normalized = normalized[..atIndex];
            }

            var slashIndex = normalized.LastIndexOf('/');
            return slashIndex > 0 ? $"{normalized[..slashIndex]}/*" : normalized;
        }

        private bool IsEntityPathDisabled(string path)
        {
            return PluginRuntimeHelper.IsPathDisabled(path, this.Settings.DisabledEntityPathFilters);
        }

        private void RecordDrawnEntity(
            GameHelper.RemoteObjects.States.InGameStateObjects.Entity entity,
            string iconName)
        {
            var key = $"{entity.Id}:{iconName}:{entity.Path}";
            if (this.drawnEntityRecords.TryGetValue(key, out var existing))
            {
                existing.Count++;
                return;
            }

            var isDead = entity.TryGetStatValue(GameStats.is_dead, out var deadValue) && deadValue > 0;
            var isTargetable = entity.TryGetComponent<Targetable>(out var targetable, false) && targetable.IsTargetable;
            var isAlive = entity.TryGetComponent<Life>(out var life, false) && life.IsAlive;
            this.drawnEntityRecords[key] = new DrawnEntityRecord
            {
                Id = entity.Id,
                Icon = iconName,
                Type = entity.EntityType.ToString(),
                Subtype = entity.EntitySubtype.ToString(),
                State = entity.EntityState.ToString(),
                Path = entity.Path,
                Components = string.Join(", ", entity.GetComponentNames()),
                IsDead = isDead,
                IsTargetable = isTargetable,
                IsAliveByLife = isAlive,
                Count = 1,
            };
        }

        private bool TryDrawMonsterLikeFallback(
            GameHelper.RemoteObjects.States.InGameStateObjects.Entity entity,
            Action<IconPicker, string> drawIcon,
            IconPicker normalMonsterIcon,
            IconPicker magicMonsterIcon,
            IconPicker rareMonsterIcon,
            IconPicker uniqueMonsterIcon)
        {
            var looksLikeMonster =
                entity.Path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal) ||
                entity.HasComponent(nameof(Life)) ||
                entity.HasComponent(nameof(Targetable)) ||
                entity.HasComponent(nameof(Buffs)) ||
                entity.HasComponent(nameof(Stats)) ||
                entity.HasComponent("BaseEvents") ||
                entity.TryGetComponent<Life>(out _) ||
                entity.TryGetComponent<ObjectMagicProperties>(out _) ||
                entity.TryGetComponent<Buffs>(out _);

            if (!looksLikeMonster)
            {
                return false;
            }

            if (entity.TryGetComponent<ObjectMagicProperties>(out var omp))
            {
                drawIcon(
                    this.RarityToIconMapping(omp.Rarity, normalMonsterIcon, magicMonsterIcon, rareMonsterIcon, uniqueMonsterIcon),
                    $"Fallback {omp.Rarity} Monster");
            }
            else
            {
                drawIcon(normalMonsterIcon, "Fallback Monster");
            }

            return true;
        }

        private IEnumerator<Wait> ClearCachesAndUpdateAreaInfo()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.CleanUpRadarPluginCaches();
                this.currentAreaName = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails.Id;
                this.GenerateMapTexture();
                this.LogBossArenaTgtMatches();
            }
        }

        private void LogBossArenaTgtMatches()
        {
            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            Console.WriteLine($"BossArena: area={this.currentAreaName}, TgtTilesLocations count={currentAreaInstance.TgtTilesLocations.Count}, BossArenaTgts count={this.Settings.BossArenaTgts.Count}");
            foreach (var bossTgt in this.Settings.BossArenaTgts)
            {
                if (currentAreaInstance.TgtTilesLocations.ContainsKey(bossTgt.Key))
                {
                    Console.WriteLine($"  BossArena MATCH: \"{bossTgt.Key}\"");
                }
            }
        }

        private IEnumerator<Wait> OnMove()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnMoved);
                this.UpdateMiniMapDetails();
                this.UpdateLargeMapDetails();
                if (this.Settings.MakeCullWindowFullScreen)
                {
                    this.Settings.CullWindowPos = Vector2.Zero;

                    this.Settings.CullWindowSize.X = Core.Process.WindowArea.Size.Width;
                    this.Settings.CullWindowSize.Y = Core.Process.WindowArea.Size.Height;
                    this.skipOneSettingChange = false;
                }
                else if (this.skipOneSettingChange)
                {
                    this.skipOneSettingChange = false;
                }
                else
                {
                    this.Settings.ModifyCullWindow = true;
                }
            }
        }

        private IEnumerator<Wait> OnClose()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnClose);
                this.skipOneSettingChange = true;
                this.CleanUpRadarPluginCaches();
            }
        }

        private IEnumerator<Wait> OnForegroundChange()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnForegroundChanged);
                this.UpdateMiniMapDetails();
                this.UpdateLargeMapDetails();
            }
        }

        private void UpdateMiniMapDetails()
        {
            var map = Core.States.InGameStateObject.GameUi.MiniMap;
            var widthSq = map.Size.X * map.Size.X;
            var heightSq = map.Size.Y * map.Size.Y;
            this.miniMapDiagonalLength = Math.Sqrt(widthSq + heightSq);
        }

        private void UpdateLargeMapDetails()
        {
            var map = Core.States.InGameStateObject.GameUi.LargeMap;
            var widthSq = map.Size.X * map.Size.X;
            var heightSq = map.Size.Y * map.Size.Y;
            this.largeMapDiagonalLength = Math.Sqrt(widthSq + heightSq);
        }

        private void ReloadMapTexture()
        {
            this.RemoveMapTexture();
            this.GenerateMapTexture();
        }

        private void RemoveMapTexture()
        {
            this.walkableMapTexture = IntPtr.Zero;
            this.walkableMapDimension = Vector2.Zero;
            Core.Overlay.RemoveImage("walkable_map");
        }

        private void GenerateMapTexture()
        {
            if (Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState))
            {
                return;
            }

            var instance = Core.States.InGameStateObject.CurrentAreaInstance;
            var gridHeightData = instance.GridHeightData;
            var mapWalkableData = instance.GridWalkableData;
            var bytesPerRow = instance.TerrainMetadata.BytesPerRow;
            var worldToGridHeightMultiplier = instance.WorldToGridConvertor * 2f;
            if (bytesPerRow <= 0)
            {
                return;
            }

            var mapEdgeDetector = new MapEdgeDetector(mapWalkableData, bytesPerRow);
            var configuration = Configuration.Default.Clone();
            configuration.PreferContiguousImageBuffers = true;
            using Image<Rgba32> image = new(configuration, bytesPerRow * 2, mapEdgeDetector.TotalRows);
            Parallel.For(0, gridHeightData.Length, y =>
            {
                for (var x = 1; x < gridHeightData[y].Length - 1; x++)
                {
                    if (!mapEdgeDetector.IsBorder(x, y))
                    {
                        continue;
                    }

                    var height = (int)(gridHeightData[y][x] / worldToGridHeightMultiplier);
                    var imageX = x - height;
                    var imageY = y - height;

                    if (mapEdgeDetector.IsInsideMapBoundary(imageX, imageY))
                    {
                        image[imageX, imageY] = new Rgba32(this.Settings.WalkableMapColor);
                    }
                }
            });
#if DEBUG
            image.Save(this.DllDirectory +
                       @$"/current_map_{Core.States.InGameStateObject.CurrentAreaInstance.AreaHash}.jpeg");
#endif
            this.walkableMapDimension = new Vector2(image.Width, image.Height);
            if (Math.Max(image.Width, image.Height) > 8192)
            {
                var (newWidth, newHeight) = (image.Width, image.Height);
                if (image.Height > image.Width)
                {
                    newWidth = newWidth * 8192 / newHeight;
                    newHeight = 8192;
                }
                else
                {
                    newHeight = newHeight * 8192 / newWidth;
                    newWidth = 8192;
                }

                var targetSize = new Size(newWidth, newHeight);
                var resizer = new ResizeProcessor(new ResizeOptions { Size = targetSize }, image.Size)
                    .CreatePixelSpecificCloningProcessor(configuration, image, image.Bounds);
                resizer.Execute();
            }

            Core.Overlay.AddOrGetImagePointer("walkable_map", image, false, out var t);
            this.walkableMapTexture = t;
        }

        private IconPicker RarityToIconMapping(
            Rarity rarity,
            IconPicker normalMonsterIcon,
            IconPicker magicMonsterIcon,
            IconPicker rareMonsterIcon,
            IconPicker uniqueMonsterIcon)
        {
            return rarity switch
            {
                Rarity.Magic => magicMonsterIcon,
                Rarity.Rare => rareMonsterIcon,
                Rarity.Unique => uniqueMonsterIcon,
                _ => normalMonsterIcon,
            };
        }

        private void DrawRadarDebugOverlay(bool largeMapVisible, bool miniMapVisible)
        {
            if (!this.Settings.ShowDebugOverlay)
            {
                return;
            }

            ImGui.SetNextWindowSize(new Vector2(430f, 250f), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Radar Diagnostics"))
            {
                var area = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails;
                var instance = Core.States.InGameStateObject.CurrentAreaInstance;
                ImGui.Text($"Reason: {this.debugSkipReason}");
                ImGui.Text($"State: {Core.States.GameCurrentState}");
                ImGui.Text($"Foreground: {Core.Process.Foreground}");
                ImGui.Text($"Hideout/Town: {area.IsHideout}/{area.IsTown}");
                ImGui.Text($"LargeMap/MiniMap: {largeMapVisible}/{miniMapVisible}");
                ImGui.Text($"Cull: pos={this.Settings.CullWindowPos}, size={this.Settings.CullWindowSize}");
                ImGui.Text($"Walkable texture: 0x{this.walkableMapTexture.ToInt64():X}, dim={this.walkableMapDimension}");
                ImGui.Text($"Entities: seen={this.debugEntitiesSeen}, render={this.debugEntitiesWithRender}, in={this.debugEntitiesInBounds}, clipped={this.debugEntitiesClipped}");
                ImGui.Text($"Entity state: valid={this.debugEntitiesValid}, useless={this.debugEntitiesUseless}, monsters={this.debugMonsterEntities}");
                ImGui.Text($"Monster signals: useless={this.debugMonsterUseless}, targetable={this.debugMonsterTargetableTrue}, lifeAlive={this.debugMonsterLifeAlive}, isDead={this.debugMonsterDeadStat}");
                ImGui.Text($"No Render: monsters={this.debugMonsterEntitiesWithoutRender}, life={this.debugLifeEntitiesWithoutRender}");
                ImGui.TextWrapped($"Type counts: {this.debugTypeCounts}");
                ImGui.TextWrapped($"Top components: {this.debugTopComponents}");
                ImGui.Text($"Drawn: icons={this.debugIconsProjected}, monsterFallback={this.debugMonsterLikeRenderables}, primitives={this.debugPrimitivesProjected}, test={this.Settings.DrawTestIcons}");
                ImGui.Text($"Last projected: {this.debugLastProjectedPosition}");
                ImGui.Text($"Last drawn: {this.debugLastDrawnPosition}");
                ImGui.Text($"Area hash: {instance.AreaHash}, network={instance.NetworkBubbleEntityCount}, awake={instance.AwakeEntities.Count}");
                ImGui.Text($"Entities offset: 0x{GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance.EntitiesOffset:X}");
                if (instance.Player.TryGetComponent<Render>(out var playerRender))
                {
                    ImGui.Separator();
                    ImGui.Text($"Player grid: {playerRender.GridPosition}");
                    ImGui.Text($"World position offset: 0x{Render.WorldPositionOffset:X}");
                    ImGui.Text($"Player terrain: {playerRender.TerrainHeight:0.####}");
                    ImGui.Text($"Terrain offset: 0x{Render.TerrainHeightOffset:X}");
                }

                if (instance.Player.TryGetComponent<Life>(out var playerLife))
                {
                    ImGui.Text($"Health offset: 0x{Life.HealthOffset:X}");
                    ImGui.Text($"Player life: {playerLife.Health.Current}/{playerLife.Health.Total}");
                }
            }

            ImGui.End();
        }

        private void DrawRadarTestIcons(Vector2 mapCenter, float iconSizeMultiplier)
        {
            if (!this.Settings.DrawTestIcons ||
                !this.Settings.BaseIcons.TryGetValue("Self", out var icon) ||
                !Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
            {
                return;
            }

            var fgDraw = ImGui.GetWindowDrawList();
            var iconSize = Vector2.One * iconSizeMultiplier * Math.Max(icon.IconScale, 1f);
            var offsets = new[]
            {
                Vector2.Zero,
                new Vector2(60f, 0f),
                new Vector2(-60f, 0f),
                new Vector2(0f, 60f),
                new Vector2(0f, -60f),
            };

            foreach (var offset in offsets)
            {
                var pos = mapCenter + offset;
                fgDraw.AddCircle(pos, iconSize.X + 4f, ImGuiHelper.Color(255, 255, 0, 255), 16, 2f);
                fgDraw.AddImage(icon.TexturePtr, pos - iconSize, pos + iconSize, icon.UV0, icon.UV1);
            }

            fgDraw.AddText(mapCenter + new Vector2(8f, 8f), ImGuiHelper.Color(255, 255, 0, 255),
                $"test icons / z {playerRender.TerrainHeight:0.##}");
        }

        private Vector2 GetTextHalfSize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Vector2.Zero;
            }

            if (!this.textHalfSizeCache.TryGetValue(text, out var size))
            {
                size = ImGui.CalcTextSize(text) / 2;
                this.textHalfSizeCache[text] = size;
            }

            return size;
        }

        private string DelveChestPathToIcon(string path)
        {
            return path.Replace(this.delveChestStarting, null, StringComparison.Ordinal);
        }

        private void DrawEntityPathEnding(string path, ImDrawListPtr fgDraw, Vector2 pos)
        {
            var lastIndex = path.LastIndexOf('/') + 1;
            if (lastIndex < 0 || lastIndex >= path.Length)
            {
                lastIndex = 0;
            }

            var displayName = path.AsSpan(lastIndex, path.Length - lastIndex);
            var pNameSizeH = ImGui.CalcTextSize(displayName) / 2;
            fgDraw.AddRectFilled(pos - pNameSizeH, pos + pNameSizeH,
                ImGuiHelper.Color(0, 0, 0, 200));
            fgDraw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), pos - pNameSizeH,
                ImGuiHelper.Color(255, 128, 128, 255), displayName);

        }

        private void AddNewPOIWidget()
        {
            var tgttilesInArea = Core.States.InGameStateObject.CurrentAreaInstance.TgtTilesLocations;
            var tgtTileKeys = tgttilesInArea.Keys
                .Where(k => string.IsNullOrEmpty(this.tmpTileFilter) ||
                    k.Contains(this.tmpTileFilter, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            ImGui.InputText("Area Name", ref this.currentAreaName, 200, ImGuiInputTextFlags.ReadOnly);
            ImGui.NewLine();
            ImGui.InputInt("Filter on Max POI frenquency", ref this.Settings.POIFrequencyFilter);
            ImGui.InputText("Filter by text", ref this.tmpTileFilter, 200);
            if (ImGui.InputInt("Select POI via Index###tgtSelectorCounter", ref this.tmpTgtSelectionCounter) &&
                this.tmpTgtSelectionCounter >= 0 &&
                this.tmpTgtSelectionCounter < tgtTileKeys.Length)
            {
                this.tmpTileName = tgtTileKeys[this.tmpTgtSelectionCounter];
            }

            ImGui.NewLine();
            if (ImGuiHelper.IEnumerableComboBox<string>("POI Path", tgtTileKeys, ref this.tmpTileName))
            {
                Console.WriteLine($"POI Path selected: {this.tmpTileName}");
            }
            ImGui.InputText("POI Display Name", ref this.tmpDisplayName, 200);
            ImGui.Checkbox("Add for all Areas", ref this.addTileForAllAreas);
            ImGui.SameLine();
            if (ImGui.Button("Add POI"))
            {
                var key = this.addTileForAllAreas ? "common" : this.currentAreaName;
                if (!string.IsNullOrEmpty(key) &&
                    !string.IsNullOrEmpty(this.tmpTileName) &&
                    !string.IsNullOrEmpty(this.tmpDisplayName))
                {
                    if (!this.Settings.ImportantTgts.ContainsKey(key))
                    {
                        this.Settings.ImportantTgts[key] = new();
                    }

                    this.Settings.ImportantTgts[key]
                        [this.tmpTileName] = this.tmpDisplayName;

                    this.tmpTileName = string.Empty;
                    this.tmpDisplayName = string.Empty;
                }
            }
        }

        private void ShowPOIWidget()
        {
            if (ImGui.TreeNode($"Important Terrain POIs common for all Areas"))
            {
                if (this.Settings.ImportantTgts.TryGetValue("common", out var commonTgts))
                {
                    foreach (var tgt in commonTgts.ToArray())
                    {
                        if (ImGui.SmallButton($"Delete##{tgt.Key}"))
                        {
                            commonTgts.Remove(tgt.Key);
                        }

                        ImGui.SameLine();
                        ImGui.Text($"POI Path: {tgt.Key}, Display: {tgt.Value}");
                        ImGuiHelper.ToolTip("Click me to Modify.");
                        if (ImGui.IsItemClicked())
                        {
                            this.tmpTileName = tgt.Key;
                            this.tmpDisplayName = tgt.Value;
                        }
                    }
                }

                ImGui.TreePop();
            }

            if (ImGui.TreeNode($"Important Terrain POIs in Area: {this.currentAreaName}##import_time_in_area"))
            {
                if (this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var areaTgts))
                {
                    foreach (var tgt in areaTgts.ToArray())
                    {
                        if (ImGui.SmallButton($"Delete##{tgt.Key}"))
                        {
                            areaTgts.Remove(tgt.Key);
                        }

                        ImGui.SameLine();
                        ImGui.Text($"POI Path: {tgt.Key}, Display: {tgt.Value}");
                        ImGuiHelper.ToolTip("Click me to Modify.");
                        if (ImGui.IsItemClicked())
                        {
                            this.tmpTileName = tgt.Key;
                            this.tmpDisplayName = tgt.Value;
                        }
                    }
                }

                ImGui.TreePop();
            }
        }

        private void CleanUpRadarPluginCaches()
        {
            this.delveChestCache.Clear();
            this.textHalfSizeCache.Clear();
            this.poiIndexHalfSizeCache.Clear();
            this.ClearInterpolatedPositionCaches();
            this.RemoveMapTexture();
            this.currentAreaName = string.Empty;
        }

        private Vector2 InterpolateMapPosition(
            uint entityId,
            Vector2 currentPosition,
            Dictionary<uint, Vector2> positionCache)
        {
            this.Settings.InterpolationRate = Math.Clamp(this.Settings.InterpolationRate, 1, 1000);
            return PluginRuntimeHelper.InterpolatePosition(
                entityId,
                currentPosition,
                positionCache,
                this.Settings.InterpolatePositions,
                this.Settings.InterpolationRate);
        }

        private void PruneInterpolatedPositionCache(
            GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance area,
            Dictionary<uint, Vector2> positionCache)
        {
            if (!this.Settings.InterpolatePositions || positionCache.Count == 0)
            {
                return;
            }

            PluginRuntimeHelper.PrunePositionCache(
                area,
                positionCache,
                this.activeEntityIdsScratch,
                this.cachedEntityIdsScratch);
        }

        private void ClearInterpolatedPositionCaches()
        {
            this.largeMapInterpolatedPositions.Clear();
            this.miniMapInterpolatedPositions.Clear();
        }
    }
}
