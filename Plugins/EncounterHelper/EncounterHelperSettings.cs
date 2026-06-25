namespace EncounterHelper
{
    using System.Collections.Generic;
    using System.Numerics;
    using GameHelper.Plugin;

    public sealed class EncounterHelperSettings : IPSettings
    {
        public bool EnableWorldMarkers = true;

        public bool ShowSummaryWindow = true;

        public bool ShowLabels = true;

        public bool HideDead = true;

        public bool HideWhenGameInBackground = true;

        public bool HideInTown = true;

        public bool HideInHideout = true;

        public float MarkerRadius = 12f;

        public float MarkerThickness = 3f;

        public int MarkerSegments = 24;

        public Vector4 MarkerOutlineColor = new(0f, 0f, 0f, 0.82f);

        public float MarkerOutlinePadding = 4f;

        public Vector4 LabelBackgroundColor = new(0f, 0f, 0f, 0.62f);

        public Vector2 LabelOffset = new(8f, -9f);

        public bool ShowLabelBackground = true;

        public bool ShowOnlyMatchedSummary = true;

        public float MaxGridDistance = 500f;

        public List<EncounterRule> Rules =
        [
            new EncounterRule { Name = "Ritual", PathContains = "Ritual", MarkerColor = new(0.82f, 0.31f, 1f, 0.96f), LabelColor = new(0.98f, 0.88f, 1f, 1f), Shape = EncounterMarkerShape.Diamond },
            new EncounterRule { Name = "Breach", PathContains = "Breach", MarkerColor = new(0.74f, 0.47f, 1f, 0.96f), LabelColor = new(0.92f, 0.84f, 1f, 1f), Shape = EncounterMarkerShape.Ring },
            new EncounterRule { Name = "Expedition", PathContains = "Expedition", MarkerColor = new(1f, 0.75f, 0.31f, 0.96f), LabelColor = new(1f, 0.9f, 0.68f, 1f), Shape = EncounterMarkerShape.Square },
            new EncounterRule { Name = "Delirium", PathContains = "Delirium", MarkerColor = new(0.71f, 0.71f, 0.82f, 0.96f), LabelColor = new(0.9f, 0.9f, 1f, 1f), Shape = EncounterMarkerShape.Triangle },
            new EncounterRule { Name = "Cache", PathContains = "Cache", MarkerColor = new(0.31f, 0.86f, 1f, 0.96f), LabelColor = new(0.78f, 0.95f, 1f, 1f), Shape = EncounterMarkerShape.Circle },
            new EncounterRule { Name = "Strongbox", PathContains = "Strongbox", MarkerColor = new(0.31f, 1f, 0.63f, 0.96f), LabelColor = new(0.78f, 1f, 0.88f, 1f), Shape = EncounterMarkerShape.Square },
            new EncounterRule { Name = "Shrine", PathContains = "Shrine", MarkerColor = new(1f, 0.94f, 0.35f, 0.96f), LabelColor = new(1f, 0.96f, 0.7f, 1f), Shape = EncounterMarkerShape.Dot },
            new EncounterRule { Name = "Boss", PathContains = "Boss", MarkerColor = new(1f, 0.31f, 0.31f, 0.96f), LabelColor = new(1f, 0.78f, 0.78f, 1f), Shape = EncounterMarkerShape.Cross, SizeMultiplier = 1.25f },
        ];
    }
}
