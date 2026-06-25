using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using GameOffsets;
using GameOffsets.Natives;
using GameOffsets.Objects;
using GameOffsets.Objects.States;
using GameOffsets.Objects.UiElement;

const long MaxUserModeAddress = 0x7FFFFFFFFFFF;
var outputDir = Path.Combine(AppContext.BaseDirectory, "dumps");
Directory.CreateDirectory(outputDir);

Console.WriteLine("AtlasProbe starting...");
var process = FindGameProcess();
if (process == null)
{
    Console.WriteLine("Game process not found.");
    return 2;
}

Console.WriteLine($"Process: {process.ProcessName} pid={process.Id} title=\"{process.MainWindowTitle}\"");
using var memory = new MemoryReader(process.Id);
if (!memory.IsOpen)
{
    Console.WriteLine("Failed to open process for VM read.");
    return 3;
}

var module = process.MainModule;
if (module == null)
{
    Console.WriteLine("Main module is not available.");
    return 4;
}

var baseAddress = module.BaseAddress;
var moduleSize = module.ModuleMemorySize;
Console.WriteLine($"Base=0x{baseAddress.ToInt64():X} size=0x{moduleSize:X}");

var gameStatesStatic = FindStaticAddress(memory, baseAddress, moduleSize, StaticOffsetsPatterns.Patterns.First(p => p.Name == "Game States"));
Console.WriteLine($"Game States static=0x{gameStatesStatic.ToInt64():X}");
var staticObj = memory.Read<GameStateStaticOffset>(gameStatesStatic);
Console.WriteLine($"GameState container=0x{staticObj.GameState.ToInt64():X}");
var gameState = memory.Read<GameStateOffset>(staticObj.GameState);
var inGameState = gameState.States[4].X;
Console.WriteLine($"InGameState=0x{inGameState.ToInt64():X}");
if (!IsValid(inGameState))
{
    Console.WriteLine("InGameState pointer is invalid. Open the game/atlas and retry.");
    return 5;
}

var inGame = memory.Read<InGameStateOffset>(inGameState);
Console.WriteLine($"UiRootStructPtr=0x{inGame.UiRootStructPtr.ToInt64():X}");
Console.WriteLine($"GamepadUiRootStructPtr=0x{inGame.GamepadUiRootStructPtr.ToInt64():X}");

