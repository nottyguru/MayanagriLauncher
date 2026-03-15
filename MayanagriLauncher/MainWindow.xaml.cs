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
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.IO.Compression;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Downloader;

namespace MayanagriLauncher
{
    // --- BOOTSTRAP MODELS ---
    public class BootstrapManifest
    {
        public string launcher_version { get; set; } = "1.0.0";
        public string launcher_download_url { get; set; } = string.Empty;
        public string launcher_hash { get; set; } = string.Empty; // NEW!
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

        private string _targetServerIp = "127.0.0.1";
        private int _targetServerPort = 25565;
        private string _dynamicManifestUrl = string.Empty;

        CancellationTokenSource? _cts;
        bool _isLaunching = false;

        private static readonly HttpClient sharedClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.Add("User-Agent", "MayanagriLauncher/1.2");
            return client;
        }

        public MainWindow()
        {
            InitializeComponent();

            mayanagriDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MayanagriLauncher");
            if (!Directory.Exists(mayanagriDir)) Directory.CreateDirectory(mayanagriDir);

            configPath = Path.Combine(mayanagriDir, "launcher_config.txt");

            LoadConfig();
            ValidateUsername();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"Client v{version?.Major}.{version?.Minor}.{version?.Build}";

            this.Loaded += async (s, e) =>
            {
                UsernameBox.Focus();
                await InitializeBootstrapAsync();
            };

            _ = StartServerMonitorAsync();
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
                // UI shows error but allows playing if cached manifest works
                ShowError($"Bootstrap offline: {ex.Message}");
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

                // Download safely to temp file first
                string tempUpdater = updaterPath + ".tmp";
                var response = await sharedClient.GetAsync(bootstrap.updater_download_url);
                response.EnsureSuccessStatusCode();

                using (var fs = new FileStream(tempUpdater, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs);
                }

                // Security Check: Verify Updater Hash to prevent hijacked repository attacks
                if (!string.IsNullOrEmpty(bootstrap.updater_hash) && CalculateSHA256(tempUpdater) != bootstrap.updater_hash.ToLower())
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
                    // We now pass the launcher_hash as the 4th argument to the Updater!
                    Arguments = $"{currentPid} \"{bootstrap.launcher_download_url}\" \"{currentExe}\" \"{bootstrap.launcher_hash}\"",
                    UseShellExecute = true
                };

                Process.Start(psi);

