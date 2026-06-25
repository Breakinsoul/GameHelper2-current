namespace AreaTracker
{
    using GameHelper.Plugin;

    public sealed class AreaTrackerSettings : IPSettings
    {
        public bool ShowWindow { get; set; } = true;

        public bool HideWhenGameInBackground { get; set; } = true;

        public bool AutoExportOnAreaChange { get; set; }

        public bool ShowDebug { get; set; }

        public int MaxVisits { get; set; } = 100;
    }
}
