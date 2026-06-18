// <copyright file="ThemeManager.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Settings
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using ImGuiNET;

    /// <summary>
    ///     Applies the visual style used by all ImGui windows.
    /// </summary>
    internal static class ThemeManager
    {
        internal readonly record struct ThemeInfo(AppTheme Theme, string Name, string Description);

        internal static IReadOnlyList<ThemeInfo> Themes { get; } =
        [
            new(AppTheme.Graphite, "Graphite", "Dense neutral dark interface with restrained blue actions."),
            new(AppTheme.CarbonMint, "Carbon Mint", "Dark carbon surfaces with mint highlights and cool contrast."),
            new(AppTheme.MidnightAmber, "Midnight Amber", "Deep midnight base with amber status and command accents."),
            new(AppTheme.ArcticGlass, "Arctic Glass", "Light frosted panels with crisp blue-gray controls."),
            new(AppTheme.SolarSlate, "Solar Slate", "Warm gray workspace with strong yellow command highlights."),
            new(AppTheme.CrimsonSteel, "Crimson Steel", "Industrial graphite shell with red active states."),
            new(AppTheme.ForestCircuit, "Forest Circuit", "Muted green controls over a low-glare dark base."),
            new(AppTheme.Oceanic, "Oceanic", "Blue-green control language on compact dark surfaces."),
            new(AppTheme.VioletTerminal, "Violet Terminal", "Dark terminal-like theme with violet and cyan signals."),
            new(AppTheme.PaperDark, "Paper Dark", "Ink-on-paper inspired dark theme with warm text contrast."),
            new(AppTheme.NeonGlass, "Neon Glass", "Black glass workspace with coral, magenta and mint highlights."),
        ];

        internal static void Apply(AppTheme theme)
        {
            var style = ImGui.GetStyle();
            ResetLayout(style);

            var palette = theme switch
            {
                AppTheme.CarbonMint => Palette(
                    new(0.07f, 0.09f, 0.09f, 0.96f), new(0.10f, 0.14f, 0.14f, 1f),
                    new(0.48f, 0.93f, 0.76f, 1f), new(0.20f, 0.50f, 0.43f, 1f), new(0.82f, 0.91f, 0.88f, 1f)),
                AppTheme.MidnightAmber => Palette(
                    new(0.05f, 0.06f, 0.10f, 0.97f), new(0.10f, 0.12f, 0.18f, 1f),
                    new(1.00f, 0.66f, 0.24f, 1f), new(0.58f, 0.34f, 0.12f, 1f), new(0.90f, 0.88f, 0.82f, 1f)),
                AppTheme.ArcticGlass => Palette(
                    new(0.88f, 0.91f, 0.94f, 0.98f), new(0.78f, 0.84f, 0.89f, 1f),
                    new(0.14f, 0.36f, 0.58f, 1f), new(0.40f, 0.56f, 0.70f, 1f), new(0.08f, 0.11f, 0.14f, 1f), true),
                AppTheme.SolarSlate => Palette(
                    new(0.12f, 0.13f, 0.13f, 0.97f), new(0.20f, 0.21f, 0.20f, 1f),
                    new(0.95f, 0.72f, 0.22f, 1f), new(0.50f, 0.42f, 0.20f, 1f), new(0.91f, 0.89f, 0.82f, 1f)),
                AppTheme.CrimsonSteel => Palette(
                    new(0.08f, 0.08f, 0.09f, 0.97f), new(0.14f, 0.14f, 0.16f, 1f),
                    new(0.89f, 0.20f, 0.25f, 1f), new(0.48f, 0.13f, 0.16f, 1f), new(0.92f, 0.88f, 0.86f, 1f)),
                AppTheme.ForestCircuit => Palette(
                    new(0.06f, 0.09f, 0.08f, 0.97f), new(0.11f, 0.16f, 0.13f, 1f),
                    new(0.45f, 0.82f, 0.46f, 1f), new(0.23f, 0.43f, 0.27f, 1f), new(0.86f, 0.92f, 0.84f, 1f)),
                AppTheme.Oceanic => Palette(
                    new(0.05f, 0.09f, 0.12f, 0.97f), new(0.09f, 0.16f, 0.20f, 1f),
                    new(0.27f, 0.79f, 0.89f, 1f), new(0.15f, 0.46f, 0.58f, 1f), new(0.84f, 0.91f, 0.94f, 1f)),
                AppTheme.VioletTerminal => Palette(
                    new(0.06f, 0.05f, 0.10f, 0.97f), new(0.11f, 0.10f, 0.17f, 1f),
                    new(0.67f, 0.48f, 0.95f, 1f), new(0.36f, 0.25f, 0.60f, 1f), new(0.88f, 0.86f, 0.94f, 1f)),
                AppTheme.PaperDark => Palette(
                    new(0.10f, 0.095f, 0.085f, 0.97f), new(0.17f, 0.155f, 0.13f, 1f),
                    new(0.74f, 0.65f, 0.48f, 1f), new(0.40f, 0.34f, 0.25f, 1f), new(0.92f, 0.88f, 0.78f, 1f)),
                AppTheme.NeonGlass => Palette(
                    new(0.025f, 0.026f, 0.040f, 0.98f), new(0.085f, 0.085f, 0.115f, 1f),
                    new(1.00f, 0.27f, 0.55f, 1f), new(0.08f, 0.73f, 0.58f, 1f), new(0.95f, 0.96f, 0.98f, 1f)),
                _ => Palette(
                    new(0.075f, 0.08f, 0.09f, 0.97f), new(0.13f, 0.14f, 0.16f, 1f),
                    new(0.35f, 0.57f, 0.95f, 1f), new(0.22f, 0.34f, 0.58f, 1f), new(0.90f, 0.91f, 0.94f, 1f)),
            };

            ApplyPalette(style, palette);
        }

        private static void ResetLayout(ImGuiStylePtr style)
        {
            style.WindowPadding = new Vector2(18f, 16f);
            style.FramePadding = new Vector2(12f, 7f);
            style.CellPadding = new Vector2(10f, 7f);
            style.ItemSpacing = new Vector2(10f, 8f);
            style.ItemInnerSpacing = new Vector2(8f, 6f);
            style.ScrollbarSize = 12f;
            style.GrabMinSize = 10f;
            style.WindowBorderSize = 1f;
            style.ChildBorderSize = 1f;
            style.PopupBorderSize = 1f;
            style.FrameBorderSize = 0f;
            style.WindowRounding = 8f;
            style.ChildRounding = 8f;
            style.FrameRounding = 6f;
            style.PopupRounding = 8f;
            style.ScrollbarRounding = 8f;
            style.GrabRounding = 5f;
            style.TabRounding = 5f;
        }

        private static (Vector4 bg, Vector4 surface, Vector4 accent, Vector4 muted, Vector4 text, bool light) Palette(
            Vector4 bg,
            Vector4 surface,
            Vector4 accent,
            Vector4 muted,
            Vector4 text,
            bool light = false)
        {
            return (bg, surface, accent, muted, text, light);
        }

        private static void ApplyPalette(
            ImGuiStylePtr style,
            (Vector4 bg, Vector4 surface, Vector4 accent, Vector4 muted, Vector4 text, bool light) p)
        {
            var colors = style.Colors;
            var panel = Mix(p.bg, p.surface, 0.60f);
            var hover = Mix(p.muted, p.accent, 0.35f);
            var active = p.accent;
            var border = WithAlpha(Mix(p.surface, p.text, p.light ? 0.28f : 0.18f), 0.65f);
            var disabled = WithAlpha(p.text, p.light ? 0.48f : 0.42f);

            colors[(int)ImGuiCol.Text] = p.text;
            colors[(int)ImGuiCol.TextDisabled] = disabled;
            colors[(int)ImGuiCol.WindowBg] = p.bg;
            colors[(int)ImGuiCol.ChildBg] = WithAlpha(panel, 0.88f);
            colors[(int)ImGuiCol.PopupBg] = WithAlpha(panel, 0.98f);
            colors[(int)ImGuiCol.Border] = border;
            colors[(int)ImGuiCol.BorderShadow] = Vector4.Zero;
            colors[(int)ImGuiCol.FrameBg] = WithAlpha(p.surface, p.light ? 0.95f : 0.72f);
            colors[(int)ImGuiCol.FrameBgHovered] = WithAlpha(hover, 0.85f);
            colors[(int)ImGuiCol.FrameBgActive] = WithAlpha(active, 0.72f);
            colors[(int)ImGuiCol.TitleBg] = WithAlpha(panel, 1f);
            colors[(int)ImGuiCol.TitleBgActive] = WithAlpha(Mix(panel, p.accent, 0.16f), 1f);
            colors[(int)ImGuiCol.TitleBgCollapsed] = WithAlpha(p.bg, 0.92f);
            colors[(int)ImGuiCol.MenuBarBg] = WithAlpha(panel, 0.95f);
            colors[(int)ImGuiCol.ScrollbarBg] = WithAlpha(p.bg, 0.45f);
            colors[(int)ImGuiCol.ScrollbarGrab] = WithAlpha(p.muted, 0.70f);
            colors[(int)ImGuiCol.ScrollbarGrabHovered] = WithAlpha(hover, 0.86f);
            colors[(int)ImGuiCol.ScrollbarGrabActive] = WithAlpha(active, 0.95f);
            colors[(int)ImGuiCol.CheckMark] = active;
            colors[(int)ImGuiCol.SliderGrab] = WithAlpha(active, 0.78f);
            colors[(int)ImGuiCol.SliderGrabActive] = active;
            colors[(int)ImGuiCol.Button] = WithAlpha(p.muted, 0.70f);
            colors[(int)ImGuiCol.ButtonHovered] = WithAlpha(hover, 0.95f);
            colors[(int)ImGuiCol.ButtonActive] = active;
            colors[(int)ImGuiCol.Header] = WithAlpha(p.muted, 0.55f);
            colors[(int)ImGuiCol.HeaderHovered] = WithAlpha(hover, 0.85f);
            colors[(int)ImGuiCol.HeaderActive] = WithAlpha(active, 0.82f);
            colors[(int)ImGuiCol.Separator] = border;
            colors[(int)ImGuiCol.SeparatorHovered] = WithAlpha(hover, 0.85f);
            colors[(int)ImGuiCol.SeparatorActive] = active;
            colors[(int)ImGuiCol.ResizeGrip] = WithAlpha(p.muted, 0.35f);
            colors[(int)ImGuiCol.ResizeGripHovered] = WithAlpha(hover, 0.80f);
            colors[(int)ImGuiCol.ResizeGripActive] = active;
            colors[(int)ImGuiCol.Tab] = WithAlpha(p.surface, 0.78f);
            colors[(int)ImGuiCol.TabHovered] = WithAlpha(hover, 0.90f);
            colors[(int)ImGuiCol.PlotLines] = active;
            colors[(int)ImGuiCol.PlotLinesHovered] = WithAlpha(active, 0.85f);
            colors[(int)ImGuiCol.PlotHistogram] = WithAlpha(p.accent, 0.75f);
            colors[(int)ImGuiCol.PlotHistogramHovered] = p.accent;
            colors[(int)ImGuiCol.TableHeaderBg] = WithAlpha(Mix(panel, p.accent, 0.10f), 1f);
            colors[(int)ImGuiCol.TableBorderStrong] = border;
            colors[(int)ImGuiCol.TableBorderLight] = WithAlpha(border, 0.55f);
            colors[(int)ImGuiCol.TableRowBg] = Vector4.Zero;
            colors[(int)ImGuiCol.TableRowBgAlt] = WithAlpha(p.surface, p.light ? 0.35f : 0.18f);
            colors[(int)ImGuiCol.TextSelectedBg] = WithAlpha(p.accent, 0.35f);
            colors[(int)ImGuiCol.DragDropTarget] = p.accent;
            colors[(int)ImGuiCol.NavWindowingHighlight] = WithAlpha(p.text, 0.70f);
            colors[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0f, 0f, 0f, p.light ? 0.35f : 0.55f);
        }

        private static Vector4 Mix(Vector4 a, Vector4 b, float amount)
        {
            return Vector4.Lerp(a, b, Math.Clamp(amount, 0f, 1f));
        }

        private static Vector4 WithAlpha(Vector4 color, float alpha)
        {
            color.W = alpha;
            return color;
        }
    }
}
