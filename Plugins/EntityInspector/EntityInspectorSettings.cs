namespace EntityInspector
{
    using GameHelper.Plugin;

    public sealed class EntityInspectorSettings : IPSettings
    {
        public bool ShowWindow { get; set; } = true;

        public bool IncludeInvalidEntities { get; set; }

        public bool IncludeUselessEntities { get; set; } = true;

        public int MaxRows { get; set; } = 300;
    }
}
