// <copyright file="Program.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
    using Microsoft.Win32;
    using Utils;

    /// <summary>
    ///     Class executed when the application starts.
    /// </summary>
    internal class Program
    {
        /// <summary>
        ///     function executed when the application starts.
        /// </summary>
        private static async Task Main()
        {
            ConfigureLocalCrashDumps();

            AppDomain.CurrentDomain.UnhandledException += (sender, exceptionArgs) =>
            {
                var errorText = "Program exited with message:\n " + exceptionArgs.ExceptionObject;
                File.AppendAllText("Error.log", $"{DateTime.Now:g} {errorText}\r\n{new string('-', 30)}\r\n");
                AppLogger.Error("UnhandledException", exceptionArgs.ExceptionObject as Exception ?? new Exception(errorText));

                // Do NOT call Environment.Exit — it skips `using` Dispose and leaks
                // the SafeMemoryHandle (audit F-061). The runtime will terminate
                // the process naturally because IsTerminating == true for unhandled
                // exceptions on the main thread.
            };

            using (Core.Overlay = new GameOverlay(MiscHelper.GenerateRandomString()))
            {
                await Core.Overlay.Run();
            }
        }

        private static void ConfigureLocalCrashDumps()
        {
            try
            {
                var processName = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName);
                if (string.IsNullOrWhiteSpace(processName))
                {
                    processName = Path.GetFileName(Environment.ProcessPath);
                }

                if (string.IsNullOrWhiteSpace(processName))
                {
                    return;
                }

                var dumpFolder = Path.Combine(AppContext.BaseDirectory, "dumps");
                Directory.CreateDirectory(dumpFolder);

                using var key = Registry.CurrentUser.CreateSubKey(
                    @$"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\{processName}");
                key?.SetValue("DumpFolder", dumpFolder, RegistryValueKind.ExpandString);
                key?.SetValue("DumpType", 2, RegistryValueKind.DWord);
                key?.SetValue("DumpCount", 10, RegistryValueKind.DWord);
            }
            catch
            {
                // Crash-dump setup is diagnostic only; startup must not depend on it.
            }
        }
    }
}
