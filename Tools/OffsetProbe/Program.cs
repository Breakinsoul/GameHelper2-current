namespace OffsetProbe;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using GameOffsets;
using GameOffsets.Natives;
using GameOffsets.Objects;
using GameOffsets.Objects.States;
using GameOffsets.Objects.States.InGameState;

internal static class Program
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;

    private static int Main()
    {
        var process = Process.GetProcessesByName("PathOfExile")
            .OrderByDescending(p => p.MainWindowTitle == "Path of Exile 2")
            .FirstOrDefault();
        if (process == null)
        {
            Console.WriteLine("PathOfExile process not found.");
            return 1;
        }

        using var reader = new ProcessReader(process);
        var module = process.MainModule;
        if (module == null)
        {
            Console.WriteLine("Main module is unavailable.");
            return 1;
        }

        Console.WriteLine($"pid={process.Id} module=0x{module.BaseAddress.ToInt64():X} size=0x{module.ModuleMemorySize:X}");

        var staticAddresses = FindStaticAddresses(reader, module);
        foreach (var pair in staticAddresses.OrderBy(p => p.Key))
        {
            Console.WriteLine($"static {pair.Key}=0x{pair.Value.ToInt64():X}");
        }

        if (!staticAddresses.TryGetValue("Game States", out var gameStatesStatic))
        {
            Console.WriteLine("Game States static address was not found.");
            return 2;
        }

        var stateStatic = reader.Read<GameStateStaticOffset>(gameStatesStatic);
        var stateOffset = reader.Read<GameStateOffset>(stateStatic.GameState);
        var inGameState = stateOffset.States[4].X;
        var inGame = reader.Read<InGameStateOffset>(inGameState);
        var areaInstance = inGame.AreaInstanceData;

        Console.WriteLine($"gameStatePtr=0x{stateStatic.GameState.ToInt64():X}");
        Console.WriteLine($"inGameState=0x{inGameState.ToInt64():X}");
        Console.WriteLine($"areaInstance=0x{areaInstance.ToInt64():X}");
        Console.WriteLine($"worldData=0x{inGame.WorldData.ToInt64():X}");
        Console.WriteLine();

        ProbeAreaInstance(reader, areaInstance);
        return 0;
    }

    private static Dictionary<string, IntPtr> FindStaticAddresses(ProcessReader reader, ProcessModule module)
    {
        var result = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        var bytes = reader.ReadBytes(module.BaseAddress, module.ModuleMemorySize);
        foreach (var pattern in StaticOffsetsPatterns.Patterns)
        {
            var matches = FindMatches(bytes, pattern).Take(3).ToArray();
            Console.WriteLine($"pattern {pattern.Name}: matches={matches.Length}");
            if (matches.Length != 1)
            {
                continue;
            }

            var relOffset = matches[0] + pattern.BytesToSkip;
            var relative = BitConverter.ToInt32(bytes, relOffset);
            var address = module.BaseAddress + relOffset + relative + 0x04;
            result[pattern.Name] = address;
        }

        return result;
    }

    private static IEnumerable<int> FindMatches(byte[] data, Pattern pattern)
    {
        for (var i = 0; i <= data.Length - pattern.Data.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < pattern.Data.Length; j++)
            {
                if (pattern.Mask[j] && data[i + j] != pattern.Data[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                yield return i;
            }
        }
    }

    private static void ProbeAreaInstance(ProcessReader reader, IntPtr areaInstance)
    {
        var current = reader.Read<AreaInstanceOffsets>(areaInstance);
        Console.WriteLine($"known level={current.CurrentAreaLevel} hash=0x{current.CurrentAreaHash:X}");
        Console.WriteLine($"known player server=0x{current.PlayerInfo.ServerDataPtr.ToInt64():X} local=0x{current.PlayerInfo.LocalPlayerPtr.ToInt64():X}");
        Console.WriteLine($"known entities@0x6D8 awake size={current.Entities.AwakeEntities.Size} head=0x{current.Entities.AwakeEntities.Head.ToInt64():X}");
        Console.WriteLine();

        DumpAreaWindow(reader, areaInstance, 0x640, 0x780);
        Console.WriteLine();

        Console.WriteLine("StdMap-like candidates:");
        for (var offset = 0x400; offset <= 0xB00; offset += 0x08)
        {
            var map = reader.Read<StdMap>(areaInstance + offset);
            var score = ScoreMap(reader, map, requireSize: false);
            if (score.Score < 3)
            {
                continue;
            }

            Console.WriteLine(
                $"  +0x{offset:X3} size32={map.Size,6} pad=0x{map.PAD_C:X8} score={score.Score,2} head=0x{map.Head.ToInt64():X} sample={score.Sample}");
        }

        Console.WriteLine();
        Console.WriteLine("EntityList candidates:");
        for (var offset = 0x400; offset <= 0xB00; offset += 0x08)
        {
            var list = reader.Read<EntityListStruct>(areaInstance + offset);
            var awakeScore = ScoreMap(reader, list.AwakeEntities, requireSize: true);
            var sleepingScore = ScoreMap(reader, list.SleepingEntities, requireSize: true);
            if (awakeScore.Score <= 0 && sleepingScore.Score <= 0)
            {
                continue;
            }

            Console.WriteLine(
                $"  +0x{offset:X3} awake size={list.AwakeEntities.Size,6} score={awakeScore.Score,2} sample={awakeScore.Sample} head=0x{list.AwakeEntities.Head.ToInt64():X} sleeping size={list.SleepingEntities.Size,6} score={sleepingScore.Score,2}");
        }

        Console.WriteLine();
        Console.WriteLine("LocalPlayer candidates:");
        for (var offset = 0x480; offset <= 0x680; offset += 0x10)
        {
            var local = reader.Read<LocalPlayerStruct>(areaInstance + offset);
            if (!LooksLikePointer(local.LocalPlayerPtr) && !LooksLikePointer(local.ServerDataPtr))
            {
                continue;
            }

            Console.WriteLine($"  +0x{offset:X3} server=0x{local.ServerDataPtr.ToInt64():X} local=0x{local.LocalPlayerPtr.ToInt64():X}");
        }
    }

    private static void DumpAreaWindow(ProcessReader reader, IntPtr areaInstance, int start, int end)
    {
        Console.WriteLine($"Raw area qwords +0x{start:X3}..+0x{end:X3}:");
        for (var offset = start; offset <= end; offset += 0x10)
        {
            var a = reader.Read<ulong>(areaInstance + offset);
            var b = reader.Read<ulong>(areaInstance + offset + 0x08);
            Console.WriteLine($"  +0x{offset:X3}: 0x{a:X16} 0x{b:X16}");
        }
    }

    private static (int Score, string Sample) ScoreMap(ProcessReader reader, StdMap map, bool requireSize)
    {
        if (!LooksLikePointer(map.Head))
        {
            return (0, string.Empty);
        }

        var sizeLooksValid = map.Size is > 0 and < 100000;
        if (requireSize && !sizeLooksValid)
        {
            return (0, string.Empty);
        }

        try
        {
            var head = reader.Read<StdMapNode<EntityNodeKey, EntityNodeValue>>(map.Head);
            if (!LooksLikePointer(head.Parent))
            {
                return (0, "bad-parent");
            }

            var root = reader.Read<StdMapNode<EntityNodeKey, EntityNodeValue>>(head.Parent);
            var score = sizeLooksValid ? 2 : 1;
            var sample = $"rootNil={root.IsNil} id={root.Data.Key.id} entity=0x{root.Data.Value.EntityPtr.ToInt64():X}";
            if (!root.IsNil)
            {
                score++;
            }

            if (LooksLikePointer(root.Data.Value.EntityPtr))
            {
                score += 2;
            }

            if (root.Data.Key.id is > 0 and < 0x40000000)
            {
                score++;
            }

            return (score, sample);
        }
        catch (Exception ex)
        {
            return (0, ex.GetType().Name);
        }
    }

    private static bool LooksLikePointer(IntPtr ptr)
    {
        var value = ptr.ToInt64();
        return value > 0x10000 && value <= 0x7FFFFFFFFFFF;
    }

    private sealed class ProcessReader : IDisposable
    {
        private readonly IntPtr handle;

        public ProcessReader(Process process)
        {
            this.handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
            if (this.handle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"OpenProcess failed: {Marshal.GetLastWin32Error()}");
            }
        }

        public T Read<T>(IntPtr address)
            where T : unmanaged
        {
            var size = Marshal.SizeOf<T>();
            var buffer = new byte[size];
            if (!ReadProcessMemory(this.handle, address, buffer, size, out var read) || read != size)
            {
                throw new InvalidOperationException($"ReadProcessMemory 0x{address.ToInt64():X} size={size} read={read} error={Marshal.GetLastWin32Error()}");
            }

            return MemoryMarshal.Read<T>(buffer.AsSpan());
        }

        public byte[] ReadBytes(IntPtr address, int size)
        {
            var buffer = new byte[size];
            if (!ReadProcessMemory(this.handle, address, buffer, size, out var read) || read != size)
            {
                Array.Resize(ref buffer, Math.Max(0, read));
            }

            return buffer;
        }

        public void Dispose()
        {
            if (this.handle != IntPtr.Zero)
            {
                CloseHandle(this.handle);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out int bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
