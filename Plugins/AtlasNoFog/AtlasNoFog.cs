namespace AtlasNoFog
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.Utils;
    using ImGuiNET;
    using Microsoft.Win32.SafeHandles;
    using Newtonsoft.Json;

    public sealed class AtlasNoFog : PCore<AtlasNoFogSettings>
    {
        private static readonly byte?[] Pattern =
        [
            0xF3, 0x0F, 0x59, 0x51, null, 0xF3, 0x0F, 0x58, 0xC1,
        ];

        private static readonly byte[] PatchBytes = [0x90, 0x90, 0x90, 0x90, 0x90];

        private readonly byte[] originalBytes = new byte[PatchBytes.Length];
        private IntPtr patchAddress = IntPtr.Zero;
        private bool hasOriginalBytes;
        private bool isPatched;
        private uint patchedPid;
        private string status = "Idle.";
        private string lastError = string.Empty;
        private int lastMatchCount;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<AtlasNoFogSettings>(content) ?? new AtlasNoFogSettings();
            }

            if (isGameOpened && this.Settings.EnablePatch && this.Settings.AutoApplyWhenGameAttached)
            {
                this.ApplyPatch();
            }
        }

        public override void OnDisable()
        {
            this.RestorePatch();
        }

        public override void SaveSettings()
        {
            JsonHelper.SafeToFile(this.Settings, new FileInfo(this.SettingPathname));
        }

        public override void DrawSettings()
        {
            if (!ImGui.BeginTabBar("AtlasNoFogSettingsTabs"))
            {
                return;
            }

            if (ImGui.BeginTabItem("General"))
            {
                this.DrawGeneralSettings();
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
            if (this.isPatched && this.patchedPid != Core.Process.Pid)
            {
                this.ResetPatchState("Game process changed; patch state reset.");
            }

            if (this.Settings.EnablePatch &&
                this.Settings.AutoApplyWhenGameAttached &&
                !this.isPatched &&
                Core.Process.Pid != 0)
            {
                this.ApplyPatch();
            }
        }

        private void DrawGeneralSettings()
        {
            var enable = this.Settings.EnablePatch;
            if (ImGui.Checkbox("Enable Atlas No Fog patch", ref enable))
            {
                if (enable)
                {
                    if (this.ApplyPatch())
                    {
                        this.Settings.EnablePatch = true;
                    }
                }
                else
                {
                    this.RestorePatch();
                    this.Settings.EnablePatch = false;
                }
            }

            ImGui.Checkbox("Auto apply when game is attached", ref this.Settings.AutoApplyWhenGameAttached);
            ImGui.Separator();

            if (ImGui.Button("Scan"))
            {
                this.ScanOnly();
            }

            ImGui.SameLine();
            if (ImGui.Button("Apply"))
            {
                this.Settings.EnablePatch = this.ApplyPatch();
            }

            ImGui.SameLine();
            if (ImGui.Button("Restore"))
            {
                this.RestorePatch();
                this.Settings.EnablePatch = false;
            }

            ImGui.TextWrapped(this.status);
            if (!string.IsNullOrEmpty(this.lastError))
            {
                ImGui.TextWrapped(this.lastError);
            }
        }

        private void DrawDebugSettings()
        {
            ImGui.Text($"Game PID: {Core.Process.Pid}");
            ImGui.Text($"Matches: {this.lastMatchCount}");
            ImGui.Text($"Patched: {this.isPatched}");
            ImGui.Text($"Patch address: 0x{this.patchAddress.ToInt64():X}");
            ImGui.Text($"Original bytes captured: {this.hasOriginalBytes}");
            ImGui.TextUnformatted("Pattern: F3 0F 59 51 ?? F3 0F 58 C1");
            ImGui.TextUnformatted("Patch: NOP 5");
        }

        private void ScanOnly()
        {
            this.lastError = string.Empty;
            if (!this.TryFindPatchAddress(out var address, out var error))
            {
                this.status = "Scan failed.";
                this.lastError = error;
                return;
            }

            this.patchAddress = address;
            this.status = $"Pattern found at 0x{address.ToInt64():X}.";
        }

        private bool ApplyPatch()
        {
            this.lastError = string.Empty;
            if (this.isPatched)
            {
                this.status = $"Already patched at 0x{this.patchAddress.ToInt64():X}.";
                return true;
            }

            if (!this.TryFindPatchAddress(out var address, out var error))
            {
                this.status = "Patch not applied.";
                this.lastError = error;
                return false;
            }

            if (!this.TryOpenProcess(out var processHandle, out error))
            {
                this.status = "Patch not applied.";
                this.lastError = error;
                return false;
            }

            using (processHandle)
            {
                if (!ReadProcessMemory(processHandle.DangerousGetHandle(), address, this.originalBytes, this.originalBytes.Length, out var read) ||
                    read.ToInt64() != this.originalBytes.Length)
                {
                    this.status = "Patch not applied.";
                    this.lastError = $"Failed to read original bytes: {GetLastWin32Error()}";
                    return false;
                }

                if (!MatchesPatternPrefix(this.originalBytes))
                {
                    this.status = "Patch not applied.";
                    this.lastError = $"Unexpected original bytes: {FormatBytes(this.originalBytes)}";
                    return false;
                }

                if (!WriteBytes(processHandle.DangerousGetHandle(), address, PatchBytes, out error))
                {
                    this.status = "Patch not applied.";
                    this.lastError = error;
                    return false;
                }
            }

            this.patchAddress = address;
            this.hasOriginalBytes = true;
            this.isPatched = true;
            this.patchedPid = Core.Process.Pid;
            this.status = $"Atlas No Fog applied at 0x{address.ToInt64():X}.";
            return true;
        }

        private void RestorePatch()
        {
            this.lastError = string.Empty;
            if (!this.isPatched)
            {
                this.status = "Nothing to restore.";
                return;
            }

            if (this.patchedPid != Core.Process.Pid)
            {
                this.ResetPatchState("Game process changed; restore skipped.");
                return;
            }

            if (!this.hasOriginalBytes || this.patchAddress == IntPtr.Zero)
            {
                this.status = "Restore skipped.";
                this.lastError = "Original bytes or patch address are missing.";
                return;
            }

            if (!this.TryOpenProcess(out var processHandle, out var error))
            {
                this.status = "Restore failed.";
                this.lastError = error;
                return;
            }

            using (processHandle)
            {
                if (!WriteBytes(processHandle.DangerousGetHandle(), this.patchAddress, this.originalBytes, out error))
                {
                    this.status = "Restore failed.";
                    this.lastError = error;
                    return;
                }
            }

            this.isPatched = false;
            this.patchedPid = 0;
            this.status = $"Original bytes restored at 0x{this.patchAddress.ToInt64():X}.";
        }

        private void ResetPatchState(string reason)
        {
            this.patchAddress = IntPtr.Zero;
            this.hasOriginalBytes = false;
            this.isPatched = false;
            this.patchedPid = 0;
            Array.Clear(this.originalBytes);
            this.status = reason;
        }

        private bool TryFindPatchAddress(out IntPtr address, out string error)
        {
            address = IntPtr.Zero;
            error = string.Empty;
            this.lastMatchCount = 0;

            if (!TryGetGameProcess(out var process, out error))
            {
                return false;
            }

            var module = process.MainModule;
            if (module == null)
            {
                error = "Game main module is unavailable.";
                return false;
            }

            if (!this.TryOpenProcess(out var processHandle, out error))
            {
                return false;
            }

            using (processHandle)
            {
                var baseAddress = module.BaseAddress;
                var moduleSize = module.ModuleMemorySize;
                const int chunkSize = 0x10000;
                var overlap = Pattern.Length - 1;
                var matches = 0;
                var foundOffset = 0;

                for (var offset = 0; offset < moduleSize; offset += chunkSize)
                {
                    var readSize = Math.Min(chunkSize + overlap, moduleSize - offset);
                    var buffer = new byte[readSize];
                    if (!ReadProcessMemory(processHandle.DangerousGetHandle(), baseAddress + offset, buffer, buffer.Length, out var bytesRead))
                    {
                        continue;
                    }

                    var count = bytesRead.ToInt64();
                    for (var i = 0; i <= count - Pattern.Length; i++)
                    {
                        if (!MatchesPattern(buffer, i))
                        {
                            continue;
                        }

                        matches++;
                        foundOffset = offset + i;
                        if (matches > 1)
                        {
                            this.lastMatchCount = matches;
                            error = "Pattern is not unique; patch skipped.";
                            return false;
                        }
                    }
                }

                this.lastMatchCount = matches;
                if (matches != 1)
                {
                    error = matches == 0 ? "Pattern not found." : "Pattern is not unique.";
                    return false;
                }

                address = baseAddress + foundOffset;
                return true;
            }
        }

        private static bool TryGetGameProcess(out Process process, out string error)
        {
            process = null!;
            error = string.Empty;
            if (Core.Process.Pid == 0)
            {
                error = "Game process is not attached.";
                return false;
            }

            try
            {
                process = Process.GetProcessById((int)Core.Process.Pid);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to get game process: {ex.Message}";
                return false;
            }
        }

        private bool TryOpenProcess(out SafeProcessHandle processHandle, out string error)
        {
            processHandle = null!;
            error = string.Empty;
            if (Core.Process.Pid == 0)
            {
                error = "Game process is not attached.";
                return false;
            }

            processHandle = OpenProcess(
                ProcessAccess.VirtualMemoryRead |
                ProcessAccess.VirtualMemoryWrite |
                ProcessAccess.VirtualMemoryOperation |
                ProcessAccess.QueryInformation,
                false,
                Core.Process.Pid);

            if (processHandle.IsInvalid)
            {
                error = $"OpenProcess failed: {GetLastWin32Error()}";
                return false;
            }

            return true;
        }

        private static bool WriteBytes(IntPtr processHandle, IntPtr address, byte[] bytes, out string error)
        {
            error = string.Empty;
            if (!VirtualProtectEx(processHandle, address, (UIntPtr)bytes.Length, MemoryProtection.ExecuteReadWrite, out var oldProtect))
            {
                error = $"VirtualProtectEx failed: {GetLastWin32Error()}";
                return false;
            }

            try
            {
                if (!WriteProcessMemory(processHandle, address, bytes, bytes.Length, out var written) ||
                    written.ToInt64() != bytes.Length)
                {
                    error = $"WriteProcessMemory failed: {GetLastWin32Error()}";
                    return false;
                }

                FlushInstructionCache(processHandle, address, (UIntPtr)bytes.Length);
                return true;
            }
            finally
            {
                VirtualProtectEx(processHandle, address, (UIntPtr)bytes.Length, oldProtect, out _);
            }
        }

        private static bool MatchesPattern(byte[] buffer, int offset)
        {
            for (var i = 0; i < Pattern.Length; i++)
            {
                if (Pattern[i].HasValue && buffer[offset + i] != Pattern[i]!.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesPatternPrefix(byte[] bytes)
        {
            return bytes.Length == PatchBytes.Length &&
                bytes[0] == 0xF3 &&
                bytes[1] == 0x0F &&
                bytes[2] == 0x59 &&
                bytes[3] == 0x51;
        }

        private static string FormatBytes(byte[] bytes)
        {
            return string.Join(" ", bytes.Select(x => x.ToString("X2")));
        }

        private static string GetLastWin32Error()
        {
            return new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }

        [Flags]
        private enum ProcessAccess : uint
        {
            QueryInformation = 0x0400,
            VirtualMemoryOperation = 0x0008,
            VirtualMemoryRead = 0x0010,
            VirtualMemoryWrite = 0x0020,
        }

        private enum MemoryProtection : uint
        {
            ExecuteReadWrite = 0x40,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(ProcessAccess desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr processHandle, IntPtr address, UIntPtr size, MemoryProtection newProtect, out MemoryProtection oldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr processHandle, IntPtr baseAddress, UIntPtr size);
    }
}
