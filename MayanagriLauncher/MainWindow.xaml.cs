using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Net.Http;
using CmlLib.Core;
using CmlLib.Core.Auth;

namespace MayanagriLauncher
{
    public class LauncherConfig
    {
        public int RamMb { get; set; } = 4096;
        public string Username { get; set; } = string.Empty;
    }

    public class BootstrapManifest
    {
        public string launcher_version { get; set; } = "1.0.0";
        public string launcher_download_url { get; set; } = string.Empty;
        public string manifest_url { get; set; } = string.Empty;
    }

    public class MayanagriManifest
    {
        public string minecraft_version { get; set; } = string.Empty;
        public string fabric_version { get; set; } = string.Empty;
        public string server_ip { get; set; } = string.Empty;
        public int server_port { get; set; } = 25565;
        public string[]? jvm_flags { get; set; }
    }

    public partial class MainWindow : Window
    {
        private const string BOOTSTRAP_URL = "https://raw.githubusercontent.com/nottyguru/MayanagriLauncher/main/bootstrap.json";
        private readonly string mayanagriDir;
        private readonly string configPath;
        private LauncherConfig _launcherConfig = new LauncherConfig();
        private bool _isLaunching = false;

        // Auto-updater variables (Change this to 1.0.1 when building the update)
        private readonly string _currentVersion = "1.0.1";
        private string _pendingUpdateUrl = string.Empty;

        private static readonly HttpClient sharedClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                mayanagriDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MayanagriLauncher");
                if (!Directory.Exists(mayanagriDir)) Directory.CreateDirectory(mayanagriDir);
                configPath = Path.Combine(mayanagriDir, "launcher_config.json");

                LoadConfig();
                ValidateUsername();

                this.Loaded += async (s, e) =>
                {
                    UsernameBox.Focus();
                    await CheckForUpdatesAsync();
                };
            }
            catch (Exception ex)
            {
                // Prevents silent crashes by exposing the exact startup error
                MessageBox.Show($"Critical Startup Error:\n{ex.Message}\n\n{ex.StackTrace}", "Startup Failure", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => this.Close();
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void UsernameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ValidateUsername();

        private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RamValueText != null) RamValueText.Text = $"{Math.Round(e.NewValue / 1024.0, 1)} GB";
        }

        private void ValidateUsername()
        {
            if (_isLaunching) return;
            bool isValid = !string.IsNullOrWhiteSpace(UsernameBox.Text) && !UsernameBox.Text.Contains(" ");
            PlayButton.IsEnabled = isValid || !string.IsNullOrEmpty(_pendingUpdateUrl);
        }

