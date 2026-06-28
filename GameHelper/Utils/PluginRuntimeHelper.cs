namespace GameHelper.Utils
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;

    public static class PluginRuntimeHelper
    {
        public static bool IsPathFilterMatch(string path, string filter)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(filter))
            {
                return false;
            }

            var normalizedFilter = filter.Trim();
            if (normalizedFilter.EndsWith("/*", StringComparison.Ordinal))
            {
                return path.StartsWith(
                    normalizedFilter[..^1],
                    StringComparison.OrdinalIgnoreCase);
            }

            return path.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPathDisabled(string path, IEnumerable<string> filters)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            foreach (var filter in filters)
            {
                if (IsPathFilterMatch(path, filter))
                {
                    return true;
                }
            }

            return false;
        }

        public static void AddUniquePathFilter(ICollection<string> filters, string pathFilter)
        {
            var normalized = pathFilter.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            foreach (var filter in filters)
            {
                if (filter.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            filters.Add(normalized);
        }

        public static Vector2 InterpolatePosition(
            uint entityId,
            Vector2 currentPosition,
            IDictionary<uint, Vector2> positionCache,
            bool enabled,
            int interpolationRate)
        {
            if (!IsSafeScreenPosition(currentPosition))
            {
                positionCache.Remove(entityId);
                return Vector2.Zero;
            }

            if (!enabled)
            {
                positionCache.Remove(entityId);
                return currentPosition;
            }

            var clampedRate = Math.Clamp(interpolationRate, 1, 1000) / 1000f;
            if (positionCache.TryGetValue(entityId, out var previousPosition))
            {
                currentPosition = IsSafeScreenPosition(previousPosition)
                    ? MathHelper.Lerp(previousPosition, currentPosition, clampedRate)
                    : currentPosition;
                if (!IsSafeScreenPosition(currentPosition))
                {
                    positionCache.Remove(entityId);
                    return Vector2.Zero;
                }
            }

            positionCache[entityId] = currentPosition;
            return currentPosition;
        }

        public static void PrunePositionCache(
            AreaInstance area,
            IDictionary<uint, Vector2> positionCache,
            ISet<uint> activeEntityIdsScratch,
            IList<uint> cachedEntityIdsScratch)
        {
            if (positionCache.Count == 0)
            {
                return;
            }

            activeEntityIdsScratch.Clear();
            foreach (var entity in area.AwakeEntities.Values)
            {
                activeEntityIdsScratch.Add(entity.Id);
            }

            cachedEntityIdsScratch.Clear();
            foreach (var cachedId in positionCache.Keys)
            {
                cachedEntityIdsScratch.Add(cachedId);
            }

            foreach (var cachedId in cachedEntityIdsScratch)
            {
                if (!activeEntityIdsScratch.Contains(cachedId))
                {
                    positionCache.Remove(cachedId);
                }
            }

            activeEntityIdsScratch.Clear();
            cachedEntityIdsScratch.Clear();
        }

        public static bool TryProjectEntityToScreen(
            Entity entity,
            out Render render,
            out Vector2 screen,
            float zOffset = 0f,
            bool useCache = true)
        {
            screen = Vector2.Zero;
            if (!entity.TryGetComponent<Render>(out var foundRender, useCache) || foundRender == null)
            {
                render = null!;
                return false;
            }

            render = foundRender;
            var worldPosition = render.WorldPosition;
            if (!IsFinite(worldPosition) || !IsFinite(render.ModelBounds))
            {
                return false;
            }

            worldPosition.Z += zOffset;
            screen = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(worldPosition, worldPosition.Z);
            return IsSafeScreenPosition(screen);
        }

        public static bool IsOutsideScreen(Vector2 location, Vector2 margin)
        {
            if (!IsSafeScreenPosition(location) || !IsFinite(margin))
            {
                return true;
            }

            return location.X < -margin.X ||
                location.Y < -margin.Y ||
                location.X > Core.Process.WindowArea.Width + margin.X ||
                location.Y > Core.Process.WindowArea.Height + margin.Y;
        }

        public static bool IsFinite(Vector2 value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y);
        }

        public static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        }

        public static bool IsFinite(StdTuple3D<float> value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        }

        public static bool IsSafeScreenPosition(Vector2 value)
        {
            const float MaxDrawCoordinate = 100000f;
            return IsFinite(value) &&
                MathF.Abs(value.X) <= MaxDrawCoordinate &&
                MathF.Abs(value.Y) <= MaxDrawCoordinate;
        }
    }
}