var dumpPath = Path.Combine(outputDir, $"atlas-probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
using var writer = new StreamWriter(dumpPath, false, Encoding.UTF8);
writer.WriteLine($"AtlasProbe {DateTime.Now:O}");
writer.WriteLine($"Process {process.ProcessName} pid={process.Id}");
writer.WriteLine($"GameStatesStatic 0x{gameStatesStatic.ToInt64():X}");
writer.WriteLine($"InGameState 0x{inGameState.ToInt64():X}");
writer.WriteLine($"UiRootStructPtr 0x{inGame.UiRootStructPtr.ToInt64():X}");
writer.WriteLine($"GamepadUiRootStructPtr 0x{inGame.GamepadUiRootStructPtr.ToInt64():X}");

DumpUiRootStruct("UiRootStructPtr", inGame.UiRootStructPtr, memory, writer);
DumpUiRootStruct("GamepadUiRootStructPtr", inGame.GamepadUiRootStructPtr, memory, writer);
ScanUiRootStructPointers("UiRootStructPtr scan", inGame.UiRootStructPtr, memory, writer);
ScanUiRootStructPointers("GamepadUiRootStructPtr scan", inGame.GamepadUiRootStructPtr, memory, writer);
AnalyzeAtlasUi(inGame.UiRootStructPtr, memory, outputDir, writer);

Console.WriteLine($"Dump written: {dumpPath}");
return 0;

static Process? FindGameProcess()
{
    foreach (var process in Process.GetProcesses())
    {
        try
        {
            if (!GameProcessDetails.ProcessName.ContainsKey(process.ProcessName))
            {
                process.Dispose();
                continue;
            }

            if (!string.Equals(process.MainWindowTitle, "Path of Exile 2", StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                continue;
            }

            return process;
        }
        catch
        {
            process.Dispose();
        }
    }

    return null;
}

static IntPtr FindStaticAddress(MemoryReader memory, IntPtr baseAddress, int moduleSize, Pattern pattern)
{
    const int chunkSize = 1024 * 1024;
    var overlap = pattern.Data.Length;
    for (var offset = 0; offset < moduleSize; offset += chunkSize)
    {
        var readSize = Math.Min(chunkSize + overlap, moduleSize - offset);
        var bytes = memory.ReadBytes(baseAddress + offset, readSize);
        for (var i = 0; i <= bytes.Length - pattern.Data.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < pattern.Data.Length; j++)
            {
                if (pattern.Mask[j] && bytes[i + j] != pattern.Data[j])
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            var instr = baseAddress + offset + i + pattern.BytesToSkip;
            var rel = memory.Read<int>(instr);
            return instr + rel + 4;
        }
    }

    throw new InvalidOperationException($"Pattern not found: {pattern.Name}");
}

static void DumpUiRootStruct(string label, IntPtr uiRootStructPtr, MemoryReader memory, StreamWriter writer)
{
    writer.WriteLine();
    writer.WriteLine($"[{label}] 0x{uiRootStructPtr.ToInt64():X}");
    if (!IsValid(uiRootStructPtr))
    {
        return;
    }

    var root = memory.Read<UiRootStruct>(uiRootStructPtr);
    DumpUiTree("UiRootPtr", root.UiRootPtr, memory, writer);
    DumpUiTree("GameUiPtr", root.GameUiPtr, memory, writer);
    DumpUiTree("GameUiControllerPtr", root.GameUiControllerPtr, memory, writer);
}

static void ScanUiRootStructPointers(string label, IntPtr basePtr, MemoryReader memory, StreamWriter writer)
{
    writer.WriteLine();
    writer.WriteLine($"[{label}] base=0x{basePtr.ToInt64():X}");
    if (!IsValid(basePtr))
    {
        return;
    }

    for (var offset = 0; offset <= 0x1400; offset += 0x8)
    {
        var ptr = memory.Read<IntPtr>(basePtr + offset);
        if (!IsValid(ptr))
        {
            continue;
        }

        var ui = memory.Read<UiElementBaseOffset>(ptr);
        if (!LooksLikeUiElement(ptr, ui))
        {
            continue;
        }

        var children = ReadVectorCount(ui.ChildrensPtr, 8);
        var stringId = memory.ReadStdWString(ui.StringIdPtr);
        writer.WriteLine($"candidate +0x{offset:X}: ptr=0x{ptr.ToInt64():X} self=0x{ui.Self.ToInt64():X} flags=0x{ui.Flags:X} children={children} size=({ui.UnscaledSize.X:0.0},{ui.UnscaledSize.Y:0.0}) rel=({ui.RelativePosition.X:0.0},{ui.RelativePosition.Y:0.0}) stringId=\"{stringId}\"");
        DumpUiTree($"candidate+0x{offset:X}", ptr, memory, writer, maxDepth: 4, maxNodes: 120);
    }
}

static void AnalyzeAtlasUi(IntPtr uiRootStructPtr, MemoryReader memory, string outputDir, StreamWriter writer)
{
    writer.WriteLine();
    writer.WriteLine("[Atlas analysis]");
    if (!IsValid(uiRootStructPtr))
    {
        writer.WriteLine("UiRootStructPtr is invalid.");
        return;
    }

    var nodes = CollectUiNodes(uiRootStructPtr, memory, maxDepth: 12, maxNodes: 30000);
    writer.WriteLine($"Collected nodes from direct UiRootStructPtr root: {nodes.Count}");
    if (nodes.Count == 0)
    {
        return;
    }

    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    var allCsvPath = Path.Combine(outputDir, $"ui-nodes-{stamp}.csv");
    WriteNodesCsv(allCsvPath, nodes);
    writer.WriteLine($"All UI nodes CSV: {allCsvPath}");

    var containers = nodes
        .Where(n => n.Children >= 50 && n.SizeX > 500 && n.SizeY > 400)
        .OrderByDescending(n => n.Children)
        .ThenByDescending(n => n.SizeX * n.SizeY)
        .ToList();

    writer.WriteLine("Large containers:");
    foreach (var container in containers.Take(20))
    {
        writer.WriteLine(FormatNode(container));
        WriteMapElementProbe(container.Address, memory, writer);
    }

    var atlas = containers
        .Where(n => n.Children >= 200 && n.SizeX >= 1200 && n.SizeY >= 800)
        .Select(n => (Node: n, MapNodeCount: CountDirectAtlasMapNodes(n, nodes)))
        .Where(n => n.MapNodeCount > 0)
        .OrderByDescending(n => n.MapNodeCount)
        .ThenByDescending(n => n.Node.Children)
        .Select(n => n.Node)
        .FirstOrDefault();

    if (atlas.Address == IntPtr.Zero)
    {
        writer.WriteLine("Atlas-like container was not found.");
        return;
    }

    writer.WriteLine($"Selected atlas container: {FormatNode(atlas)}");
    writer.WriteLine($"Selected direct map-node count: {CountDirectAtlasMapNodes(atlas, nodes)}");
    WriteMapElementProbe(atlas.Address, memory, writer);

    var descendants = nodes
        .Where(n => IsDescendantOf(n, atlas.Address, nodes))
        .OrderBy(n => n.Depth)
        .ThenBy(n => n.AbsY)
        .ThenBy(n => n.AbsX)
        .ToList();
    var directChildren = nodes
        .Where(n => n.Parent == atlas.Address)
        .OrderBy(n => n.RelY)
        .ThenBy(n => n.RelX)
        .ToList();

    writer.WriteLine("Atlas direct child type counts:");
    foreach (var group in directChildren
                 .GroupBy(n => $"{(n.Visible ? "visible" : "hidden")},children={n.Children},size={F(n.SizeX)}x{F(n.SizeY)},flags=0x{n.Flags:X}")
                 .OrderByDescending(g => g.Count())
                 .Take(30))
    {
        writer.WriteLine($"{group.Count(),5} {group.Key}");
    }

    var atlasCsvPath = Path.Combine(outputDir, $"atlas-container-{stamp}.csv");
    WriteNodesCsv(atlasCsvPath, descendants);
    writer.WriteLine($"Atlas descendants CSV: {atlasCsvPath}");

    var directCsvPath = Path.Combine(outputDir, $"atlas-direct-children-{stamp}.csv");
    WriteNodesCsv(directCsvPath, directChildren);
    writer.WriteLine($"Atlas direct children CSV: {directCsvPath}");

    var graphItemsPath = Path.Combine(outputDir, $"atlas-graph-items-{stamp}.csv");
    WriteAtlasGraphItemsCsv(graphItemsPath, directChildren, memory);
    writer.WriteLine($"Atlas graph items CSV: {graphItemsPath}");

    var rawPath = Path.Combine(outputDir, $"atlas-node-raw-{stamp}.txt");
    using var raw = new StreamWriter(rawPath, false, Encoding.UTF8);
    raw.WriteLine($"Atlas container {FormatNode(atlas)}");
    raw.WriteLine();
    foreach (var node in PickRawCandidates(directChildren).Take(80))
    {
        raw.WriteLine(FormatNode(node));
        DumpRawWindow(node.Address, memory, raw);
        raw.WriteLine();
    }

    writer.WriteLine($"Atlas raw candidate windows: {rawPath}");
}

static void WriteMapElementProbe(IntPtr address, MemoryReader memory, StreamWriter writer)
{
    var map = memory.Read<MapUiElementOffset>(address);
    writer.WriteLine($"  map-probe shift=({F(map.Shift.X)},{F(map.Shift.Y)}) defaultShift=({F(map.DefaultShift.X)},{F(map.DefaultShift.Y)}) zoom={F(map.Zoom)}");
}

static List<UiDumpNode> CollectUiNodes(IntPtr root, MemoryReader memory, int maxDepth, int maxNodes)
{
    var output = new List<UiDumpNode>();
    var visited = new HashSet<IntPtr>();
    CollectUiNode(root, IntPtr.Zero, depth: 0, absX: 0, absY: 0, memory, visited, output, maxDepth, maxNodes);
    return output;
}

static void CollectUiNode(
    IntPtr address,
    IntPtr parent,
    int depth,
    float absX,
    float absY,
    MemoryReader memory,
    HashSet<IntPtr> visited,
    List<UiDumpNode> output,
    int maxDepth,
    int maxNodes)
{
    if (output.Count >= maxNodes || depth > maxDepth || !IsValid(address) || !visited.Add(address))
    {
        return;
    }

    var ui = memory.Read<UiElementBaseOffset>(address);
    if (!LooksLikeUiElement(address, ui))
    {
        return;
    }

    var visible = (ui.Flags & (1u << 0x0B)) != 0;
    var children = memory.ReadStdVector<IntPtr>(ui.ChildrensPtr, maxCount: 10000);
    var stringId = memory.ReadStdWString(ui.StringIdPtr);
    var childAbsX = absX + ui.RelativePosition.X;
    var childAbsY = absY + ui.RelativePosition.Y;
    output.Add(new UiDumpNode(
        address,
        parent,
        depth,
        visible,
        ui.Flags,
        children.Length,
        ui.RelativePosition.X,
        ui.RelativePosition.Y,
        childAbsX,
        childAbsY,
        ui.UnscaledSize.X,
        ui.UnscaledSize.Y,
        ui.ScaleIndex,
        ui.LocalScaleMultiplier,
        ui.BackgroundColor,
        stringId));

    foreach (var child in children)
    {
        CollectUiNode(child, address, depth + 1, childAbsX, childAbsY, memory, visited, output, maxDepth, maxNodes);
    }
}

static IEnumerable<UiDumpNode> PickRawCandidates(IReadOnlyCollection<UiDumpNode> directChildren)
{
    var visible = directChildren.Where(n => n.Visible).ToList();
    var nodeLike = visible
        .Where(n => n.SizeY is >= 20 and <= 80 && n.SizeX is >= 20 and <= 400)
        .OrderByDescending(n => n.Children)
        .ThenBy(n => n.RelY)
        .ThenBy(n => n.RelX);

    foreach (var node in nodeLike)
    {
        yield return node;
    }

    foreach (var node in visible.OrderByDescending(n => n.Children).ThenBy(n => n.RelY).ThenBy(n => n.RelX))
    {
        yield return node;
    }
}

static bool IsDescendantOf(UiDumpNode node, IntPtr ancestor, IReadOnlyCollection<UiDumpNode> nodes)
{
    if (node.Address == ancestor)
    {
        return true;
    }

    var byAddress = nodes.ToDictionary(n => n.Address);
    var current = node.Parent;
    for (var i = 0; i < 64 && current != IntPtr.Zero; i++)
    {
        if (current == ancestor)
        {
            return true;
        }

        if (!byAddress.TryGetValue(current, out var parent))
        {
            return false;
        }

        current = parent.Parent;
    }

    return false;
}

static void WriteNodesCsv(string path, IEnumerable<UiDumpNode> nodes)
{
    using var csv = new StreamWriter(path, false, Encoding.UTF8);
    csv.WriteLine("address,parent,depth,visible,flags,children,relX,relY,absX,absY,sizeX,sizeY,scaleIndex,localScale,backgroundColor,stringId");
    foreach (var n in nodes)
    {
        csv.WriteLine(string.Join(',',
            Hex(n.Address),
            Hex(n.Parent),
            n.Depth.ToString(CultureInfo.InvariantCulture),
            n.Visible ? "1" : "0",
            $"0x{n.Flags:X}",
            n.Children.ToString(CultureInfo.InvariantCulture),
            F(n.RelX),
            F(n.RelY),
            F(n.AbsX),
            F(n.AbsY),
            F(n.SizeX),
            F(n.SizeY),
            n.ScaleIndex.ToString(CultureInfo.InvariantCulture),
            F(n.LocalScale),
            $"0x{n.BackgroundColor:X8}",
            Csv(n.StringId)));
    }
}

static void WriteAtlasGraphItemsCsv(string path, IEnumerable<UiDumpNode> directChildren, MemoryReader memory)
{
    using var csv = new StreamWriter(path, false, Encoding.UTF8);
    csv.WriteLine("category,address,visible,flags,children,x,y,width,height,unknown2F8,unknown300,unknown308,unknown320,unknown324,unknown328,unknown330,unknown338,child0,child0Flags,child0SizeX,child0SizeY,ptr300Ascii,ptr300Utf16,ptr308Ascii,ptr308Utf16,child0Ascii,child0Utf16");
    foreach (var n in directChildren.OrderBy(n => n.RelY).ThenBy(n => n.RelX))
    {
        var category = ClassifyAtlasItem(n);
        var child0 = IntPtr.Zero;
        UiElementBaseOffset childUi = default;
        var ptr300 = memory.Read<IntPtr>(n.Address + 0x300);
        var ptr308 = memory.Read<IntPtr>(n.Address + 0x308);
        var children = memory.ReadStdVector<IntPtr>(memory.Read<UiElementBaseOffset>(n.Address).ChildrensPtr, maxCount: 8);
        if (children.Length > 0 && IsValid(children[0]))
        {
            child0 = children[0];
            childUi = memory.Read<UiElementBaseOffset>(child0);
        }

        csv.WriteLine(string.Join(',',
            category,
            Hex(n.Address),
            n.Visible ? "1" : "0",
            $"0x{n.Flags:X}",
            n.Children.ToString(CultureInfo.InvariantCulture),
            F(n.RelX),
            F(n.RelY),
            F(n.SizeX),
            F(n.SizeY),
            $"0x{memory.Read<int>(n.Address + 0x2F8):X8}",
            Hex(ptr300),
            Hex(ptr308),
            $"0x{memory.Read<int>(n.Address + 0x320):X8}",
            $"0x{memory.Read<int>(n.Address + 0x324):X8}",
            $"0x{memory.Read<long>(n.Address + 0x328):X16}",
            $"0x{memory.Read<int>(n.Address + 0x330):X8}",
            $"0x{memory.Read<int>(n.Address + 0x338):X8}",
            Hex(child0),
            $"0x{childUi.Flags:X}",
            F(childUi.UnscaledSize.X),
            F(childUi.UnscaledSize.Y),
            Csv(ReadLikelyAscii(memory, ptr300)),
            Csv(ReadLikelyUtf16(memory, ptr300)),
            Csv(ReadLikelyAscii(memory, ptr308)),
            Csv(ReadLikelyUtf16(memory, ptr308)),
            Csv(ReadLikelyAscii(memory, child0)),
            Csv(ReadLikelyUtf16(memory, child0))));
    }
}

static int CountDirectAtlasMapNodes(UiDumpNode container, IReadOnlyCollection<UiDumpNode> nodes)
{
    return nodes.Count(n => n.Parent == container.Address && ClassifyAtlasItem(n) == "MapNode");
}

static string ReadLikelyAscii(MemoryReader memory, IntPtr address)
{
    if (!IsValid(address))
    {
        return string.Empty;
    }

    var bytes = memory.ReadBytes(address, 96);
    var chars = new List<byte>();
    foreach (var b in bytes)
    {
        if (b == 0)
        {
            break;
        }

        if (b < 0x20 || b > 0x7E)
        {
            return string.Empty;
        }

        chars.Add(b);
    }

    return chars.Count >= 3 ? Encoding.ASCII.GetString(chars.ToArray()) : string.Empty;
}

static string ReadLikelyUtf16(MemoryReader memory, IntPtr address)
{
    if (!IsValid(address))
    {
        return string.Empty;
    }

    var bytes = memory.ReadBytes(address, 192);
    var chars = new List<byte>();
    for (var i = 0; i + 1 < bytes.Length; i += 2)
    {
        if (bytes[i] == 0 && bytes[i + 1] == 0)
        {
            break;
        }

        if (bytes[i + 1] != 0 || bytes[i] < 0x20 || bytes[i] > 0x7E)
        {
            return string.Empty;
        }

        chars.Add(bytes[i]);
    }

    return chars.Count >= 3 ? Encoding.ASCII.GetString(chars.ToArray()) : string.Empty;
}

static string ClassifyAtlasItem(UiDumpNode node)
{
    if (Math.Abs(node.SizeX - 40) < 0.01 && Math.Abs(node.SizeY - 40) < 0.01 && node.Children == 1)
    {
        return "MapNode";
    }

    if (node.Children == 1 && (node.SizeX >= 55 || node.SizeY >= 55))
    {
        return "RouteOrBadge";
    }

    return "Other";
}

static void DumpRawWindow(IntPtr address, MemoryReader memory, StreamWriter writer)
{
    const int size = 0x500;
    var bytes = memory.ReadBytes(address, size);
    for (var offset = 0; offset < bytes.Length; offset += 0x10)
    {
        var len = Math.Min(0x10, bytes.Length - offset);
        var hex = BitConverter.ToString(bytes, offset, len).Replace('-', ' ');
        writer.WriteLine($"+0x{offset:X3}: {hex}");
    }
}

static string FormatNode(UiDumpNode n)
{
    return $"addr={Hex(n.Address)} parent={Hex(n.Parent)} depth={n.Depth} visible={n.Visible} flags=0x{n.Flags:X} children={n.Children} rel=({F(n.RelX)},{F(n.RelY)}) abs=({F(n.AbsX)},{F(n.AbsY)}) size=({F(n.SizeX)},{F(n.SizeY)}) scaleIdx={n.ScaleIndex} localScale={F(n.LocalScale)} bg=0x{n.BackgroundColor:X8} str=\"{n.StringId}\"";
}

static string Hex(IntPtr value)
{
    return $"0x{value.ToInt64():X}";
}

static string F(float value)
{
    return value.ToString("0.###", CultureInfo.InvariantCulture);
}

static string Csv(string value)
{
    if (string.IsNullOrEmpty(value))
    {
        return string.Empty;
    }

    return $"\"{value.Replace("\"", "\"\"")}\"";
}

