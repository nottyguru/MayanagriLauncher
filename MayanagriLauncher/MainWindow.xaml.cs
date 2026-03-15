using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.IO.Compression;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Downloader;

namespace MayanagriLauncher
{
    public class LauncherConfig
    {
        public int RamMb { get; set; } = 4096;
        public string Username { get; set; } = string.Empty;
        public bool CloseAfterLaunch { get; set; } = false;
        public string CachedManifestUrl { get; set; } = string.Empty;
    }

    public class BootstrapManifest
    {
        public string launcher_version { get; set; } = "1.0.0";
        public string launcher_download_url { get; set; } = string.Empty;
        public string launcher_hash { get; set; } = string.Empty;
        public string updater_download_url { get; set; } = string.Empty;
        public string updater_hash { get; set; } = string.Empty;
        public string manifest_url { get; set; } = string.Empty;
    }

    public class SyncPolicies
    {
        public string[] strict_folders { get; set; } = { "mods", "config" };
        public string[] lenient_folders { get; set; } = { "resourcepacks", "shaderpacks" };
        public string[] initial_only_files { get; set; } = { "options.txt", "servers.dat" };
        public string[] ignored_paths { get; set; } = Array.Empty<string>();
    }

    public class ManifestFile
    {
        public string path { get; set; } = string.Empty;
        public string hash { get; set; } = string.Empty;
        public string url { get; set; } = string.Empty;
    }

    public class MayanagriManifest
    {
        public string minecraft_version { get; set; } = string.Empty;
        public string fabric_version { get; set; } = string.Empty;
        public int java_version { get; set; } = 0; // 0 = Auto-detect
        public string server_ip { get; set; } = string.Empty;
        public int server_port { get; set; } = 25565;
        public string[]? jvm_flags { get; set; }
        public SyncPolicies policies { get; set; } = new SyncPolicies();
        public ManifestFile[] files { get; set; } = Array.Empty<ManifestFile>();
    }

    public partial class MainWindow : Window
    {
        private const string BOOTSTRAP_URL = "https://raw.githubusercontent.com/nottyguru/MayanagriLauncher/main/bootstrap.json";

        private readonly string mayanagriDir;
        private readonly string configPath;

        private LauncherConfig _launcherConfig = new LauncherConfig();
        private string _targetServerIp = "127.0.0.1";
        private int _targetServerPort = 25565;
        private string _dynamicManifestUrl = string.Empty;

        private CancellationTokenSource? _launchCts;
        private readonly CancellationTokenSource _appLifetimeCts = new CancellationTokenSource();
        private bool _isLaunching = false;
        private CMLauncher? _activeCmlLauncher;

        private static readonly HttpClient sharedClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.Add("User-Agent", "MayanagriLauncher/1.2.1");
            return client;
        }

        public MainWindow()
        {
            InitializeComponent();

            mayanagriDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MayanagriLauncher");
            if (!Directory.Exists(mayanagriDir)) Directory.CreateDirectory(mayanagriDir);

            configPath = Path.Combine(mayanagriDir, "launcher_config.json");

            LoadConfig();
            ValidateUsername();

            var assemblyInfo = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var fallbackVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            VersionText.Text = $"Client v{assemblyInfo ?? fallbackVersion}";

            this.Loaded += async (s, e) =>
            {
                UsernameBox.Focus();
                await InitializeBootstrapAsync();
            };

            this.Closed += (s, e) =>
            {
                _appLifetimeCts.Cancel();
                _appLifetimeCts.Dispose();
            };

            _ = StartServerMonitorAsync(_appLifetimeCts.Token);
        }

