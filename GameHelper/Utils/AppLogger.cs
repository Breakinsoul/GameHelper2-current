// <copyright file="AppLogger.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Utils
{
    using System;
    using System.IO;
    using System.Threading;

    internal static class AppLogger
    {
        private static readonly object LockObject = new();

        internal static void Info(string message)
        {
            Write("INFO", message);
        }

        internal static void Error(string scope, Exception exception)
        {
            Write("ERROR", $"{scope}: {exception}");
        }

        private static void Write(string level, string message)
        {
            try
            {
                Directory.CreateDirectory("logs");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [t{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
                lock (LockObject)
                {
                    File.AppendAllText(Path.Join("logs", $"gamehelper-{DateTime.Now:yyyy-MM-dd}.log"), line);
                }
            }
            catch
            {
                // Logging must never affect overlay execution.
            }
        }
    }
}