static void DumpUiTree(string label, IntPtr root, MemoryReader memory, StreamWriter writer, int maxDepth = 12, int maxNodes = 5000)
{
    writer.WriteLine();
    writer.WriteLine($"[{label}] 0x{root.ToInt64():X}");
    var visited = new HashSet<IntPtr>();
    var count = 0;
    DumpUiNode(root, memory, writer, visited, depth: 0, maxDepth, maxNodes, ref count);
    writer.WriteLine($"[{label}] nodes={count}");
}

static void DumpUiNode(IntPtr address, MemoryReader memory, StreamWriter writer, HashSet<IntPtr> visited, int depth, int maxDepth, int maxNodes, ref int count)
{
    if (count >= maxNodes || depth > maxDepth || !IsValid(address) || !visited.Add(address))
    {
        return;
    }

    var ui = memory.Read<UiElementBaseOffset>(address);
    if (!LooksLikeUiElement(address, ui))
    {
        writer.WriteLine($"{new string(' ', depth * 2)}0x{address.ToInt64():X} not-ui self=0x{ui.Self.ToInt64():X} vtable=0x{ui.Vtable.ToInt64():X}");
        return;
    }

    count++;
    var visible = (ui.Flags & (1u << 0x0B)) != 0;
    var children = memory.ReadStdVector<IntPtr>(ui.ChildrensPtr, maxCount: 10000);
    var stringId = memory.ReadStdWString(ui.StringIdPtr);
    writer.WriteLine($"{new string(' ', depth * 2)}addr=0x{address.ToInt64():X} depth={depth} visible={visible} flags=0x{ui.Flags:X} children={children.Length} rel=({ui.RelativePosition.X:0.0},{ui.RelativePosition.Y:0.0}) size=({ui.UnscaledSize.X:0.0},{ui.UnscaledSize.Y:0.0}) scaleIdx={ui.ScaleIndex} localScale={ui.LocalScaleMultiplier:0.00} str=\"{stringId}\"");

    foreach (var child in children)
    {
        DumpUiNode(child, memory, writer, visited, depth + 1, maxDepth, maxNodes, ref count);
    }
}

