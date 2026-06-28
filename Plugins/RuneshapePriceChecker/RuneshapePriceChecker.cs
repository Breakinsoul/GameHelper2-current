namespace RuneshapePriceChecker
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Drawing.Imaging;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Numerics;
    using System.Runtime.InteropServices.WindowsRuntime;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;
    using Windows.Graphics.Imaging;
    using Windows.Media.Ocr;

#pragma warning disable CA1416

    public sealed class RuneshapePriceChecker : PCore<RuneshapePriceCheckerSettings>
    {
        private const string BaseUrl = "https://api.poe2scout.com/poe2";
        private static readonly Regex QuantityWithX = new("^(?<qty>\\d+|[AaIiLlTt|Oo0])\\s*[xX\\u0445\\u0425]\\s+(?<name>.+)$", RegexOptions.Compiled);
        private static readonly Regex QuantityWithoutX = new("^(?<qty>\\d+|[IiLl|Oo0])\\s+(?<name>.+)$", RegexOptions.Compiled);
        private static readonly Regex NonAlphaNumeric = new("[^A-Za-z0-9]+", RegexOptions.Compiled);
        private static readonly Regex TrailingLevel = new("\\s+LEVEL\\s+\\d+$", RegexOptions.Compiled);
        private static readonly Regex TrailingOrb = new("\\s+ORB$", RegexOptions.Compiled);
        private static readonly Regex PriceHintLine = new("^\\s*\\d+(?:[,.]\\d+)?\\s*(?:[?\\s]*)?(?:c|ch|chaos|d|div|divine|e?x|exalt|exalted|x)\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly HttpClient httpClient = new();
        private readonly Dictionary<string, decimal> prices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal> fallbackPrices = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PriceRow> rows = [];
        private readonly object ocrPreviewLock = new();
        private CancellationTokenSource? refreshCts;
        private Task? refreshTask;
        private DateTimeOffset lastRefreshUtc = DateTimeOffset.MinValue;
        private string status = "Price cache is empty.";
        private string lastError = string.Empty;
        private bool isRefreshing;
        private bool manualItemsDirty = true;
        private Task<List<OcrLine>>? ocrTask;
        private CancellationTokenSource? ocrCts;
        private OcrEngine? windowsOcrEngine;
        private string ocrText = string.Empty;
        private List<OcrLine> ocrLines = [];
        private string ocrStatus = "OCR is disabled.";
        private string ocrError = string.Empty;
        private DateTimeOffset lastOcrStartedUtc = DateTimeOffset.MinValue;
        private DateTimeOffset lastOcrCompletedUtc = DateTimeOffset.MinValue;
        private long lastOcrFrameHash;
        private int skippedOcrFrames;
        private int sourceLineCount = 1;
        private bool ocrSkippedUnchangedFrame;
        private int ocrPreviewVersion;
        private int loadedOcrPreviewVersion = -1;
        private IntPtr ocrPreviewTexture = IntPtr.Zero;
        private Vector2 ocrPreviewSize = Vector2.Zero;
        private string ocrPreviewStatus = "No OCR image captured yet.";

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        private string OcrPreviewPathname => Path.Join(this.DllDirectory, "cache", "ocr-preview.png");

        private sealed record OcrLine(string Text, Vector2 HintPosition);

        private sealed record OcrRow(Rectangle Bounds, Vector2 HintPosition);

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<RuneshapePriceCheckerSettings>(content) ?? new RuneshapePriceCheckerSettings();
            }

            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GameHelper2-RuneshapePriceChecker/1.0");
            this.TryInitializeOcr();
            this.RebuildRows();

            if (this.Settings.AutoRefresh)
            {
                this.StartRefresh();
            }
        }

        public override void OnDisable()
        {
            this.refreshCts?.Cancel();
            this.refreshCts?.Dispose();
            this.refreshCts = null;
            this.ocrCts?.Cancel();
            this.ocrCts?.Dispose();
            this.ocrCts = null;
            this.RemoveOcrPreviewTexture();
        }

        public override void SaveSettings()
        {
            JsonHelper.SafeToFile(this.Settings, new FileInfo(this.SettingPathname));
        }

        public override void DrawSettings()
        {
            if (!ImGui.BeginTabBar("RuneshapePriceCheckerSettingsTabs"))
            {
                return;
            }

            if (ImGui.BeginTabItem("General"))
            {
                this.DrawGeneralSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Items"))
            {
                this.DrawItemsSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("OCR"))
            {
                this.DrawOcrSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("OCR Image"))
            {
                this.DrawOcrImageSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Debug"))
            {
                this.DrawDebugSettings();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        public override void DrawUI()
        {
            this.PollRefresh();
            this.PollOcr();
            if (this.Settings.AutoRefresh &&
                !this.isRefreshing &&
                DateTimeOffset.UtcNow - this.lastRefreshUtc > TimeSpan.FromMinutes(Math.Max(1, this.Settings.RefreshMinutes)))
            {
                this.StartRefresh();
            }

            if (this.Settings.EnableOcr &&
                Core.Process.Pid != 0 &&
                Core.Process.Foreground &&
                !this.IsOcrRunning &&
                DateTimeOffset.UtcNow - this.lastOcrStartedUtc > TimeSpan.FromMilliseconds(Math.Max(100, this.Settings.OcrIntervalMs)))
            {
                this.StartOcr();
            }

            if (this.Settings.EnableOcr && this.Settings.ShowOcrBounds && Core.Process.Pid != 0)
            {
                this.DrawOcrBounds();
            }

            if (Core.Process.Pid == 0 ||
                Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            if (this.manualItemsDirty)
            {
                this.RebuildRows();
            }

            if (this.Settings.ShowInlinePriceHints)
            {
                this.DrawInlinePriceHints();
            }

            if (!this.Settings.ShowWindow)
            {
                return;
            }

            if (this.Settings.WindowPos != Vector2.Zero)
            {
                ImGui.SetNextWindowPos(this.Settings.WindowPos, ImGuiCond.FirstUseEver);
            }

            ImGui.SetNextWindowSize(this.Settings.WindowSize, ImGuiCond.FirstUseEver);
            var show = this.Settings.ShowWindow;
            if (!ImGui.Begin("Runeshape Price Checker", ref show))
            {
                this.Settings.ShowWindow = show;
                ImGui.End();
                return;
            }

            this.Settings.ShowWindow = show;
            this.Settings.WindowPos = ImGui.GetWindowPos();
            this.Settings.WindowSize = ImGui.GetWindowSize();
            this.DrawPriceRows();
            ImGui.End();
        }

        private void DrawGeneralSettings()
        {
            this.DrawRuntimeStatus();
            ImGui.Checkbox("Show price window", ref this.Settings.ShowWindow);
            ImGui.Checkbox("Show inline price hints", ref this.Settings.ShowInlinePriceHints);
            ImGui.Checkbox("Auto refresh prices", ref this.Settings.AutoRefresh);
            ImGui.SetNextItemWidth(240f);
            ImGui.InputText("League", ref this.Settings.League, 80);
            ImGui.SetNextItemWidth(120f);
            ImGui.InputText("Display currency", ref this.Settings.DisplayCurrency, 16);
            ImGui.SliderInt("Refresh minutes", ref this.Settings.RefreshMinutes, 1, 120);
            ImGui.SliderFloat("Red threshold", ref this.Settings.RedThreshold, 0f, 10f);
            ImGui.SliderFloat("Orange threshold", ref this.Settings.OrangeThreshold, 0f, 25f);
            ImGui.SliderFloat("Green threshold", ref this.Settings.GreenThreshold, 0f, 100f);

            if (ImGui.Button(this.isRefreshing ? "Refreshing..." : "Refresh now"))
            {
                this.StartRefresh();
            }

            ImGui.TextWrapped(this.status);
            if (!string.IsNullOrEmpty(this.lastError))
            {
                ImGui.TextWrapped(this.lastError);
            }
        }

        private void DrawItemsSettings()
        {
            ImGui.TextDisabled("Fallback/manual rows. OCR rows are used when OCR is enabled and has text.");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextMultiline("##manual-items", ref this.Settings.ManualItems, 8192, new Vector2(0f, 260f)))
            {
                this.manualItemsDirty = true;
            }

            if (ImGui.Button("Recalculate"))
            {
                this.RebuildRows();
            }
        }

        private void DrawOcrSettings()
        {
            if (ImGui.Checkbox("Enable OCR capture", ref this.Settings.EnableOcr))
            {
                if (this.Settings.EnableOcr)
                {
                    this.TryInitializeOcr();
                    this.StartOcr();
                }
                else
                {
                    this.ocrStatus = "OCR is disabled.";
                }
            }

            ImGui.Checkbox("Use OCR results for price rows", ref this.Settings.UseOcrResults);
            ImGui.Checkbox("Show OCR capture bounds", ref this.Settings.ShowOcrBounds);
            ImGui.Checkbox("Skip unchanged frames", ref this.Settings.OcrUseFrameHash);
            ImGui.SliderInt("OCR interval ms", ref this.Settings.OcrIntervalMs, 100, 3000);
            ImGui.SliderInt("OCR upscale", ref this.Settings.OcrUpscale, 1, 4);
            ImGui.Separator();
            ImGui.Checkbox("Grayscale", ref this.Settings.OcrGrayscale);
            ImGui.Checkbox("Threshold", ref this.Settings.OcrThreshold);
            ImGui.SliderInt("Threshold value", ref this.Settings.OcrThresholdValue, 0, 255);
            ImGui.SliderFloat("Contrast", ref this.Settings.OcrContrast, 0.5f, 3.0f);
            ImGui.Separator();
            ImGui.DragInt("Region X", ref this.Settings.OcrOffsetX, 1f, 0, 5000);
            ImGui.DragInt("Region Y", ref this.Settings.OcrOffsetY, 1f, 0, 5000);
            ImGui.DragInt("Region W", ref this.Settings.OcrWidth, 1f, 50, 2000);
            ImGui.DragInt("Region H", ref this.Settings.OcrHeight, 1f, 50, 2000);
            ImGui.DragInt("Text inset left", ref this.Settings.OcrTextInsetLeft, 1f, 0, 1000);

            if (ImGui.Button(this.IsOcrRunning ? "Reading..." : "Read OCR now"))
            {
                this.StartOcr(force: true);
            }

            ImGui.SameLine();
            if (ImGui.Button("Use current game window defaults"))
            {
                this.SetDefaultOcrRegion();
            }

            ImGui.TextWrapped(this.ocrStatus);
            if (!string.IsNullOrEmpty(this.ocrError))
            {
                ImGui.TextWrapped(this.ocrError);
            }

            ImGui.Separator();
            ImGui.TextDisabled("Last OCR text");
            ImGui.InputTextMultiline("##ocr-text", ref this.ocrText, 8192, new Vector2(0f, 180f), ImGuiInputTextFlags.ReadOnly);
        }

        private void DrawOcrImageSettings()
        {
            if (ImGui.Button(this.IsOcrRunning ? "Reading..." : "Update OCR image"))
            {
                this.StartOcr(force: true);
            }

            ImGui.SameLine();
            if (ImGui.Button("Reload preview texture"))
            {
                lock (this.ocrPreviewLock)
                {
                    this.ocrPreviewVersion++;
                }
            }

            ImGui.TextWrapped(this.ocrPreviewStatus);
            ImGui.TextDisabled(this.OcrPreviewPathname);
            this.TryLoadOcrPreviewTexture();

            if (this.ocrPreviewTexture == IntPtr.Zero || this.ocrPreviewSize == Vector2.Zero)
            {
                ImGui.TextDisabled("No image to show yet. Press Update OCR image while the runeshape panel is visible.");
                return;
            }

            var availableWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X);
            var maxHeight = 520f;
            var scale = Math.Min(1f, Math.Min(availableWidth / this.ocrPreviewSize.X, maxHeight / this.ocrPreviewSize.Y));
            var imageSize = new Vector2(
                Math.Max(1f, this.ocrPreviewSize.X * scale),
                Math.Max(1f, this.ocrPreviewSize.Y * scale));
            ImGui.Image(this.ocrPreviewTexture, imageSize);
        }

        private void DrawDebugSettings()
        {
            ImGui.Text($"Cached prices: {this.prices.Count}");
            ImGui.Text($"Fallback prices: {this.fallbackPrices.Count}");
            ImGui.Text($"Rows: {this.rows.Count}");
            ImGui.Text($"Last refresh UTC: {(this.lastRefreshUtc == DateTimeOffset.MinValue ? "-" : this.lastRefreshUtc.ToString("u"))}");
            ImGui.Text($"Refreshing: {this.isRefreshing}");
            ImGui.Text($"OCR running: {this.IsOcrRunning}");
            ImGui.Text($"Last OCR UTC: {(this.lastOcrCompletedUtc == DateTimeOffset.MinValue ? "-" : this.lastOcrCompletedUtc.ToString("u"))}");
            ImGui.Text($"OCR frame hash: 0x{this.lastOcrFrameHash:X}");
            ImGui.Text($"Skipped unchanged OCR frames: {this.skippedOcrFrames}");
        }

        private void DrawPriceRows()
        {
            if (this.rows.Count == 0)
            {
                ImGui.TextDisabled("No items.");
                return;
            }

            if (ImGui.BeginTable("RuneshapePriceRows", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 42f);
                ImGui.TableSetupColumn("Item");
                ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 92f);
                ImGui.TableHeadersRow();

                foreach (var row in this.rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text($"{row.Quantity}x");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.ItemName);
                    ImGui.TableNextColumn();
                    var color = row.Matched ? this.GetPriceColor(row.ChaosValue) : new Vector4(0.95f, 0.32f, 0.32f, 1f);
                    ImGui.TextColored(color, row.Label);
                }

                ImGui.EndTable();
            }
        }

        private void DrawRuntimeStatus()
        {
            var refreshAge = this.lastRefreshUtc == DateTimeOffset.MinValue
                ? "never"
                : $"{(DateTimeOffset.UtcNow - this.lastRefreshUtc).TotalMinutes:0.#}m ago";
            ImGui.TextUnformatted($"Prices: {this.prices.Count} cached / {this.rows.Count} rows");
            ImGui.SameLine();
            ImGui.TextDisabled($"last refresh: {refreshAge}");
            ImGui.SameLine();
            ImGui.TextDisabled(this.Settings.EnableOcr ? $"OCR: {this.ocrStatus}" : "OCR: off");
            if (this.Settings.EnableOcr && !Core.Process.Foreground)
            {
                ImGui.TextColored(new Vector4(1f, 0.78f, 0.25f, 1f), "OCR is enabled but the game is not foreground.");
            }
        }

        private void DrawInlinePriceHints()
        {
            if (this.rows.Count == 0 || Core.Process.Pid == 0)
            {
                return;
            }

            var game = Core.Process.WindowArea;
            if (game.Width <= 0 || game.Height <= 0)
            {
                return;
            }

            var drawList = ImGui.GetForegroundDrawList();
            var lineHeight = Math.Max(24f, this.Settings.OcrHeight / Math.Max(1f, this.sourceLineCount + 1f));
            var rightLimit = game.Left + this.Settings.OcrOffsetX + this.Settings.OcrWidth - 8f;
            var regionTop = game.Top + this.Settings.OcrOffsetY;
            foreach (var row in this.rows.Where(row => row.Matched))
            {
                var text = row.Label;
                var textSize = ImGui.CalcTextSize(text);
                var fallbackY = regionTop + 18f + (row.SourceLineIndex * lineHeight);
                var pos = row.HintPosition == Vector2.Zero
                    ? new Vector2(rightLimit - textSize.X, fallbackY)
                    : row.HintPosition;
                if (pos.X + textSize.X + 8f > rightLimit)
                {
                    pos.X = rightLimit - textSize.X - 8f;
                }

                var pad = new Vector2(6f, 3f);
                var bgMin = pos - pad;
                var bgMax = pos + textSize + pad;
                var bg = ImGui.GetColorU32(new Vector4(0.02f, 0.02f, 0.03f, 0.78f));
                var border = ImGui.GetColorU32(this.GetPriceColor(row.ChaosValue));
                drawList.AddRectFilled(bgMin, bgMax, bg, 4f);
                drawList.AddRect(bgMin, bgMax, border, 4f, ImDrawFlags.None, 1.5f);
                drawList.AddText(pos, border, text);
            }
        }

        private void StartRefresh()
        {
            if (this.isRefreshing)
            {
                return;
            }

            this.refreshCts?.Cancel();
            this.refreshCts?.Dispose();
            this.refreshCts = new CancellationTokenSource();
            this.isRefreshing = true;
            this.status = "Refreshing poe2scout prices...";
            this.lastError = string.Empty;
            var token = this.refreshCts.Token;
            this.refreshTask = Task.Run(() => this.RefreshPricesAsync(token), token);
        }

        private void PollRefresh()
        {
            if (this.refreshTask == null || !this.refreshTask.IsCompleted)
            {
                return;
            }

            if (this.refreshTask.IsFaulted)
            {
                this.lastError = this.refreshTask.Exception?.GetBaseException().Message ?? "Unknown refresh error.";
                this.status = "Refresh failed.";
            }

            this.refreshTask.Dispose();
            this.refreshTask = null;
            this.isRefreshing = false;
        }

        private bool IsOcrRunning => this.ocrTask is { IsCompleted: false };

        private void TryInitializeOcr()
        {
            if (this.windowsOcrEngine != null)
            {
                return;
            }

            try
            {
                this.windowsOcrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                this.ocrStatus = this.windowsOcrEngine == null
                    ? "Windows OCR engine is not available."
                    : "Windows OCR engine is ready.";
            }
            catch (Exception ex)
            {
                this.ocrStatus = "Windows OCR initialization failed.";
                this.ocrError = ex.Message;
            }
        }

        private void StartOcr(bool force = false)
        {
            if (this.IsOcrRunning)
            {
                return;
            }

            this.TryInitializeOcr();
            if (this.windowsOcrEngine == null)
            {
                return;
            }

            if (Core.Process.Pid == 0)
            {
                this.ocrStatus = "Game process is not attached.";
                return;
            }

            this.ocrCts?.Cancel();
            this.ocrCts?.Dispose();
            this.ocrCts = new CancellationTokenSource();
            this.ocrStatus = "Reading OCR...";
            this.ocrError = string.Empty;
            this.ocrSkippedUnchangedFrame = false;
            if (force)
            {
                this.lastOcrFrameHash = 0;
            }

            this.lastOcrStartedUtc = DateTimeOffset.UtcNow;
            var token = this.ocrCts.Token;
            this.ocrTask = Task.Run(() => this.CaptureAndRecognizeAsync(token), token);
        }

        private void PollOcr()
        {
            if (this.ocrTask == null || !this.ocrTask.IsCompleted)
            {
                return;
            }

            try
            {
                if (this.ocrTask.IsFaulted)
                {
                    this.ocrError = this.ocrTask.Exception?.GetBaseException().Message ?? "Unknown OCR error.";
                    this.ocrStatus = "OCR failed.";
                }
                else if (this.ocrTask.IsCanceled)
                {
                    this.ocrStatus = "OCR cancelled.";
                }
                else
                {
                    var result = this.ocrTask.Result;
                    this.lastOcrCompletedUtc = DateTimeOffset.UtcNow;
                    if (result.Count == 0)
                    {
                        this.ocrStatus = this.ocrSkippedUnchangedFrame
                            ? $"OCR skipped unchanged frame ({this.skippedOcrFrames})."
                            : "OCR completed with no text.";
                        if (!this.ocrSkippedUnchangedFrame)
                        {
                            this.ocrLines = [];
                            this.ocrText = string.Empty;
                            this.rows.Clear();
                            this.sourceLineCount = 1;
                            this.manualItemsDirty = false;
                        }
                    }
                    else
                    {
                        this.ocrLines = CleanOcrLines(result);
                        this.ocrText = string.Join('\n', this.ocrLines.Select(line => line.Text));
                        this.ocrStatus = string.IsNullOrWhiteSpace(this.ocrText)
                            ? "OCR completed with no text."
                            : $"OCR completed: {this.ocrText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} rows.";
                        this.manualItemsDirty = true;
                    }
                }
            }
            finally
            {
                this.ocrTask.Dispose();
                this.ocrTask = null;
            }
        }

        private async Task<List<OcrLine>> CaptureAndRecognizeAsync(CancellationToken ct)
        {
            if (this.windowsOcrEngine == null)
            {
                return [];
            }

            using var bitmap = this.CaptureOcrRegion();
            ct.ThrowIfCancellationRequested();
            var textInsetLeft = Math.Clamp(this.Settings.OcrTextInsetLeft, 0, Math.Max(0, bitmap.Width - 32));
            using var textBitmap = this.CropBitmap(bitmap, textInsetLeft, 0, bitmap.Width - textInsetLeft, bitmap.Height);

            var frameHash = ComputeFrameHash(textBitmap);
            if (this.Settings.OcrUseFrameHash && frameHash != 0 && frameHash == this.lastOcrFrameHash)
            {
                this.skippedOcrFrames++;
                this.ocrSkippedUnchangedFrame = true;
                return [];
            }

            this.lastOcrFrameHash = frameHash;
            var game = Core.Process.WindowArea;
            var captureX = Math.Clamp(game.Left + this.Settings.OcrOffsetX, game.Left, game.Right - 1);
            var captureY = Math.Clamp(game.Top + this.Settings.OcrOffsetY, game.Top, game.Bottom - 1);
            var upscale = Math.Clamp(this.Settings.OcrUpscale, 1, 4);
            using var textOnlyBitmap = this.KeepTextAndNeighbors(textBitmap);
            using var prepared = this.PrepareBitmapForOcr(textOnlyBitmap);
            var contentBounds = FindContentBounds(prepared);
            var rows = DetectOcrRows(prepared, contentBounds)
                .Select(row => new OcrRow(
                    row,
                    new Vector2(
                        captureX + textInsetLeft + ((float)(row.X + row.Width) / upscale) + 8f,
                        captureY + ((float)(row.Y + (row.Height * 0.5)) / upscale) - 9f)))
                .ToList();

            this.SaveOcrPreview(prepared, contentBounds, rows.Select(row => row.Bounds));
            if (rows.Count == 0)
            {
                return [];
            }

            var recognized = new List<OcrLine>(rows.Count);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                using var rowBitmap = this.PrepareRowBitmapForOcr(prepared, row.Bounds);
                var text = await this.RecognizeBitmapTextAsync(rowBitmap, ct);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    recognized.Add(new OcrLine(text, row.HintPosition));
                }
            }

            return recognized;
        }

        private Bitmap CaptureOcrRegion()
        {
            var game = Core.Process.WindowArea;
            if (game.Width <= 0 || game.Height <= 0)
            {
                throw new InvalidOperationException("Game window bounds are unavailable.");
            }

            var x = Math.Clamp(game.Left + this.Settings.OcrOffsetX, game.Left, game.Right - 1);
            var y = Math.Clamp(game.Top + this.Settings.OcrOffsetY, game.Top, game.Bottom - 1);
            var w = Math.Clamp(this.Settings.OcrWidth, 1, Math.Max(1, game.Right - x));
            var h = Math.Clamp(this.Settings.OcrHeight, 1, Math.Max(1, game.Bottom - y));
            var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            return bitmap;
        }

        private Bitmap CropBitmap(Bitmap source, int x, int y, int width, int height)
        {
            var crop = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(crop);
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, width, height),
                new Rectangle(x, y, width, height),
                GraphicsUnit.Pixel);
            return crop;
        }

        private static Windows.Foundation.Rect UnionWordBounds(IEnumerable<Windows.Foundation.Rect> rects)
        {
            var hasAny = false;
            var left = 0d;
            var top = 0d;
            var right = 0d;
            var bottom = 0d;
            foreach (var rect in rects)
            {
                if (!hasAny)
                {
                    left = rect.X;
                    top = rect.Y;
                    right = rect.X + rect.Width;
                    bottom = rect.Y + rect.Height;
                    hasAny = true;
                    continue;
                }

                left = Math.Min(left, rect.X);
                top = Math.Min(top, rect.Y);
                right = Math.Max(right, rect.X + rect.Width);
                bottom = Math.Max(bottom, rect.Y + rect.Height);
            }

            return hasAny
                ? new Windows.Foundation.Rect(left, top, Math.Max(1d, right - left), Math.Max(1d, bottom - top))
                : new Windows.Foundation.Rect();
        }

        private Bitmap PrepareBitmapForOcr(Bitmap source)
        {
            var upscale = Math.Clamp(this.Settings.OcrUpscale, 1, 4);
            var target = new Bitmap(source.Width * upscale, source.Height * upscale, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(target);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(0, 0, target.Width, target.Height));
            this.ApplyOcrPreprocessing(target);
            return target;
        }

        private Bitmap PrepareRowBitmapForOcr(Bitmap source, Rectangle rowBounds)
        {
            var padX = 4;
            var padY = 2;
            var x = Math.Max(0, rowBounds.X - padX);
            var y = Math.Max(0, rowBounds.Y - padY);
            var right = Math.Min(source.Width, rowBounds.Right + padX);
            var bottom = Math.Min(source.Height, rowBounds.Bottom + padY);
            var crop = new Rectangle(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
            using var row = this.CropBitmap(source, crop.X, crop.Y, crop.Width, crop.Height);
            return AddWhiteBorder(row, 2);
        }

        private async Task<string> RecognizeBitmapTextAsync(Bitmap bitmap, CancellationToken ct)
        {
            if (this.windowsOcrEngine == null)
            {
                return string.Empty;
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Bmp);
            var randomAccessStream = stream.ToArray().AsBuffer().AsStream().AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
            ct.ThrowIfCancellationRequested();
            var result = await this.windowsOcrEngine.RecognizeAsync(softwareBitmap);
            return string.Join(
                " ",
                result.Lines
                    .Where(line => line.Words.Count > 0)
                    .Select(line => string.Join(" ", line.Words.Select(word => word.Text))));
        }

        private void SaveOcrPreview(Bitmap bitmap, Rectangle? contentBounds, IEnumerable<Rectangle> rowBounds)
        {
            try
            {
                var directory = Path.GetDirectoryName(this.OcrPreviewPathname);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var preview = (Bitmap)bitmap.Clone();
                using (var graphics = Graphics.FromImage(preview))
                {
                    if (contentBounds.HasValue)
                    {
                        using var contentPen = new Pen(Color.FromArgb(220, 30, 180, 80), 2f);
                        graphics.DrawRectangle(contentPen, contentBounds.Value);
                    }

                    using var rowPen = new Pen(Color.FromArgb(230, 200, 50, 220), 2f);
                    foreach (var row in rowBounds)
                    {
                        graphics.DrawRectangle(rowPen, row);
                    }
                }

                preview.Save(this.OcrPreviewPathname, ImageFormat.Png);
                lock (this.ocrPreviewLock)
                {
                    this.ocrPreviewVersion++;
                    this.ocrPreviewStatus = $"OCR image updated: {bitmap.Width}x{bitmap.Height}, {DateTimeOffset.Now:HH:mm:ss}. Green = content bounds, purple = OCR rows.";
                }
            }
            catch (Exception ex)
            {
                lock (this.ocrPreviewLock)
                {
                    this.ocrPreviewStatus = $"Failed to save OCR image: {ex.Message}";
                }
            }
        }

        private void TryLoadOcrPreviewTexture()
        {
            int version;
            lock (this.ocrPreviewLock)
            {
                version = this.ocrPreviewVersion;
            }

            if (version == this.loadedOcrPreviewVersion)
            {
                return;
            }

            this.RemoveOcrPreviewTexture();
            if (!File.Exists(this.OcrPreviewPathname))
            {
                this.loadedOcrPreviewVersion = version;
                return;
            }

            try
            {
                Core.Overlay.AddOrGetImagePointer(this.OcrPreviewPathname, false, out var texture, out var width, out var height);
                this.ocrPreviewTexture = texture;
                this.ocrPreviewSize = new Vector2(width, height);
                this.loadedOcrPreviewVersion = version;
            }
            catch (Exception ex)
            {
                this.loadedOcrPreviewVersion = version;
                lock (this.ocrPreviewLock)
                {
                    this.ocrPreviewStatus = $"Failed to load OCR image preview: {ex.Message}";
                }
            }
        }

        private void RemoveOcrPreviewTexture()
        {
            if (this.ocrPreviewTexture != IntPtr.Zero)
            {
                Core.Overlay.RemoveImage(this.OcrPreviewPathname);
            }

            this.ocrPreviewTexture = IntPtr.Zero;
            this.ocrPreviewSize = Vector2.Zero;
        }

        private Bitmap KeepTextAndNeighbors(Bitmap source)
        {
            var rect = new Rectangle(0, 0, source.Width, source.Height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = Math.Abs(data.Stride);
                var bytes = new byte[stride * source.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var keep = new byte[source.Width * source.Height];
                var threshold = Math.Clamp(this.Settings.OcrThresholdValue, 0, 255);

                for (var y = 0; y < source.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < source.Width; x++)
                    {
                        var idx = row + (x * 4);
                        var b = bytes[idx];
                        var g = bytes[idx + 1];
                        var r = bytes[idx + 2];
                        var max = Math.Max(r, Math.Max(g, b));
                        var min = Math.Min(r, Math.Min(g, b));
                        var luminance = ((299 * r) + (587 * g) + (114 * b)) / 1000;
                        var dr = r - 50;
                        var dg = g - 42;
                        var db = b - 34;
                        var textColorDistance = (dr * dr) + (dg * dg) + (db * db);
                        var isLikelyText =
                            luminance <= threshold + 8 &&
                            (max - min <= 45 || textColorDistance <= 47 * 47);
                        if (!isLikelyText)
                        {
                            continue;
                        }

                        var y0 = Math.Max(0, y - 6);
                        var y1 = Math.Min(source.Height - 1, y + 6);
                        var x0 = Math.Max(0, x - 6);
                        var x1 = Math.Min(source.Width - 1, x + 6);
                        for (var ny = y0; ny <= y1; ny++)
                        {
                            var keepRow = ny * source.Width;
                            for (var nx = x0; nx <= x1; nx++)
                            {
                                keep[keepRow + nx] = 1;
                            }
                        }
                    }
                }

                var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
                var resultData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var resultStride = Math.Abs(resultData.Stride);
                    var resultBytes = new byte[resultStride * source.Height];
                    for (var y = 0; y < source.Height; y++)
                    {
                        var srcRow = y * stride;
                        var dstRow = y * resultStride;
                        var keepRow = y * source.Width;
                        for (var x = 0; x < source.Width; x++)
                        {
                            var src = srcRow + (x * 4);
                            var dst = dstRow + (x * 4);
                            if (keep[keepRow + x] != 0)
                            {
                                resultBytes[dst] = bytes[src];
                                resultBytes[dst + 1] = bytes[src + 1];
                                resultBytes[dst + 2] = bytes[src + 2];
                            }
                            else
                            {
                                resultBytes[dst] = 255;
                                resultBytes[dst + 1] = 255;
                                resultBytes[dst + 2] = 255;
                            }

                            resultBytes[dst + 3] = 255;
                        }
                    }

                    System.Runtime.InteropServices.Marshal.Copy(resultBytes, 0, resultData.Scan0, resultBytes.Length);
                }
                finally
                {
                    result.UnlockBits(resultData);
                }

                return result;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        private static Rectangle? FindContentBounds(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = Math.Abs(data.Stride);
                var bytes = new byte[stride * bitmap.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var minX = bitmap.Width;
                var minY = bitmap.Height;
                var maxX = -1;
                var maxY = -1;

                for (var y = 0; y < bitmap.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        if (bytes[row + (x * 4)] >= 128)
                        {
                            continue;
                        }

                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }

                if (maxX < minX || maxY < minY)
                {
                    return null;
                }

                const int margin = 4;
                minX = Math.Max(0, minX - margin);
                minY = Math.Max(0, minY - margin);
                maxX = Math.Min(bitmap.Width - 1, maxX + margin);
                maxY = Math.Min(bitmap.Height - 1, maxY + margin);
                var width = maxX - minX + 1;
                var height = maxY - minY + 1;
                return width * height >= bitmap.Width * bitmap.Height * 0.8
                    ? null
                    : new Rectangle(minX, minY, width, height);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static List<Rectangle> DetectOcrRows(Bitmap bitmap, Rectangle? contentBounds)
        {
            var scan = contentBounds ?? new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(scan, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = Math.Abs(data.Stride);
                var bytes = new byte[stride * scan.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var widthThreshold = Math.Max(2, (int)(scan.Width * 0.03f));
                var blackCounts = new int[scan.Height];

                for (var y = 0; y < scan.Height; y++)
                {
                    var row = y * stride;
                    var count = 0;
                    for (var x = 0; x < scan.Width; x++)
                    {
                        if (bytes[row + (x * 4)] < 128)
                        {
                            count++;
                        }
                    }

                    blackCounts[y] = count >= widthThreshold ? count : 0;
                }

                var regions = new List<(int Start, int End)>();
                var inRow = false;
                var start = 0;
                for (var y = 0; y < blackCounts.Length; y++)
                {
                    if (blackCounts[y] > 0)
                    {
                        if (!inRow)
                        {
                            start = y;
                            inRow = true;
                        }
                    }
                    else if (inRow)
                    {
                        if (y - start >= 4)
                        {
                            regions.Add((start, y - 1));
                        }

                        inRow = false;
                    }
                }

                if (inRow && blackCounts.Length - start >= 4)
                {
                    regions.Add((start, blackCounts.Length - 1));
                }

                if (regions.Count == 0)
                {
                    return [];
                }

                var merged = new List<(int Start, int End)> { regions[0] };
                for (var i = 1; i < regions.Count; i++)
                {
                    var previous = merged[^1];
                    if (regions[i].Start - previous.End <= 10)
                    {
                        merged[^1] = (previous.Start, regions[i].End);
                    }
                    else
                    {
                        merged.Add(regions[i]);
                    }
                }

                return merged
                    .Select(row =>
                    {
                        var y = scan.Y + Math.Max(0, row.Start - 4);
                        var bottom = scan.Y + Math.Min(scan.Height - 1, row.End + 4);
                        return new Rectangle(scan.X, y, scan.Width, Math.Max(1, bottom - y + 1));
                    })
                    .ToList();
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static Bitmap AddWhiteBorder(Bitmap source, int borderPx)
        {
            var border = Math.Clamp(borderPx, 0, 8);
            if (border == 0)
            {
                return (Bitmap)source.Clone();
            }

            var result = new Bitmap(source.Width + (border * 2), source.Height + (border * 2), PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(result);
            graphics.Clear(Color.White);
            graphics.DrawImage(source, border, border, source.Width, source.Height);
            return result;
        }

        private void ApplyOcrPreprocessing(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                var stride = Math.Abs(data.Stride);
                var bytes = new byte[stride * bitmap.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var threshold = Math.Clamp(this.Settings.OcrThresholdValue, 0, 255);
                var contrast = Math.Clamp(this.Settings.OcrContrast, 0.5f, 3f);
                var width = bitmap.Width;
                var height = bitmap.Height;
                var luminance = new byte[width * height];
                var keep = new byte[width * height];

                for (var y = 0; y < height; y++)
                {
                    var row = y * stride;
                    var lumRow = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var idx = row + (x * 4);
                        var b = bytes[idx];
                        var g = bytes[idx + 1];
                        var r = bytes[idx + 2];
                        var gray = (byte)(((77 * r) + (150 * g) + (29 * b)) >> 8);
                        luminance[lumRow + x] = gray;

                        var max = Math.Max(r, Math.Max(g, b));
                        var min = Math.Min(r, Math.Min(g, b));
                        var isLikelyText = gray <= threshold + 35 && max - min <= 95;
                        if (!isLikelyText)
                        {
                            continue;
                        }

                        var y0 = Math.Max(0, y - 5);
                        var y1 = Math.Min(height - 1, y + 5);
                        var x0 = Math.Max(0, x - 5);
                        var x1 = Math.Min(width - 1, x + 5);
                        for (var ny = y0; ny <= y1; ny++)
                        {
                            var keepRow = ny * width;
                            for (var nx = x0; nx <= x1; nx++)
                            {
                                keep[keepRow + nx] = 1;
                            }
                        }
                    }
                }

                var integral = BuildIntegralImage(luminance, width, height);
                var binary = new byte[width * height];
                var kernelRadius = Math.Clamp(Math.Min(width, height) / 40, 4, 10);
                var contrastBias = Math.Clamp((threshold - 100) / 6, 6, 20);
                for (var y = 0; y < height; y++)
                {
                    var offset = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = offset + x;
                        if (keep[pixel] == 0)
                        {
                            binary[pixel] = 255;
                            continue;
                        }

                        var mean = GetLocalMean(integral, width, height, x, y, kernelRadius);
                        var gray = Math.Clamp((int)(((luminance[pixel] - 128) * contrast) + 128), 0, 255);
                        binary[pixel] = this.Settings.OcrThreshold && gray + contrastBias < mean
                            ? (byte)0
                            : (byte)255;
                    }
                }

                RemoveIsolatedBlackNoise(binary, width, height);

                for (var y = 0; y < height; y++)
                {
                    var row = y * stride;
                    var offset = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var value = this.Settings.OcrGrayscale || this.Settings.OcrThreshold
                            ? binary[offset + x]
                            : luminance[offset + x];
                        var idx = row + (x * 4);
                        bytes[idx] = value;
                        bytes[idx + 1] = value;
                        bytes[idx + 2] = value;
                        bytes[idx + 3] = 255;
                    }
                }

                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static int[] BuildIntegralImage(byte[] pixels, int width, int height)
        {
            var integralWidth = width + 1;
            var integral = new int[integralWidth * (height + 1)];
            for (var y = 1; y <= height; y++)
            {
                var rowSum = 0;
                var src = (y - 1) * width;
                var dst = y * integralWidth;
                var previous = (y - 1) * integralWidth;
                for (var x = 1; x <= width; x++)
                {
                    rowSum += pixels[src + x - 1];
                    integral[dst + x] = integral[previous + x] + rowSum;
                }
            }

            return integral;
        }

        private static int GetLocalMean(int[] integral, int width, int height, int x, int y, int radius)
        {
            var integralWidth = width + 1;
            var x0 = Math.Max(0, x - radius);
            var y0 = Math.Max(0, y - radius);
            var x1 = Math.Min(width - 1, x + radius);
            var y1 = Math.Min(height - 1, y + radius);
            var area = Math.Max(1, (x1 - x0 + 1) * (y1 - y0 + 1));
            var topLeft = integral[(y0 * integralWidth) + x0];
            var topRight = integral[(y0 * integralWidth) + x1 + 1];
            var bottomLeft = integral[((y1 + 1) * integralWidth) + x0];
            var bottomRight = integral[((y1 + 1) * integralWidth) + x1 + 1];
            return (bottomRight - bottomLeft - topRight + topLeft) / area;
        }

        private static void RemoveIsolatedBlackNoise(byte[] pixels, int width, int height)
        {
            var source = (byte[])pixels.Clone();
            for (var y = 1; y < height - 1; y++)
            {
                var offset = y * width;
                for (var x = 1; x < width - 1; x++)
                {
                    var idx = offset + x;
                    if (source[idx] != 0)
                    {
                        continue;
                    }

                    var blackCount = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var row = (y + dy) * width;
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (source[row + x + dx] == 0)
                            {
                                blackCount++;
                            }
                        }
                    }

                    if (blackCount <= 2)
                    {
                        pixels[idx] = 255;
                    }
                }
            }
        }

        private static long ComputeFrameHash(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data;
            try
            {
                data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            }
            catch
            {
                return 0;
            }

            try
            {
                var stride = Math.Abs(data.Stride);
                var bytes = new byte[stride * bitmap.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                unchecked
                {
                    long hash = 1469598103934665603;
                    const long prime = 1099511628211;
                    const int stepX = 8;
                    const int stepY = 4;
                    for (var y = 0; y < bitmap.Height; y += stepY)
                    {
                        var row = y * stride;
                        for (var x = 0; x < bitmap.Width; x += stepX)
                        {
                            var idx = row + (x * 4);
                            hash = (hash ^ bytes[idx]) * prime;
                            hash = (hash ^ bytes[idx + 1]) * prime;
                            hash = (hash ^ bytes[idx + 2]) * prime;
                        }
                    }

                    return hash;
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private string CleanOcrText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            return string.Join(
                '\n',
                CleanOcrLines(raw.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => new OcrLine(line, Vector2.Zero)))
                    .Select(line => line.Text));
        }

        private static List<OcrLine> CleanOcrLines(IEnumerable<OcrLine> lines)
        {
            return lines
                .Select(line => line with { Text = NormalizeOcrLine(Regex.Replace(line.Text.Trim(), "\\s+", " ")) })
                .Where(line => line.Text.Length > 1 && line.Text.Any(char.IsLetter) && !IsPriceHintLine(line.Text))
                .ToList();
        }

        private static bool IsPriceHintLine(string line)
        {
            return PriceHintLine.IsMatch(line) &&
                !Regex.IsMatch(line, "[A-Za-z]{3,}\\s+[A-Za-z]{3,}", RegexOptions.IgnoreCase);
        }

        private void DrawOcrBounds()
        {
            var game = Core.Process.WindowArea;
            if (game.Width <= 0 || game.Height <= 0)
            {
                return;
            }

            var p1 = new Vector2(game.Left + this.Settings.OcrOffsetX, game.Top + this.Settings.OcrOffsetY);
            var p2 = p1 + new Vector2(this.Settings.OcrWidth, this.Settings.OcrHeight);
            var drawList = ImGui.GetForegroundDrawList();
            drawList.AddRect(p1, p2, ImGui.GetColorU32(new Vector4(0.25f, 0.95f, 0.72f, 0.95f)), 0f, ImDrawFlags.None, 2f);
            var inset = Math.Clamp(this.Settings.OcrTextInsetLeft, 0, Math.Max(0, this.Settings.OcrWidth - 32));
            var textP1 = p1 + new Vector2(inset, 0f);
            drawList.AddLine(textP1, new Vector2(textP1.X, p2.Y), ImGui.GetColorU32(new Vector4(1f, 0.79f, 0.22f, 0.95f)), 2f);
        }

        private void SetDefaultOcrRegion()
        {
            var game = Core.Process.WindowArea;
            if (game.Width <= 0 || game.Height <= 0)
            {
                return;
            }

            this.Settings.OcrWidth = Math.Clamp((int)(game.Width * 0.28f), 280, 620);
            this.Settings.OcrHeight = Math.Clamp((int)(game.Height * 0.58f), 320, 780);
            this.Settings.OcrOffsetX = Math.Max(0, game.Width - this.Settings.OcrWidth - 48);
            this.Settings.OcrOffsetY = Math.Max(0, (int)(game.Height * 0.18f));
            this.Settings.OcrTextInsetLeft = Math.Clamp((int)(this.Settings.OcrWidth * 0.35f), 120, 260);
        }

        private async Task RefreshPricesAsync(CancellationToken ct)
        {
            var shortLeague = await this.ResolveShortLeagueAsync(this.Settings.League, ct).ConfigureAwait(false);
            var nextPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var categories = new[] { "currency", "expedition", "uncutgems", "runes", "verisium" };
            foreach (var category in categories)
            {
                var url = $"{BaseUrl}/Leagues/{shortLeague}/Currencies/ByCategory?Category={category}&ReferenceCurrency=chaos&SmoothingDays=1";
                foreach (var item in await this.FetchAllPagesAsync(url, ct).ConfigureAwait(false))
                {
                    var name = GetString(item, "Text");
                    var chaos = GetDecimal(item, "CurrentPrice");
                    if (!string.IsNullOrWhiteSpace(name) && chaos > 0m)
                    {
                        nextPrices[Normalize(name)] = chaos;
                    }
                }
            }

            lock (this.prices)
            {
                this.prices.Clear();
                foreach (var pair in nextPrices)
                {
                    this.prices[pair.Key] = pair.Value;
                }

                this.RebuildFallbackPrices();
            }

            this.lastRefreshUtc = DateTimeOffset.UtcNow;
            this.status = $"Loaded {nextPrices.Count} prices from poe2scout.";
            this.manualItemsDirty = true;
        }

        private async Task<string> ResolveShortLeagueAsync(string fullName, CancellationToken ct)
        {
            try
            {
                foreach (var item in await this.FetchJsonArrayAsync($"{BaseUrl}/Leagues", ct).ConfigureAwait(false))
                {
                    if (string.Equals(GetString(item, "Value"), fullName, StringComparison.OrdinalIgnoreCase))
                    {
                        return GetString(item, "ShortName") ?? NormalizeLeagueName(fullName);
                    }
                }
            }
            catch
            {
            }

            return NormalizeLeagueName(fullName);
        }

        private async Task<List<JsonElement>> FetchAllPagesAsync(string baseUrl, CancellationToken ct)
        {
            const int perPage = 200;
            var allItems = new List<JsonElement>();
            for (var page = 1; page <= 50; page++)
            {
                var separator = baseUrl.Contains('?') ? "&" : "?";
                var pageItems = await this.FetchJsonArrayAsync($"{baseUrl}{separator}Page={page}&PerPage={perPage}", ct).ConfigureAwait(false);
                allItems.AddRange(pageItems);
                if (pageItems.Count < perPage)
                {
                    break;
                }
            }

            return allItems;
        }

        private async Task<List<JsonElement>> FetchJsonArrayAsync(string url, CancellationToken ct)
        {
            using var response = await this.httpClient.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Items", out var itemsProperty)
                ? itemsProperty
                : root;

            var list = new List<JsonElement>();
            if (items.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in items.EnumerateArray())
            {
                list.Add(item.Clone());
            }

            return list;
        }

        private void RebuildRows()
        {
            this.rows.Clear();
            var useOcrMode = this.Settings.EnableOcr && this.Settings.UseOcrResults;
            var useOcrSource = useOcrMode && !string.IsNullOrWhiteSpace(this.ocrText);
            var sourceLines = useOcrSource
                ? this.ocrLines.Select(line => line.Text).ToArray()
                : useOcrMode
                    ? []
                : this.Settings.ManualItems.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            this.sourceLineCount = Math.Max(1, sourceLines.Length);

            for (var lineIndex = 0; lineIndex < sourceLines.Length; lineIndex++)
            {
                var line = sourceLines[lineIndex];
                var parsed = ParseItemLine(line.Trim());
                if (string.IsNullOrWhiteSpace(parsed.Name))
                {
                    continue;
                }

                var hintPosition = useOcrSource && lineIndex < this.ocrLines.Count
                    ? this.ocrLines[lineIndex].HintPosition
                    : Vector2.Zero;
                var quote = this.TryGetQuote(parsed.Name, parsed.Quantity, lineIndex, hintPosition);
                if (useOcrSource && !quote.Matched)
                {
                    continue;
                }

                this.rows.Add(quote);
            }

            this.manualItemsDirty = false;
        }

        private PriceRow TryGetQuote(string itemName, int quantity, int sourceLineIndex, Vector2 hintPosition)
        {
            var candidates = BuildLookupCandidates(itemName);
            lock (this.prices)
            {
                foreach (var candidate in candidates)
                {
                    if (this.prices.TryGetValue(candidate, out var chaos) ||
                        this.fallbackPrices.TryGetValue(candidate, out chaos))
                    {
                        var total = chaos * Math.Max(1, quantity);
                        return new PriceRow(itemName, itemName, quantity, FormatAmount(total), total, true, sourceLineIndex, hintPosition);
                    }
                }

                if (this.TryFindFuzzyQuote(candidates, quantity, sourceLineIndex, hintPosition, out var fuzzyRow))
                {
                    return fuzzyRow;
                }
            }

            return new PriceRow(itemName, itemName, quantity, "N/A", -1m, false, sourceLineIndex, hintPosition);
        }

        private bool TryFindFuzzyQuote(string[] candidates, int quantity, int sourceLineIndex, Vector2 hintPosition, out PriceRow row)
        {
            row = new PriceRow(string.Empty, string.Empty, quantity, "N/A", -1m, false, sourceLineIndex, hintPosition);
            if (candidates.Length == 0)
            {
                return false;
            }

            var bestName = string.Empty;
            var bestScore = int.MaxValue;
            var bestChaos = 0m;
            foreach (var candidate in candidates)
            {
                foreach (var pair in this.prices.Concat(this.fallbackPrices))
                {
                    if (!IsFuzzyCategoryCompatible(candidate, pair.Key))
                    {
                        continue;
                    }

                    var distance = LevenshteinDistance(candidate, pair.Key);
                    var allowed = Math.Max(2, Math.Min(8, pair.Key.Length / 4));
                    if (distance < bestScore && distance <= allowed)
                    {
                        bestScore = distance;
                        bestName = pair.Key;
                        bestChaos = pair.Value;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(bestName))
            {
                return false;
            }

            var total = bestChaos * Math.Max(1, quantity);
            row = new PriceRow(bestName, ToDisplayName(bestName), quantity, FormatAmount(total), total, true, sourceLineIndex, hintPosition);
            return true;
        }

        private static bool IsFuzzyCategoryCompatible(string candidate, string priceKey)
        {
            if (candidate.Contains(" RUNE OF ", StringComparison.OrdinalIgnoreCase))
            {
                return priceKey.Contains(" RUNE OF ", StringComparison.OrdinalIgnoreCase);
            }

            if (candidate.EndsWith(" RUNE", StringComparison.OrdinalIgnoreCase))
            {
                return priceKey.EndsWith(" RUNE", StringComparison.OrdinalIgnoreCase);
            }

            if (candidate.Contains(" ORB", StringComparison.OrdinalIgnoreCase))
            {
                return priceKey.Contains(" ORB", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private void RebuildFallbackPrices()
        {
            this.fallbackPrices.Clear();
            foreach (var pair in this.prices)
            {
                if (TryGetTierFallbackKey(pair.Key, out var fallback))
                {
                    this.fallbackPrices[fallback] = this.fallbackPrices.TryGetValue(fallback, out var existing)
                        ? Math.Min(existing, pair.Value)
                        : pair.Value;
                }
            }
        }

        private Vector4 GetPriceColor(decimal chaosValue)
        {
            if (chaosValue < 0m)
            {
                return new Vector4(1f, 0.28f, 0.28f, 1f);
            }

            var red = new Vector4(1f, 0.28f, 0.28f, 1f);
            var orange = new Vector4(1f, 0.77f, 0.21f, 1f);
            var green = new Vector4(0.35f, 1f, 0.48f, 1f);
            var redThreshold = (decimal)Math.Max(0f, this.Settings.RedThreshold);
            var orangeThreshold = (decimal)Math.Max(this.Settings.RedThreshold + 0.01f, this.Settings.OrangeThreshold);
            var greenThreshold = (decimal)Math.Max(this.Settings.OrangeThreshold + 0.01f, this.Settings.GreenThreshold);

            if (chaosValue <= redThreshold)
            {
                return red;
            }

            if (chaosValue < orangeThreshold)
            {
                return Lerp(red, orange, (float)((chaosValue - redThreshold) / (orangeThreshold - redThreshold)));
            }

            if (chaosValue < greenThreshold)
            {
                return Lerp(orange, green, (float)((chaosValue - orangeThreshold) / (greenThreshold - orangeThreshold)));
            }

            return green;
        }

        private string FormatAmount(decimal chaos)
        {
            var divine = this.prices.TryGetValue("DIVINE ORB", out var divineValue) ? divineValue : 150m;
            var exalted = this.prices.TryGetValue("EXALTED ORB", out var exaltedValue) ? exaltedValue : 0m;
            if (string.Equals(this.Settings.DisplayCurrency, "exalt", StringComparison.OrdinalIgnoreCase) && exalted > 0m)
            {
                if (divine > 0m && chaos >= divine)
                {
                    return $"{Truncate(chaos / divine):0.#}d";
                }

                return $"{Truncate(chaos / exalted):0.#}ex";
            }

            if (divine > 0m && chaos >= divine)
            {
                return $"{Truncate(chaos / divine):0.#}d";
            }

            return $"{Truncate(chaos):0.#}c";
        }

        private static (string Name, int Quantity) ParseItemLine(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return (string.Empty, 1);
            }

            var match = QuantityWithX.Match(raw);
            if (!match.Success)
            {
                match = QuantityWithoutX.Match(raw);
            }

            if (!match.Success)
            {
                return (raw, 1);
            }

            return (match.Groups["name"].Value.Trim(), NormalizeQuantity(match.Groups["qty"].Value));
        }

        private static int NormalizeQuantity(string raw)
        {
            if (int.TryParse(raw, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            return raw.Trim().ToLowerInvariant() switch
            {
                "o" or "0" => 2,
                _ => 1,
            };
        }

        private static string[] BuildLookupCandidates(string itemName)
        {
            var normalized = Normalize(itemName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return [];
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string value)
            {
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                {
                    result.Add(value);
                }
            }

            Add(normalized);
            var withoutLevel = TrailingLevel.Replace(normalized, string.Empty).Trim();
            Add(withoutLevel);
            Add(TrailingOrb.Replace(normalized, string.Empty).Trim());
            Add(TrailingOrb.Replace(withoutLevel, string.Empty).Trim());
            return [.. result];
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = NormalizeOcrLine(value).ToUpperInvariant();
            normalized = NonAlphaNumeric.Replace(normalized, " ").Trim();
            normalized = Regex.Replace(normalized, "\\s+", " ");
            normalized = Regex.Replace(normalized, "^(SKILL|SUPPORT)\\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
            normalized = normalized.Replace("0RB", "ORB", StringComparison.OrdinalIgnoreCase)
                .Replace("GRB", "ORB", StringComparison.OrdinalIgnoreCase)
                .Replace("PCRFCCT", "PERFECT", StringComparison.OrdinalIgnoreCase)
                .Replace("PFRFCCT", "PERFECT", StringComparison.OrdinalIgnoreCase)
                .Replace("PFRFECT", "PERFECT", StringComparison.OrdinalIgnoreCase)
                .Replace("RCGAL", "REGAL", StringComparison.OrdinalIgnoreCase)
                .Replace("RCGAT", "REGAL", StringComparison.OrdinalIgnoreCase)
                .Replace("RFGAL", "REGAL", StringComparison.OrdinalIgnoreCase)
                .Replace("SCRLC", "SERLE", StringComparison.OrdinalIgnoreCase)
                .Replace("SFRLC", "SERLE", StringComparison.OrdinalIgnoreCase)
                .Replace("MCDVCD", "MEDVED", StringComparison.OrdinalIgnoreCase)
                .Replace("MFDVFD", "MEDVED", StringComparison.OrdinalIgnoreCase)
                .Replace("TRIUNN PH", "TRIUMPH", StringComparison.OrdinalIgnoreCase)
                .Replace("1 RIUNN PH", "TRIUMPH", StringComparison.OrdinalIgnoreCase)
                .Replace("1RIUNN PH", "TRIUMPH", StringComparison.OrdinalIgnoreCase)
                .Replace("CRCATCR", "GREATER", StringComparison.OrdinalIgnoreCase)
                .Replace("CRCATFR", "GREATER", StringComparison.OrdinalIgnoreCase)
                .Replace("GRCATCR", "GREATER", StringComparison.OrdinalIgnoreCase)
                .Replace("GRCATER", "GREATER", StringComparison.OrdinalIgnoreCase)
                .Replace("UHTRCD", "UHTRED", StringComparison.OrdinalIgnoreCase)
                .Replace("UHTRFD", "UHTRED", StringComparison.OrdinalIgnoreCase)
                .Replace("RLLNE", "RUNE", StringComparison.OrdinalIgnoreCase)
                .Replace("RUNC", "RUNE", StringComparison.OrdinalIgnoreCase)
                .Replace("LCSSCR", "LESSER", StringComparison.OrdinalIgnoreCase)
                .Replace("LCSSER", "LESSER", StringComparison.OrdinalIgnoreCase)
                .Replace("LCSCR", "LESSER", StringComparison.OrdinalIgnoreCase)
                .Replace("LADY ILCESTRA", "LADY HESTRA", StringComparison.OrdinalIgnoreCase)
                .Replace("LADY ILFSTRA", "LADY HESTRA", StringComparison.OrdinalIgnoreCase)
                .Replace("HCSTRA", "HESTRA", StringComparison.OrdinalIgnoreCase)
                .Replace("WIN TCR", "WINTER", StringComparison.OrdinalIgnoreCase)
                .Replace("WINTCR", "WINTER", StringComparison.OrdinalIgnoreCase)
                .Replace("CRUCLTY", "CRUELTY", StringComparison.OrdinalIgnoreCase)
                .Replace("CRUCL TY", "CRUELTY", StringComparison.OrdinalIgnoreCase)
                .Replace("CRUCLT", "CRUELTY", StringComparison.OrdinalIgnoreCase)
                .Replace("CRUCL", "CRUELTY", StringComparison.OrdinalIgnoreCase)
                .Replace("STONC", "STONE", StringComparison.OrdinalIgnoreCase)
                .Replace("LNSPIRATION", "INSPIRATION", StringComparison.OrdinalIgnoreCase)
                .Replace("TRAN SNNUTATION", "TRANSMUTATION", StringComparison.OrdinalIgnoreCase)
                .Replace("TRANSNNUTATION", "TRANSMUTATION", StringComparison.OrdinalIgnoreCase)
                .Replace("TRANSMNNUTATION", "TRANSMUTATION", StringComparison.OrdinalIgnoreCase)
                .Replace("CEM", "GEM", StringComparison.OrdinalIgnoreCase);
            return normalized;
        }

        private static string NormalizeOcrLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var cleaned = value
                .Replace("\u201C", "'")
                .Replace("\u201D", "'")
                .Replace("\u2018", "'")
                .Replace("\u2019", "'")
                .Replace("`", "'")
                .Replace("\u0445", "x")
                .Replace("\u0425", "x");
            cleaned = Regex.Replace(cleaned, "^\\s*(?:Skill|Support)\\s*[:;.,\\-]\\s*", string.Empty, RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\b0rb\\b", "Orb", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bPcrfcct\\b", "Perfect", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bPfrfcct\\b", "Perfect", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bPfrfect\\b", "Perfect", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bRcgal\\b", "Regal", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bRcgat\\b", "Regal", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bRfgal\\b", "Regal", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bScrlc's\\b", "Serle's", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bSfrlc's\\b", "Serle's", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bMcdvcd's\\b", "Medved's", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bMfdvfd's\\b", "Medved's", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\b(?:1|l|I)?\\s*'?riunn\\s*ph\\b", "Triumph", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bCrcatcr\\b", "Greater", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bCrcatfr\\b", "Greater", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bGrcatcr\\b", "Greater", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bUhtrcd\\b", "Uhtred", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bRllne\\b", "Rune", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bRunc\\b", "Rune", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bLcsscr\\b", "Lesser", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bLcsser\\b", "Lesser", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bLcscr\\b", "Lesser", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bHcstra's\\b", "Hestra's", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bWintcr\\b", "Winter", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bCruclty\\b", "Cruelty", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bCruclt\\b", "Cruelty", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bCrucl\\b", "Cruelty", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bStonc\\b", "Stone", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\blnspjration\\b", "Inspiration", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\blnspiration\\b", "Inspiration", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bInspjration\\b", "Inspiration", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bTransnnutation\\b", "Transmutation", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bTransmnnutation\\b", "Transmutation", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bIllransnn\\s*utation\\b", "Transmutation", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bIrransnn\\s*utation\\b", "Transmutation", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\brransnn\\s*utation\\b", "Transmutation", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bLady\\s+Ilcestra", "Lady Hestra", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bLady\\s+Ilfstra", "Lady Hestra", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bCourtesan\\s+[^A-Za-z]{0,4}[Xx][^A-Za-z]{0,4}1?annan's", "Courtesan Mannan's", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\bCourtesan\\s+1?annan's", "Courtesan Mannan's", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
            return cleaned;
        }

        private static int LevenshteinDistance(string left, string right)
        {
            if (left.Length == 0)
            {
                return right.Length;
            }

            if (right.Length == 0)
            {
                return left.Length;
            }

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (var j = 0; j <= right.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= right.Length; j++)
                {
                    var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[right.Length];
        }

        private static string ToDisplayName(string normalized)
        {
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
        }

        private static bool TryGetTierFallbackKey(string normalizedItemName, out string fallbackKey)
        {
            fallbackKey = string.Empty;
            if (normalizedItemName.StartsWith("GREATER ORB OF ", StringComparison.OrdinalIgnoreCase) ||
                normalizedItemName.StartsWith("PERFECT ORB OF ", StringComparison.OrdinalIgnoreCase))
            {
                fallbackKey = normalizedItemName[(normalizedItemName.IndexOf("ORB OF ", StringComparison.OrdinalIgnoreCase))..];
                return true;
            }

            if ((normalizedItemName.StartsWith("GREATER ", StringComparison.OrdinalIgnoreCase) ||
                 normalizedItemName.StartsWith("PERFECT ", StringComparison.OrdinalIgnoreCase)) &&
                normalizedItemName.EndsWith(" RUNE", StringComparison.OrdinalIgnoreCase))
            {
                fallbackKey = normalizedItemName[(normalizedItemName.IndexOf(' ') + 1)..];
                return true;
            }

            return false;
        }

        private static string NormalizeLeagueName(string league)
        {
            var parts = league.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? league.ToLowerInvariant() : parts[0].ToLowerInvariant();
        }

        private static string? GetString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static decimal GetDecimal(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDecimal()
                : 0m;
        }

        private static decimal Truncate(decimal value)
        {
            return Math.Truncate(value * 10m) / 10m;
        }

        private static Vector4 Lerp(Vector4 a, Vector4 b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return a + ((b - a) * t);
        }
    }
}
#pragma warning restore CA1416