                // Brief delay to ensure Updater hooks before we terminate
                await Task.Delay(500);
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                ShowError($"Update failed: {ex.Message}");
                ResetUI();
            }
        }

        private async Task StartServerMonitorAsync()
        {
            while (true)
            {
                try
                {
                    using var client = new TcpClient();
                    var connectTask = client.ConnectAsync(_targetServerIp, _targetServerPort);
                    var timeoutTask = Task.Delay(2000);

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

                await Task.Delay(10000);
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

            if (isValid)
            {
                UsernameBorder.ClearValue(Border.BorderBrushProperty);
            }
            else
            {
                UsernameBorder.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
            }
        }

        private void LoadConfig()
        {
            if (File.Exists(configPath))
            {
                string[] lines = File.ReadAllLines(configPath);
                if (lines.Length > 0 && int.TryParse(lines[0], out int ram)) RamSlider.Value = ram;
                if (lines.Length > 1) UsernameBox.Text = lines[1];

                if (lines.Length > 2 && bool.TryParse(lines[2], out bool closeChecked))
                    CloseAfterLaunchCheck.IsChecked = closeChecked;
            }
        }

        private void SaveConfig()
        {
            string[] lines = {
                ((int)RamSlider.Value).ToString(),
                UsernameBox.Text,
                (CloseAfterLaunchCheck.IsChecked ?? false).ToString()
            };
            File.WriteAllLines(configPath, lines);
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

        // Feature: Robust Directory Deletion (Bypasses ReadOnly locks on folders)
        private void ForceDeleteDirectory(string targetDir)
        {
            if (!Directory.Exists(targetDir)) return;

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
        }

        private void RepairButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLaunching) return;

            var result = System.Windows.MessageBox.Show("This will forcefully verify and re-download any corrupted game files. Continue?",
                                         "Repair Installation", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            try
            {
                string[] folders = { "mods", "config", "shaderpacks", "resourcepacks" };
                foreach (var folder in folders)
                {
                    ForceDeleteDirectory(Path.Combine(mayanagriDir, folder));
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

                // Policy: Initial-only files preserve player settings. If it exists, skip hash check.
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

                        // Safety: Atomic downloading via Temp file to prevent IO corruption
                        string tempPath = targetPath + ".tmp";
                        using (var response = await sharedClient.GetAsync(fileData.url, HttpCompletionOption.ResponseHeadersRead, token))
                        {
                            response.EnsureSuccessStatusCode();
                            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await response.Content.CopyToAsync(fs, token);
                            }
                        }
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

        // Feature: Safe Version parsing for Snapshot / Pre-releases
        private int GetRequiredJavaVersion(string mcVersion)
        {
            string cleanVersion = mcVersion.Split('-')[0]; // Safely removes strings like "-pre1"
            if (Version.TryParse(cleanVersion, out Version v))
            {
                if (v.Minor >= 21 || (v.Minor == 20 && v.Build >= 5)) return 21;
                if (v.Minor >= 17) return 17;
            }
            return 8; // Safest fallback
        }

        private async Task<string> EnsureJavaAsync(int javaVersion, CancellationToken token)
        {
            string runtimeDir = Path.Combine(mayanagriDir, "runtime", $"jre{javaVersion}");

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

        // Feature: Deterministic Offline UUIDs (Preserves player stats in non-premium mode)
        private string GenerateOfflineUUID(string username)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
                hash[6] = (byte)(hash[6] & 0x0f | 0x30); // Version 3 UUID
                hash[8] = (byte)(hash[8] & 0x3f | 0x80); // Variant 1
                return new Guid(hash).ToString("N");
            }
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLaunching)
            {
                _cts?.Cancel();
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
            _cts = new CancellationTokenSource();

            try
            {
                ProgressText.Text = "Fetching server blueprint...";
                MayanagriManifest manifest;

                using (var response = await sharedClient.GetAsync(_dynamicManifestUrl, _cts.Token))
                {
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync(_cts.Token);
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

                int requiredJava = GetRequiredJavaVersion(manifest.minecraft_version);
                string javaPath = await EnsureJavaAsync(requiredJava, _cts.Token);

                var path = new MinecraftPath(mayanagriDir);
                await SyncFilesAsync(manifest, path.BasePath, _cts.Token);

                ProgressText.Text = "Installing Fabric Engine...";
                GameProgressBar.IsIndeterminate = true;

                string targetVersion = $"fabric-loader-{manifest.fabric_version}-{manifest.minecraft_version}";
                string profileDir = Path.Combine(path.Versions, targetVersion);
                string profileJsonPath = Path.Combine(profileDir, $"{targetVersion}.json");

                if (!File.Exists(profileJsonPath))
                {
                    Directory.CreateDirectory(profileDir);
                    string fabricApiUrl = $"https://meta.fabricmc.net/v2/versions/loader/{manifest.minecraft_version}/{manifest.fabric_version}/profile/json";

                    using (var response = await sharedClient.GetAsync(fabricApiUrl, _cts.Token))
                    {
                        response.EnsureSuccessStatusCode();
                        string jsonProfile = await response.Content.ReadAsStringAsync(_cts.Token);
                        File.WriteAllText(profileJsonPath, jsonProfile);
                    }
                }

                var launcher = new CMLauncher(path);
                await launcher.GetAllVersionsAsync();
                launcher.FileChanged += Launcher_FileChanged;

                GameProgressBar.IsIndeterminate = false;

                var session = new MSession
                {
                    Username = UsernameBox.Text,
                    UUID = GenerateOfflineUUID(UsernameBox.Text), // Use deterministic UUID
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

                var process = await launcher.CreateProcessAsync(targetVersion, launchOptions);

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

                launcher.FileChanged -= Launcher_FileChanged;
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
                _cts?.Dispose();
                _cts = null;

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