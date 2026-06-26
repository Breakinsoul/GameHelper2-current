namespace AtlasHelper
{
    using System.Numerics;
    using GameHelper.Plugin;

    public sealed class AtlasHelperSettings : IPSettings
    {
        public bool ShowWindow = true;

        public bool HideWhenGameInBackground = true;

        public bool DrawLiveAtlasNodes = true;

        public bool DrawMapNames = true;

        public bool DrawNodeIds;

        public bool DrawRawKeys;

        public bool DrawHiddenNodes;

        public bool CaptureUiTree;

        public Vector2 WindowPos = Vector2.Zero;

        public Vector2 WindowSize = new(520f, 360f);

        public Vector2 AnchorNudge = Vector2.Zero;

        public float LiveNodeRadius = 5f;

        public float CaptureRadius = 120f;

        public float RefreshIntervalMs = 1000f;

        public Vector4 AccessibleNodeColor = new(1f, 0.784f, 0.314f, 0.9f);

        public Vector4 HiddenNodeColor = new(0.75f, 0.12f, 0.12f, 0.85f);

        public Vector4 LabelColor = new(1f, 1f, 1f, 0.95f);
    }
}