static bool LooksLikeUiElement(IntPtr address, UiElementBaseOffset ui)
{
    if (!IsValid(address) || !IsValid(ui.Vtable))
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

    if (Math.Abs(ui.RelativePosition.X) > 100000 || Math.Abs(ui.RelativePosition.Y) > 100000 ||
        Math.Abs(ui.UnscaledSize.X) > 100000 || Math.Abs(ui.UnscaledSize.Y) > 100000)
    {
        return false;
    }

    return true;
}

static int ReadVectorCount(StdVector vector, int elementSize)
{
    var len = vector.Last.ToInt64() - vector.First.ToInt64();
    if (len <= 0 || len % elementSize != 0 || len > 10_000_000)
    {
        return 0;
    }

    return (int)(len / elementSize);
}

static bool IsValid(IntPtr address)
{
    var value = address.ToInt64();
    return value > 0x10000 && value <= MaxUserModeAddress;
}

readonly record struct UiDumpNode(
    IntPtr Address,
    IntPtr Parent,
    int Depth,
    bool Visible,
    uint Flags,
    int Children,
    float RelX,
    float RelY,
    float AbsX,
    float AbsY,
    float SizeX,
    float SizeY,
    byte ScaleIndex,
    float LocalScale,
    uint BackgroundColor,
    string StringId);

