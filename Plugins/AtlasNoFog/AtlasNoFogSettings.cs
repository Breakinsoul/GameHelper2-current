namespace AtlasNoFog
{
    using GameHelper.Plugin;

    public sealed class AtlasNoFogSettings : IPSettings
    {
        public bool EnablePatch = false;

        public bool AutoApplyWhenGameAttached = false;
    }
}
