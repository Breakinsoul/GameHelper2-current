namespace GameHelper.RemoteObjects.States
{
    using System;
    using System.Numerics;

    public enum AtlasUiGraphItemKind
    {
        Unknown,
        MapNode,
        RouteOrBadge,
    }

    public readonly record struct AtlasUiGraphItem(
        int Id,
        AtlasUiGraphItemKind Kind,
        IntPtr Address,
        IntPtr ChildAddress,
        Vector2 WorldPosition,
        Vector2 ScreenPosition,
        Vector2 Size,
        Vector2 ScreenSize,
        bool IsVisible,
        bool IsAccessible,
        int RawX,
        int RawY,
        int RawState,
        long RawKey,
        string MapName,
        string BiomeName,
        string TooltipText);
}
