// <copyright file="HealthBarsSettings.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace HealthBars
{
    using System.Collections.Generic;
    using System.Numerics;
    using GameHelper.Plugin;

    /// <summary>
    ///     <see cref="HealthBars" /> plugin settings class.
    /// </summary>
    public sealed class HealthBarsSettings : IPSettings
    {
        /// <summary>
        ///     Draw Healthbars when in town.
        /// </summary>
        public bool DrawInTown = true;

        /// <summary>
        ///     Draw Healthbars when in hideout.
        /// </summary>
        public bool DrawInHideout = true;

        /// <summary>
        ///     Draw Healthbars when game is not foreground.
        /// </summary>
        public bool DrawWhenGameInBackground = true;

        /// <summary>
        ///     % health after which monster is cullable per rarity
        /// </summary>
        public int[] CullingStrikeRangePerRarity = [30, 20, 10, 5];

        /// <summary>
        ///     Interpolate entity position to minimize flicker effect.
        /// </summary>
        public bool InterpolatePosition = true;

        /// <summary>
        ///     Interpolate entity position rate to minimize flickering effect.
        /// </summary>
        public int InterpolationRate = 400;

        /// <summary>
        ///     Gets a value indicating if user want to see mana on the healthbar rather than energyshield.
        /// </summary>
        public bool ShowManaRatherThanESOnSelf = false;

        /// <summary>
        ///     Shows per-entity healthbar diagnostics.
        /// </summary>
        public bool ShowDebugTable = false;

        /// <summary>
        ///     Opens a floating diagnostics window while drawing.
        /// </summary>
        public bool ShowDebugOverlay = false;

        /// <summary>
        ///     Shows a compact runtime status overlay while tuning healthbars.
        /// </summary>
        public bool ShowStatusOverlay = false;

        /// <summary>
        ///     Position of the compact runtime status overlay.
        /// </summary>
        public Vector2 StatusOverlayPosition = new(20f, 120f);

        /// <summary>
        ///     Draws monster bars when Life component exists but current/total HP is unreadable.
        /// </summary>
        public bool DrawUnknownHealthMonsters = true;

        public bool ShowNormalMonsters = true;

        public bool ShowMagicMonsters = true;

        public bool ShowRareMonsters = true;

        public bool ShowUniqueMonsters = true;

        public bool ShowFriendlyMonsters = true;

        public bool ShowPoiMonsters = true;

        /// <summary>
        ///     Uses the same monster-like fallback detection as Radar for renderable/unidentified monsters.
        /// </summary>
        public bool UseRadarMonsterDetection = true;

        /// <summary>
        ///     Skips entities that project outside of the game window.
        /// </summary>
        public bool CullOutsideScreen = true;

        /// <summary>
        ///     Hides entities outside the active network bubble.
        /// </summary>
        public bool HideOutsideNetworkBubble = true;

        /// <summary>
        ///     Shows the current HP value above the bar.
        /// </summary>
        public bool ShowCurrentHealthText = true;

        /// <summary>
        ///     Color of the current HP text above the bar.
        /// </summary>
        public Vector4 CurrentHealthTextColor = new(0.92156863f, 0.9607843f, 1f, 1f);

        /// <summary>
        ///     Uses the global current HP text color instead of per-bar text colors.
        /// </summary>
        public bool UseGlobalCurrentHealthTextColor = true;

        /// <summary>
        ///     Color of the current HP text shadow.
        /// </summary>
        public Vector4 CurrentHealthTextShadowColor = new(0f, 0f, 0f, 0.85f);

        /// <summary>
        ///     Shows the entity object name below the healthbar for debugging.
        /// </summary>
        public bool ShowDebugObjectNameUnderBar = false;

        /// <summary>
        ///     Shows the full object path instead of only the short object name.
        /// </summary>
        public bool ShowDebugObjectFullPath = false;

        /// <summary>
        ///     Color of the debug object name below the healthbar.
        /// </summary>
        public Vector4 DebugObjectNameTextColor = new(0.74f, 0.88f, 1f, 0.95f);

        /// <summary>
        ///     Draw bars with vector shapes instead of legacy texture sprites.
        /// </summary>
        public bool UseModernBars = true;

        /// <summary>
        ///     Corner rounding for modern bars.
        /// </summary>
        public float ModernBarRounding = 2.244f;

        /// <summary>
        ///     Border thickness for modern bars.
        /// </summary>
        public float ModernBarBorderThickness = 0f;

        /// <summary>
        ///     Shadow opacity for modern bars.
        /// </summary>
        public int ModernBarShadowAlpha = 48;

        /// <summary>
        ///     Border color for modern bars.
        /// </summary>
        public Vector4 ModernBarBorderColor = new(0.92156863f, 0.9607843f, 1f, 1f);

        /// <summary>
        ///     Healthbar config for monsters.
        /// </summary>
        public Dictionary<string, Config> Monster = new()
        {
            { "white",    new(new(0.5019608f, 0f, 0f, 1f), 9, false, 3f) { BackgroundColor = new(0f, 0f, 0f, 0f), TextColor = new(0f, 1f, 1f, 0.99215686f) } },
            { "magic",    new(new(0f, 0.5f, 1f, 1f), 8, false, 5f) },
            { "rare",     new(new(1f, 1f, 0f, 1f), 9, true, 7f) },
            { "unique",   new(new(1f, 0.5f, 0f, 1f), 9, true, 12f) },
            { "friendly", new(new(0f, 1f, 0f, 1f)) },
        };

        /// <summary>
        ///     Healthbar config for POI monsters.
        /// </summary>
        public Dictionary<int, Config> POIMonster = new()
        {
            { -1, new(new(0.5f, 0.5f, 0.5f, 0.5f)) },
        };

        /// <summary>
        ///     Healthbar config for player.
        /// </summary>
        public Dictionary<string, Config> Player = new()
        {
            { "self", new(new(1f, 0f, 1f, 1f)) },
            { "leader", new(new(1f, 0f, 1f, 1f)) },
            { "member", new(new(0.5f, 1f, 0.5f, 1f)) },
        };
    }
}