        private void LoadConfig()
        {
            if (File.Exists(configPath))
            {
                try
                {
                    _launcherConfig = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(configPath)) ?? new LauncherConfig();
                    RamSlider.Value = _launcherConfig.RamMb;
                    UsernameBox.Text = _launcherConfig.Username;
                }
                catch { /* Ignore corrupt config */ }
            }
        }

        private void SaveConfig()
        {
            _launcherConfig.RamMb = (int)RamSlider.Value;
            _launcherConfig.Username = UsernameBox.Text;
            try { File.WriteAllText(configPath, JsonSerializer.Serialize(_launcherConfig)); } catch { }
        }

        private void UpdateProgress(string status, double percentage)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = status;
                LaunchProgressBar.Value = percentage;
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

        // --- Auto Updater Logic ---
        private async Task CheckForUpdatesAsync()
        {
            try
            {
                string json = await sharedClient.GetStringAsync(BOOTSTRAP_URL);
                var bootstrap = JsonSerializer.Deserialize<BootstrapManifest>(json);

                if (bootstrap != null && Version.TryParse(bootstrap.launcher_version, out Version latestVersion))
                {
                    Version current = new Version(_currentVersion);
                    if (latestVersion > current)
                    {
                        _pendingUpdateUrl = bootstrap.launcher_download_url;
                        PlayButton.Content = "UPDATE LAUNCHER";
                        PlayButton.Background = new System.Windows.Media.SolidColorBrush(
                            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981"));

                        ProgressArea.Visibility = Visibility.Visible;
                        UpdateProgress($"Version {bootstrap.launcher_version} available!", 100);
                        PlayButton.IsEnabled = true;
                    }
                }
            }
            catch { /* Fail silently */ }
        }

        private async Task PerformUpdateAsync()
        {
            _isLaunching = true;
            PlayButton.IsEnabled = false;
            PlayButton.Content = "DOWNLOADING...";

            try
            {
                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? throw new Exception("Cannot find executable path.");
                string tempExePath = currentExePath + ".update";
                string batPath = Path.Combine(Path.GetDirectoryName(currentExePath) ?? "", "update.bat");

                using (var response = await sharedClient.GetAsync(_pendingUpdateUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long? totalBytes = response.Content.Headers.ContentLength;

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempExePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;

                            if (totalBytes.HasValue)
                            {
                                double percentage = Math.Round((double)totalRead / totalBytes.Value * 100, 1);
                                UpdateProgress("Downloading update...", percentage);
                            }
                        }
                    }
                }

                UpdateProgress("Applying update...", 100);

                string exeName = Path.GetFileName(currentExePath);
                string batContent = $@"@echo off
timeout /t 2 /nobreak > NUL
move /Y ""{Path.GetFileName(tempExePath)}"" ""{exeName}""
start """" ""{exeName}""
del ""%~f0""";

                await File.WriteAllTextAsync(batPath, batContent);

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = batPath,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _pendingUpdateUrl = string.Empty;
                ResetUI();
            }
        }

        // --- Game Launch Logic ---
        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_pendingUpdateUrl))
            {
                await PerformUpdateAsync();
                return;
            }

            if (_isLaunching || string.IsNullOrWhiteSpace(UsernameBox.Text) || UsernameBox.Text.Contains(" ")) return;

            _isLaunching = true;
            SaveConfig();

            PlayButton.IsEnabled = false;
            PlayButton.Content = "LAUNCHING...";
            ProgressArea.Visibility = Visibility.Visible;
            UpdateProgress("Fetching manifest...", 5);

            try
            {
                string json = await sharedClient.GetStringAsync(BOOTSTRAP_URL);
                var bootstrap = JsonSerializer.Deserialize<BootstrapManifest>(json) ?? new BootstrapManifest();

                string manifestJson = await sharedClient.GetStringAsync(bootstrap.manifest_url);
                var manifest = JsonSerializer.Deserialize<MayanagriManifest>(manifestJson) ?? new MayanagriManifest();

                var path = new MinecraftPath(mayanagriDir);

                UpdateProgress("Verifying Fabric installation...", 15);
                string targetVersion = $"fabric-loader-{manifest.fabric_version}-{manifest.minecraft_version}";
                string versionDir = Path.Combine(path.Versions, targetVersion);
                string profileJsonPath = Path.Combine(versionDir, $"{targetVersion}.json");

                if (!File.Exists(profileJsonPath))
                {
                    Directory.CreateDirectory(versionDir);
                    string fabricApiUrl = $"https://meta.fabricmc.net/v2/versions/loader/{manifest.minecraft_version}/{manifest.fabric_version}/profile/json";
                    string profileData = await sharedClient.GetStringAsync(fabricApiUrl);
                    await File.WriteAllTextAsync(profileJsonPath, profileData);
                }

                var launcher = new CMLauncher(path);
                launcher.FileChanged += (fileEvent) =>
                {
                    if (fileEvent.TotalFileCount > 0)
                    {
                        string action = fileEvent.ProgressedFileCount == fileEvent.TotalFileCount ? "Verifying" : "Syncing";
                        string kind = fileEvent.FileKind.ToString().ToLower();
                        UpdateProgress($"{action} {kind}...", ((double)fileEvent.ProgressedFileCount / fileEvent.TotalFileCount) * 100);
                    }
                };

                await launcher.GetAllVersionsAsync();
                UpdateProgress("Preparing launch arguments...", 100);

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
                    JVMArguments = manifest.jvm_flags
                };

                if (!string.IsNullOrEmpty(manifest.server_ip))
                {
                    launchOptions.ServerIp = manifest.server_ip;
                    launchOptions.ServerPort = manifest.server_port > 0 ? manifest.server_port : 25565;
                }

                var process = await launcher.CreateProcessAsync(targetVersion, launchOptions);

                process.Start();
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Launch sequence failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ResetUI();
            }
        }

        private void ResetUI()
        {
            _isLaunching = false;
            PlayButton.Content = "PLAY";
            PlayButton.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2563EB"));
            ProgressArea.Visibility = Visibility.Collapsed;
            ValidateUsername();
        }
    }
}