namespace Launcher
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;

    public static class AutoUpdate
    {
        private const string DefaultRepository = "Breakinsoul/GameHelper2-custom";
        private const string DefaultAssetPattern = @"^GameHelper.*\.zip$";
        
        private static readonly HttpClient HttpClient = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static string? extractedPath;
        private static string? newVersion;

        static AutoUpdate()
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "GameHelper-Launcher");
        }

        public static async Task<bool> CheckAndUpdateAsync(string gameHelperExePath)
        {
            try
            {
                Console.WriteLine("Checking for updates...");
 
                var currentVersion = GetCurrentVersion(gameHelperExePath);
                if (currentVersion == "Dev")
                {
                    Console.WriteLine("Dev build detected. Skipping auto-update.");
                    return false;
                }

                var latestRelease = await GetLatestReleaseAsync();
                if (latestRelease == null)
                {
                    Console.WriteLine("Failed to check for updates.");
                    return false;
                }
 
                if (IsNewerVersion(latestRelease.Value.Version, currentVersion))
                {
                    Console.WriteLine($"New version available: {latestRelease.Value.Version}");
                    return await DownloadAndInstallUpdateAsync(latestRelease.Value);
                }
 
                Console.WriteLine("No updates were found.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update check failed: {ex.Message}");
                return false;
            }
        }

        private static string GetCurrentVersion(string gameHelperExePath)
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(gameHelperExePath);
                var version = versionInfo.FileVersion;
                if (string.IsNullOrEmpty(version) || version == "1.0.0.0")
                {
                    return "Dev";
                }
                var parts = version.Split('.');
                return $"v{parts[0]}.{parts[1]}.{parts[2]}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read version from {gameHelperExePath}: {ex.Message}");
                return "Dev";
            }
        }

        private static async Task<ReleaseInfo?> GetLatestReleaseAsync()
        {
            try
            {
                var settings = UpdateSettings.Load();
                var releasesUrl = $"https://api.github.com/repos/{settings.Repository}/releases";
                using var request = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

                var token = Environment.GetEnvironmentVariable("GAMEHELPER_GITHUB_TOKEN")
                    ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                using var response = await HttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"GitHub update check failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                foreach (var release in JArray.Parse(content))
                {
                    if (!settings.AllowPrerelease && release["prerelease"]?.Value<bool>() == true)
                    {
                        continue;
                    }

                    if (release["draft"]?.Value<bool>() == true)
                    {
                        continue;
                    }

                    var version = release["tag_name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        continue;
                    }

                    var downloadUrl = GetDownloadUrl(release, settings.AssetPattern);
                    if (!string.IsNullOrWhiteSpace(downloadUrl))
                    {
                        return new ReleaseInfo(version, downloadUrl);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get latest release: {ex.Message}");
                return null;
            }
        }

        private static bool IsNewerVersion(string latestVersion, string currentVersion)
        {
            try
            {
                if (!latestVersion.StartsWith('v'))
                    return false;
                
                var latest = latestVersion.TrimStart('v');
                var current = currentVersion.TrimStart('v');

                var latestParts = latest.Split('.');
                var currentParts = current.Split('.');

                for (int i = 0; i < Math.Max(latestParts.Length, currentParts.Length); i++)
                {
                    int latestPart = i < latestParts.Length ? int.Parse(latestParts[i]) : 0;
                    int currentPart = i < currentParts.Length ? int.Parse(currentParts[i]) : 0;

                    if (latestPart > currentPart) return true;
                    if (latestPart < currentPart) return false;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> DownloadAndInstallUpdateAsync(ReleaseInfo release)
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "GameHelperUpdate");
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
                Directory.CreateDirectory(tempDir);

                var zipPath = Path.Combine(tempDir, "update.zip");

                Console.WriteLine("Downloading update...");
                await DownloadFileWithProgressAsync(release.DownloadUrl, zipPath);

                Console.WriteLine("Extracting update...");
                var extractDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                extractedPath = ResolveExtractedPayloadPath(extractDir);
                newVersion = release.Version;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update preparation failed: {ex.Message}");
                return false;
            }
        }

        private static string? GetDownloadUrl(JToken release, string assetPattern)
        {
            var assets = release["assets"] as JArray;
            if (assets == null)
            {
                return null;
            }

            foreach (var asset in assets)
            {
                var name = asset["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) ||
                    !Regex.IsMatch(name, assetPattern, RegexOptions.IgnoreCase))
                {
                    continue;
                }

                var downloadUrl = asset["browser_download_url"]?.ToString();
                if (!string.IsNullOrWhiteSpace(downloadUrl))
                {
                    return downloadUrl;
                }
            }

            return null;
        }

        private static string ResolveExtractedPayloadPath(string extractDir)
        {
            if (File.Exists(Path.Combine(extractDir, "GameHelper.exe")))
            {
                return extractDir;
            }

            var payloadDir = Directory
                .EnumerateFiles(extractDir, "GameHelper.exe", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (string.IsNullOrWhiteSpace(payloadDir))
            {
                throw new InvalidDataException("Downloaded archive does not contain GameHelper.exe.");
            }

            return payloadDir;
        }

        private static async Task DownloadFileWithProgressAsync(string url, string destinationPath)
        {
            using (var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using (var contentStream = await response.Content.ReadAsStreamAsync())
                {
                    await using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[8192];
                        long totalDownloaded = 0;
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalDownloaded += bytesRead;

                            if (totalBytes > 0)
                            {
                                var percentage = (int)((totalDownloaded * 100) / totalBytes);
                                DrawProgressBar(percentage, 50);
                            }
                        }
                    }
                }
            }
            Console.WriteLine();
        }

        private static void DrawProgressBar(int percentage, int barLength)
        {
            var filled = (int)((percentage / 100.0) * barLength);
            var bar = new string('█', filled) + new string('░', barLength - filled);

            Console.Write($"\r[{bar}] {percentage}%");
        }

        public static void LaunchUpdateAndExit()
        {
            try
            {
                if (string.IsNullOrEmpty(extractedPath) || string.IsNullOrEmpty(newVersion))
                {
                    Console.WriteLine("Update paths not initialized.");
                    return;
                }

                var currentDir = AppDomain.CurrentDomain.BaseDirectory;
                var launcherPath = Path.Combine(currentDir, "Launcher.exe");
                var tempDir = Path.Combine(Path.GetTempPath(), "GameHelperUpdate");
                var runnerDir = Path.Combine(tempDir, "runner");
                var runnerPath = StageUpdateRunner(currentDir, runnerDir);

                var processInfo = new ProcessStartInfo
                {
                    FileName = runnerPath,
                    WorkingDirectory = runnerDir,
                    UseShellExecute = false,
                };
                processInfo.ArgumentList.Add("--apply-update");
                processInfo.ArgumentList.Add(extractedPath);
                processInfo.ArgumentList.Add(currentDir);
                processInfo.ArgumentList.Add(Environment.ProcessId.ToString());
                processInfo.ArgumentList.Add(tempDir);

                Process.Start(processInfo);

                Console.WriteLine("Updating GameHelper. Launcher will restart after update is completed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to launch update process: {ex.Message}");
            }
        }

        public static int ApplyUpdateAndRestart(string extractedPayloadPath, string installDir, int launcherProcessId, string tempDir)
        {
            try
            {
                Console.WriteLine("Waiting for launcher to exit...");
                WaitForLauncherExit(launcherProcessId);

                Console.WriteLine("Installing update...");
                var backupDir = Path.Combine(tempDir, "config-backup");
                BackupUserConfig(installDir, backupDir);
                CopyUpdatePayload(extractedPayloadPath, installDir);
                RestoreUserConfig(backupDir, installDir);

                Console.WriteLine("Update completed successfully.");
                var launcherPath = Path.Combine(installDir, "Launcher.exe");
                if (File.Exists(launcherPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = launcherPath,
                        WorkingDirectory = installDir,
                        UseShellExecute = true,
                    });
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update failed: {ex}");
                Console.Write("Press any key to continue.");
                Console.ReadKey();
                return 1;
            }
        }

        private static string StageUpdateRunner(string installDir, string runnerDir)
        {
            Directory.CreateDirectory(runnerDir);
            foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("Launcher.", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Launcher.exe", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("AsmResolver", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file, Path.Combine(runnerDir, name), true);
                }
            }

            var runnerPath = Path.Combine(runnerDir, "Launcher.exe");
            if (!File.Exists(runnerPath))
            {
                throw new FileNotFoundException("Could not stage updater runner.", runnerPath);
            }

            return runnerPath;
        }

        private static void WaitForLauncherExit(int launcherProcessId)
        {
            try
            {
                using var process = Process.GetProcessById(launcherProcessId);
                if (!process.HasExited)
                {
                    process.WaitForExit(30000);
                }
            }
            catch (ArgumentException)
            {
                // The launcher process already exited.
            }
        }

        private static void BackupUserConfig(string installDir, string backupDir)
        {
            if (Directory.Exists(backupDir))
            {
                Directory.Delete(backupDir, true);
            }

            Directory.CreateDirectory(backupDir);
            CopyDirectoryIfExists(Path.Combine(installDir, "configs"), Path.Combine(backupDir, "configs"));

            var pluginsDir = Path.Combine(installDir, "Plugins");
            if (!Directory.Exists(pluginsDir))
            {
                return;
            }

            foreach (var configDir in Directory.EnumerateDirectories(pluginsDir, "config", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(installDir, configDir);
                CopyDirectory(configDir, Path.Combine(backupDir, relativePath));
            }
        }

        private static void RestoreUserConfig(string backupDir, string installDir)
        {
            if (!Directory.Exists(backupDir))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(backupDir))
            {
                CopyFileSystemEntry(entry, Path.Combine(installDir, Path.GetFileName(entry)));
            }
        }

        private static void CopyUpdatePayload(string extractedPayloadPath, string installDir)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(extractedPayloadPath))
            {
                var name = Path.GetFileName(entry);
                if (name is "configs" or "logs" or "tempFileLocations.dat" or "imgui.ini")
                {
                    continue;
                }

                CopyFileSystemEntry(entry, Path.Combine(installDir, name));
            }
        }

        private static void CopyDirectoryIfExists(string sourceDir, string destinationDir)
        {
            if (Directory.Exists(sourceDir))
            {
                CopyDirectory(sourceDir, destinationDir);
            }
        }

        private static void CopyFileSystemEntry(string sourcePath, string destinationPath)
        {
            if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, destinationPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
                File.Copy(sourcePath, destinationPath, true);
            }
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, file);
                var destinationFile = Path.Combine(destinationDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile) ?? destinationDir);
                File.Copy(file, destinationFile, true);
            }
        }

        private readonly record struct ReleaseInfo(string Version, string DownloadUrl);

        private sealed class UpdateSettings
        {
            public string Repository { get; set; } = DefaultRepository;

            public string AssetPattern { get; set; } = DefaultAssetPattern;

            public bool AllowPrerelease { get; set; }

            public static UpdateSettings Load()
            {
                var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updater.json");
                var settings = File.Exists(settingsPath)
                    ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(settingsPath), JsonOptions) ?? new UpdateSettings()
                    : new UpdateSettings();

                settings.Repository = Environment.GetEnvironmentVariable("GAMEHELPER_UPDATE_REPOSITORY")
                    ?? settings.Repository;
                settings.AssetPattern = Environment.GetEnvironmentVariable("GAMEHELPER_UPDATE_ASSET_PATTERN")
                    ?? settings.AssetPattern;

                if (string.IsNullOrWhiteSpace(settings.Repository))
                {
                    settings.Repository = DefaultRepository;
                }

                if (string.IsNullOrWhiteSpace(settings.AssetPattern))
                {
                    settings.AssetPattern = DefaultAssetPattern;
                }

                return settings;
            }
        }
    }
}
