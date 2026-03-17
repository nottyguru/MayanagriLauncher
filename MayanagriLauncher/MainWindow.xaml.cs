using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
        // Yaha apna repo ka raw link set rakhna hamesha
        private const string BOOTSTRAP_URL = "https://raw.githubusercontent.com/nottyguru/MayanagriLauncher/main/bootstrap.json";
        private readonly string mayanagriDir;
        private readonly string configPath;
        private LauncherConfig _config = new LauncherConfig();
        private bool _isBusy = false;

        // Jab bhi naya build banaye, isko badha diyo (e.g. 1.0.2)
        private readonly string _currentVersion = "1.0.1";
        private string _pendingUpdateUrl = string.Empty;

        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        public MainWindow()
        {
            InitializeComponent();

            // AppData folder set kar raha hu yaha
            mayanagriDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MayanagriLauncher");
            if (!Directory.Exists(mayanagriDir)) Directory.CreateDirectory(mayanagriDir);
            configPath = Path.Combine(mayanagriDir, "launcher_config.json");

            LoadConfig();
            ValidateUI();

            this.Loaded += async (s, e) =>
            {
                VersionText.Text = $"v{_currentVersion}";
                UsernameBox.Focus();
                await CheckForUpdatesAsync();
            };
        }

        private void DragWindow(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
        private void CloseApp(object sender, RoutedEventArgs e) => Close();
        private void MinimizeApp(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void UsernameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ValidateUI();
        private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RamValueText != null) RamValueText.Text = $"{Math.Round(e.NewValue / 1024.0, 1)} GB";
        }

        private void ValidateUI()
        {
            if (_isBusy) return;
            bool isValid = !string.IsNullOrWhiteSpace(UsernameBox.Text) && !UsernameBox.Text.Contains(" ");
            PlayBtn.IsEnabled = isValid || !string.IsNullOrEmpty(_pendingUpdateUrl);
        }

        private void LoadConfig()
        {
            if (File.Exists(configPath))
            {
                try
                {
                    _config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(configPath)) ?? new LauncherConfig();
                    RamSlider.Value = _config.RamMb;
                    UsernameBox.Text = _config.Username;
                }
                catch { /* config corrupt hai toh ignore maar */ }
            }
        }

        private void SaveConfig()
        {
            _config.RamMb = (int)RamSlider.Value;
            _config.Username = UsernameBox.Text;
            File.WriteAllText(configPath, JsonSerializer.Serialize(_config));
        }

        private void LogStatus(string status, double percent = 0)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = status;
                ProgressBar.Value = percent;
                ProgressPanel.Visibility = percent > 0 ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        private string GetOfflineUUID(string username)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
                hash[6] = (byte)(hash[6] & 0x0f | 0x30);
                hash[8] = (byte)(hash[8] & 0x3f | 0x80);
                return new Guid(hash).ToString("N");
            }
        }

        // --- Auto Update Scene ---
        private async Task CheckForUpdatesAsync()
        {
            try
            {
                LogStatus("Checking for updates...");
                BootstrapManifest? bootstrap = JsonSerializer.Deserialize<BootstrapManifest>(await client.GetStringAsync(BOOTSTRAP_URL));

                if (bootstrap != null && Version.TryParse(bootstrap.launcher_version, out Version latest) && latest > new Version(_currentVersion))
                {
                    _pendingUpdateUrl = bootstrap.launcher_download_url;
                    PlayBtn.Content = "UPDATE LAUNCHER";
                    PlayBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)); // Greenish
                    LogStatus($"Update v{bootstrap.launcher_version} ready!", 100);
                    PlayBtn.IsEnabled = true;
                }
                else { LogStatus(""); }
            }
            catch { LogStatus(""); /* Offline ya repo down hai, aage badho */ }
        }

        private async Task ExecuteUpdateAsync()
        {
            _isBusy = true;
            PlayBtn.IsEnabled = false;
            PlayBtn.Content = "DOWNLOADING...";

            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? throw new Exception("Exe path not found.");
                string tempExe = exePath + ".update";
                string batPath = Path.Combine(Path.GetDirectoryName(exePath) ?? "", "update.bat");

                using (var response = await client.GetAsync(_pendingUpdateUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long? totalBytes = response.Content.Headers.ContentLength;

                    using (var fs = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        var buffer = new byte[8192];
                        long read = 0;
                        int bytes;
                        while ((bytes = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, bytes);
                            read += bytes;
                            if (totalBytes.HasValue) LogStatus("Downloading update...", (read * 100.0) / totalBytes.Value);
                        }
                    }
                }

                LogStatus("Restarting...", 100);

                // Ye script purane launcher ko uda ke naya replace kar degi
                string batCode = $@"@echo off
timeout /t 2 /nobreak > NUL
move /Y ""{Path.GetFileName(tempExe)}"" ""{Path.GetFileName(exePath)}""
start """" ""{Path.GetFileName(exePath)}""
del ""%~f0""";
                await File.WriteAllTextAsync(batPath, batCode);

                Process.Start(new ProcessStartInfo { FileName = batPath, UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update fail ho gaya bhai: {ex.Message}");
                _pendingUpdateUrl = string.Empty;
                ResetUI();
            }
        }

        // --- Game Launch Scene ---
        private async void PlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_pendingUpdateUrl)) { await ExecuteUpdateAsync(); return; }
            if (_isBusy || string.IsNullOrWhiteSpace(UsernameBox.Text)) return;

            _isBusy = true;
            SaveConfig();
            PlayBtn.IsEnabled = false;
            PlayBtn.Content = "LAUNCHING...";

            try
            {
                LogStatus("Fetching server info...", 10);
                BootstrapManifest? bootstrap = JsonSerializer.Deserialize<BootstrapManifest>(await client.GetStringAsync(BOOTSTRAP_URL)) ?? new BootstrapManifest();
                MayanagriManifest? manifest = JsonSerializer.Deserialize<MayanagriManifest>(await client.GetStringAsync(bootstrap.manifest_url)) ?? new MayanagriManifest();

                var path = new MinecraftPath(mayanagriDir);

                LogStatus("Setting up Fabric...", 30);
                string fabricVer = $"fabric-loader-{manifest.fabric_version}-{manifest.minecraft_version}";
                string verDir = Path.Combine(path.Versions, fabricVer);
                string jsonPath = Path.Combine(verDir, $"{fabricVer}.json");

                if (!File.Exists(jsonPath))
                {
                    Directory.CreateDirectory(verDir);
                    string fabricJson = await client.GetStringAsync($"https://meta.fabricmc.net/v2/versions/loader/{manifest.minecraft_version}/{manifest.fabric_version}/profile/json");
                    await File.WriteAllTextAsync(jsonPath, fabricJson);
                }

                var launcher = new CMLauncher(path);
                launcher.FileChanged += (e) =>
                {
                    if (e.TotalFileCount > 0) LogStatus($"Downloading assets: {e.ProgressedFileCount}/{e.TotalFileCount}", (e.ProgressedFileCount * 100.0) / e.TotalFileCount);
                };

                await launcher.GetAllVersionsAsync();
                LogStatus("Starting Game...", 100);

                var launchOps = new MLaunchOption
                {
                    MaximumRamMb = (int)RamSlider.Value,
                    Session = new MSession { Username = UsernameBox.Text, UUID = GetOfflineUUID(UsernameBox.Text), AccessToken = "access_token", UserType = "Legacy" },
                    JVMArguments = manifest.jvm_flags,
                    ServerIp = manifest.server_ip,
                    ServerPort = manifest.server_port > 0 ? manifest.server_port : 25565
                };

                var process = await launcher.CreateProcessAsync(fabricVer, launchOps);
                process.Start();
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Launch mein dikkat aayi: {ex.Message}");
                ResetUI();
            }
        }

        private void ResetUI()
        {
            _isBusy = false;
            PlayBtn.Content = "PLAY";
            PlayBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
            LogStatus("");
            ValidateUI();
        }
    }
}