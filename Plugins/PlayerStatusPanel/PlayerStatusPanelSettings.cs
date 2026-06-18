namespace PlayerStatusPanel
{
    using GameHelper.Plugin;

    public sealed class PlayerStatusPanelSettings : IPSettings
    {
        public bool ShowWindow { get; set; } = true;

        public bool HideWhenGameInBackground { get; set; } = true;

        public bool ShowBuffs { get; set; } = true;

        public bool ShowOnlyTimedBuffs { get; set; } = true;

        public int MaxBuffRows { get; set; } = 20;
    }
}
