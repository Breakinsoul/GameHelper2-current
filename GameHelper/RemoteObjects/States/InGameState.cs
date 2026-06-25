// <copyright file="InGameState.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.RemoteObjects.States
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text;
    using Coroutine;
    using CoroutineEvents;
    using GameHelper.Cache;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.UiElement;
    using GameOffsets.Objects.States;
    using GameOffsets.Objects.UiElement;
    using InGameStateObjects;
    using GameHelper.Utils;

    /// <summary>
    ///     Reads InGameState Game Object.
    /// </summary>
    public class InGameState : RemoteObjectBase
    {
        private IntPtr uiRootAddress;
        private IntPtr gameUiAddress;
        private IntPtr gameUiControllerAddress;
        private IntPtr atlasGraphAddress;
        private readonly UiElementParents atlasGraphCache;
        private readonly Dictionary<long, string> atlasTooltipTextCache = [];

        /// <summary>
        ///     Initializes a new instance of the <see cref="InGameState" /> class.
        /// </summary>
        /// <param name="address">address of the remote memory object.</param>
        internal InGameState(IntPtr address)
            : base(address)
        {
            this.atlasGraphCache = new(null, GameStateTypes.InGameState, GameStateTypes.EscapeState, "AtlasGraph");
            Core.CoroutinesRegistrar.Add(CoroutineHandler.Start(
                this.OnPerFrame(), "[InGameState] Update Game State", int.MaxValue - 2));
        }

        /// <summary>
        ///     Gets the data related to the currently loaded world area.
        /// </summary>
        public WorldData CurrentWorldInstance { get; } = new(IntPtr.Zero);

        /// <summary>
        ///     Gets the data related to the current area instance.
        /// </summary>
        public AreaInstance CurrentAreaInstance { get; } = new(IntPtr.Zero);

        /// <summary>
        ///     Gets the UiRoot main child which contains all the UiElements of the game.
        /// </summary>
        public ImportantUiElements GameUi { get; } = new(IntPtr.Zero);

        /// <summary>
        ///     Dumps the UI tree from UiRoot. Used to discover new panels/offsets.
        /// </summary>
        /// <param name="path">Output file path.</param>
        /// <param name="maxDepth">Maximum recursion depth.</param>
        /// <param name="maxNodes">Maximum nodes to write.</param>
        /// <returns>Summary string.</returns>
        public string DumpVisibleUiTree(string path, int maxDepth = 12, int maxNodes = 5000)
        {
            if (this.uiRootAddress == IntPtr.Zero)
            {
                return "UiRoot is zero.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var cache = new UiElementParents(null, GameStateTypes.InGameState, GameStateTypes.EscapeState, "AtlasProbe");
            var builder = new StringBuilder();
            var count = 0;
            DumpUiRoot("UiManagerPtr", this.GameUi.Address, cache, builder, maxDepth, maxNodes, ref count);
            DumpUiRoot("UiRootPtr", this.uiRootAddress, cache, builder, maxDepth, maxNodes, ref count);
            DumpUiRoot("GameUiPtr", this.gameUiAddress, cache, builder, maxDepth, maxNodes, ref count);
            DumpUiRoot("GameUiControllerPtr", this.gameUiControllerAddress, cache, builder, maxDepth, maxNodes, ref count);
            File.WriteAllText(path, builder.ToString());
            return $"Dumped {count} UI nodes to {path}.";
        }

        public string ProbeAtlasUiElementPointers(IntPtr address, string label)
        {
            var builder = new StringBuilder();
            if (address == IntPtr.Zero)
            {
                return string.Empty;
            }

            var reader = Core.Process.Handle;
            builder.AppendLine();
            builder.AppendLine($"{label} pointer probe:");
            for (var offset = 0; offset <= 0x500; offset += 0x8)
            {
                var pointer = reader.ReadMemory<IntPtr>(address + offset);
                if (!LooksLikePointer(pointer))
                {
                    continue;
                }

                var ascii = ReadLikelyAscii(reader, pointer);
                var utf16 = ReadLikelyUtf16(reader, pointer);
                var stdString = reader.ReadStdString(reader.ReadMemory<GameOffsets.Natives.StdString>(address + offset));
                var stdWString = reader.ReadStdWString(reader.ReadMemory<GameOffsets.Natives.StdWString>(address + offset));
                if (string.IsNullOrWhiteSpace(ascii) && string.IsNullOrWhiteSpace(utf16))
                {
                    if (!string.IsNullOrWhiteSpace(stdString) || !string.IsNullOrWhiteSpace(stdWString))
                    {
                        builder
                            .Append("+0x").Append(offset.ToString("X3"))
                            .Append(" std");
                        if (!string.IsNullOrWhiteSpace(stdString))
                        {
                            builder.Append(" string=\"").Append(stdString.Replace("\"", "'")).Append('"');
                        }

                        if (!string.IsNullOrWhiteSpace(stdWString))
                        {
                            builder.Append(" wstring=\"").Append(stdWString.Replace("\"", "'")).Append('"');
                        }

                        builder.AppendLine();
                    }

                    AppendNestedPointerProbe(builder, reader, offset, pointer);
                    continue;
                }

                builder
                    .Append("+0x").Append(offset.ToString("X3"))
                    .Append(" -> 0x").Append(pointer.ToInt64().ToString("X"));
                if (!string.IsNullOrWhiteSpace(ascii))
                {
                    builder.Append(" ascii=\"").Append(ascii.Replace("\"", "'")).Append('"');
                }

                if (!string.IsNullOrWhiteSpace(utf16))
                {
                    builder.Append(" utf16=\"").Append(utf16.Replace("\"", "'")).Append('"');
                }

                builder.AppendLine();
                AppendNestedPointerProbe(builder, reader, offset, pointer);
            }

            return builder.ToString();
        }

        /// <summary>
        ///     Reads the live endgame atlas graph UI from the root UI manager.
        /// </summary>
        /// <remarks>
        ///     The atlas graph is not exposed through the legacy LargeMap/SkillTree helpers.
        ///     In the current client it is a large direct UI subtree under UiRootStructPtr.
        /// </remarks>
        public IReadOnlyList<AtlasUiGraphItem> ReadAtlasUiGraphItems(int maxNodes = 35000, bool readTooltips = false)
        {
            if (this.GameUi.Address == IntPtr.Zero)
            {
                return Array.Empty<AtlasUiGraphItem>();
            }

            try
            {
                var reader = Core.Process.Handle;
                var atlas = this.TryReadCachedAtlasGraph(reader);
                if (atlas.Address == IntPtr.Zero)
                {
                    var nodes = CollectUiNodes(this.GameUi.Address, reader, maxDepth: 8, maxNodes);
                    atlas = nodes
                        .Where(n => n.Children.Length >= 200 && n.Size.X >= 1200f && n.Size.Y >= 800f)
                        .Select(n => (Node: n, MapNodeCount: CountDirectAtlasMapNodes(n, reader)))
                        .Where(n => n.MapNodeCount > 0)
                        .OrderByDescending(n => n.MapNodeCount)
                        .ThenByDescending(n => n.Node.Children.Length)
                        .Select(n => n.Node)
                        .FirstOrDefault();
                    this.atlasGraphAddress = atlas.Address;
                }

                if (atlas.Address == IntPtr.Zero)
                {
                    return Array.Empty<AtlasUiGraphItem>();
                }

                var items = new List<AtlasUiGraphItem>(atlas.Children.Length);
                foreach (var childAddress in atlas.Children)
                {
                    if (childAddress == IntPtr.Zero)
                    {
                        continue;
                    }

                    var child = reader.ReadMemory<UiElementBaseOffset>(childAddress);
                    if (!LooksLikeUiElement(childAddress, child))
                    {
                        continue;
                    }

                    var children = reader.ReadStdVector<IntPtr>(child.ChildrensPtr);
                    if (children.Length != 1)
                    {
                        continue;
                    }

                    var kind = ClassifyAtlasGraphItem(child);
                    if (kind == AtlasUiGraphItemKind.Unknown)
                    {
                        continue;
                    }

                    var childContentAddress = children[0];
                    var rawX = reader.ReadMemory<int>(childAddress + 0x320);
                    var rawY = reader.ReadMemory<int>(childAddress + 0x324);
                    var rawKey = reader.ReadMemory<long>(childAddress + 0x328);
                    var rawState = reader.ReadMemory<int>(childAddress + 0x338);
                    var tooltipText = readTooltips
                        ? this.ReadCachedAtlasNodeTooltipText(reader, childAddress, childContentAddress, rawKey)
                        : string.Empty;
                    var (mapName, biomeName) = ParseAtlasNodeTooltipText(tooltipText);
                    var isVisible = UiElementBaseFuncs.IsVisibleChecker(child.Flags);
                    var isAccessible = isVisible && (rawState & 0x100) != 0;
                    var screenPosition = new Vector2(child.RelativePosition.X, child.RelativePosition.Y);
                    var screenSize = new Vector2(child.UnscaledSize.X, child.UnscaledSize.Y);
                    try
                    {
                        var uiElement = new UiElementBase(childAddress, this.atlasGraphCache);
                        screenPosition = uiElement.Position;
                        screenSize = uiElement.Size;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[InGameState.ReadAtlasUiGraphItems] screen position failed 0x{childAddress.ToInt64():X}: {ex.Message}");
                    }

                    items.Add(new AtlasUiGraphItem(
                        StableAtlasNodeId(rawX, rawY, rawKey),
                        kind,
                        childAddress,
                        childContentAddress,
                        new Vector2(child.RelativePosition.X, child.RelativePosition.Y),
                        screenPosition,
                        new Vector2(child.UnscaledSize.X, child.UnscaledSize.Y),
                        screenSize,
                        isVisible,
                        isAccessible,
                        rawX,
                        rawY,
                        rawState,
                        rawKey,
                        mapName,
                        biomeName,
                        tooltipText));
                }

                return items;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InGameState.ReadAtlasUiGraphItems] {ex.Message}");
                return Array.Empty<AtlasUiGraphItem>();
            }
        }

        /// <inheritdoc />
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGuiHelper.IntPtrToImGui("UiRoot", this.uiRootAddress);
            ImGuiHelper.IntPtrToImGui("GameUi", this.gameUiAddress);
            ImGuiHelper.IntPtrToImGui("GameUiController", this.gameUiControllerAddress);
        }

        /// <inheritdoc />
        protected override void CleanUpData()
        {
            this.CurrentAreaInstance.Address = IntPtr.Zero;
            this.uiRootAddress = IntPtr.Zero;
            this.gameUiAddress = IntPtr.Zero;
            this.gameUiControllerAddress = IntPtr.Zero;
            this.atlasGraphAddress = IntPtr.Zero;
            this.atlasTooltipTextCache.Clear();
            this.atlasGraphCache.Clear();
            this.GameUi.Address = IntPtr.Zero;
            this.CurrentWorldInstance.Address = IntPtr.Zero;
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            var data = reader.ReadMemory<InGameStateOffset>(this.Address);
            this.CurrentAreaInstance.Address = data.AreaInstanceData;
            this.CurrentWorldInstance.Address = data.WorldData;

            Core.GHSettings.EnableControllerMode = data.UiRootStructPtr == IntPtr.Zero;
            var uiManagerPtr = Core.GHSettings.EnableControllerMode
                ? data.GamepadUiRootStructPtr
                : data.UiRootStructPtr;

            var uiRootStruct = reader.ReadMemory<UiRootStruct>(uiManagerPtr);
            this.uiRootAddress = uiRootStruct.UiRootPtr;
            this.gameUiAddress = uiRootStruct.GameUiPtr;
            this.gameUiControllerAddress = uiRootStruct.GameUiControllerPtr;
            if (this.GameUi.Address != uiManagerPtr)
            {
                this.atlasGraphAddress = IntPtr.Zero;
                this.atlasTooltipTextCache.Clear();
            }

            this.GameUi.Address = uiManagerPtr;
        }

        private AtlasUiNodeCandidate TryReadCachedAtlasGraph(Utils.SafeMemoryHandle reader)
        {
            if (this.atlasGraphAddress == IntPtr.Zero)
            {
                return default;
            }

            var ui = reader.ReadMemory<UiElementBaseOffset>(this.atlasGraphAddress);
            if (!LooksLikeUiElement(this.atlasGraphAddress, ui))
            {
                this.atlasGraphAddress = IntPtr.Zero;
                return default;
            }

            var children = reader.ReadStdVector<IntPtr>(ui.ChildrensPtr);
            if (children.Length < 200)
            {
                this.atlasGraphAddress = IntPtr.Zero;
                return default;
            }

            return new AtlasUiNodeCandidate(
                this.atlasGraphAddress,
                children,
                new Vector2(ui.RelativePosition.X, ui.RelativePosition.Y),
                new Vector2(ui.UnscaledSize.X, ui.UnscaledSize.Y));
        }

        private static void DumpUiRoot(
            string label,
            IntPtr address,
            UiElementParents cache,
            StringBuilder builder,
            int maxDepth,
            int maxNodes,
            ref int count)
        {
            builder.AppendLine($"[{label}] 0x{address.ToInt64():X}");
            if (address == IntPtr.Zero || count >= maxNodes)
            {
                return;
            }

            try
            {
                var root = new UiElementBase(address, cache);
                DumpUiElement(root, builder, 0, maxDepth, maxNodes, ref count);
            }
            catch (Exception ex)
            {
                builder.AppendLine($"  dump failed: {ex.Message}");
            }
        }

        private static void DumpUiElement(UiElementBase element, StringBuilder builder, int depth, int maxDepth, int maxNodes, ref int count)
        {
            if (count >= maxNodes || depth > maxDepth || element.Address == IntPtr.Zero)
            {
                return;
            }

            count++;
            var indent = new string(' ', depth * 2);
            var pos = element.Position;
            var size = element.Size;
            var isVisible = element.IsVisible;
            builder.Append(indent)
                .Append("addr=0x").Append(element.Address.ToInt64().ToString("X"))
                .Append(" depth=").Append(depth)
                .Append(" visible=").Append(isVisible ? "true" : "false")
                .Append(" children=").Append(element.TotalChildrens)
                .Append(" pos=(").Append(pos.X.ToString("0.0")).Append(',').Append(pos.Y.ToString("0.0")).Append(')')
                .Append(" size=(").Append(size.X.ToString("0.0")).Append(',').Append(size.Y.ToString("0.0")).Append(')')
                .Append(" stringId=\"").Append(element.StringId.Replace("\"", "'")).Append('"')
                .AppendLine();

            for (var i = 0; i < element.TotalChildrens; i++)
            {
                var child = element[i];
                if (child == null)
                {
                    continue;
                }

                DumpUiElement(child, builder, depth + 1, maxDepth, maxNodes, ref count);
            }
        }

        private static List<AtlasUiNodeCandidate> CollectUiNodes(IntPtr root, Utils.SafeMemoryHandle reader, int maxDepth, int maxNodes)
        {
            var output = new List<AtlasUiNodeCandidate>();
            var visited = new HashSet<IntPtr>();
            CollectUiNode(root, reader, visited, output, depth: 0, maxDepth, maxNodes);
            return output;
        }

        private static void CollectUiNode(
            IntPtr address,
            Utils.SafeMemoryHandle reader,
            HashSet<IntPtr> visited,
            List<AtlasUiNodeCandidate> output,
            int depth,
            int maxDepth,
            int maxNodes)
        {
            if (address == IntPtr.Zero || depth > maxDepth || output.Count >= maxNodes || !visited.Add(address))
            {
                return;
            }

            var ui = reader.ReadMemory<UiElementBaseOffset>(address);
            if (!LooksLikeUiElement(address, ui))
            {
                return;
            }

            var children = reader.ReadStdVector<IntPtr>(ui.ChildrensPtr);
            output.Add(new AtlasUiNodeCandidate(
                address,
                children,
                new Vector2(ui.RelativePosition.X, ui.RelativePosition.Y),
                new Vector2(ui.UnscaledSize.X, ui.UnscaledSize.Y)));

            foreach (var child in children)
            {
                CollectUiNode(child, reader, visited, output, depth + 1, maxDepth, maxNodes);
            }
        }

        private static bool LooksLikeUiElement(IntPtr address, UiElementBaseOffset ui)
        {
            if (address == IntPtr.Zero || ui.Vtable == IntPtr.Zero)
            {
                return false;
            }

            if (ui.Self != IntPtr.Zero && ui.Self != address)
            {
                return false;
            }

            if (float.IsNaN(ui.RelativePosition.X) || float.IsNaN(ui.RelativePosition.Y) ||
                float.IsNaN(ui.UnscaledSize.X) || float.IsNaN(ui.UnscaledSize.Y))
            {
                return false;
            }

            return MathF.Abs(ui.RelativePosition.X) <= 250000f &&
                MathF.Abs(ui.RelativePosition.Y) <= 250000f &&
                MathF.Abs(ui.UnscaledSize.X) <= 250000f &&
                MathF.Abs(ui.UnscaledSize.Y) <= 250000f;
        }

        private static bool LooksLikePointer(IntPtr address)
        {
            var value = address.ToInt64();
            return value > 0x10000 && value <= 0x7FFFFFFFFFFF;
        }

        private string ReadCachedAtlasNodeTooltipText(
            Utils.SafeMemoryHandle reader,
            IntPtr nodeAddress,
            IntPtr contentAddress,
            long rawKey)
        {
            var cacheKey = rawKey != 0 ? rawKey : nodeAddress.ToInt64();
            if (this.atlasTooltipTextCache.TryGetValue(cacheKey, out var cachedText))
            {
                return cachedText;
            }

            var text = ReadAtlasNodeTooltipText(reader, nodeAddress, contentAddress);
            if (!string.IsNullOrWhiteSpace(text))
            {
                this.atlasTooltipTextCache[cacheKey] = text;
            }

            if (this.atlasTooltipTextCache.Count > 2048)
            {
                this.atlasTooltipTextCache.Clear();
            }

            return text;
        }

        private static string ReadAtlasNodeTooltipText(Utils.SafeMemoryHandle reader, IntPtr nodeAddress, IntPtr contentAddress)
        {
            foreach (var text in EnumerateAtlasTooltipCandidates(reader, nodeAddress, contentAddress))
            {
                if (IsPlausibleAtlasTooltip(text))
                {
                    return NormalizeAtlasTooltipText(text);
                }
            }

            return string.Empty;
        }

        private static IEnumerable<string> EnumerateAtlasTooltipCandidates(
            Utils.SafeMemoryHandle reader,
            IntPtr nodeAddress,
            IntPtr contentAddress)
        {
            yield return ReadStdWStringAt(reader, nodeAddress + 0x2E0);

            yield return ReadNestedStdWString(reader, nodeAddress, 0x008, 0x2E0);
            yield return ReadNestedStdWString(reader, nodeAddress, 0x010, 0x2B8);
            yield return ReadNestedStdWString(reader, nodeAddress, 0x018, 0x2B0);
            yield return ReadNestedStdWString(reader, nodeAddress, 0x020, 0x298);
            yield return ReadNestedStdWString(reader, nodeAddress, 0x048, 0x280);
            yield return ReadNestedStdWString(reader, nodeAddress, 0x050, 0x278);
            yield return ReadNestedStdWString(reader, nodeAddress, 0x058, 0x260);
            yield return ReadNestedStdWString(reader, nodeAddress, 0x418, 0x2E0);

            yield return ReadNestedStdWString(reader, contentAddress, 0x0B8, 0x2E0);
        }

        private static string ReadNestedStdWString(Utils.SafeMemoryHandle reader, IntPtr baseAddress, int pointerOffset, int stringOffset)
        {
            if (!LooksLikePointer(baseAddress))
            {
                return string.Empty;
            }

            var pointer = reader.ReadMemory<IntPtr>(baseAddress + pointerOffset);
            if (!LooksLikePointer(pointer))
            {
                return string.Empty;
            }

            return ReadStdWStringAt(reader, pointer + stringOffset);
        }

        private static string ReadStdWStringAt(Utils.SafeMemoryHandle reader, IntPtr address)
        {
            if (!LooksLikePointer(address))
            {
                return string.Empty;
            }

            return reader.ReadStdWString(reader.ReadMemory<GameOffsets.Natives.StdWString>(address));
        }

        private static bool IsPlausibleAtlasTooltip(string text)
        {
            var lines = SplitAtlasTooltipLines(text);
            if (lines.Length == 0)
            {
                return false;
            }

            var firstLine = lines[0];
            if (firstLine.Length is < 2 or > 80 ||
                firstLine.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) ||
                firstLine.StartsWith("Art/", StringComparison.OrdinalIgnoreCase) ||
                firstLine.Contains("://", StringComparison.Ordinal) ||
                firstLine.Any(static c => char.IsControl(c)))
            {
                return false;
            }

            return firstLine.Any(static c => char.IsLetter(c)) &&
                firstLine.All(static c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '\'' or '-' or ',' or '.');
        }

        private static string NormalizeAtlasTooltipText(string text)
        {
            return string.Join(Environment.NewLine, SplitAtlasTooltipLines(text));
        }

        private static (string MapName, string BiomeName) ParseAtlasNodeTooltipText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return (string.Empty, string.Empty);
            }

            var lines = SplitAtlasTooltipLines(text);
            var mapName = lines.Length > 0 ? lines[0] : string.Empty;
            var biomeName = lines.Length > 1 ? lines[1] : string.Empty;
            return (mapName, biomeName);
        }

        private static string[] SplitAtlasTooltipLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            return text
                .Replace('\0', '\n')
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
        }

        private static string ReadLikelyAscii(Utils.SafeMemoryHandle reader, IntPtr address)
        {
            var buffer = reader.ReadMemoryArray<byte>(address, 160);
            var bytes = new List<byte>();
            foreach (var value in buffer)
            {
                if (value == 0)
                {
                    break;
                }

                if (value < 0x20 || value > 0x7E)
                {
                    return string.Empty;
                }

                bytes.Add(value);
            }

            return bytes.Count >= 3 ? Encoding.ASCII.GetString(bytes.ToArray()) : string.Empty;
        }

        private static string ReadLikelyUtf16(Utils.SafeMemoryHandle reader, IntPtr address)
        {
            var buffer = reader.ReadMemoryArray<byte>(address, 320);
            var bytes = new List<byte>();
            for (var i = 0; i + 1 < buffer.Length; i += 2)
            {
                if (buffer[i] == 0 && buffer[i + 1] == 0)
                {
                    break;
                }

                if (buffer[i + 1] != 0 || buffer[i] < 0x20 || buffer[i] > 0x7E)
                {
                    return string.Empty;
                }

                bytes.Add(buffer[i]);
            }

            return bytes.Count >= 3 ? Encoding.ASCII.GetString(bytes.ToArray()) : string.Empty;
        }

        private static void AppendNestedPointerProbe(
            StringBuilder builder,
            Utils.SafeMemoryHandle reader,
            int parentOffset,
            IntPtr parentPointer)
        {
            for (var nestedOffset = 0; nestedOffset <= 0x300; nestedOffset += 0x8)
            {
                var nestedPointer = reader.ReadMemory<IntPtr>(parentPointer + nestedOffset);
                if (!LooksLikePointer(nestedPointer))
                {
                    continue;
                }

                var ascii = ReadLikelyAscii(reader, nestedPointer);
                var utf16 = ReadLikelyUtf16(reader, nestedPointer);
                var stdString = reader.ReadStdString(reader.ReadMemory<GameOffsets.Natives.StdString>(parentPointer + nestedOffset));
                var stdWString = reader.ReadStdWString(reader.ReadMemory<GameOffsets.Natives.StdWString>(parentPointer + nestedOffset));
                if (string.IsNullOrWhiteSpace(ascii) &&
                    string.IsNullOrWhiteSpace(utf16) &&
                    string.IsNullOrWhiteSpace(stdString) &&
                    string.IsNullOrWhiteSpace(stdWString))
                {
                    continue;
                }

                builder
                    .Append("  +0x").Append(parentOffset.ToString("X3"))
                    .Append(":+0x").Append(nestedOffset.ToString("X3"))
                    .Append(" -> 0x").Append(nestedPointer.ToInt64().ToString("X"));
                if (!string.IsNullOrWhiteSpace(ascii))
                {
                    builder.Append(" ascii=\"").Append(ascii.Replace("\"", "'")).Append('"');
                }

                if (!string.IsNullOrWhiteSpace(utf16))
                {
                    builder.Append(" utf16=\"").Append(utf16.Replace("\"", "'")).Append('"');
                }

                if (!string.IsNullOrWhiteSpace(stdString))
                {
                    builder.Append(" string=\"").Append(stdString.Replace("\"", "'")).Append('"');
                }

                if (!string.IsNullOrWhiteSpace(stdWString))
                {
                    builder.Append(" wstring=\"").Append(stdWString.Replace("\"", "'")).Append('"');
                }

                builder.AppendLine();
            }
        }

        private static AtlasUiGraphItemKind ClassifyAtlasGraphItem(UiElementBaseOffset ui)
        {
            if (MathF.Abs(ui.UnscaledSize.X - 40f) < 0.01f && MathF.Abs(ui.UnscaledSize.Y - 40f) < 0.01f)
            {
                return AtlasUiGraphItemKind.MapNode;
            }

            if (ui.UnscaledSize.X >= 55f || ui.UnscaledSize.Y >= 55f)
            {
                return AtlasUiGraphItemKind.RouteOrBadge;
            }

            return AtlasUiGraphItemKind.Unknown;
        }

        private static int CountDirectAtlasMapNodes(AtlasUiNodeCandidate node, Utils.SafeMemoryHandle reader)
        {
            var count = 0;
            foreach (var childAddress in node.Children)
            {
                if (childAddress == IntPtr.Zero)
                {
                    continue;
                }

                var child = reader.ReadMemory<UiElementBaseOffset>(childAddress);
                if (!LooksLikeUiElement(childAddress, child))
                {
                    continue;
                }

                if (ClassifyAtlasGraphItem(child) == AtlasUiGraphItemKind.MapNode)
                {
                    count++;
                }
            }

            return count;
        }

        private static int StableAtlasNodeId(int rawX, int rawY, long rawKey)
        {
            return Math.Abs(HashCode.Combine(rawX, rawY, rawKey)) % 900000 + 100000;
        }

        private readonly record struct AtlasUiNodeCandidate(
            IntPtr Address,
            IntPtr[] Children,
            Vector2 Position,
            Vector2 Size);

        private IEnumerator<Wait> OnPerFrame()
        {
            // TODO optimization: convert this into OnAreaChange.
            while (true)
            {
                yield return new Wait(GameHelperEvents.PerFrameDataUpdate);
                try
                {
                    if (this.Address != IntPtr.Zero)
                    {
                        this.UpdateData(false);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[InGameState.OnPerFrame] {ex}");
                }
            }
        }
    }
}