sealed class MemoryReader : IDisposable
{
    private readonly IntPtr handle;

    public MemoryReader(int pid)
    {
        this.handle = OpenProcess(0x0010, false, pid);
    }

    public bool IsOpen => this.handle != IntPtr.Zero;

    public T Read<T>(IntPtr address)
        where T : unmanaged
    {
        if (!IsValidAddress(address))
        {
            return default;
        }

        var bytes = this.ReadBytes(address, Marshal.SizeOf<T>());
        return bytes.Length >= Marshal.SizeOf<T>()
            ? MemoryMarshal.Read<T>(bytes)
            : default;
    }

    public byte[] ReadBytes(IntPtr address, int count)
    {
        if (!IsValidAddress(address) || count <= 0)
        {
            return [];
        }

        var buffer = new byte[count];
        if (!ReadProcessMemory(this.handle, address, buffer, count, out var read) || read.ToInt64() <= 0)
        {
            return [];
        }

        if (read.ToInt64() == count)
        {
            return buffer;
        }

        Array.Resize(ref buffer, (int)read.ToInt64());
        return buffer;
    }

    public T[] ReadStdVector<T>(StdVector vector, int maxCount)
        where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        var len = vector.Last.ToInt64() - vector.First.ToInt64();
        if (len <= 0 || len % size != 0 || len / size > maxCount || len > 16_000_000)
        {
            return [];
        }

