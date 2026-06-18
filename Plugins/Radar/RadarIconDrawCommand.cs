namespace Radar
{
    using System.Numerics;
    using GameHelper.RemoteEnums.Entity;

    public sealed class RadarIconDrawCommand
    {
        public Vector2 Center { get; init; }

        public Vector2 HalfSize { get; init; }

        public IconPicker Icon { get; init; } = null!;

        public string IconName { get; init; } = string.Empty;

        public EntityTypes EntityType { get; init; }

        public int Priority { get; init; }

        public int Sequence { get; init; }
    }
}
