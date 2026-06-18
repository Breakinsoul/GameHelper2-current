namespace Radar
{
    public sealed class DrawnEntityRecord
    {
        public uint Id { get; set; }

        public string Icon { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string Subtype { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Components { get; set; } = string.Empty;

        public bool IsDead { get; set; }

        public bool IsTargetable { get; set; }

        public bool IsAliveByLife { get; set; }

        public int Count { get; set; }
    }
}
