// TRPName: TRPServerPanel
// Author: TEAM_RUST_PLUGINS
// Changelog:
// - v17.0.0: Fixed WebView2 IPC command routing to run safely on WPF UI dispatcher thread.

using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Extensions.DependencyInjection;
using TRPServerPanel.ViewModels;
using TRPServerPanel.Views;
using TRPServerPanel.Services;
using TRPServerPanel.Models;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TRPServerPanel
{
    public partial class MainWindow : Window
    {
        private MainViewModel _vm;
        private DispatcherTimer _statsTimer = new();
        private bool _isSelectingFolder = false;
        private string _lastServersHash = string.Empty;
        private string _lastSelectedServer = "NONE";
        private bool _canClose = false;
        // Tracks open secondary page windows (key = page filename, e.g. "Players.html")
        private readonly System.Collections.Generic.Dictionary<string, Views.TRPWebWindow> _pageWindows = new();

        private string _lastServerStatus = string.Empty;
        private string _lastServerUptime = string.Empty;
        private int _lastPlayerCount = -1;
        private double _lastCpuValue = -1.0;
        private double _lastRamValue = -1.0;
        private int _lastFps = -1;
        private int _lastEntities = -1;
        private int _lastPing = -1;
        private double _lastPanelCpu = -1.0;
        private double _lastPanelRam = -1.0;
        private string _lastInstallStatus = string.Empty;
        private double _lastInstallProgress = -1.0;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null, // [CRITICAL] Maintain PascalCase for JS compatibility
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };

        // Win32 API for reliable window dragging in Hybrid apps
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        public MainWindow()
        {
            InitializeComponent();
            _vm = (MainViewModel)App.ServiceProvider.GetRequiredService(typeof(MainViewModel));
            this.DataContext = _vm;

            _vm.OnSecurityUpdate += (plugin, risk) => {
                Dispatcher.BeginInvoke(() => {
                    if (MainBrowser?.CoreWebView2 != null)
                    {
                        var resp = new { type = "security_update", plugin = plugin, risk = risk };
                        MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                    }
                });
            };

            var notifyService = App.ServiceProvider.GetRequiredService<NotificationService>();
            notifyService.OnNotificationReceived += (n) => {
                Dispatcher.BeginInvoke(() => {
                    if (MainBrowser?.CoreWebView2 != null)
                    {
                        var resp = new { 
                            type = "notification", 
                            title = n.Title, 
                            message = n.Message, 
                            notificationType = n.Type.ToString().ToLower() 
                        };
                        MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp, _jsonOptions));
                    }
                });
            };

            // Subscribe to UI event requests (CRITICAL FOR BUTTONS)
            _vm.RequestOpenWindow += OnRequestOpenWindow;
            this.Closing += MainWindow_Closing;

            AppLogService.OnLogAdded += (entry) => {
                Dispatcher.BeginInvoke(() => {
                    if (MainBrowser?.CoreWebView2 != null)
                    {
                        var resp = new { type = "app_logs_sync", entry = entry };
                        MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp, _jsonOptions));
                    }
                });
            };

            AppLogService.Log("Diagnostic Bridge Initialized", AppLogLevel.INFO, "SYSTEM");

            // Sync installation status to Frontend
            _vm.PropertyChanged += (s, e) =>
            {
                Dispatcher.InvokeAsync(() => {
                    if (MainBrowser?.CoreWebView2 == null) return;

                    if (e.PropertyName == nameof(MainViewModel.InstallStatus) || e.PropertyName == nameof(MainViewModel.InstallProgress))
                    {
                        var data = new { 
                            type = "install_update", 
                            status = _vm.InstallStatus, 
                            progress = _vm.InstallProgress 
                        };
                        string json = System.Text.Json.JsonSerializer.Serialize(data);
                        MainBrowser.CoreWebView2.PostWebMessageAsJson(json);
                    }
                    
                    if (e.PropertyName == nameof(MainViewModel.InstallLog))
                    {
                        var data = new { 
                            type = "install_log", 
                            message = _vm.InstallLog 
                        };
                        string json = System.Text.Json.JsonSerializer.Serialize(data);
                        MainBrowser.CoreWebView2.PostWebMessageAsJson(json);
                    }

                    if (e.PropertyName == nameof(MainViewModel.AgentStatus))
                    {
                        var data = new { 
                            type = "agent_status", 
                            status = _vm.AgentStatus 
                        };
                        string json = System.Text.Json.JsonSerializer.Serialize(data);
                        MainBrowser.CoreWebView2.PostWebMessageAsJson(json);
                    }

                    if (e.PropertyName == nameof(MainViewModel.SelectedServer))
                    {
                        SyncVmToUi();
                    }

                    if (e.PropertyName == nameof(MainViewModel.CurrentLanguage))
                    {
                        var data = new { 
                            type = "language_updated", 
                            lang = _vm.CurrentLanguage 
                        };
                        string json = System.Text.Json.JsonSerializer.Serialize(data, _jsonOptions);
                        MainBrowser.CoreWebView2.PostWebMessageAsJson(json);
                    }
                });
            };

            _vm.Console.OnLogBatchReceived += batch =>
            {
                Dispatcher.BeginInvoke(() => {
                    if (MainBrowser?.CoreWebView2 != null)
                    {
                        var logList = batch.Select(l => new { message = l.Message, level = l.Type.ToString() }).ToList();
                        var data = new { type = "batch_log", logs = logList };
                        MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data, _jsonOptions));
                    }
                });
            };

            _vm.RequestConfigSync += cfg =>
            {
                Dispatcher.BeginInvoke(() => {
                    if (MainBrowser?.CoreWebView2 != null)
                    {
                        var data = new { type = "force_config_sync", payload = cfg };
                        MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data, _jsonOptions));
                    }
                });
            };

            InitializeAsync();
            SetupStatsSync();

            // Push player list to WebView immediately after RefreshPlayersAsync() completes.
            // We serialize PlayerIdentities directly to avoid going through GetPlayerListAsync()
            // which previously caused an infinite loop (empty list → RefreshPlayersAsync → repeat).
            _vm.PlayerIdentitiesUpdated += () =>
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (MainBrowser?.CoreWebView2 == null) return;
                    List<PlayerIdentity> snapshot;
                    lock (_vm.PlayerIdentities) { snapshot = _vm.PlayerIdentities.ToList(); }
                    var opts = new System.Text.Json.JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.WriteAsString
                    };
                    string payload = System.Text.Json.JsonSerializer.Serialize(snapshot, opts);
                    var resp = new { type = "player_list_update", payload };
                    MainBrowser.CoreWebView2.PostWebMessageAsJson(
                        System.Text.Json.JsonSerializer.Serialize(resp, opts));
                });
            };
        }

        private bool _isShuttingDown = false;

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_canClose) 
            {
                if (!_isShuttingDown)
                {
                    _isShuttingDown = true;
                    e.Cancel = true;
                    this.IsEnabled = false;
                    
                    // Close all open page windows before exiting
                    foreach (var pw in _pageWindows.Values.ToList())
                    {
                        try { pw.Close(); } catch { }
                    }
                    _pageWindows.Clear();

                    // Show visual loader
                    SystemLoaderText.Text = "ЗАВЕРШЕНИЕ РАБОТЫ...";
                    SystemLoaderSubText.Text = "Остановка серверов и сохранение данных...";
                    SystemLoader.Visibility = Visibility.Visible;
                    
                    await _vm.ShutdownAsync();
                    
                    System.Windows.Application.Current.Shutdown();
                    Environment.Exit(0);
                }
                return;
            }

            if (!_vm.IsServerRunning)
            {
                _canClose = true;
                _isShuttingDown = true;
                e.Cancel = true;
                this.IsEnabled = false;

                // Close all open page windows before exiting
                foreach (var pw in _pageWindows.Values.ToList())
                {
                    try { pw.Close(); } catch { }
                }
                _pageWindows.Clear();

                // Show visual loader
                SystemLoaderText.Text = "ЗАВЕРШЕНИЕ РАБОТЫ...";
                SystemLoaderSubText.Text = "Остановка серверов и сохранение данных...";
                SystemLoader.Visibility = Visibility.Visible;

                await _vm.ShutdownAsync();
                System.Windows.Application.Current.Shutdown();
                Environment.Exit(0);
                return;
            }

            // Stop immediate close and show styled UI
            e.Cancel = true; 
            
            var data = new { type = "request_exit_confirmation" };
            MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(data, _jsonOptions));
        }

        private void OnRequestOpenWindow(string windowName)
        {
            // [HYBRID V5] Navigation is now handled internally by WebView2 (window.location.href)
            // This method is kept for backward compatibility with VM commands if needed.
            System.Diagnostics.Debug.WriteLine($"[HYBRID] Navigation request for '{windowName}' handled by Frontend.");
        }

        private async void InitializeAsync()
        {
            try
            {
                var env = await App.GetSharedEnvironmentAsync();
                await MainBrowser.EnsureCoreWebView2Async(env);
                
                try
                {
                    var uaJson = await MainBrowser.CoreWebView2.ExecuteScriptAsync("navigator.userAgent");
                    var ua = JsonSerializer.Deserialize<string>(uaJson);
                    if (!string.IsNullOrEmpty(ua))
                    {
                        App.UserAgent = ua;
                    }
                }
                catch { }
                
                string resourcesPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
                if (System.IO.Directory.Exists(resourcesPath))
                {
                    // [HYBRID V6] Use HTTP for virtual host to avoid SSL/TLS local certificate restrictions
                    MainBrowser.CoreWebView2.SetVirtualHostNameToFolderMapping("trp.app", resourcesPath, CoreWebView2HostResourceAccessKind.Allow);
                    
                    // [HYBRID V7] Direct skip to Dashboard as Splash already handled diagnostics
                    MainBrowser.Source = new Uri("http://trp.app/Dashboard.html");
                }
                else
                {
                    System.Windows.MessageBox.Show("Critical Error: Resources folder missing at " + resourcesPath);
                }
                
                // 2. Setup Security & UX (Allow copying)
                MainBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                MainBrowser.CoreWebView2.Settings.IsZoomControlEnabled = false;

                // v10.7: Stats are now handled exclusively by the StatsTimer to prevent UI flooding
                // Removed PropertyChanged -> SyncVmToUi bridge
            }
            catch (Exception ex)
            {
                App.ShowFatalError(ex, "MainWindow Initialization");
            }
        }

        private void SetupStatsSync()
        {
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += (s, e) => SyncVmToUi();
            _statsTimer.Start();
        }

        private void SyncVmToUi(bool force = false)
        {
            if (MainBrowser.CoreWebView2 == null) return;

            // 1. Mandatory Server List Sync (Optimized with Hash)
            var serversSummary = _vm.ServerList.Servers.Select(s => new { s.Name, s.Path, s.Status }).ToList();
            string currentHash = JsonSerializer.Serialize(serversSummary, _jsonOptions);
            
            // v10.4.3: Added deep diagnostic for selection state
            var activeSrv = _vm.SelectedServer;
            string currentActive = activeSrv?.Name ?? "NONE";
            
            if (activeSrv == null && _vm.ServerList.SelectedServer != null) {
                // Emergency sync if main VM lost selection but sub-VM still has it
                _vm.SelectedServer = _vm.ServerList.SelectedServer;
                activeSrv = _vm.SelectedServer;
                currentActive = activeSrv?.Name ?? "NONE";
            }
            
            // v10.4.4: Emergency recovery if state is lost
            if (_vm.SelectedServer == null && _vm.ServerList.SelectedServer != null)
            {
                _vm.SelectedServer = _vm.ServerList.SelectedServer;
                currentActive = _vm.SelectedServer?.Name ?? "NONE";
            }

            if (force || currentHash != _lastServersHash || currentActive != _lastSelectedServer) 
            {
                var listData = new
                {
                    type = "server_list_update",
                    AllServers = serversSummary,
                    ActiveServer = currentActive
                };
                MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(listData, _jsonOptions));
                _lastServersHash = currentHash;
                _lastSelectedServer = currentActive;
            }

            // 2. Conditional Active Stats Sync
            if (_vm.SelectedServer != null)
            {
                try
                {
                    string currentStatus = _vm.SelectedServer.Status;
                    string currentUptime = _vm.GetServerUptime();
                    int currentPlayers = _vm.SelectedServer.PlayerCount;
                    double currentCpu = _vm.SelectedServer.CpuHistory.LastOrDefault();
                    double currentRam = _vm.SelectedServer.RamHistory.LastOrDefault();
                    int currentFps = _vm.SelectedServer.Fps;
                    int currentEntities = _vm.SelectedServer.Entities;
                    int currentPing = _vm.SelectedServer.Ping;
                    double currentPanelCpu = _vm.PanelCpu;
                    double currentPanelRam = _vm.PanelRam;

                    if (force ||
                        currentStatus != _lastServerStatus ||
                        currentUptime != _lastServerUptime ||
                        currentPlayers != _lastPlayerCount ||
                        currentCpu != _lastCpuValue ||
                        currentRam != _lastRamValue ||
                        currentFps != _lastFps ||
                        currentEntities != _lastEntities ||
                        currentPing != _lastPing ||
                        currentPanelCpu != _lastPanelCpu ||
                        currentPanelRam != _lastPanelRam)
                    {
                        var stats = new
                        {
                            type = "stats_update",
                            ServerStatus = currentStatus,
                            ServerName = _vm.SelectedServer.Name,
                            Uptime = currentUptime,
                            CpuLoad = currentCpu,
                            RamUsageValue = currentRam,
                            PlayerCount = currentPlayers,
                            MaxPlayers = _vm.SelectedServer.MaxPlayers,
                            Fps = currentFps,
                            Entities = currentEntities,
                            Ping = currentPing,
                            Config = _vm.SelectedServer.Config,
                            CpuHistory = _vm.SelectedServer.CpuHistory.ToArray(),
                            RamHistory = _vm.SelectedServer.RamHistory.ToArray(),
                            FpsHistory = _vm.SelectedServer.FpsHistory.ToArray(),
                            PlayerHistory = _vm.SelectedServer.PlayerHistory.ToArray(),
                            EntitiesHistory = _vm.SelectedServer.EntitiesHistory.ToArray(),
                            PingHistory = _vm.SelectedServer.PingHistory.ToArray(),
                            NetworkUsage = _vm.SelectedServer.NetworkUsage,
                            NetworkHistory = _vm.SelectedServer.NetworkHistory.ToArray(),
                            AppUptime = _vm.AppUptime,
                            PanelCpu = currentPanelCpu,
                            PanelRam = currentPanelRam
                        };
                        
                        MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(stats, _jsonOptions));

                        _lastServerStatus = currentStatus;
                        _lastServerUptime = currentUptime;
                        _lastPlayerCount = currentPlayers;
                        _lastCpuValue = currentCpu;
                        _lastRamValue = currentRam;
                        _lastFps = currentFps;
                        _lastEntities = currentEntities;
                        _lastPing = currentPing;
                        _lastPanelCpu = currentPanelCpu;
                        _lastPanelRam = currentPanelRam;
                    }
                }
                catch (Exception ex)
                {
                    _vm.AddLog($"[BRIDGE ERROR] Failed to send stats_update: {ex.Message}", Models.LogType.Error);
                }
            }

            // 3. Global Installer Stats
            if (force || _vm.InstallStatus != _lastInstallStatus || _vm.InstallProgress != _lastInstallProgress)
            {
                var installStats = new {
                    type = "install_progress",
                    status = _vm.InstallStatus,
                    progress = _vm.InstallProgress
                };
                MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(installStats));
                _lastInstallStatus = _vm.InstallStatus;
                _lastInstallProgress = _vm.InstallProgress;
            }

            // 4. Commands Data Sync (with Hash to prevent UI flickering)
            string commandsHash = _vm.AvailableCommandsJson;
            if (force || commandsHash != _lastCommandsHash)
            {
                var cmdMsg = new { type = "commands_update", data = _vm.AvailableCommandsJson };
                MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(cmdMsg));
                _lastCommandsHash = commandsHash;
            }
        }

        private string _lastCommandsHash = "";

        private void MainBrowser_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                System.Diagnostics.Debug.WriteLine($"[HYBRID] Message Received: {json}");

                var message = JsonDocument.Parse(json).RootElement;
                
                // v16.5: Support both 'action' and 'type' for cross-page compatibility
                string action = "";
                if (message.TryGetProperty("action", out var actProp)) action = actProp.GetString() ?? "";
                else if (message.TryGetProperty("type", out var typeProp)) action = typeProp.GetString() ?? "";

                // Diagnostic log
                try {
                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge_debug.log");
                    File.AppendAllText(logPath, $"[{DateTime.Now}] BRIDGE RECV: action='{action}' json='{json}'\n");
                } catch { }

                if (string.IsNullOrEmpty(action)) return;

                switch (action)
                {
                    case "ready_to_launch":
                        // Welcome screen completed its animations
                        Dispatcher.BeginInvoke(() => {
                            if (MainBrowser?.CoreWebView2 != null) {
                                MainBrowser.Source = new Uri("http://trp.app/Dashboard.html");
                            }
                        });
                        break;
                    case "ai_ask":
                        if (message.TryGetProperty("payload", out var aiPayload))
                        {
                            string question = SanitizeInput(aiPayload.GetString() ?? "");
                            if (string.IsNullOrWhiteSpace(question)) return;
                            _ = Dispatcher.InvokeAsync(async () => {
                                try {
                                    var response = await _vm.GetAiResponseAsync(question);
                                    var sanitizedResponse = SanitizeInput(response);
                                    _vm.AddLog($"[TRP AI]: {sanitizedResponse}", LogType.Success);
                                } catch (Exception ex) {
                                    _vm.AddLog($"[ERROR] AI Bridge Failed: {SanitizeInput(ex.Message)}", LogType.Error);
                                }
                            });
                        }
                        break;
                    case "install":
                        {
                            if (message.TryGetProperty("path", out var pathProp))
                            {
                                string installPath = pathProp.GetString() ?? @"C:\TRP_Server";
                                string serverName = (message.TryGetProperty("server_name", out var nameProp) ? nameProp.GetString() : "My_Rust_Server") ?? "My_Rust_Server";
                                string modType = (message.TryGetProperty("mod_type", out var modProp) ? modProp.GetString() : "vanilla") ?? "vanilla";

                                if (!IsValidInstallPath(installPath) || !IsValidServerName(serverName))
                                {
                                    _vm.AddLog("[SECURITY] Rejected installation request: Invalid path or server name.", LogType.Error);
                                    return;
                                }

                                modType = modType.ToLower();
                                if (modType != "vanilla" && modType != "oxide" && modType != "carbon")
                                {
                                    modType = "vanilla";
                                }

                                _ = Dispatcher.InvokeAsync(async () => await _vm.TriggerInstallAsync(serverName, installPath, modType));
                            }
                        }
                        break;
                    case "setLanguage":
                        if (message.TryGetProperty("lang", out var langP))
                        {
                            _vm.CurrentLanguage = langP.GetString() ?? "ru";
                        }
                        break;
                    case "download_steamcmd":
                        _ = Dispatcher.InvokeAsync(async () => {
                            await _vm.PrepareSteamCmdOnly();
                        });
                        break;
                    case "select_folder":
                        if (_isSelectingFolder) return;
                        _isSelectingFolder = true;

                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                                {
                                    dialog.Description = "Select Server Installation Directory";
                                    dialog.UseDescriptionForTitle = true;
                                    dialog.ShowNewFolderButton = true;

                                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                                    {
                                        var data = new { type = "folder_selected", path = dialog.SelectedPath };
                                        string jsonResponse = System.Text.Json.JsonSerializer.Serialize(data);
                                        MainBrowser?.CoreWebView2?.PostWebMessageAsJson(jsonResponse);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Windows.MessageBox.Show($"Folder Dialog Error: {ex.Message}", "System Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                            finally
                            {
                                _isSelectingFolder = false;
                            }
                        });
                        break;
                    case "select_map_file":
                        _ = Dispatcher.InvokeAsync(async () => {
                            if (_vm.SelectedServer == null)
                            {
                                _vm.AddLog("[WARN] select_map_file: No server selected.", LogType.Warning);
                                return;
                            }

                            var dialog = new Microsoft.Win32.OpenFileDialog
                            {
                                Title = "Выберите файл кастомной карты (.map)",
                                Filter = "Rust Map Files (*.map)|*.map|All Files (*.*)|*.*",
                                Multiselect = false
                            };

                            if (dialog.ShowDialog() == true)
                            {
                                string selectedFilePath = dialog.FileName;
                                try
                                {
                                    string serverPath = _vm.SelectedServer.Path;
                                    string mapsDir = Path.Combine(serverPath, "maps");
                                    if (!Directory.Exists(mapsDir))
                                    {
                                        Directory.CreateDirectory(mapsDir);
                                    }

                                    string fileName = Path.GetFileName(selectedFilePath);
                                    string targetPath = Path.Combine(mapsDir, fileName);

                                    // Copy the map file to server's maps directory
                                    File.Copy(selectedFilePath, targetPath, true);
                                    
                                    // Update the server configuration
                                    if (_vm.SelectedServer.Config != null)
                                    {
                                        _vm.SelectedServer.Config.MapLevel = "Procedural Map";
                                        string cleanPath = targetPath.Replace('\\', '/');
                                        _vm.SelectedServer.Config.LevelUrl = "file:///" + cleanPath;
                                        await _vm.SaveServerConfigAsync(_vm.SelectedServer.Config);
                                    }

                                    _vm.AddLog($"[SYSTEM] Кастомная карта '{Path.GetFileNameWithoutExtension(fileName)}' успешно установлена через локальный URL.", LogType.Success);
                                    
                                    // Send response back to frontend to update UI
                                    if (MainBrowser?.CoreWebView2 != null)
                                    {
                                        var resp = new { 
                                            type = "map_file_selected", 
                                            mapName = "Procedural Map",
                                            config = _vm.SelectedServer.Config
                                        };
                                        MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _vm.AddLog($"[ERROR] Ошибка при установке кастомной карты: {ex.Message}", LogType.Error);
                                }
                            }
                        });
                        break;
                    case "minimize":
                        Dispatcher.BeginInvoke(() => this.WindowState = WindowState.Minimized);
                        break;
                    case "maximize":
                        Dispatcher.BeginInvoke(() => 
                        {
                            if (this.WindowState == WindowState.Maximized)
                                this.WindowState = WindowState.Normal;
                            else
                                this.WindowState = WindowState.Maximized;
                        });
                        break;
                    case "close":
                    case "quit":
                        Dispatcher.BeginInvoke(() => this.Close());
                        break;
                    case "confirm_exit":
                        Dispatcher.BeginInvoke(() => 
                        {
                            _canClose = true;
                            this.Close();
                        });
                        break;
                    case "exit_confirm_result":
                        Dispatcher.BeginInvoke(() => 
                        {
                            bool confirmed = false;
                            if (message.TryGetProperty("result", out var resultProp))
                            {
                                confirmed = resultProp.GetBoolean();
                            }
                            if (confirmed)
                            {
                                _canClose = true;
                                this.Close();
                            }
                        });
                        break;
                    case "drag":
                        Dispatcher.BeginInvoke(() => 
                        { 
                            try 
                            { 
                                if (this.WindowState == WindowState.Maximized)
                                {
                                    this.WindowState = WindowState.Normal;
                                }
                                
                                var helper = new WindowInteropHelper(this);
                                ReleaseCapture();
                                SendMessage(helper.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                            } 
                            catch { } 
                        });
                        break;
                    case "verify":
                        _ = _vm.RunDiagnostics(result => {
                            Dispatcher.BeginInvoke(() => {
                                if (MainBrowser?.CoreWebView2 != null)
                                {
                                    string jsonResp = JsonSerializer.Serialize(result);
                                    MainBrowser.CoreWebView2.PostWebMessageAsJson(jsonResp);
                                }
                            });
                        });
                        break;
                    case "command":
                        if (message.TryGetProperty("name", out var cmdNameProp))
                        {
                            string? cmdName = cmdNameProp.GetString();
                            if (cmdName == "ConsoleCommand")
                            {
                                if (message.TryGetProperty("payload", out var payloadProp))
                                {
                                    _vm.ExecuteConsoleCommand(payloadProp.GetString() ?? "");
                                }
                            }
                            else
                            {
                                ExecuteVmCommand(cmdName ?? "");
                            }
                        }
                        break;
                    case "get_player_list":
                        _vm.AddLog("[DEBUG] Bridge: Received 'get_player_list' request", LogType.System);
                        _ = Dispatcher.InvokeAsync(async () => {
                            if (MainBrowser?.CoreWebView2 != null) {
                                await _vm.RefreshPlayersAsync();
                                string playersJson = await _vm.GetPlayerListAsync();
                                var resp = new { type = "player_list_update", payload = playersJson };
                                var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                                string finalJson = JsonSerializer.Serialize(resp, options);
                                MainBrowser.CoreWebView2.PostWebMessageAsJson(finalJson);
                            }
                        });
                        break;

                    case "get_server_metadata":
                        _vm.AddLog("[DEBUG] Bridge: Received 'get_server_metadata' request", LogType.System);
                        _ = Dispatcher.InvokeAsync(async () => {
                            if (MainBrowser?.CoreWebView2 != null) {
                                string metaJson = await _vm.GetMetaDataAsync();
                                var resp = new { type = "server_metadata_update", payload = metaJson };
                                var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                                string finalJson = JsonSerializer.Serialize(resp, options);
                                MainBrowser.CoreWebView2.PostWebMessageAsJson(finalJson);
                            }
                        });
                        break;

                    case "player_kick":
                        if (message.TryGetProperty("steamId", out var kId) && message.TryGetProperty("reason", out var kReason)) {
                            string steamId = kId.GetString() ?? "";
                            string reason = SanitizeInput(kReason.GetString() ?? "");
                            if (IsValidSteamId(steamId)) {
                                _ = _vm.ExecutePlayerActionAsync(steamId, "kick", reason);
                            }
                        }
                        break;

                    case "player_ban":
                        if (message.TryGetProperty("steamId", out var bId) && message.TryGetProperty("reason", out var bReason)) {
                            string steamId = bId.GetString() ?? "";
                            string reason = SanitizeInput(bReason.GetString() ?? "");
                            string duration = message.TryGetProperty("duration", out var dur) ? dur.GetString()! : "0";
                            if (IsValidSteamId(steamId)) {
                                _ = _vm.ExecutePlayerActionAsync(steamId, "ban", reason, duration);
                            }
                        }
                        break;

                    case "player_action":
                        if (message.TryGetProperty("steamId", out var aId) && message.TryGetProperty("action", out var aAct) && message.TryGetProperty("extra", out var aExtra)) {
                            string steamId = aId.GetString() ?? "";
                            string actionType = aAct.GetString() ?? "";
                            string extra = SanitizeInput(aExtra.GetString() ?? "");
                            if (IsValidSteamId(steamId)) {
                                _ = _vm.ExecutePlayerActionAsync(steamId, actionType, extra);
                            }
                        }
                        break;

                    case "mute":
                    case "unmute":
                    case "teleport":
                    case "teleport2me":
                    case "global.playerbpwipe":
                        if (message.TryGetProperty("steamId", out var bpSteamId)) {
                            string steamId = bpSteamId.GetString() ?? "";
                            string extra = message.TryGetProperty("extra", out var bpExtra) ? SanitizeInput(bpExtra.GetString() ?? "") : "";
                            if (IsValidSteamId(steamId)) {
                                _ = _vm.ExecutePlayerActionAsync(steamId, action, extra);
                            }
                        }
                        break;

                    case "get_local_plugins":
                    case "search_plugins":
                        _ = Dispatcher.InvokeAsync(async () => {
                            try {
                                if (MainBrowser?.CoreWebView2 != null) {
                                    if (message.TryGetProperty("query", out var queryProp) && message.TryGetProperty("source", out var sourceProp))
                                    {
                                        string query = queryProp.GetString() ?? "";
                                        string source = sourceProp.GetString() ?? "umod";
                                        _vm.AddLog($"[BRIDGE] Plugin Search: '{query}' ({source})", LogType.System);
                                        await _vm.SearchMarketplaceAsync(query, source);
                                        var resp = new { type = "search_results", plugins = _vm.MarketplaceResultsJson };
                                        MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp, _jsonOptions));
                                    }
                                    else
                                    {
                                        _vm.AddLog("[BRIDGE] Requesting local plugins list...", LogType.System);
                                        string pluginsJson = await _vm.GetLocalPluginsAsync();
                                        var resp = new { type = "local_plugins_results", plugins = JsonSerializer.Deserialize<JsonElement>(pluginsJson) };
                                        MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp, _jsonOptions));
                                    }
                                }
                            } catch (Exception ex) {
                                _vm.AddLog($"[BRIDGE ERROR] Plugin request failed: {ex.Message}", LogType.Error);
                            }
                        });
                        break;


                    case "get_plugin_data":
                        if (message.TryGetProperty("pluginName", out var pdName)) {
                            _ = Dispatcher.InvokeAsync(async () => {
                                string filesJson = await _vm.GetPluginRelatedFilesAsync(pdName.GetString()!);
                                var resp = new { type = "plugin_data_results", files = JsonSerializer.Deserialize<JsonElement>(filesJson) };
                                if (MainBrowser?.CoreWebView2 != null)
                                    MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;

                    case "read_plugin_file":
                        if (message.TryGetProperty("path", out var rfPath)) {
                            _ = Dispatcher.InvokeAsync(async () => {
                                string content = await _vm.ReadPluginFileAsync(rfPath.GetString()!);
                                var resp = new { type = "file_content_results", content = content, path = rfPath.GetString() };
                                if (MainBrowser?.CoreWebView2 != null)
                                    MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;

                    case "save_plugin_file":
                        if (message.TryGetProperty("path", out var sfPath) && message.TryGetProperty("content", out var sfContent)) {
                            _ = _vm.SavePluginFileAsync(sfPath.GetString()!, sfContent.GetString()!);
                        }
                        break;

                    case "plugin_action":
                        if (message.TryGetProperty("pluginName", out var paName) && message.TryGetProperty("pluginAction", out var paAct)) {
                            _ = _vm.ExecutePluginCommandAsync(paName.GetString()!, paAct.GetString()!);
                        }
                        break;

                    case "delete_plugin":
                        if (message.TryGetProperty("pluginName", out var dpName)) {
                            _ = _vm.DeletePluginAsync(dpName.GetString()!);
                        }
                        break;

                    case "select_server":
                        if (message.TryGetProperty("name", out var selName))
                        {
                            _vm.SelectServer(selName.GetString() ?? "");
                            SyncVmToUi(); // Provide immediate visual feedback
                        }
                        break;

                    case "delete_server":
                        if (message.TryGetProperty("name", out var delName))
                        {
                            string nameToDelete = delName.GetString() ?? "";
                            _ = Dispatcher.InvokeAsync(async () => {
                                await _vm.DeleteServerAsync(nameToDelete);
                                SyncVmToUi(); // Explicit sync to refresh the list in HUD
                            });
                        }
                        break;
                    case "get_server_list":
                        SyncVmToUi(true);
                        break;
                    case "get_audit_plugins":
                        Dispatcher.BeginInvoke(() => {
                            if (MainBrowser?.CoreWebView2 != null)
                            {
                                var plugins = _vm.GetCurrentServerPlugins();
                                var resp = new { type = "audit_plugins_list", plugins = plugins };
                                MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            }
                        });
                        break;
                    case "get_server_config":
                        Dispatcher.BeginInvoke(() => {
                            if (MainBrowser?.CoreWebView2 != null && _vm.SelectedServer != null)
                            {
                                var resp = new { type = "force_config_sync", payload = _vm.SelectedServer.Config };
                                string json = JsonSerializer.Serialize(resp, _jsonOptions);
                                MainBrowser.CoreWebView2.PostWebMessageAsJson(json);
                                _vm.AddLog($"[BRIDGE] Synchronized config for '{_vm.SelectedServer.Name}' to UI.", Models.LogType.System);
                            }
                        });
                        break;
                    case "save_server_settings":
                        if (message.TryGetProperty("payload", out var cfgProp))
                        {
                            var configJson = cfgProp.GetRawText();
                            _ = Dispatcher.InvokeAsync(async () => {
                                try {
                                    var config = JsonSerializer.Deserialize<ServerConfig>(configJson);
                                    if (config != null) await _vm.SaveServerConfigAsync(config);
                                } catch (Exception ex) {
                                    _vm.AddLog($"[ERROR] Failed to parse server settings: {ex.Message}", LogType.Error);
                                }
                            });
                        }
                        break;
                    case "start_plugin_audit":
                        if (message.TryGetProperty("payload", out var auditPathProp))
                        {
                            string pluginPath = auditPathProp.GetString() ?? "";
                            _ = Dispatcher.InvokeAsync(async () => {
                                string report = await _vm.RunSecurityAuditAsync(pluginPath);
                                if (MainBrowser?.CoreWebView2 != null)
                                {
                                    var resp = new { 
                                        type = "audit_report", 
                                        report = report,
                                        originalCode = _vm.GetLastAuditedCode(),
                                        hasFix = report.Contains("РИСК: ВЫСОКИЙ") || report.Contains("РИСК: КРИТИЧЕСКИЙ") || report.Contains("SUGGESTION"),
                                        riskLevel = report.Contains("КРИТИЧЕСКИЙ") ? "КРИТИЧЕСКИЙ" : (report.Contains("ВЫСОКИЙ") ? "ВЫСОКИЙ" : "СРЕДНИЙ")
                                    };
                                    MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                                }
                            });
                        }
                        break;
                    case "apply_audit_fix":
                        if (message.TryGetProperty("path", out var fixPathProp) && message.TryGetProperty("report", out var fixReportProp))
                        {
                            string pluginPath = fixPathProp.GetString() ?? "";
                            string report = fixReportProp.GetString() ?? "";
                            _ = Dispatcher.InvokeAsync(async () => {
                                bool success = await _vm.ApplyAuditFixAsync(pluginPath, report);
                                if (MainBrowser?.CoreWebView2 != null)
                                {
                                    var resp = new { type = "audit_fix_result", success = success };
                                    MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                                }
                            });
                        }
                        break;
                    case "get_plugin_details":
                        if (message.TryGetProperty("slug", out var gdSlug) && message.TryGetProperty("source", out var gdSource))
                        {
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                string details = await _vm.GetPluginDetailsHtmlAsync(gdSlug.GetString()!, gdSource.GetString()!);
                                var r = new { type = "plugin_details_result", details = details, slug = gdSlug.GetString() };
                                if (MainBrowser?.CoreWebView2 != null)
                                    MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                            });
                        }
                        break;
                    case "umod_login":
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            var loginWin = new Views.UModLoginWindow();
                            loginWin.Owner = this;
                            bool? res = loginWin.ShowDialog();
                            var r = new { type = "umod_login_result", success = res == true };
                            if (MainBrowser?.CoreWebView2 != null)
                                MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                        });
                        break;
                    case "import_server":
                        _ = Dispatcher.InvokeAsync(async () => {
                            var dialog = new OpenFolderDialog
                            {
                                Title = "Укажите корневую директорию сервера Rust",
                                Multiselect = false
                            };

                            if (dialog.ShowDialog() == true)
                            {
                                await _vm.ImportServerAsync(dialog.FolderName);
                            }
                        });
                        break;
                    case "market_check_dependencies":
                        if (message.TryGetProperty("plugin", out var depPluginProp))
                        {
                            var pluginJson = depPluginProp.GetRawText();
                            _ = Dispatcher.InvokeAsync(async () => {
                                string missingJson = await _vm.CheckMarketplacePluginDependenciesAsync(pluginJson);
                                var resp = new { type = "dependency_results", missing = missingJson, originalRequest = pluginJson };
                                MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;
                    case "market_audit":
                        if (message.TryGetProperty("plugin", out var auditProp))
                        {
                            var pluginJson = auditProp.GetRawText();
                            _ = Dispatcher.InvokeAsync(async () => {
                                var (safe, desc) = await _vm.AuditMarketplacePluginAsync(pluginJson);
                                var resp = new { type = "audit_results", safe = safe, description = desc };
                                MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;
                    case "market_install":
                        if (message.TryGetProperty("plugin", out var pluginProp))
                        {
                            var pluginJson = pluginProp.GetRawText();
                            _ = Dispatcher.InvokeAsync(async () => {
                                bool success = await _vm.InstallMarketplacePluginAsync(pluginJson);
                                var resp = new { type = "install_status", success = success };
                                MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;
                    case "market_check_updates":
                        _ = Dispatcher.InvokeAsync(async () => {
                            string updatesJson = await _vm.CheckForPluginUpdatesAsync();
                            var resp = new { type = "search_results", plugins = updatesJson };
                            MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                        });
                        break;
                    case "market_update":
                        if (message.TryGetProperty("plugin", out var updateProp))
                        {
                            var pluginJson = updateProp.GetRawText();
                            _ = Dispatcher.InvokeAsync(async () => {
                                bool success = await _vm.UpdateMarketplacePluginAsync(pluginJson);
                                var resp = new { type = "install_status", success = success };
                                MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;
                    case "save_system_settings":
                        if (message.TryGetProperty("payload", out var settingsProp))
                        {
                            var settingsJson = settingsProp.GetRawText();
                            // Implementation of Autorun, Tray, etc.
                            try {
                                using (var doc = JsonDocument.Parse(settingsJson))
                                {
                                    bool autorun = doc.RootElement.GetProperty("autorun").GetBoolean();
                                    bool tray = doc.RootElement.GetProperty("tray").GetBoolean();
                                    
                                    // Windows Autorun Logic
                                    string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                                    string appName = "TRPServerPanel";
                                    string appPath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

                                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true))
                                    {
                                        if (key != null)
                                        {
                                            if (autorun) key.SetValue(appName, $"\"{appPath}\"");
                                            else key.DeleteValue(appName, false);
                                        }
                                    }
                                    
                                    _vm.AddLog($"[SYSTEM] App Settings Updated: Autorun={autorun}, Tray={tray}", LogType.System);
                                }
                            } catch(Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                        }
                        break;
                    case "sync_logs":
                        Dispatcher.BeginInvoke(() => {
                            var logsJson = _vm.Console.GetConsoleLogsJson();
                            var syncMsg = new { type = "log_sync", data = logsJson };
                            MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(syncMsg));
                        });
                        break;
                    case "get_app_logs":
                        Dispatcher.BeginInvoke(() => {
                            if (MainBrowser?.CoreWebView2 != null)
                            {
                                var logs = AppLogService.Logs.ToList();
                                var resp = new { type = "app_logs_initial", logs = logs };
                                MainBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp, _jsonOptions));
                            }
                        });
                        break;
                    case "broadcast_theme":
                        if (message.TryGetProperty("theme", out var themeDataProp))
                        {
                            Dispatcher.BeginInvoke(() => {
                                var broadcastMsg = new { type = "theme_updated", theme = themeDataProp };
                                MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(broadcastMsg));
                            });
                        }
                        break;
                    case "get_files":
                        {
                            if (message.TryGetProperty("path", out var pathProp))
                            {
                                string path = pathProp.GetString() ?? "";
                                _ = Dispatcher.InvokeAsync(() => {
                                    string json = _vm.GetFileListJson(path);
                                    var resp = new { type = "file_list", path = path, items = json };
                                    MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                                });
                            }
                        }
                        break;
                    case "read_file":
                        if (message.TryGetProperty("path", out var readPathProp))
                        {
                            string path = readPathProp.GetString() ?? "";
                            _ = Dispatcher.InvokeAsync(async () => {
                                string content = await _vm.ReadFileContentAsync(path);
                                var resp = new { type = "file_content", path = path, content = content };
                                MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;
                    case "get_backups":
                        _ = Dispatcher.InvokeAsync(() => {
                            string json = _vm.GetBackupsJson();
                            var resp = new { type = "backup_list", items = json };
                            MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                        });
                        break;
                    case "trigger_backup":
                        _ = Dispatcher.InvokeAsync(async () => {
                            bool success = await _vm.TriggerBackupAsync();
                            var resp = new { type = "backup_status", success = success };
                            MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                        });
                        break;
                    case "save_file":
                        {
                            if (message.TryGetProperty("path", out var savePathProp) && message.TryGetProperty("content", out var contentProp))
                            {
                                string path = savePathProp.GetString() ?? "";
                                string content = contentProp.GetString() ?? "";
                                _ = Dispatcher.InvokeAsync(async () => {
                                    bool success = await _vm.SaveFileContentAsync(path, content);
                                    var resp = new { type = "save_status", path = path, success = success };
                                    MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                                });
                            }
                        }
                        break;
                    case "delete_file":
                        if (message.TryGetProperty("path", out var delPathProp))
                        {
                            string path = delPathProp.GetString() ?? "";
                            _ = Dispatcher.InvokeAsync(() => {
                                bool success = _vm.DeleteFileItem(path);
                                var resp = new { type = "delete_status", path = path, success = success };
                                MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;
                    case "create_folder":
                        {
                            if (message.TryGetProperty("parent", out var parentProp) && message.TryGetProperty("name", out var nameProp))
                            {
                                string parent = parentProp.GetString() ?? "";
                                string name = nameProp.GetString() ?? "";
                                _ = Dispatcher.InvokeAsync(() => {
                                    bool success = _vm.CreateNewFolder(parent, name);
                                    var resp = new { type = "create_status", success = success };
                                    MainBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                                });
                            }
                        }
                        break;

                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HYBRID] Error: {ex.Message}");
            }
        }

        private void ToggleWindowState()
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (this.WindowState == WindowState.Maximized)
                    this.WindowState = WindowState.Normal;
                else
                    this.WindowState = WindowState.Maximized;
            });
        }

        private void ExecuteVmCommand(string? name)
        {
            if (string.IsNullOrEmpty(name)) return;
            System.Diagnostics.Debug.WriteLine($"[HYBRID] Executing Command: {name}");
            
            Dispatcher.BeginInvoke(() => {
                switch (name)
                {
                    case "StartServer": _ = _vm.StartServerAsync(); break;
                    case "StopServer": _ = _vm.StopServerAsync(); break;
                    case "RestartServer": _ = _vm.RestartServerAsync(); break;
                    case "WipeServer": _ = _vm.TriggerWipeAsync(); break;
                    case "OpenFolder": _vm.OpenFolderCommand?.Execute(null); break;
                    case "InstallServer": _vm.OpenInstallCommand?.Execute(null); break;
                }
            });
        }

        private void MainBrowser_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            string uri = e.Uri ?? "";
            
            // [PAGE WINDOWS] If navigating to a non-Dashboard page, open it as a separate window
            if (uri.StartsWith("http://trp.app/", StringComparison.OrdinalIgnoreCase) &&
                !uri.Contains("Dashboard.html", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;

                // Extract page name (e.g. "Players.html")
                string pageName = System.IO.Path.GetFileName(new Uri(uri).LocalPath);

                Dispatcher.BeginInvoke(() =>
                {
                    // Reuse existing window if already open
                    if (_pageWindows.TryGetValue(pageName, out var existing) && existing.IsLoaded)
                    {
                        existing.Activate();
                        return;
                    }

                    var win = new Views.TRPWebWindow(uri);
                    _pageWindows[pageName] = win;
                    win.Closed += (_, __) => _pageWindows.Remove(pageName);
                    win.Show();
                });
                return; // DO NOT show loader if we cancelled
            }

            // Regular navigation (within same window)
            var loader = (FrameworkElement?)this.FindName("SystemLoader");
            if (loader != null) loader.Visibility = Visibility.Visible;
        }

        private void MainBrowser_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var loader = (FrameworkElement?)this.FindName("SystemLoader");
            if (loader != null) 
            {
                loader.Visibility = Visibility.Collapsed;
                loader.IsHitTestVisible = false;
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState != WindowState.Minimized)
            {
                // Force WebView2 to redraw/re-compose (fix for AllowsTransparency="True" bug)
                MainBrowser.Visibility = Visibility.Collapsed;
                MainBrowser.Visibility = Visibility.Visible;
            }
        }

        private static bool IsValidServerName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            // Only allow alphanumeric characters, underscores, dashes, spaces and dots. Length 1-64.
            return name.Length <= 64 && Regex.IsMatch(name, @"^[a-zA-Z0-9_\-\.\s]+$");
        }

        private static bool IsValidInstallPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return false;
                
                var invalidChars = Path.GetInvalidPathChars();
                if (path.Any(c => invalidChars.Contains(c) || c == '"' || c == '<' || c == '>' || c == '|' || c == '*' || c == '?'))
                    return false;
                
                // Block command execution chains or navigation hacks
                if (path.Contains("&") || path.Contains(";") || path.Contains(".."))
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string SanitizeInput(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            // Strip HTML tags to mitigate XSS
            string sanitized = Regex.Replace(input, @"<[^>]*>", string.Empty);
            // HTML encode to prevent script injection in DOM rendering
            sanitized = System.Net.WebUtility.HtmlEncode(sanitized);
            return sanitized;
        }

        private static bool IsValidSteamId(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId)) return false;
            return ulong.TryParse(steamId, out _) && steamId.Length == 17;
        }
    }
}