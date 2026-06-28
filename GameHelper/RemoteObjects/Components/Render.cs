// <copyright file="Render.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.RemoteObjects.Components
{
    using System;
    using System.Linq;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using GameOffsets.Objects.Components;
    using GameOffsets.Objects.States.InGameState;
    using ImGuiNET;

    /// <summary>
    ///     The <see cref="Render" /> component in the entity.
    /// </summary>
    public class Render : ComponentBase
    {
        public readonly record struct WorldPositionCandidate(int Offset, StdTuple3D<float> Value, bool Plausible);

        public readonly record struct TerrainHeightCandidate(int Offset, float Value, float? Expected, float Delta, bool Plausible);

        private static readonly float WorldToGridRatio =
            TileStructure.TileToWorldConversion / TileStructure.TileToGridConversion;
        private static readonly int[] WorldPositionOffsetCandidates =
        [
            0x138,
            0x130,
            0x140,
            0x128,
            0x148,
            0x150,
            0x158,
            0x160,
        ];

        private static readonly int[] TerrainHeightOffsetCandidates =
        [
            0x1AC,
            0x1B0,
            0x1A8,
            0x1B4,
            0x1A4,
            0x1B8,
            0x1C0,
        ];

        private static int worldPositionOffset = 0x138;
        private static int terrainHeightOffset = 0x1AC;

        private GridPos2DSnap gridSnap = new(0f, 0f);

        private sealed record GridPos2DSnap(float X, float Y);

        /// <summary>
        ///     Initializes a new instance of the <see cref="Render" /> class.
        /// </summary>
        /// <param name="address">address of the <see cref="Render" /> component.</param>
        public Render(IntPtr address)
            : base(address) { }

        /// <summary>
        ///     Gets the position where entity is located on the grid (map).
        ///     Returns a per-call snapshot — atomic with respect to UpdateData.
        ///     Z is always 0 (the underlying field is 2D).
        /// </summary>
        public StdTuple3D<float> GridPosition
        {
            get
            {
                var s = System.Threading.Volatile.Read(ref this.gridSnap);
                return new StdTuple3D<float> { X = s.X, Y = s.Y, Z = 0f };
            }
        }

        /// <summary>
        ///     Gets the position where entity is located on the grid (map).
        /// </summary>
        public StdTuple3D<float> ModelBounds { get; private set; }

        /// <summary>
        ///     Gets the position where entity is rendered in the game world.
        ///     NOTE: Z-Axis is pointing to the (visible/invisible) healthbar.
        /// </summary>
        public StdTuple3D<float> WorldPosition { get; private set; }

        /// <summary>
        ///     Gets the terrain height on which the Entity is standing.
        /// </summary>
        public float TerrainHeight { get; private set; }

        public static int TerrainHeightOffset => terrainHeightOffset;

        public static int WorldPositionOffset => worldPositionOffset;

        public static int[] TerrainHeightCandidates => TerrainHeightOffsetCandidates.ToArray();

        /// <summary>
        ///     Converts the <see cref="Render" /> class data to ImGui.
        /// </summary>
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGui.Text($"Grid Position: {this.GridPosition}");
            ImGui.Text($"World Position: {this.WorldPosition}");
            ImGui.Text($"World Position Offset: 0x{worldPositionOffset:X}");
            foreach (var candidate in this.GetWorldPositionDiagnostics())
            {
                ImGui.Text($"  0x{candidate.Offset:X}: {candidate.Value}, ok={candidate.Plausible}");
            }

            ImGui.Text($"Terrain Height (Z-Axis): {this.TerrainHeight}");
            ImGui.Text($"Terrain Height Offset: 0x{terrainHeightOffset:X}");
            foreach (var candidate in this.GetTerrainHeightDiagnostics())
            {
                ImGui.Text($"  0x{candidate.Offset:X}: {candidate.Value:0.####}, expected={candidate.Expected?.ToString("0.####") ?? "n/a"}, delta={candidate.Delta:0.####}, ok={candidate.Plausible}");
            }

            ImGui.Text($"Model Bounds: {this.ModelBounds}");
        }

        public WorldPositionCandidate[] GetWorldPositionDiagnostics()
        {
            var ret = new WorldPositionCandidate[WorldPositionOffsetCandidates.Length];
            for (var i = 0; i < WorldPositionOffsetCandidates.Length; i++)
            {
                var offset = WorldPositionOffsetCandidates[i];
                var value = ReadWorldPositionAtOffset(Core.Process.Handle, offset);
                ret[i] = new WorldPositionCandidate(offset, value, IsWorldPositionPlausible(value));
            }

            return ret;
        }

        public TerrainHeightCandidate[] GetTerrainHeightDiagnostics()
        {
            var grid = this.GridPosition;
            var expected = TryGetTerrainHeightFromGrid(grid.X, grid.Y);
            var ret = new TerrainHeightCandidate[TerrainHeightOffsetCandidates.Length];
            for (var i = 0; i < TerrainHeightOffsetCandidates.Length; i++)
            {
                var offset = TerrainHeightOffsetCandidates[i];
                var value = ReadTerrainHeightAtOffset(Core.Process.Handle, offset);
                var delta = expected == null || !float.IsFinite(value) ? float.NaN : Math.Abs(value - expected.Value);
                ret[i] = new TerrainHeightCandidate(
                    offset,
                    value,
                    expected,
                    delta,
                    expected != null && IsTerrainHeightPlausible(value, expected.Value));
            }

            return ret;
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            var header = reader.ReadMemory<ComponentHeader>(this.Address);
            this.OwnerEntityAddress = header.EntityPtr;
            this.WorldPosition = this.ResolveWorldPosition();
            this.ModelBounds = ReadWorldPositionAtOffset(reader, worldPositionOffset + 0x0C);

            var newX = this.WorldPosition.X / WorldToGridRatio;
            var newY = this.WorldPosition.Y / WorldToGridRatio;
            System.Threading.Volatile.Write(ref this.gridSnap, new GridPos2DSnap(newX, newY));
            this.TerrainHeight = (float)Math.Round(this.ResolveTerrainHeight(newX, newY), 4);
        }

        private StdTuple3D<float> ResolveWorldPosition()
        {
            var reader = Core.Process.Handle;
            var cached = ReadWorldPositionAtOffset(reader, worldPositionOffset);
            if (IsWorldPositionPlausible(cached))
            {
                return cached;
            }

            foreach (var offset in WorldPositionOffsetCandidates)
            {
                var value = ReadWorldPositionAtOffset(reader, offset);
                if (!IsWorldPositionPlausible(value))
                {
                    continue;
                }

                if (offset != worldPositionOffset)
                {
                    worldPositionOffset = offset;
                    Console.WriteLine($"[Render] Auto-selected WorldPosition offset 0x{worldPositionOffset:X}.");
                }

                return value;
            }

            return cached;
        }

        private float ResolveTerrainHeight(float gridX, float gridY)
        {
            var reader = Core.Process.Handle;
            var expected = TryGetTerrainHeightFromGrid(gridX, gridY);
            var cached = ReadTerrainHeightAtOffset(reader, terrainHeightOffset);
            if (expected == null)
            {
                return cached;
            }

            var bestOffset = terrainHeightOffset;
            var bestValue = cached;
            var bestDelta = float.IsFinite(cached) ? Math.Abs(cached - expected.Value) : float.MaxValue;

            foreach (var offset in TerrainHeightOffsetCandidates)
            {
                var value = ReadTerrainHeightAtOffset(reader, offset);
                var delta = Math.Abs(value - expected.Value);
                if (float.IsFinite(value) && delta < bestDelta)
                {
                    bestOffset = offset;
                    bestValue = value;
                    bestDelta = delta;
                }
            }

            var currentDelta = float.IsFinite(cached) ? Math.Abs(cached - expected.Value) : float.MaxValue;
            if (bestOffset != terrainHeightOffset &&
                IsTerrainHeightPlausible(bestValue, expected.Value) &&
                bestDelta + 1f < currentDelta)
            {
                terrainHeightOffset = bestOffset;
                Console.WriteLine($"[Render] Auto-selected TerrainHeight offset 0x{terrainHeightOffset:X}.");
            }

            return bestValue;
        }

        private float ReadTerrainHeightAtOffset(SafeMemoryHandle reader, int offset)
        {
            try
            {
                return reader.ReadMemory<float>(IntPtr.Add(this.Address, offset));
            }
            catch
            {
                return float.NaN;
            }
        }

        private StdTuple3D<float> ReadWorldPositionAtOffset(SafeMemoryHandle reader, int offset)
        {
            try
            {
                return reader.ReadMemory<StdTuple3D<float>>(IntPtr.Add(this.Address, offset));
            }
            catch
            {
                return default;
            }
        }

        private static float? TryGetTerrainHeightFromGrid(float gridX, float gridY)
        {
            try
            {
                var grid = Core.States.InGameStateObject.CurrentAreaInstance.GridHeightData;
                var x = (int)gridX;
                var y = (int)gridY;
                if (y < 0 || y >= grid.Length)
                {
                    return null;
                }

                if (x < 0 || x >= grid[y].Length)
                {
                    return null;
                }

                return grid[y][x];
            }
            catch
            {
                return null;
            }
        }

        private static bool IsTerrainHeightPlausible(float value, float expected)
        {
            return float.IsFinite(value) && Math.Abs(value - expected) <= 5f;
        }

        private static bool IsWorldPositionPlausible(StdTuple3D<float> value)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            {
                return false;
            }

            var gridX = value.X / WorldToGridRatio;
            var gridY = value.Y / WorldToGridRatio;
            if (Math.Abs(gridX) > 10000f || Math.Abs(gridY) > 10000f || Math.Abs(value.Z) > 100000f)
            {
                return false;
            }

            try
            {
                var grid = Core.States.InGameStateObject.CurrentAreaInstance.GridHeightData;
                if (grid.Length > 0)
                {
                    return gridY >= -64f && gridY <= grid.Length + 64f &&
                        gridX >= -64f && gridX <= grid[0].Length + 64f;
                }
            }
            catch
            {
                // Fall through to broad sanity bounds before terrain data is ready.
            }

            return Math.Abs(value.X) > 0.01f || Math.Abs(value.Y) > 0.01f;
        }
    }
}
