namespace PerformanceMonitor
{
    using GameHelper.Plugin;

    public sealed class PerformanceMonitorSettings : IPSettings
    {
        public bool ShowWindow { get; set; } = true;

        public bool CompactMode { get; set; }

        public bool HideWhenGameInBackground { get; set; } = true;

        public int SampleWindowSeconds { get; set; } = 15;

        public bool ShowCoroutineStats { get; set; } = true;

        public bool ShowEntityStats { get; set; } = true;
    }
}
