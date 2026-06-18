namespace MonsterRadarPro
{
    using GameHelper.Plugin;
    using GameHelper.Utils;

    public sealed class MonsterRadarProSettings : IPSettings
    {
        public bool EnableWorldMarkers { get; set; } = true;

        public bool ShowSummaryWindow { get; set; } = true;

        public bool HideDeadMonsters { get; set; } = true;

        public bool HideFriendlyMonsters { get; set; } = true;

        public bool ShowLabels { get; set; }

        public bool ShowNormal { get; set; } = true;

        public bool ShowMagic { get; set; } = true;

        public bool ShowRare { get; set; } = true;

        public bool ShowUnique { get; set; } = true;

        public float MarkerRadius { get; set; } = 8f;

        public float MaxGridDistance { get; set; } = 240f;

        public uint NormalColor { get; set; } = ImGuiHelper.Color(255, 255, 255, 230);

        public uint MagicColor { get; set; } = ImGuiHelper.Color(80, 150, 255, 240);

        public uint RareColor { get; set; } = ImGuiHelper.Color(255, 220, 60, 245);

        public uint UniqueColor { get; set; } = ImGuiHelper.Color(255, 120, 40, 245);

        public uint UnknownColor { get; set; } = ImGuiHelper.Color(255, 80, 80, 230);
    }
}
