namespace RuneshapePriceChecker
{
    using GameHelper.Plugin;
    using System.Numerics;

    public sealed class RuneshapePriceCheckerSettings : IPSettings
    {
        public bool ShowWindow = false;

        public bool ShowInlinePriceHints = true;

        public bool AutoRefresh = true;

        public string League = "Runes of Aldur";

        public string DisplayCurrency = "exalt";

        public int RefreshMinutes = 15;

        public float RedThreshold = 0.5f;

        public float OrangeThreshold = 1.0f;

        public float GreenThreshold = 5.0f;

        public Vector2 WindowPos = Vector2.Zero;

        public Vector2 WindowSize = new(320f, 260f);

        public string ManualItems = "1x Chaos Orb\n1x Exalted Orb\n1x Divine Orb";

        public bool EnableOcr = true;

        public bool UseOcrResults = true;

        public bool ShowOcrBounds = true;

        public int OcrIntervalMs = 500;

        public int OcrOffsetX = 63;

        public int OcrOffsetY = 165;

        public int OcrWidth = 631;

        public int OcrHeight = 668;

        public int OcrTextInsetLeft = 200;

        public int OcrUpscale = 2;

        public bool OcrUseFrameHash = true;

        public bool OcrGrayscale = true;

        public bool OcrThreshold = true;

        public int OcrThresholdValue = 150;

        public float OcrContrast = 1.35f;
    }
}
