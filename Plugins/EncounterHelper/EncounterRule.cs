namespace EncounterHelper
{
    using System.Numerics;
    using GameHelper.Utils;

    public sealed class EncounterRule
    {
        public string Name = string.Empty;

        public string PathContains = string.Empty;

        public bool Enabled = true;

        public Vector4 MarkerColor = new(0.47f, 0.86f, 1f, 0.96f);

        public Vector4 LabelColor = new(0.92f, 0.96f, 1f, 1f);

        public EncounterMarkerShape Shape = EncounterMarkerShape.Ring;

        public float SizeMultiplier = 1f;

        public bool ShowLabel = true;

        public bool Filled = false;

        public bool ShowCenterDot = true;

        /// <summary>
        ///     Legacy packed color retained for settings migration.
        /// </summary>
        public uint Color = ImGuiHelper.Color(120, 220, 255, 245);
    }
}