        private async Task InitializeBootstrapAsync()
        {
            try
            {
                using (var response = await sharedClient.GetAsync(BOOTSTRAP_URL))
                {
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync();
                    var bootstrap = JsonSerializer.Deserialize<BootstrapManifest>(json) ?? new BootstrapManifest();

                    _dynamicManifestUrl = bootstrap.manifest_url;
                    _launcherConfig.CachedManifestUrl = _dynamicManifestUrl;
                    SaveConfig(); // Cache immediately for future offline use

                    var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                    var latestVersion = new Version(bootstrap.launcher_version);

                    if (latestVersion > currentVersion)
                    {
                        var result = System.Windows.MessageBox.Show(
                            $"A new launcher update (v{bootstrap.launcher_version}) is available. Would you like to update now?",
                            "Update Available",
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Information);

                        if (result == System.Windows.MessageBoxResult.Yes)
                        {
                            await TriggerSelfUpdateAsync(bootstrap);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Bootstrap Resilience: Attempt to use cached manifest
                _dynamicManifestUrl = _launcherConfig.CachedManifestUrl;
                if (string.IsNullOrEmpty(_dynamicManifestUrl))
                {
                    ShowError("Could not check for updates. You may be offline, and no cached data was found.");
                }
                else
                {
                    ShowError("Could not connect to update server. Proceeding in offline mode.");
                }
            }
        }

        private async Task TriggerSelfUpdateAsync(BootstrapManifest bootstrap)
        {
            PlayButton.IsEnabled = false;
            ProgressText.Text = "Preparing update...";
            FadeInElement(ProgressArea);
            GameProgressBar.IsIndeterminate = true;

            try
            {
                string updaterPath = Path.Combine(mayanagriDir, "Updater.exe");
                string tempUpdater = updaterPath + ".tmp";

                await DownloadFileWithRetryAsync(bootstrap.updater_download_url, tempUpdater, CancellationToken.None);

                // Strict Updater Hash Enforcement
                if (string.IsNullOrWhiteSpace(bootstrap.updater_hash))
                {
                    File.Delete(tempUpdater);
                    throw new Exception("Security mismatch: Updater signature is missing from manifest.");
                }

                if (CalculateSHA256(tempUpdater) != bootstrap.updater_hash.ToLower())
                {
                    File.Delete(tempUpdater);
                    throw new Exception("Security mismatch: Updater signature invalid.");
                }

                File.Move(tempUpdater, updaterPath, true);

                string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                int currentPid = Process.GetCurrentProcess().Id;

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = $"{currentPid} \"{bootstrap.launcher_download_url}\" \"{currentExe}\" \"{bootstrap.launcher_hash}\"",
                    UseShellExecute = true
                };

                Process.Start(psi);
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                ShowError($"Update failed: {ex.Message}");
                ResetUI();
            }
        }

        // Retry Utility for Downloads
        private async Task DownloadFileWithRetryAsync(string url, string destination, CancellationToken token, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    using (var response = await sharedClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await response.Content.CopyToAsync(fs, token);
                        }
                    }
                    return; // Success
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    if (i == maxRetries - 1) throw;
                    await Task.Delay(1000 * (i + 1), token); // Exponential backoff
                }
            }
        }

        private async Task StartServerMonitorAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient();
                    var connectTask = client.ConnectAsync(_targetServerIp, _targetServerPort);
                    var timeoutTask = Task.Delay(2000, token);

                    bool isOnline = false;
                    if (await Task.WhenAny(connectTask, timeoutTask) == connectTask)
                    {
                        isOnline = client.Connected;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        ServerStatusLight.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isOnline ? "#10B981" : "#EF4444"));
                        ServerStatusPanel.ToolTip = isOnline ? $"Server Online: {_targetServerIp}:{_targetServerPort}" : $"Server Offline: {_targetServerIp}:{_targetServerPort}";
                    });
                }
                catch
                {
                    Dispatcher.Invoke(() =>
                    {
                        ServerStatusLight.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
                        ServerStatusPanel.ToolTip = $"Server Offline: {_targetServerIp}:{_targetServerPort}";
                    });
                }

                try { await Task.Delay(10000, token); }
                catch (TaskCanceledException) { break; }
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => this.Close();
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;

        private void UsernameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ValidateUsername();

        private void ValidateUsername()
        {
            if (_isLaunching) return;

            bool isValid = !string.IsNullOrWhiteSpace(UsernameBox.Text) && !UsernameBox.Text.Contains(" ");
            PlayButton.IsEnabled = isValid;
            UsernameBorder.BorderBrush = isValid
                ? null
                : new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
        }

        private void LoadConfig()
        {
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    _launcherConfig = JsonSerializer.Deserialize<LauncherConfig>(json) ?? new LauncherConfig();

                    RamSlider.Value = _launcherConfig.RamMb;
                    UsernameBox.Text = _launcherConfig.Username;
                    CloseAfterLaunchCheck.IsChecked = _launcherConfig.CloseAfterLaunch;
                }
                catch { /* Corrupt config, fall back to defaults */ }
            }
        }

        private void SaveConfig()
        {
            _launcherConfig.RamMb = (int)RamSlider.Value;
            _launcherConfig.Username = UsernameBox.Text;
            _launcherConfig.CloseAfterLaunch = CloseAfterLaunchCheck.IsChecked ?? false;

            try
            {
                string json = JsonSerializer.Serialize(_launcherConfig, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
            }
            catch { /* Ignore IO errors on save */ }
        }

        private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RamValueText != null) RamValueText.Text = $"{Math.Round(e.NewValue / 1024.0, 1)} GB";
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void FadeInElement(UIElement element)
        {
            element.Visibility = Visibility.Visible;
            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3));
            element.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        // Retry File Lock Handling
        private void ForceDeleteDirectory(string targetDir)
        {
            if (!Directory.Exists(targetDir)) return;

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    string[] files = Directory.GetFiles(targetDir);
                    string[] dirs = Directory.GetDirectories(targetDir);

                    foreach (string file in files)
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }

                    foreach (string dir in dirs)
                    {
                        ForceDeleteDirectory(dir);
                    }

                    File.SetAttributes(targetDir, FileAttributes.Normal);
                    Directory.Delete(targetDir, false);
                    return; // Success
                }
                catch (IOException)
                {
                    if (i == 2) throw;
                    Thread.Sleep(500); // Wait for lock to clear
                }
            }
        }

        private void RepairButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLaunching) return;

            var result = System.Windows.MessageBox.Show("This will forcefully verify and re-download any corrupted game files, and reset your configurations. Continue?",
                                         "Repair Installation", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            try
            {
                string[] folders = { "mods", "config", "shaderpacks", "resourcepacks" };
                foreach (var folder in folders)
                {
                    ForceDeleteDirectory(Path.Combine(mayanagriDir, folder));
                }

                // Initial-only resets
                string[] resetFiles = { "options.txt", "servers.dat" };
                foreach (var file in resetFiles)
                {
                    string path = Path.Combine(mayanagriDir, file);
                    if (File.Exists(path)) File.Delete(path);
                }

                ForceDeleteDirectory(Path.Combine(mayanagriDir, "versions"));
                System.Windows.MessageBox.Show("Cleanup complete! Click PLAY to begin the fresh installation.", "Repair", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowError($"Repair failed: {ex.Message}");
            }
        }

        private string CalculateSHA256(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private async Task SyncFilesAsync(MayanagriManifest manifest, string baseDir, CancellationToken token)
        {
            Dispatcher.Invoke(() => {
                ProgressText.Text = "Applying sync policies...";
                GameProgressBar.IsIndeterminate = true;
            });

            var policies = manifest.policies ?? new SyncPolicies();
            var allowedPaths = manifest.files.Select(f => f.path.Replace("/", "\\")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var initialOnlyPaths = policies.initial_only_files.Select(p => p.Replace("/", "\\")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ignoredPaths = policies.ignored_paths.Select(p => p.Replace("/", "\\")).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in policies.strict_folders)
            {
                string folderPath = Path.Combine(baseDir, folder);
                if (Directory.Exists(folderPath))
                {
                    var localFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
                    foreach (var file in localFiles)
                    {
                        string relativePath = Path.GetRelativePath(baseDir, file);
                        if (ignoredPaths.Contains(relativePath)) continue;

                        if (!allowedPaths.Contains(relativePath))
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                    }
                }
            }

            var filesToDownload = new List<ManifestFile>();
            foreach (var fileData in manifest.files)
            {
                string relativePath = fileData.path.Replace("/", "\\");
                string targetPath = Path.Combine(baseDir, relativePath);

                // Keep initial_only behavior: preserves user settings. Repaired ONLY via the Repair Button.
                if (initialOnlyPaths.Contains(relativePath) && File.Exists(targetPath))
                {
                    continue;
                }

                if (File.Exists(targetPath))
                {
                    if (CalculateSHA256(targetPath) == fileData.hash) continue;
                }

                filesToDownload.Add(fileData);
            }

            if (filesToDownload.Count == 0) return;

            Dispatcher.Invoke(() => {
                GameProgressBar.IsIndeterminate = false;
                GameProgressBar.Value = 0;
            });

            int totalRequired = filesToDownload.Count;
            int completedFiles = 0;

            using (var semaphore = new SemaphoreSlim(5))
            {
                var downloadTasks = filesToDownload.Select(async fileData =>
                {
                    await semaphore.WaitAsync(token);
                    string targetPath = Path.Combine(baseDir, fileData.path.Replace("/", "\\"));

                    try
                    {
                        token.ThrowIfCancellationRequested();

                        string? targetDir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                            Directory.CreateDirectory(targetDir);

                        string tempPath = targetPath + ".tmp";

                        // Resilient Downloads
                        await DownloadFileWithRetryAsync(fileData.url, tempPath, token, 3);
                        File.Move(tempPath, targetPath, true);

                        int currentCount = Interlocked.Increment(ref completedFiles);
                        Dispatcher.Invoke(() => {
                            ProgressText.Text = $"Syncing updates... ({currentCount}/{totalRequired})";
                            GameProgressBar.Value = ((double)currentCount / totalRequired) * 100;
                        });
                    }
                    catch
                    {
                        string tempPath = targetPath + ".tmp";
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                        throw;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(downloadTasks);
            }
        }

        private int GetRequiredJavaVersion(string mcVersion)
        {
            try
            {
                string cleanVersion = mcVersion.Split('-')[0];
                if (Version.TryParse(cleanVersion, out Version v))
                {
                    if (v.Minor >= 21 || (v.Minor == 20 && v.Build >= 5)) return 21;
                    if (v.Minor >= 17) return 17;
                }
            }
            catch { /* Fallback */ }
            return 8;
        }

        private async Task<string> EnsureJavaAsync(MayanagriManifest manifest, CancellationToken token)
        {
            int javaVersion = manifest.java_version > 0 ? manifest.java_version : GetRequiredJavaVersion(manifest.minecraft_version);
            string runtimeRoot = Path.Combine(mayanagriDir, "runtime");
            string runtimeDir = Path.Combine(runtimeRoot, $"jre{javaVersion}");

            // Java Runtime Cleanup: Delete old JREs to save disk space
            if (Directory.Exists(runtimeRoot))
            {
                foreach (var dir in Directory.GetDirectories(runtimeRoot))
                {
                    if (Path.GetFileName(dir) != $"jre{javaVersion}")
                    {
                        ForceDeleteDirectory(dir);
                    }
                }
            }

            if (Directory.Exists(runtimeDir))
            {
                string? existingJavaw = Directory.GetFiles(runtimeDir, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (existingJavaw != null) return existingJavaw;
            }

            Dispatcher.Invoke(() => {
                ProgressText.Text = "Downloading Runtime Environment...";
                GameProgressBar.IsIndeterminate = false;
                GameProgressBar.Value = 0;
            });

            string zipPath = Path.Combine(mayanagriDir, $"jre{javaVersion}.zip");
            string downloadUrl = $"https://api.adoptium.net/v3/binary/latest/{javaVersion}/ga/windows/x64/jre/hotspot/normal/adoptium";

            try
            {
                // Resilient Java Download
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        using (var response = await sharedClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
                        {
                            response.EnsureSuccessStatusCode();
                            long? totalBytes = response.Content.Headers.ContentLength;

                            using (var contentStream = await response.Content.ReadAsStreamAsync(token))
                            using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                var buffer = new byte[8192];
                                long totalRead = 0;
                                int bytesRead;

                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                                {
                                    token.ThrowIfCancellationRequested();
                                    await fileStream.WriteAsync(buffer, 0, bytesRead, token);
                                    totalRead += bytesRead;

                                    if (totalBytes.HasValue)
                                    {
                                        double percentage = Math.Round((double)totalRead / totalBytes.Value * 100, 1);
                                        Dispatcher.Invoke(() => GameProgressBar.Value = percentage);
                                    }
                                }
                            }
                        }
                        break; // Success
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        if (i == 2) throw;
                        await Task.Delay(2000, token);
                    }
                }
            }
            catch
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
                throw;
            }

            Dispatcher.Invoke(() => {
                ProgressText.Text = "Extracting Runtime...";
                GameProgressBar.IsIndeterminate = true;
            });

            if (!Directory.Exists(runtimeDir)) Directory.CreateDirectory(runtimeDir);
            ZipFile.ExtractToDirectory(zipPath, runtimeDir, true);

            string? finalJavawPath = Directory.GetFiles(runtimeDir, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (finalJavawPath == null) throw new Exception("Java runtime not found after extraction.");

            File.Delete(zipPath);
            return finalJavawPath;
        }

        private void Launcher_FileChanged(DownloadFileChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                string displayCategory = e.FileKind.ToString() switch
                {
                    "Library" => "Engine Libraries",
                    "Client" => "Game Core",
                    "Asset" => "Game Assets",
                    "Resource" => "Resources",
                    _ => e.FileKind.ToString()
                };

                ProgressText.Text = $"Syncing {displayCategory}...";

                if (e.TotalFileCount > 0)
                {
                    GameProgressBar.IsIndeterminate = false;
                    GameProgressBar.Value = ((double)e.ProgressedFileCount / e.TotalFileCount) * 100;
                }
            });
        }

        private string GenerateOfflineUUID(string username)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
                hash[6] = (byte)(hash[6] & 0x0f | 0x30);
                hash[8] = (byte)(hash[8] & 0x3f | 0x80);
                return new Guid(hash).ToString("N");
            }
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLaunching)
            {
                _launchCts?.Cancel();
                return;
            }

            if (string.IsNullOrEmpty(_dynamicManifestUrl))
            {
                ShowError("Bootstrap synchronization failed. Please restart the launcher.");
                return;
            }

            _isLaunching = true;
            ErrorText.Visibility = Visibility.Collapsed;
            PlayButton.Content = "CANCEL LAUNCH";

            GameProgressBar.Value = 0;
            GameProgressBar.IsIndeterminate = true;
            FadeInElement(ProgressArea);

            SaveConfig();
            _launchCts = new CancellationTokenSource();

            try
            {
                ProgressText.Text = "Fetching server blueprint...";
                MayanagriManifest manifest;

                using (var response = await sharedClient.GetAsync(_dynamicManifestUrl, _launchCts.Token))
                {
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync(_launchCts.Token);
                    manifest = JsonSerializer.Deserialize<MayanagriManifest>(json) ?? new MayanagriManifest();
                }

                if (string.IsNullOrEmpty(manifest.minecraft_version) || string.IsNullOrEmpty(manifest.fabric_version))
                {
                    throw new Exception("Server blueprint is missing required engine versions.");
                }

                if (!string.IsNullOrEmpty(manifest.server_ip))
                {
                    _targetServerIp = manifest.server_ip;
                    _targetServerPort = manifest.server_port > 0 ? manifest.server_port : 25565;
                }

                string javaPath = await EnsureJavaAsync(manifest, _launchCts.Token);

                var path = new MinecraftPath(mayanagriDir);
                await SyncFilesAsync(manifest, path.BasePath, _launchCts.Token);

                ProgressText.Text = "Installing Fabric Engine...";
                GameProgressBar.IsIndeterminate = true;

                string targetVersion = $"fabric-loader-{manifest.fabric_version}-{manifest.minecraft_version}";
                string profileDir = Path.Combine(path.Versions, targetVersion);
                string profileJsonPath = Path.Combine(profileDir, $"{targetVersion}.json");

                if (!File.Exists(profileJsonPath))
                {
                    Directory.CreateDirectory(profileDir);
                    string fabricApiUrl = $"https://meta.fabricmc.net/v2/versions/loader/{manifest.minecraft_version}/{manifest.fabric_version}/profile/json";

                    // Resilient Profile Download
                    await DownloadFileWithRetryAsync(fabricApiUrl, profileJsonPath, _launchCts.Token, 3);
                }

                _activeCmlLauncher = new CMLauncher(path);
                await _activeCmlLauncher.GetAllVersionsAsync();
                _activeCmlLauncher.FileChanged += Launcher_FileChanged;

                GameProgressBar.IsIndeterminate = false;

                var session = new MSession
                {
                    Username = UsernameBox.Text,
                    UUID = GenerateOfflineUUID(UsernameBox.Text),
                    AccessToken = "access_token",
                    UserType = "Legacy"
                };

                var launchOptions = new MLaunchOption
                {
                    MaximumRamMb = (int)RamSlider.Value,
                    Session = session,
                    JavaPath = javaPath,
                    JVMArguments = manifest.jvm_flags != null && manifest.jvm_flags.Length > 0 ? manifest.jvm_flags : null
                };

                if (!string.IsNullOrEmpty(manifest.server_ip))
                {
                    launchOptions.ServerIp = manifest.server_ip;
                    launchOptions.ServerPort = manifest.server_port > 0 ? manifest.server_port : 25565;
                }

                ProgressText.Text = "Starting game...";
                var process = await _activeCmlLauncher.CreateProcessAsync(targetVersion, launchOptions);

                if (CloseAfterLaunchCheck.IsChecked == true)
                {
                    process.Start();
                    System.Windows.Application.Current.Shutdown();
                    return;
                }

                this.Hide();

                using (var trayIcon = new System.Windows.Forms.NotifyIcon())
                {
                    trayIcon.Icon = System.Drawing.SystemIcons.Application;
                    trayIcon.Text = "Mayanagri is running...";
                    trayIcon.Visible = true;
                    trayIcon.DoubleClick += (s, args) => System.Windows.MessageBox.Show("Minecraft is currently running.", "Mayanagri");

                    process.Start();
                    await process.WaitForExitAsync();

                    trayIcon.Visible = false;
                }
            }
            catch (TaskCanceledException)
            {
                ShowError("Launch was canceled.");
            }
            catch (AggregateException agg)
            {
                string errorMsg = agg.InnerExceptions.FirstOrDefault()?.Message ?? agg.Message;
                ShowError($"Download failed: {errorMsg}");
            }
            catch (Exception ex)
            {
                ShowError($"Launch failed: {ex.Message}");
            }
            finally
            {
                // Proper cleanup of events preventing memory leaks
                if (_activeCmlLauncher != null)
                {
                    _activeCmlLauncher.FileChanged -= Launcher_FileChanged;
                    _activeCmlLauncher = null;
                }

                _launchCts?.Dispose();
                _launchCts = null;

                Dispatcher.Invoke(() =>
                {
                    GameProgressBar.IsIndeterminate = false;
                    ResetUI();
                });
            }
        }

        private void ResetUI()
        {
            _isLaunching = false;
            ProgressArea.Visibility = Visibility.Hidden;
            ProgressArea.Opacity = 0;
            PlayButton.Content = "PLAY";
            ValidateUsername();

            if (!this.IsVisible && CloseAfterLaunchCheck.IsChecked != true)
            {
                this.Show();
            }
        }
    }
}