using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace SephiriaTrial
{
    // Keep one updater object per process, but check the release again whenever
    // AddOnLoader loads the mod after returning from the title screen.
    internal sealed class TrialAutoUpdater : MonoBehaviour
    {
        private const string ReleaseApi = "https://api.github.com/repos/TaeHyun015/Sephiria_Endless_Trial/releases/latest";
        private const string AssetPrefix = "https://github.com/TaeHyun015/Sephiria_Endless_Trial/releases/download/";
        private const string AssetName = "Endless_Trial.zip";
        private const long MaximumArchiveBytes = 256L * 1024 * 1024;
        private const long MaximumExtractedBytes = 512L * 1024 * 1024;
        private static TrialAutoUpdater? instance;
        private string installedVersion = string.Empty;
        private ReleaseAsset? pendingAsset;
        private string pendingVersion = string.Empty;
        private bool prompted;
        private bool installing;

        private sealed class ReleaseInfo
        {
            public ReleaseInfo() { }
            [JsonProperty("tag_name")] public string TagName = string.Empty;
            [JsonProperty("draft")] public bool Draft { get; set; }
            [JsonProperty("prerelease")] public bool Prerelease { get; set; }
            [JsonProperty("assets")] public List<ReleaseAsset> Assets = new List<ReleaseAsset>();
        }

        private sealed class ReleaseAsset
        {
            public ReleaseAsset() { }
            [JsonProperty("name")] public string Name = string.Empty;
            [JsonProperty("state")] public string State = string.Empty;
            [JsonProperty("size")] public long Size { get; set; }
            [JsonProperty("digest")] public string Digest = string.Empty;
            [JsonProperty("browser_download_url")] public string DownloadUrl = string.Empty;
        }

        private sealed class PackageMetadata
        {
            public PackageMetadata() { }
            [JsonProperty("modVersion")] public string Version = string.Empty;
            [JsonProperty("dllFile")] public string DllFile = string.Empty;
        }

        internal static void EnsureStarted(string version)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer) return;
            try
            {
                TrialAutoUpdater? updater = instance;
                if (updater == null)
                {
                    GameObject gameObject = new GameObject("EndlessTrial_AutoUpdater");
                    DontDestroyOnLoad(gameObject);
                    updater = gameObject.AddComponent<TrialAutoUpdater>();
                    instance = updater;
                }
                if (updater.installing) return;
                updater.StopAllCoroutines();
                updater.pendingAsset = null;
                updater.pendingVersion = string.Empty;
                updater.prompted = false;
                updater.installedVersion = version;
                updater.StartCoroutine(updater.CleanupCompletedUpdate(version));
                updater.StartCoroutine(updater.CheckRelease());
                UnityEngine.Debug.Log("[Endless Trial Update] Release check scheduled: installed=" + version);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[Endless Trial Update] Could not start release check: " + exception.Message);
            }
        }

        internal static void SuspendUntilNextLoad()
        {
            TrialAutoUpdater? updater = instance;
            if (updater == null || updater.installing) return;
            updater.StopAllCoroutines();
            updater.pendingAsset = null;
            updater.pendingVersion = string.Empty;
            updater.prompted = false;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(instance, this)) instance = null;
        }

        private IEnumerator CleanupCompletedUpdate(string version)
        {
            // The helper may still be exiting when the restarted game loads.
            yield return new WaitForSecondsRealtime(5f);
            string gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string updateRoot = Path.Combine(gameRoot, "__EndlessTrial_Update");
            if (!Directory.Exists(updateRoot)) yield break;
            bool unsafeUpdateRoot = true;
            try { unsafeUpdateRoot = (File.GetAttributes(updateRoot) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[Endless Trial Update] Cannot inspect cleanup directory: " + exception.Message);
            }
            if (unsafeUpdateRoot) yield break;

            try
            {
                foreach (string workDir in Directory.GetDirectories(updateRoot))
                {
                    string marker = Path.Combine(workDir, "completed.txt");
                    if (!File.Exists(marker) ||
                        (File.GetAttributes(workDir) & FileAttributes.ReparsePoint) != 0 ||
                        (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                        continue;

                    string[] completed = File.ReadAllLines(marker);
                    if (completed.Length != 2 || !string.Equals(completed[0], version, StringComparison.Ordinal))
                        continue;
                    string backupName = completed[1];
                    const string backupPrefix = "__EndlessTrial_Backup_";
                    if (!backupName.StartsWith(backupPrefix, StringComparison.Ordinal) ||
                        !DateTime.TryParseExact(backupName.Substring(backupPrefix.Length),
                            "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        continue;

                    string backup = Path.GetFullPath(Path.Combine(gameRoot, backupName));
                    if (!string.Equals(Path.GetDirectoryName(backup), gameRoot, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Path.GetDirectoryName(Path.GetFullPath(workDir)), updateRoot,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Directory.Exists(backup))
                    {
                        if ((File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0) continue;
                        Directory.Delete(backup, true);
                    }
                    Directory.Delete(workDir, true);
                    UnityEngine.Debug.Log("[Endless Trial Update] Completed update files removed: " + version);
                }
                if (Directory.GetFileSystemEntries(updateRoot).Length == 0)
                {
                    Directory.Delete(updateRoot);
                    string logPath = Path.Combine(gameRoot, "__EndlessTrial_Update.log");
                    if (File.Exists(logPath)) File.Delete(logPath);
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[Endless Trial Update] Cleanup will retry on the next load: " + exception.Message);
            }
        }

        private IEnumerator CheckRelease()
        {
            yield return new WaitForSecondsRealtime(2f);
            if (!TryParseVersion(installedVersion, out Version current))
            {
                UnityEngine.Debug.LogWarning("[Endless Trial Update] Installed version is invalid: " + installedVersion);
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequest.Get(ReleaseApi))
            {
                request.timeout = 10;
                request.SetRequestHeader("Accept", "application/vnd.github+json");
                request.SetRequestHeader("User-Agent", "Sephiria-Endless-Trial-Updater");
                request.SetRequestHeader("X-GitHub-Api-Version", "2022-11-28");
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                {
                    UnityEngine.Debug.Log($"[Endless Trial Update] Release check skipped: HTTP {request.responseCode}, {request.error}");
                    yield break;
                }

                ReleaseInfo? release = null;
                try { release = JsonConvert.DeserializeObject<ReleaseInfo>(request.downloadHandler.text); }
                catch (Exception exception)
                { UnityEngine.Debug.LogWarning("[Endless Trial Update] Release response could not be read: " + exception.Message); }
                UnityEngine.Debug.Log("[Endless Trial Update] Latest release: " + (release?.TagName ?? "unavailable"));
                if (release == null || release.Draft || release.Prerelease ||
                    !TryParseVersion(release.TagName, out Version latest) || latest.CompareTo(current) <= 0)
                    yield break;

                ReleaseAsset? asset = release.Assets?.FirstOrDefault(a => a.Name == AssetName &&
                    a.State == "uploaded" && a.Size > 0 && a.Size <= MaximumArchiveBytes);
                if (asset == null || !IsExpectedAssetUrl(asset.DownloadUrl) || !TryGetDigest(asset.Digest, out _))
                {
                    UnityEngine.Debug.LogWarning("[Endless Trial Update] Release asset or SHA256 digest is missing or invalid.");
                    yield break;
                }
                pendingVersion = latest.ToString();
                pendingAsset = asset;
                UnityEngine.Debug.Log("[Endless Trial Update] Update available; waiting for the in-game popup: " + pendingVersion);
                StartCoroutine(WaitForPopup());
            }
        }

        private static bool TryParseVersion(string? value, out Version version)
        {
            version = new Version(0, 0, 0);
            if (value == null || string.IsNullOrWhiteSpace(value)) return false;
            string normalized = value.Trim().TrimStart('v', 'V');
            string[] parts = normalized.Split('.');
            if (parts.Length != 3 && parts.Length != 4) return false;
            if (parts.Any(part => part.Length == 0 || part.Any(c => c < '0' || c > '9'))) return false;
            if (!Version.TryParse(normalized, out Version? parsed) || parsed == null) return false;
            version = parsed;
            return true;
        }

        private static bool IsExpectedAssetUrl(string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri != null &&
                uri.Scheme == Uri.UriSchemeHttps &&
                value.StartsWith(AssetPrefix, StringComparison.Ordinal) &&
                uri.AbsolutePath.EndsWith("/" + AssetName, StringComparison.Ordinal) &&
                string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
        }

        private static bool TryGetDigest(string? value, out string digest)
        {
            digest = string.Empty;
            if (value == null || !value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return false;
            string hex = value.Substring(7);
            if (hex.Length != 64 || hex.Any(c => !Uri.IsHexDigit(c))) return false;
            digest = hex.ToUpperInvariant();
            return true;
        }

        private IEnumerator WaitForPopup()
        {
            FloorGenerator? readyFloor = null;
            float floorReadyAt = 0f;
            bool loggedReadyFloor = false;
            bool loggedPopupError = false;
            int fallbackSearchedSceneHandle = int.MinValue;
            WaitForSecondsRealtime retryDelay = new WaitForSecondsRealtime(1f);
            while (!prompted && pendingAsset != null)
            {
                GameCamera? camera = GameCamera.Instance;
                FloorGenerator? floor = camera != null ? camera.CurrentSeeingFloor : null;
                if (floor == null || !floor.IsFloorFinalized)
                {
                    readyFloor = null;
                    loggedReadyFloor = false;
                }
                else
                {
                    if (readyFloor != floor)
                    {
                        readyFloor = floor;
                        floorReadyAt = Time.time;
                        fallbackSearchedSceneHandle = int.MinValue;
                    }

                    // GameCamera calls PlayerLocalDataStorage.OnFloorRenderFinalized
                    // 1.5 seconds after SetFloorFinalized. Before that, the game's
                    // startup/travel UI cleanup can immediately close our popup.
                    if (Time.time - floorReadyAt >= 2f)
                    {
                        if (!loggedReadyFloor)
                        {
                            UnityEngine.Debug.Log("[Endless Trial Update] Floor render complete; locating the native popup holder.");
                            loggedReadyFloor = true;
                        }
                        try
                        {
                            UI_MessageBoxHolder? holder = UIManager.Instance != null
                                ? UIManager.Instance.GetElement<UI_MessageBoxHolder>() : null;
                            if (holder != null && (!holder.gameObject.scene.IsValid() ||
                                                   !holder.gameObject.scene.isLoaded))
                                holder = null;
                            int sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                            if (holder == null && fallbackSearchedSceneHandle != sceneHandle)
                            {
                                // The native holder starts inactive. Scan inactive
                                // scene objects only once per scene if UIManager has
                                // not registered it yet.
                                fallbackSearchedSceneHandle = sceneHandle;
                                foreach (UI_MessageBoxHolder candidate in Resources.FindObjectsOfTypeAll<UI_MessageBoxHolder>())
                                    if (candidate != null && candidate.gameObject.scene.IsValid() &&
                                        candidate.gameObject.scene.isLoaded)
                                    {
                                        holder = candidate;
                                        break;
                                    }
                            }
                            if (holder != null && !holder.HasOpenedBox)
                            {
                                string message = string.Format(EndlessMod.GetSafeText("trial.update.prompt",
                                    "Endless Trial {1} 업데이트가 있습니다. (현재 {0})\n다운로드 후 게임을 종료하고 설치할까요?"),
                                    installedVersion, pendingVersion);
                                UI_MessageBox popup = holder.OpenYesNo(message, ConfirmUpdate,
                                    () => { prompted = true; }, false);
                                EndlessMod.TrackTrialPopup(popup, "trial.update.prompt", installedVersion, pendingVersion);
                                prompted = true;
                                UnityEngine.Debug.Log("[Endless Trial Update] In-game update popup shown: " + pendingVersion);
                            }
                        }
                        catch (Exception exception)
                        {
                            if (!loggedPopupError)
                            {
                                UnityEngine.Debug.LogWarning("[Endless Trial Update] Could not show popup: " + exception);
                                loggedPopupError = true;
                            }
                        }
                        if (prompted) yield break;
                    }
                }
                yield return retryDelay;
            }
        }

        private void ConfirmUpdate()
        {
            ReleaseAsset? asset = pendingAsset;
            if (installing || asset == null) return;
            installing = true;
            StartCoroutine(DownloadAndInstall(asset, pendingVersion));
        }

        private IEnumerator DownloadAndInstall(ReleaseAsset asset, string version)
        {
            string gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string modPath = Path.Combine(gameRoot, "AddOns", "Endless_Trial");
            string gameExe = Path.Combine(gameRoot, "Sephiria.exe");
            string workDir = Path.Combine(gameRoot, "__EndlessTrial_Update", Guid.NewGuid().ToString("N"));
            string partialPath = Path.Combine(workDir, "Endless_Trial.zip.part");
            string archivePath = Path.Combine(workDir, AssetName);

            try
            {
                if (!File.Exists(gameExe) || !File.Exists(Path.Combine(modPath, "Endless_Trial.dll")))
                    throw new FileNotFoundException("Game or installed mod was not found.");
                Directory.CreateDirectory(workDir);
            }
            catch (Exception exception)
            {
                Fail("Cannot prepare download: " + exception.Message, workDir);
                yield break;
            }

            string? downloadError = null;
            using (UnityWebRequest request = UnityWebRequest.Get(asset.DownloadUrl))
            {
                request.timeout = 180;
                request.downloadHandler = new DownloadHandlerFile(partialPath);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                    downloadError = $"Download failed: HTTP {request.responseCode}, {request.error}";
            }
            if (downloadError != null)
            {
                Fail(downloadError, workDir);
                yield break;
            }

            try
            {
                if (!TryGetDigest(asset.Digest, out string expectedHash) ||
                    new FileInfo(partialPath).Length != asset.Size ||
                    !string.Equals(GetSha256(partialPath), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Release SHA256 or archive size does not match.");
                File.Move(partialPath, archivePath);
                ValidateArchive(archivePath, version);
                string helperPath = Path.Combine(workDir, "UpdateHelper.ps1");
                File.WriteAllText(helperPath, HelperScript, new UTF8Encoding(false));
                LaunchHelper(helperPath, archivePath, modPath, gameExe, workDir, expectedHash, version);
                UnityEngine.Debug.Log("[Endless Trial Update] Verified update downloaded. Waiting for the game to exit.");
                Application.Quit();
            }
            catch (Exception exception)
            {
                Fail("Update preparation failed: " + exception, workDir);
            }
        }

        private static string GetSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static void ValidateArchive(string path, string expectedVersion)
        {
            using (FileStream stream = File.OpenRead(path))
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > 1000) throw new InvalidDataException("Too many files in update archive.");
                long expandedBytes = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.StartsWith("/", StringComparison.Ordinal) || name.IndexOf(':') >= 0 ||
                        name.Split('/').Any(part => part == "." || part == "..") ||
                        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("Unsafe path in update archive.");
                    if (name.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Save files must not be included in the update archive.");
                    if (!name.EndsWith("/", StringComparison.Ordinal) && !names.Add(name))
                        throw new InvalidDataException("Duplicate file in update archive.");
                    expandedBytes += entry.Length;
                    if (expandedBytes > MaximumExtractedBytes)
                        throw new InvalidDataException("Update archive is too large when extracted.");
                }

                string prefix = names.Contains("metadata.json") ? string.Empty : "Endless_Trial/";
                if (names.Any(name => !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Update archive has files outside the mod folder.");
                ZipArchiveEntry? metadata = zip.GetEntry(prefix + "metadata.json");
                ZipArchiveEntry? dll = zip.GetEntry(prefix + "Endless_Trial.dll");
                ZipArchiveEntry? bundle = zip.GetEntry(prefix + "endless_trial_floor");
                if (metadata == null || dll == null || bundle == null || dll.Length == 0 || bundle.Length == 0)
                    throw new InvalidDataException("Update ZIP must contain metadata.json, Endless_Trial.dll and endless_trial_floor.");
                using (StreamReader reader = new StreamReader(metadata.Open(), Encoding.UTF8))
                {
                    PackageMetadata? data = JsonConvert.DeserializeObject<PackageMetadata>(reader.ReadToEnd());
                    if (data == null || data.DllFile != "Endless_Trial.dll" ||
                        !string.Equals(data.Version, expectedVersion, StringComparison.Ordinal))
                        throw new InvalidDataException("Update metadata version does not match the release tag.");
                }
            }
        }

        private static void LaunchHelper(string helperPath, string archivePath, string modPath,
            string gameExe, string workDir, string hash, string version)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string powershell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) throw new FileNotFoundException("Windows PowerShell was not found.", powershell);
            var info = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(helperPath) +
                    " -GameProcessId " + Process.GetCurrentProcess().Id +
                    " -ArchivePath " + Quote(archivePath) + " -ModPath " + Quote(modPath) +
                    " -GameExecutable " + Quote(gameExe) + " -WorkDir " + Quote(workDir) +
                    " -ExpectedHash " + Quote(hash) + " -ExpectedVersion " + Quote(version),
                WorkingDirectory = Path.GetDirectoryName(gameExe) ?? workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using Process? process = Process.Start(info);
            if (process == null) throw new InvalidOperationException("Updater helper did not start.");
            if (process.WaitForExit(300) && process.ExitCode != 0)
                throw new InvalidOperationException("Updater helper exited before the game closed.");
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";

        private void Fail(string message, string workDir)
        {
            installing = false;
            UnityEngine.Debug.LogError("[Endless Trial Update] " + message);
            try
            {
                string gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string updateRoot = Path.GetFullPath(Path.Combine(gameRoot, "__EndlessTrial_Update"));
                string resolvedWork = Path.GetFullPath(workDir);
                if (resolvedWork.StartsWith(updateRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedWork))
                    Directory.Delete(resolvedWork, true);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[Endless Trial Update] Could not remove temporary download: " + exception.Message);
            }
            try
            {
                if (UIManager.Instance != null)
                    EndlessMod.ShowLocalizedSystemMessage("trial.update.failed", duration: 4f);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[Endless Trial Update] Could not show update failure message: " + exception.Message);
            }
        }

        private const string HelperScript = @"
param(
    [int]$GameProcessId,
    [string]$ArchivePath,
    [string]$ModPath,
    [string]$GameExecutable,
    [string]$WorkDir,
    [string]$ExpectedHash,
    [string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
$gameRoot = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($GameExecutable))
$expectedMod = [IO.Path]::GetFullPath((Join-Path $gameRoot 'AddOns\Endless_Trial'))
$updateRoot = [IO.Path]::GetFullPath((Join-Path $gameRoot '__EndlessTrial_Update'))
$resolvedWork = [IO.Path]::GetFullPath($WorkDir)
if (![string]::Equals([IO.Path]::GetFullPath($ModPath), $expectedMod, [StringComparison]::OrdinalIgnoreCase) -or
    !$resolvedWork.StartsWith(($updateRoot.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    ![string]::Equals([IO.Path]::GetFullPath($ArchivePath), (Join-Path $resolvedWork 'Endless_Trial.zip'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Updater paths are outside the expected game directories.'
}
$logPath = Join-Path $gameRoot '__EndlessTrial_Update.log'
try {
    $deadline = (Get-Date).AddMinutes(3)
    while ((Get-Process -Id $GameProcessId -ErrorAction SilentlyContinue) -or
           (Get-Process -Name 'Sephiria' -ErrorAction SilentlyContinue)) {
        if ((Get-Date) -gt $deadline) { throw 'Game did not exit within three minutes.' }
        Start-Sleep -Milliseconds 500
    }
    if (![string]::Equals((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash, $ExpectedHash,
        [StringComparison]::OrdinalIgnoreCase)) { throw 'Downloaded ZIP hash changed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    $stage = Join-Path $resolvedWork 'stage'
    try {
        if (Test-Path -LiteralPath $stage) { throw 'Update staging folder already exists.' }
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        $prefix = if ($zip.GetEntry('metadata.json')) { '' } else { 'Endless_Trial/' }
        $count = 0
        [long]$expanded = 0
        foreach ($entry in $zip.Entries) {
            $count++
            $name = $entry.FullName.Replace('\', '/')
            $parts = $name.Split('/')
            if ($count -gt 1000 -or $name.StartsWith('/') -or $name.Contains(':') -or
                ($parts | Where-Object { $_ -eq '.' -or $_ -eq '..' }) -or
                !$name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Unsafe ZIP entry.'
            }
            $relative = $name.Substring($prefix.Length)
            if ($relative.Length -eq 0 -or $relative.EndsWith('/')) { continue }
            $expanded += $entry.Length
            if ($expanded -gt 536870912) { throw 'ZIP expands beyond the size limit.' }
            $target = [IO.Path]::GetFullPath((Join-Path $stage $relative))
            if (!$target.StartsWith(($stage.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
                throw 'ZIP entry escapes staging folder.'
            }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            $input = $entry.Open()
            try {
                $output = [IO.File]::Create($target)
                try { $input.CopyTo($output) } finally { $output.Dispose() }
            } finally { $input.Dispose() }
        }
    } finally { $zip.Dispose() }
    $metadataPath = Join-Path $stage 'metadata.json'
    $dllPath = Join-Path $stage 'Endless_Trial.dll'
    $bundlePath = Join-Path $stage 'endless_trial_floor'
    if (!(Test-Path -LiteralPath $metadataPath) -or !(Test-Path -LiteralPath $dllPath) -or
        !(Test-Path -LiteralPath $bundlePath) -or (Get-Item -LiteralPath $dllPath).Length -le 0 -or
        (Get-Item -LiteralPath $bundlePath).Length -le 0) { throw 'Required mod files are missing.' }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.dllFile -cne 'Endless_Trial.dll' -or
        $metadata.modVersion -cne $ExpectedVersion) { throw 'Package metadata version mismatch.' }
    if (!(Test-Path -LiteralPath $ModPath -PathType Container)) { throw 'Installed mod folder is missing.' }
    $backup = Join-Path $gameRoot ('__EndlessTrial_Backup_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
    if (Test-Path -LiteralPath $backup) { throw 'Backup folder already exists.' }
    Move-Item -LiteralPath $ModPath -Destination $backup
    try { Move-Item -LiteralPath $stage -Destination $ModPath }
    catch {
        Move-Item -LiteralPath $backup -Destination $ModPath
        throw
    }
    Add-Content -LiteralPath $logPath -Value ('Installed Endless Trial ' + $ExpectedVersion + '; backup: ' + $backup)
    [IO.File]::WriteAllLines((Join-Path $resolvedWork 'completed.txt'),
        [string[]]@($ExpectedVersion, [IO.Path]::GetFileName($backup)))
    try { Start-Process -FilePath $GameExecutable -WorkingDirectory $gameRoot }
    catch {
        $failed = Join-Path $gameRoot ('__EndlessTrial_Failed_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
        Move-Item -LiteralPath $ModPath -Destination $failed
        Move-Item -LiteralPath $backup -Destination $ModPath
        throw
    }
}
catch {
    Add-Content -LiteralPath $logPath -Value ('FAILED: ' + $_.Exception.Message)
    exit 1
}
";
    }
}
