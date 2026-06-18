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
        public readonly record struct TerrainHeightCandidate(int Offset, float Value, float? Expected, float Delta, bool Plausible);

        private static readonly float WorldToGridRatio =
            TileStructure.TileToWorldConversion / TileStructure.TileToGridConversion;
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

        public static int[] TerrainHeightCandidates => TerrainHeightOffsetCandidates.ToArray();

        /// <summary>
        ///     Converts the <see cref="Render" /> class data to ImGui.
        /// </summary>
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGui.Text($"Grid Position: {this.GridPosition}");
            ImGui.Text($"World Position: {this.WorldPosition}");
            ImGui.Text($"Terrain Height (Z-Axis): {this.TerrainHeight}");
            ImGui.Text($"Terrain Height Offset: 0x{terrainHeightOffset:X}");
            foreach (var candidate in this.GetTerrainHeightDiagnostics())
            {
                ImGui.Text($"  0x{candidate.Offset:X}: {candidate.Value:0.####}, expected={candidate.Expected?.ToString("0.####") ?? "n/a"}, delta={candidate.Delta:0.####}, ok={candidate.Plausible}");
            }

            ImGui.Text($"Model Bounds: {this.ModelBounds}");
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
            var data = reader.ReadMemory<RenderOffsets>(this.Address);
            this.OwnerEntityAddress = data.Header.EntityPtr;
            this.WorldPosition = data.CurrentWorldPosition;
            this.ModelBounds = data.CharactorModelBounds;

            var newX = data.CurrentWorldPosition.X / WorldToGridRatio;
            var newY = data.CurrentWorldPosition.Y / WorldToGridRatio;
            System.Threading.Volatile.Write(ref this.gridSnap, new GridPos2DSnap(newX, newY));
            this.TerrainHeight = (float)Math.Round(this.ResolveTerrainHeight(newX, newY), 4);
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
                AppLogger.Info($"[Render] Auto-selected TerrainHeight offset 0x{terrainHeightOffset:X}.");
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
    }
}
