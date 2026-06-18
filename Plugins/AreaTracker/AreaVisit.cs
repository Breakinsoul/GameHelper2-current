namespace AreaTracker
{
    using System;

    public sealed class AreaVisit
    {
        public DateTimeOffset EnteredAt { get; set; }

        public DateTimeOffset LastSeenAt { get; set; }

        public string AreaHash { get; set; } = string.Empty;

        public int AreaLevel { get; set; }

        public int AwakeEntities { get; set; }

        public int NetworkBubbleEntities { get; set; }

        public int UselessAwakeEntities { get; set; }
    }
}
