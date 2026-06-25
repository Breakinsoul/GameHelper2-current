namespace AtlasHelper
{
    using System;
    using System.Numerics;

    internal readonly record struct AtlasLiveNode(
        int Id,
        Vector2 Position,
        Vector2 Size,
        bool IsAccessible,
        bool IsMapNode,
        IntPtr Address,
        IntPtr ChildAddress,
        int RawX,
        int RawY,
        int RawState,
        long RawKey,
        string MapName,
        string BiomeName,
        string TooltipText)
    {
        public Vector2 Center => this.Position + (this.Size / 2f);
    }
}
