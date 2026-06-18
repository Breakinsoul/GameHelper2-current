// <copyright file="GameOverlay.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using ClickableTransparentOverlay;
    using Coroutine;
    using CoroutineEvents;
    using GameHelper.Utils;
    using ImGuiNET;
    using Plugin;
    using Settings;
    using Ui;

    /// <inheritdoc />
    public sealed class GameOverlay : Overlay
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="GameOverlay" /> class.
        /// </summary>
        internal GameOverlay(string windowTitle)
            : base(windowTitle, true, 3840, 2160)
        {
            CoroutineHandler.Start(this.UpdateOverlayBounds(), priority: int.MaxValue);
            SettingsWindow.InitializeCoroutines();
            PerformanceStats.InitializeCoroutines();
            DataVisualization.InitializeCoroutines();
            GameUiExplorer.InitializeCoroutines();
            PerformanceProfiler.InitializeCoroutines();
            OverlayKiller.InitializeCoroutines();
            NearbyVisualization.InitializeCoroutines();
            KrangledPassiveDetector.InitializeCoroutines();
        }

        /// <summary>
        ///     Gets the fonts loaded in the overlay.
        /// </summary>
        public ImFontPtr[]? Fonts { get; private set; }

        /// <inheritdoc />
        public override async Task Run()
        {
            Core.Initialize();
            Core.InitializeCororutines();
            this.VSync = Core.GHSettings.Vsync;
            await base.Run();
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Core.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        protected override Task PostInitialized()
        {
            ImGuiHelper.ApplyModernTheme();
            if (MiscHelper.TryConvertStringToImGuiGlyphRanges(Core.GHSettings.FontCustomGlyphRange, out var glyphRanges))
            {
                Core.Overlay.ReplaceFont(
                    Core.GHSettings.FontPathName,
                    Core.GHSettings.FontSize,
                    glyphRanges);
            }
            else
            {
                Core.Overlay.ReplaceFont(
                    Core.GHSettings.FontPathName,
                    Core.GHSettings.FontSize,
                    Core.GHSettings.FontLanguage);
            }

            PManager.InitializePlugins();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override void Render()
        {
            PerformanceProfiler.StartFrame();
            PerformanceHitchDetector.StartFrame();

            var sectionStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try { CoroutineHandler.Tick(ImGui.GetIO().DeltaTime); }
            catch (Exception ex) { Console.WriteLine($"[GameOverlay.Render.Tick] {ex}"); }
            finally { PerformanceHitchDetector.RecordSection("Coroutine Tick", System.Diagnostics.Stopwatch.GetTimestamp() - sectionStart); }

            sectionStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try { CoroutineHandler.RaiseEvent(GameHelperEvents.PerFrameDataUpdate); }
            catch (Exception ex) { Console.WriteLine($"[GameOverlay.Render.PerFrameDataUpdate] {ex}"); }
            finally { PerformanceHitchDetector.RecordSection("PerFrameDataUpdate", System.Diagnostics.Stopwatch.GetTimestamp() - sectionStart); }

            sectionStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try { CoroutineHandler.RaiseEvent(GameHelperEvents.PostPerFrameDataUpdate); }
            catch (Exception ex) { Console.WriteLine($"[GameOverlay.Render.PostPerFrameDataUpdate] {ex}"); }
            finally { PerformanceHitchDetector.RecordSection("PostPerFrameDataUpdate", System.Diagnostics.Stopwatch.GetTimestamp() - sectionStart); }

            sectionStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try { CoroutineHandler.RaiseEvent(GameHelperEvents.OnRender); }
            catch (Exception ex) { Console.WriteLine($"[GameOverlay.Render.OnRender] {ex}"); }
            finally { PerformanceHitchDetector.RecordSection("OnRender", System.Diagnostics.Stopwatch.GetTimestamp() - sectionStart); }

            sectionStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try { CoroutineHandler.RaiseEvent(GameHelperEvents.OnPostRender); }
            catch (Exception ex) { Console.WriteLine($"[GameOverlay.Render.OnPostRender] {ex}"); }
            finally { PerformanceHitchDetector.RecordSection("OnPostRender", System.Diagnostics.Stopwatch.GetTimestamp() - sectionStart); }

            PerformanceHitchDetector.EndFrame(Core.CoroutinesRegistrar);

            if (!Core.GHSettings.IsOverlayRunning)
            {
                this.Close();
            }
        }

        private IEnumerator<Wait> UpdateOverlayBounds()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnMoved);
                this.Position = Core.Process.WindowArea.Location;
                this.Size = Core.Process.WindowArea.Size -
                    (Core.GHSettings.FixTaskbarNotShowing ?
                        new System.Drawing.Size(0, 1) :
                        System.Drawing.Size.Empty);
            }
        }
    }
}