        var count = (int)(len / size);
        var bytes = this.ReadBytes(vector.First, (int)len);
        if (bytes.Length < len)
        {
            return [];
        }

        var output = new T[count];
        Buffer.BlockCopy(bytes, 0, output, 0, (int)len);
        return output;
    }

    public string ReadStdWString(StdWString value)
    {
        if (value.Length <= 0 || value.Length > 1000 || value.Capacity <= 0 || value.Capacity > 1000)
        {
            return string.Empty;
        }

        if (value.Capacity <= 8)
        {
            var bytes = BitConverter.GetBytes(value.Buffer.ToInt64())
                .Concat(BitConverter.GetBytes(value.ReservedBytes.ToInt64()))
                .ToArray();
            var text = Encoding.Unicode.GetString(bytes);
            return value.Length < text.Length ? text[..value.Length] : string.Empty;
        }

        var buffer = this.ReadBytes(value.Buffer, value.Length * 2);
        return buffer.Length >= value.Length * 2 ? Encoding.Unicode.GetString(buffer, 0, value.Length * 2) : string.Empty;
    }

    public void Dispose()
    {
        if (this.handle != IntPtr.Zero)
        {
            CloseHandle(this.handle);
        }
    }

    private static bool IsValidAddress(IntPtr address)
    {
        var value = address.ToInt64();
        return value > 0x10000 && value <= 0x7FFFFFFFFFFF;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
