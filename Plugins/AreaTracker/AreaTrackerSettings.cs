namespace AreaTracker
{
    using GameHelper.Plugin;
    using System.Numerics;

    public sealed class AreaTrackerSettings : IPSettings
    {
        public bool ShowWindow { get; set; } = true;

        public bool HideWhenGameInBackground { get; set; } = true;

        public bool AutoExportOnAreaChange { get; set; }

        public bool ShowDebug { get; set; }

        public int MaxVisits { get; set; } = 100;

        public Vector2 WindowPos { get; set; } = Vector2.Zero;

        public Vector2 WindowSize { get; set; } = new(620f, 360f);
    }
}
