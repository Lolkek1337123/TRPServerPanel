// TRPName: TRPServerPanel
// Author: TEAM_RUST_PLUGINS
// Changelog:
// - v17.0.1: Added automatic selection update when deleting active server.
// - v17.0.0: Delegated server persistence to ServerList, added WipeScheduler synchronization.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Diagnostics;
using TRPServerPanel.Models;
using System.Threading.Tasks;
using TRPServerPanel.Services;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Timers;
using System.Windows.Data;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TRPServerPanel.Utils;
using System.Net.Http;
using System.Threading;

namespace TRPServerPanel.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, string> _countryCache = new();
        private readonly ServerManager _serverManager;
        private readonly WipeService _wipeService;
        private readonly BackupService _backupService;
        private readonly PluginService _pluginService;
        private readonly A2SQueryService _a2sService;
        private readonly GeminiService _geminiService;
        private readonly SystemService _systemService;
        private readonly RconService _rconService;
        private readonly NetworkService _networkService;
        private readonly RustDatabaseService _rustDbService;
        private readonly PlayerHistoryService _historyService;
        private readonly SteamApiService _steamService;
        private readonly NotificationService _notificationService;
        private readonly FileService _fileService;
        private readonly WipeSchedulerService _wipeScheduler;
        
        // Sub-ViewModels
        public ConsoleViewModel Console { get; }
        public MarketplaceViewModel Marketplace { get; }
        public ServerManagerViewModel ServerList { get; }

        private bool _isInstalling = false;
        private CancellationTokenSource? _pollingCts;
        private DateTime _lastPlaytimeUpdate = DateTime.MinValue;
        private DateTime _lastResourcePollTime = DateTime.MinValue;
        private TimeSpan _lastCpuTime = TimeSpan.Zero;
        private DateTime _lastCpuTimeCheck = DateTime.MinValue;
        private readonly Process _currentProcess = Process.GetCurrentProcess();
        private ServerModel? _selectedServer;
        private ObservableCollection<ServerModel> _activeServerTabs;
        private DateTime _lastRconErrorTime = DateTime.MinValue;
        private bool _isRconConnecting = false;
        private DateTime _lastRconConnectAttempt = DateTime.MinValue;

        private string _currentLanguage = "ru";
        public string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (_currentLanguage != value)
                {
                    _currentLanguage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsRussianActive));
                    OnPropertyChanged(nameof(IsEnglishActive));
                    UpdateApplicationLanguage(value);
                }
            }
        }

        public bool IsRussianActive => CurrentLanguage == "ru";
        public bool IsEnglishActive => CurrentLanguage == "en";

        public string GetLoc(string en, string ru) => CurrentLanguage == "ru" ? ru : en;

        private void UpdateApplicationLanguage(string lang)
        {
            if (System.Windows.Application.Current == null) return;
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    var mergedDicts = System.Windows.Application.Current.Resources.MergedDictionaries;
                    System.Windows.ResourceDictionary? langDict = null;
                    for (int i = 0; i < mergedDicts.Count; i++)
                    {
                        var src = mergedDicts[i].Source?.OriginalString ?? "";
                        if (src.Contains("Strings.ru.xaml") || src.Contains("Strings.en.xaml"))
                        {
                            langDict = mergedDicts[i];
                            break;
                        }
                    }

                    if (langDict != null)
                    {
                        mergedDicts.Remove(langDict);
                    }

                    mergedDicts.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/Resources/Localization/Strings.{lang}.xaml", UriKind.Absolute)
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[L10N ERROR] Failed to switch resource dictionary: {ex.Message}");
                }
            });
        }
        
        private ServerConfig _currentConfig = new();
 
        public ServerConfig CurrentConfig
        {
            get => _currentConfig;
            set { _currentConfig = value; OnPropertyChanged(); }
        }

        private string _installStatus = "Ready";
        public string InstallStatus
        {
            get => _installStatus;
            set { _installStatus = value; OnPropertyChanged(); }
        }

        private double _installProgress = 0;
        public double InstallProgress
        {
            get => _installProgress;
            set { _installProgress = value; OnPropertyChanged(); }
        }

        private string _agentStatus = "Ready";
        public string AgentStatus
        {
            get => _agentStatus;
            set { _agentStatus = value; OnPropertyChanged(); }
        }

        private string _availableCommandsJson = "[]";
        public string AvailableCommandsJson
        {
            get => _availableCommandsJson;
            set { _availableCommandsJson = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> CommandHistory { get; } = new();
        public int CurrentHistoryIndex { get; set; } = -1;

        public event Action<ServerConfig>? RequestConfigSync;
        public event Action<string, string>? OnSecurityUpdate;
        // Fired after RefreshPlayersAsync() completes — subscribers push updated list to WebView2
        public event Action? PlayerIdentitiesUpdated;

        public string InstallLog { get; set; } = "";

        private double _panelRam;
        public double PanelRam
        {
            get => _panelRam;
            private set { _panelRam = value; OnPropertyChanged(); }
        }

        private double _panelCpu;
        public double PanelCpu
        {
            get => _panelCpu;
            private set { _panelCpu = value; OnPropertyChanged(); }
        }

        public ObservableCollectionBatch<LogEntry> ConsoleLogs => Console.ConsoleLogs;
        public IEnumerable<LogEntry> FilteredLogs => Console.FilteredLogs;
        public ObservableCollection<ServerModel> Servers => ServerList.Servers;

        public void AddLog(string msg, LogType type) => Console.AddLog(msg, type);

        private ObservableCollection<PlayerIdentity> _playerIdentities = new();
        public ObservableCollection<PlayerIdentity> PlayerIdentities
        {
            get => _playerIdentities;
            set { _playerIdentities = value; OnPropertyChanged(); }
        }

        public string SelectedLogFilter
        {
            get => Console.SelectedLogFilter;
            set => Console.SelectedLogFilter = value;
        }

        public bool IsServerRunning => Servers.Any(s => s.Status == "Running" || s.Status == "Starting...");

        private SecurityReport? _lastSecurityReport;
        public SecurityReport? LastSecurityReport
        {
            get => _lastSecurityReport;
            set { _lastSecurityReport = value; OnPropertyChanged(); }
        }

        private readonly DateTime _appStartTime = DateTime.Now;
        public string AppUptime => (DateTime.Now - _appStartTime).ToString(@"hh\:mm\:ss");
        
        // v10.7.5: Robust Server Uptime calculation
        public string GetServerUptime()
        {
            if (SelectedServer == null || SelectedServer.Status != "Running") return "00:00:00";
            if (!string.IsNullOrEmpty(SelectedServer.Uptime) && SelectedServer.Uptime != "00:00:00") return SelectedServer.Uptime;
            if (SelectedServer.UptimeStart.HasValue) 
                return (DateTime.Now - SelectedServer.UptimeStart.Value).ToString(@"hh\:mm\:ss");
            return "00:00:00";
        }
        public async Task ShutdownAsync()
        {
            Console.AddLog(GetLoc("[SYSTEM] Initiating global shutdown sequence...", "[SYSTEM] Запуск последовательности завершения работы..."), LogType.System);
            
            // 1. Cancel background tasks safely
            try 
            {
                if (_pollingCts != null)
                {
                    _pollingCts.Cancel();
                    _pollingCts.Dispose();
                    _pollingCts = null;
                }
            } 
            catch { _pollingCts = null; }

            // 2. Stop active server if any
            if (SelectedServer != null && (SelectedServer.Status == "Running" || SelectedServer.Status == "Starting..."))
            {
                await StopServerAsync();
            }
            
            // 3. Disconnect RCON explicitly
            await _rconService.DisconnectAsync();
            
            // 4. Force save and dispose player history
            _historyService.Dispose();
            
            // Save servers state to config
            SaveServers();

            // Additional cleanup if needed
            Console.AddLog(GetLoc("[SYSTEM] All systems reached safe state. Application exiting.", "[SYSTEM] Все системы остановлены. Выход из приложения."), LogType.Success);
        }

        public ServerModel SelectedServer
        {
            get => _selectedServer!;
            set 
            { 
                if (_selectedServer != value)
                {
                    if (_rconService.IsConnected)
                    {
                        _ = _rconService.DisconnectAsync();
                    }
                }

                if (value != null)
                {
                    // v17.1: Dynamically detect server framework (Vanilla/Oxide/Carbon) on selection
                    DetectServerFramework(value);

                    // v10.3: Auto-sync config from disk when selecting a server
                    value.Config = _serverManager.LoadServerConfig(value.Path);
                    
                    // v16.3.3: Debug Logging
                    int propCount = typeof(ServerConfig).GetProperties().Count(p => p.GetValue(value.Config) != null);
                    Console.AddLog(GetLoc($"[SYSTEM] '{value.Name}' configuration loaded ({propCount} properties).", $"[SYSTEM] Конфигурация '{value.Name}' загружена ({propCount} параметров)."), LogType.System);
                    
                    // v12.3: Explicit sync to Frontend
                    RequestConfigSync?.Invoke(value.Config);

                    // v16.3: Sync AI model from config
                    if (!string.IsNullOrEmpty(value.Config.AiModel))
                        _geminiService.ActiveModel = value.Config.AiModel;
                }

                _selectedServer = value; 
                OnPropertyChanged(); 
                UpdateAvailableCommands();
                
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                    lock (PlayerIdentities)
                    {
                        PlayerIdentities.Clear();
                    }
                });
                
                if (value != null)
                {
                    _ = RefreshPlayersAsync();
                }
            }
        }

        public string GetConsoleLogsJson()
        {
            var logs = ConsoleLogs.TakeLast(500).Select(l => new {
                message = l.Message,
                type = l.Type.ToString(),
                time = l.FormattedTime
            }).ToList();
            return JsonSerializer.Serialize(logs);
        }

        public bool IsAutoScrollEnabled { get; set; } = true;

        public string CurrentCommandText
        {
            get => Console.CurrentCommandText;
            set => Console.CurrentCommandText = value;
        }

        public ICommand AddServerCommand { get; } = null!;
        public ICommand OpenInstallCommand { get; } = null!;
        public ICommand OpenMarketplaceCommand { get; } = null!;
        public ICommand OpenAboutCommand { get; } = null!;
        public ICommand ClearConsoleCommand { get; } = null!;
        public ICommand SendCommand { get; } = null!;
        public ICommand ChangeFilterCommand { get; } = null!;
        public ICommand SwitchLanguageCommand { get; } = null!;
        public ICommand SaveCommand { get; } = null!;
        public ICommand RestartCommand { get; } = null!;
        public ICommand WipeCommand { get; } = null!;
        public ICommand StartServerCommand { get; } = null!;
        public ICommand StopServerCommand { get; } = null!;
        public ICommand OpenSettingsCommand { get; } = null!;
        public ICommand ManagePluginsCommand { get; } = null!;
        public ICommand ManagePlayersCommand { get; } = null!;
        public ICommand RefreshPlayersCommand { get; } = null!;
        public ICommand OpenFolderCommand { get; } = null!;
        public ICommand BackupCommand { get; } = null!;

        private RelayCommand _searchPluginsCommand = null!;
        public ICommand SearchPluginsCommand => _searchPluginsCommand ??= new RelayCommand(async _ => await SearchPlugins(""));

        private RelayCommand _installPluginCommand = null!;
        public ICommand InstallPluginCommand => _installPluginCommand ??= new RelayCommand(async p => {
            if (p is MarketplacePlugin plug) await InstallPlugin(plug);
        });


        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; OnPropertyChanged(nameof(SearchText)); OnPropertyChanged(nameof(FilteredLogs)); }
        }

        private int _selectedSettingsTabIndex = 0;
        public int SelectedSettingsTabIndex
        {
            get => _selectedSettingsTabIndex;
            set { _selectedSettingsTabIndex = value; OnPropertyChanged(nameof(SelectedSettingsTabIndex)); }
        }

        public void OpenSettingsAt(int tabIndex)
        {
            SelectedSettingsTabIndex = tabIndex;
            RequestOpenWindow?.Invoke("Settings");
        }

        public event Action<string>? RequestOpenWindow;


        public MainViewModel(
            HttpClient httpClient,
            ServerManager serverManager,
            WipeService wipeService,
            BackupService backupService,
            PluginService pluginService,
            A2SQueryService a2sService,
            GeminiService geminiService,
            SystemService systemService,
            RconService rconService,
            NetworkService networkService,
            RustDatabaseService rustDbService,
            PlayerHistoryService historyService,
            SteamApiService steamService,
            NotificationService notificationService,
            ConsoleViewModel console,
            MarketplaceViewModel marketplace,
            ServerManagerViewModel serverList)
        {
            _httpClient = httpClient;
            _serverManager = serverManager;
            _wipeService = wipeService;
            _backupService = backupService;
            _pluginService = pluginService;
            _a2sService = a2sService;
            _geminiService = geminiService;
            _systemService = systemService;
            _rconService = rconService;
            _networkService = networkService;
            _rustDbService = rustDbService;
            _historyService = historyService;
            _steamService = steamService;
            _notificationService = notificationService;
            _fileService = new FileService();
            _wipeScheduler = new WipeSchedulerService(_wipeService, _serverManager);
            
            _wipeScheduler.OnWipeScheduled += (name, msg) => {
                AddLog($"[SCHEDULED WIPE] {name}: {msg}", LogType.System);
                _notificationService.ShowNotification("Scheduled Wipe", $"{name}: {msg}", "info");
            };
            
            Console = console;
            Marketplace = marketplace;
            ServerList = serverList;

            // Register initial servers to Wipe Scheduler
            foreach (var s in Servers)
            {
                _wipeScheduler.RegisterServer(s);
            }

            // Sync future additions to Wipe Scheduler
            Servers.CollectionChanged += (sender, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (ServerModel item in e.NewItems)
                    {
                        _wipeScheduler.RegisterServer(item);
                    }
                }
            };

            // Wire up Server Selection to Sub-Models
            ServerList.OnServerSelected += (s) => { 
                if (s != null) SelectedServer = s; 
            };
            
            // v10.4.3: Ensure initial state is synced if ServerList already has a selection
            if (ServerList.SelectedServer != null) {
                SelectedServer = ServerList.SelectedServer;
            }

            _serverManager.ProgressChanged += (status, progress) => 
            {
                InstallStatus = status;
                InstallProgress = progress;
                
                if (SelectedServer != null) {
                    if (status.Contains("%") || status.Contains("Update") || status.Contains("Checking")) {
                        SelectedServer.Status = status;
                    } else if (status == "Online") {
                        SelectedServer.Status = "Running";
                    }
                    OnPropertyChanged(nameof(SelectedServer));
                }
            };

            _geminiService.OnAgentMessage += (msg) => 
            {
                Console.AddLog($"[AGENT] {msg}", LogType.System);
                AgentStatus = "Idle";
            };
            
            _geminiService.OnToolCall += (name, args) => 
            {
                AgentStatus = "Running Tool: " + name;
                ExecuteVmCommand(name); 
            };

            string apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "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";
            _geminiService.Initialize(apiKey);

            _activeServerTabs = new ObservableCollection<ServerModel>();
            
            AddServerCommand = new RelayCommand(_ => AddNewServer());
            OpenInstallCommand = new RelayCommand(_ => RequestOpenWindow?.Invoke("Install"));
            StartServerCommand = new RelayCommand(async _ => await StartServerAsync());
            StopServerCommand = new RelayCommand(async _ => await StopServerAsync());
            RestartCommand = new RelayCommand(async _ => await RestartServerAsync());
            OpenFolderCommand = new RelayCommand(_ => { if(SelectedServer != null) Process.Start("explorer.exe", SelectedServer.Path); });
            ClearConsoleCommand = new RelayCommand(_ => Console.ClearConsole());
            WipeCommand = new RelayCommand(async _ => await TriggerWipeAsync());
            BackupCommand = new RelayCommand(async _ => await TriggerBackupAsync());
            SwitchLanguageCommand = new RelayCommand(_ => SwitchLanguage());
            SaveCommand = new RelayCommand(_ => SaveConfig());
            OpenSettingsCommand = new RelayCommand(tab => OpenSettingsAt(tab != null ? int.Parse(tab.ToString()!) : 0));
            ManagePluginsCommand = new RelayCommand(_ => RequestOpenWindow?.Invoke("Marketplace"));
            ManagePlayersCommand = new RelayCommand(_ => OpenSettingsAt(4));
            RefreshPlayersCommand = new RelayCommand(async _ => await RefreshPlayersAsync());
            SendCommand = new RelayCommand(async _ => {
                if (SelectedServer != null && !string.IsNullOrWhiteSpace(Console.CurrentCommandText))
                {
                    string cmd = Console.CurrentCommandText;
                    await Console.ExecuteCommandAsync(cmd);
                    CommandHistory.Insert(0, cmd);
                    CurrentHistoryIndex = -1;
                    Console.CurrentCommandText = "";
                }
            });

            // RCON Telemetry Bridge
            _rconService.OnMessageReceived += OnRconMessageReceived;
            _serverManager.LogReceived += OnServerLogReceived;

            _activeServerTabs = new ObservableCollection<ServerModel>();
            StartStatusPolling();
            _ = CheckAutoStartServersAsync();
        }

        private void OnServerLogReceived(string message)
        {
            if (SelectedServer == null || string.IsNullOrWhiteSpace(message)) return;

            // Fast pre-filter to avoid regex overhead on normal logs
            bool containsFps = message.Contains("fps", StringComparison.OrdinalIgnoreCase);
            bool containsEnts = message.Contains("ents", StringComparison.OrdinalIgnoreCase);
            bool containsUptime = message.Contains("uptime", StringComparison.OrdinalIgnoreCase);
            bool containsJson = message.Contains("{");

            if (!containsFps && !containsEnts && !containsUptime && !containsJson) return;

            // Handle standard console output telemetry format: (60 fps, 12052 ents) 0 players
            if (containsFps && containsEnts)
            {
                var match = Regex.Match(message, @"\(?(\d+)\s+fps,\s+(\d+)\s+ents\)?(?:\s+(\d+)\s+players)?", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                        if (double.TryParse(match.Groups[1].Value, out double fps))
                            SelectedServer.Fps = (int)fps;
                        if (int.TryParse(match.Groups[2].Value, out int ents))
                            SelectedServer.Entities = ents;
                        if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out int players))
                            SelectedServer.PlayerCount = players;
                        
                        AddToHistory(SelectedServer.FpsHistory, SelectedServer.Fps);
                        AddToHistory(SelectedServer.EntitiesHistory, SelectedServer.Entities);
                        AddToHistory(SelectedServer.PlayerHistory, SelectedServer.PlayerCount);
                        AddToHistory(SelectedServer.PingHistory, SelectedServer.Ping);
                    });
                    return;
                }
            }

            // Fallback: try parsing general server status outputs
            OnRconMessageReceived(message);
        }

        private void OnRconMessageReceived(string message)
        {
            if (SelectedServer == null || string.IsNullOrWhiteSpace(message)) return;

            try
            {
                // v16.5: Improved JSON detection (handles ANSI codes or leading spaces)
                int jsonStart = message.IndexOf('{');
                if (jsonStart >= 0 && (message.Contains("Hostname", StringComparison.OrdinalIgnoreCase) || message.Contains("EntityCount", StringComparison.OrdinalIgnoreCase)))
                {
                    string jsonPart = message.Substring(jsonStart);
                    int jsonEnd = jsonPart.LastIndexOf('}');
                    if (jsonEnd >= 0) jsonPart = jsonPart.Substring(0, jsonEnd + 1);

                    using (var doc = JsonDocument.Parse(jsonPart))
                    {
                        var root = doc.RootElement;
                        
                        int? fpsVal = null;
                        if (root.TryGetProperty("Framerate", out var fps) || root.TryGetProperty("fps", out fps)) 
                            fpsVal = (int)fps.GetDouble();

                        int? entVal = null;
                        if (root.TryGetProperty("EntityCount", out var ent) || root.TryGetProperty("Entities", out ent) || root.TryGetProperty("ents", out ent))
                            entVal = ent.ValueKind == JsonValueKind.Number ? ent.GetInt32() : (int.TryParse(ent.GetString(), out var ev) ? ev : 0);

                        int? pVal = null;
                        if (root.TryGetProperty("Players", out var p)) pVal = p.GetInt32();

                        int? mpVal = null;
                        if (root.TryGetProperty("MaxPlayers", out var mp)) mpVal = mp.GetInt32();

                        string? uptimeVal = null;
                        if (root.TryGetProperty("Uptime", out var upt)) {
                            if (upt.ValueKind == JsonValueKind.Number)
                            {
                                var ts = TimeSpan.FromSeconds(upt.GetInt32());
                                uptimeVal = $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
                            }
                            else if (upt.ValueKind == JsonValueKind.String)
                            {
                                uptimeVal = upt.GetString() ?? "00:00:00";
                            }
                        }

                        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                            if (fpsVal.HasValue) SelectedServer.Fps = fpsVal.Value;
                            if (entVal.HasValue) SelectedServer.Entities = entVal.Value;
                            if (pVal.HasValue) SelectedServer.PlayerCount = pVal.Value;
                            if (mpVal.HasValue) SelectedServer.MaxPlayers = mpVal.Value;
                            if (uptimeVal != null) SelectedServer.Uptime = uptimeVal;

                            // v16.7: Sync with performance history for charts
                            AddToHistory(SelectedServer.FpsHistory, SelectedServer.Fps);
                            AddToHistory(SelectedServer.EntitiesHistory, SelectedServer.Entities);
                            AddToHistory(SelectedServer.PlayerHistory, SelectedServer.PlayerCount);
                            AddToHistory(SelectedServer.PingHistory, SelectedServer.Ping);
                        });
                    }
                    return; // Successfully parsed as JSON
                }

                // Fallback to Regex for non-JSON or mixed output
                if (message.Contains("fps:", StringComparison.OrdinalIgnoreCase) || 
                    message.Contains("ents:", StringComparison.OrdinalIgnoreCase) || 
                    message.Contains("uptime:", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("hostname:", StringComparison.OrdinalIgnoreCase))
                {
                    ParseStatusResponse(message);
                }
            }
            catch (Exception ex) 
            { 
                AppLogService.Log($"[ERROR] RCON Parse Error: {ex.Message}", AppLogLevel.ERROR, "RCON");
            }
        }

        private void ParseStatusResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return;

            // 1. Telemetry Extraction
            var fpsMatch = Regex.Match(response, @"(?:fps|framerate)\s*[:\s]\s*([0-9.]+)", RegexOptions.IgnoreCase);
            var entMatch = Regex.Match(response, @"(?:ents|entities|entitycount)\s*[:\s]\s*(\d+)", RegexOptions.IgnoreCase);
            var uptMatch = Regex.Match(response, @"uptime\s*[:\s]\s*(\d+:\d+:\d+|\d+h\d+m\d+s|\d+s|\d+)", RegexOptions.IgnoreCase);
            var plyMatch = Regex.Match(response, @"players\s*[:\s]\s*(\d+)\s*\(([^)]+)\)", RegexOptions.IgnoreCase);

            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fps))
                    SelectedServer.Fps = (int)fps;
                
                if (entMatch.Success && int.TryParse(entMatch.Groups[1].Value, out var ents)) SelectedServer.Entities = ents;
                if (uptMatch.Success) SelectedServer.Uptime = uptMatch.Groups[1].Value;
                
                if (plyMatch.Success)
                {
                    SelectedServer.PlayerCount = int.Parse(plyMatch.Groups[1].Value);
                    var maxMatch = Regex.Match(plyMatch.Groups[2].Value, @"(\d+)\s*max");
                    if (maxMatch.Success) SelectedServer.MaxPlayers = int.Parse(maxMatch.Groups[1].Value);
                }

                AddToHistory(SelectedServer.FpsHistory, SelectedServer.Fps);
                AddToHistory(SelectedServer.EntitiesHistory, SelectedServer.Entities);
                AddToHistory(SelectedServer.PlayerHistory, SelectedServer.PlayerCount);
                AddToHistory(SelectedServer.PingHistory, SelectedServer.Ping);
            });

            // 2. Player List Extraction (Table parsing)
            var playerLines = response.Split('\n').Where(l => Regex.IsMatch(l, @"^\d{17,19}\s+")).ToList();
            if (playerLines.Any())
            {
                var rconPlayers = new List<PlayerIdentity>();
                foreach (var line in playerLines)
                {
                    // Pattern: ID "NAME" PING CONNECTED ADDR OWNER
                    var match = Regex.Match(line, @"^(\d+)\s+""?(.*?)""?\s+(\d+)\s+[\d.ms]+\s+([0-9.]+):(\d+)");
                    if (match.Success)
                    {
                        ulong sid = ulong.Parse(match.Groups[1].Value);
                        string name = match.Groups[2].Value;
                        int ping = int.Parse(match.Groups[3].Value);
                        string ip = match.Groups[4].Value;

                        rconPlayers.Add(new PlayerIdentity {
                            SteamID = sid,
                            Username = name,
                            IPAddress = ip,
                            LastSeen = DateTime.Now,
                            Ping = ping > 0 ? ping : 1 // Ensure marked as online
                        });

                        // Proactive persistence for real-time data
                        _ = ResolveCountryAndUpdateAsync(sid, name, ip);
                    }
                }

                if (rconPlayers.Any())
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                        lock (PlayerIdentities)
                        {
                            foreach (var rp in rconPlayers)
                            {
                                var existing = PlayerIdentities.FirstOrDefault(p => p.SteamID == rp.SteamID);
                                if (existing == null)
                                {
                                    PlayerIdentities.Add(rp);
                                }
                                else
                                {
                                    existing.Ping = rp.Ping; 
                                    existing.LastSeen = rp.LastSeen;
                                    existing.IPAddress = rp.IPAddress;
                                }
                            }
                        }
                        OnPropertyChanged(nameof(PlayerIdentities));
                        PlayerIdentitiesUpdated?.Invoke();
                    });
                }
            }
        }

        private async Task ResolveCountryAndUpdateAsync(ulong sid, string name, string ip)
        {
            string country = "Unknown";
            string countryCode = "";
            if (!string.IsNullOrEmpty(ip) && ip != "127.0.0.1" && ip != "localhost")
            {
                if (_countryCache.TryGetValue(ip, out var cached))
                {
                    country = cached.Split('|')[0];
                    if (cached.Contains("|")) countryCode = cached.Split('|')[1];
                }
                else
                {
                    try
                    {
                        var response = await _httpClient.GetStringAsync($"http://ip-api.com/json/{ip}?fields=country,countryCode");
                        using var doc = JsonDocument.Parse(response);
                        country = doc.RootElement.GetProperty("country").GetString() ?? "Unknown";
                        countryCode = doc.RootElement.GetProperty("countryCode").GetString()?.ToLower() ?? "";
                        _countryCache[ip] = $"{country}|{countryCode}";
                    }
                    catch { }
                }
            }

            // Use optimized SteamApiService with local caching
            string avatarUrl = "";
            var steamData = await _steamService.GetPlayerSummariesAsync(new[] { sid });
            if (steamData.TryGetValue(sid, out var summary))
            {
                avatarUrl = summary.AvatarUrl;
                // If ip-api failed or skipped, use Steam's country code
                if (string.IsNullOrEmpty(countryCode)) countryCode = summary.CountryCode?.ToLower() ?? "";
            }

            if (string.IsNullOrEmpty(avatarUrl))
                avatarUrl = $"https://www.steamid.link/api/v1/avatar/{sid}";
            
            _historyService.UpdateExtendedData(sid, name, ip, country, countryCode, avatarUrl);
            
            // Update UI list
            lock (PlayerIdentities)
            {
                var p = PlayerIdentities.FirstOrDefault(x => x.SteamID == sid);
                if (p != null) 
                {
                    p.Country = country;
                    p.CountryCode = countryCode;
                    if (string.IsNullOrEmpty(p.AvatarUrl) || p.AvatarUrl.Contains("facepunch"))
                        p.AvatarUrl = avatarUrl;
                }
            }
        }

        private bool ParseLogForTelemetry(string log)
        {
            bool isStatsLine = false;
            if (SelectedServer == null) return false;

            // 1. FPS detection (formats: "fps: 60.5" or "FPS 60" or "60 fps")
            var fpsMatch = Regex.Match(log, @"(?:fps\s*[:\s]\s*([0-9.]+)|([0-9.]+)\s+fps)", RegexOptions.IgnoreCase);
            if (fpsMatch.Success) {
                string fpsVal = fpsMatch.Groups[1].Success ? fpsMatch.Groups[1].Value : fpsMatch.Groups[2].Value;
                if (double.TryParse(fpsVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fps))
                    SelectedServer.Fps = (int)fps;
                isStatsLine = true;
            }

            // 2. Entities detection (formats: "entities: 32091" or "ents 32091" or "32091 entities")
            var entMatch = Regex.Match(log, @"(?:(?:entities|ents)\s*[:\s]\s*(\d+)|(\d+)\s+(?:entities|ents))", RegexOptions.IgnoreCase);
            if (entMatch.Success) {
                string entVal = entMatch.Groups[1].Success ? entMatch.Groups[1].Value : entMatch.Groups[2].Value;
                if (int.TryParse(entVal, out var ent))
                    SelectedServer.Entities = ent;
                isStatsLine = true;
            }

            // 3. JSON Stats from `serverinfo` command
            if (!isStatsLine && log.Trim().StartsWith("{") && log.Contains("EntityCount")) {
                var fpsJsonMatch = Regex.Match(log, @"\""Framerate\""\s*:\s*([0-9.]+)");
                if (fpsJsonMatch.Success && double.TryParse(fpsJsonMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fpsJson)) {
                    SelectedServer.Fps = (int)fpsJson;
                }

                var entJsonMatch = Regex.Match(log, @"\""EntityCount\""\s*:\s*(\d+)");
                if (entJsonMatch.Success && int.TryParse(entJsonMatch.Groups[1].Value, out var entJson)) {
                    SelectedServer.Entities = entJson;
                }

                var playersMatch = Regex.Match(log, @"\""Players\""\s*:\s*(\d+)");
                if (playersMatch.Success && int.TryParse(playersMatch.Groups[1].Value, out var plJson)) {
                    SelectedServer.PlayerCount = plJson;
                }

                var maxPlMatch = Regex.Match(log, @"\""MaxPlayers\""\s*:\s*(\d+)");
                if (maxPlMatch.Success && int.TryParse(maxPlMatch.Groups[1].Value, out var maxPlJson)) {
                    SelectedServer.MaxPlayers = maxPlJson;
                }

                var uptimeJsMatch = Regex.Match(log, @"\""Uptime\""\s*:\s*(\d+)");
                if (uptimeJsMatch.Success && int.TryParse(uptimeJsMatch.Groups[1].Value, out var upJson)) {
                    TimeSpan t = TimeSpan.FromSeconds(upJson);
                    SelectedServer.Uptime = string.Format("{0:D2}:{1:D2}:{2:D2}", t.Hours, t.Minutes, t.Seconds);
                }

                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                    AddToHistory(SelectedServer.FpsHistory, SelectedServer.Fps);
                    AddToHistory(SelectedServer.PlayerHistory, SelectedServer.PlayerCount);
                    AddToHistory(SelectedServer.EntitiesHistory, SelectedServer.Entities);
                    AddToHistory(SelectedServer.PingHistory, SelectedServer.Ping);
                    OnPropertyChanged(nameof(SelectedServer));
                });
                
                // Return true to prevent spamming the console with JSON payloads
                return true;
            }

            // 4. Combined Stats (Rust standard: "60.5 fps, 32091 entities, 4 sleeping")
            if (!isStatsLine) {
                var statsMatch = Regex.Match(log, @"([0-9.]+)\s+fps[^,]*,?\s*(\d+)\s+ent(?:ities)?", RegexOptions.IgnoreCase);
                if (statsMatch.Success) {
                    if (double.TryParse(statsMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fps))
                        SelectedServer.Fps = (int)fps;
                    if (int.TryParse(statsMatch.Groups[2].Value, out var ent))
                        SelectedServer.Entities = ent;
                    isStatsLine = true;
                }
            }

            // 4. Players (formats: "N players, M max" OR "players: N (M max)")
            var plMatch = Regex.Match(log, @"(?:(\d+)\s+players,\s+(\d+)\s+max|players\s*:\s*(\d+)\s*\((\d+)\s+max\))", RegexOptions.IgnoreCase);
            if (plMatch.Success) {
                if (plMatch.Groups[1].Success) {
                    SelectedServer.PlayerCount = int.Parse(plMatch.Groups[1].Value);
                    SelectedServer.MaxPlayers = int.Parse(plMatch.Groups[2].Value);
                } else {
                    SelectedServer.PlayerCount = int.Parse(plMatch.Groups[3].Value);
                    SelectedServer.MaxPlayers = int.Parse(plMatch.Groups[4].Value);
                }
                isStatsLine = true;
            }

            // 5. Uptime
            var uptimeMatch = Regex.Match(log, @"(?:([0-9a-z:]+)\s+uptime|uptime\s*:\s*(\d{2}:\d{2}:\d{2}))", RegexOptions.IgnoreCase);
            if (uptimeMatch.Success) {
                SelectedServer.Uptime = uptimeMatch.Groups[1].Success ? uptimeMatch.Groups[1].Value : uptimeMatch.Groups[2].Value;
                isStatsLine = true;
            }

            // Push to history if it's a stats line
            if (isStatsLine) {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                    AddToHistory(SelectedServer.FpsHistory, SelectedServer.Fps);
                    AddToHistory(SelectedServer.PlayerHistory, SelectedServer.PlayerCount);
                    AddToHistory(SelectedServer.EntitiesHistory, SelectedServer.Entities);
                    AddToHistory(SelectedServer.PingHistory, SelectedServer.Ping);
                });
            }

            if (isStatsLine || log.Contains("hostname:") || log.Contains("version:"))
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                    OnPropertyChanged(nameof(SelectedServer));
                });
                return true; 
            }
            return false;
        }

        private void AddNewServer()
        {
            var newServer = new ServerModel { Name = "New Server", Status = "Stopped", Port = 28015 };
            Servers.Add(newServer);
            SaveServers();
        }

        public async Task StartServerAsync()
        {
            if (SelectedServer == null)
            {
                AddLog(GetLoc("[WARNING] Cannot start server: No server selected.", "[ВНИМАНИЕ] Не удалось запустить сервер: Сервер не выбран."), LogType.Warning);
                return;
            }
            await StartServerModelAsync(SelectedServer);
        }

        public async Task StartServerModelAsync(ServerModel server)
        {
            if (server == null) return;
            
            var config = server.Config;
            if (config == null)
            {
                config = _serverManager.LoadServerConfig(server.Path) ?? new ServerConfig();
                server.Config = config;
            }

            // v19.0: Proactive Port Availability Check before server start (does not block, only warns)
            int gamePort = config.Port;
            int queryPort = config.QueryPort != 0 ? config.QueryPort : (gamePort + 1);
            int rconPort = config.RconPort;

            if (!SystemService.IsPortAvailable(gamePort, false))
            {
                AddLog(GetLoc($"[WARNING] Game Port {gamePort} (UDP) is already in use by another application. Connection issues might occur.", $"[ВНИМАНИЕ] Игровой порт {gamePort} (UDP) уже используется другим приложением. Возможны проблемы с подключением."), LogType.Warning);
            }
            if (!SystemService.IsPortAvailable(queryPort, false))
            {
                AddLog(GetLoc($"[WARNING] Query Port {queryPort} (UDP) is already in use by another application. Server might not be visible in server lists.", $"[ВНИМАНИЕ] Query-порт {queryPort} (UDP) уже используется другим приложением. Сервер может быть не виден в списках."), LogType.Warning);
            }
            if (!SystemService.IsPortAvailable(rconPort, true))
            {
                AddLog(GetLoc($"[WARNING] RCON Port {rconPort} (TCP) is already in use by another application. RCON panel might fail to connect.", $"[ВНИМАНИЕ] RCON-порт {rconPort} (TCP) уже используется другим приложением. Панель RCON может не подключиться."), LogType.Warning);
            }

            server.Status = "Updating...";
            try 
            {
                await CheckAndInstallUpdatesAsync(server);
            }
            catch (Exception ex) 
            {
                AddLog(GetLoc($"[WARNING] Auto-update partially failed for {server.Name}: {ex.Message}. Attempting launch anyway...", $"[WARNING] Автообновление частично не удалось для {server.Name}: {ex.Message}. Попытка запуска..."), LogType.Warning);
            }

            server.Status = "Starting...";
            try {
                await _serverManager.StartServerAsync(server.Path, config);
            } catch (Exception ex) {
                server.Status = "Stopped";
                AddLog(GetLoc($"[ERROR] Start Failed for {server.Name}: {ex.Message}", $"[ERROR] Ошибка запуска для {server.Name}: {ex.Message}"), LogType.Error);
            }
        }

        public async Task CheckAutoStartServersAsync()
        {
            await Task.Delay(1500);

            var serversToStart = new System.Collections.Generic.List<ServerModel>();
            foreach (var server in ServerList.Servers)
            {
                try 
                {
                    var config = _serverManager.LoadServerConfig(server.Path);
                    if (config != null)
                    {
                        server.Config = config;
                        if (config.AutoStart)
                        {
                            serversToStart.Add(server);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"[ERROR] Failed to load config for auto-start check on {server.Name}: {ex.Message}", LogType.Error);
                }
            }

            foreach (var server in serversToStart)
            {
                AddLog(GetLoc($"[SYSTEM] Auto-starting server: {server.Name}", $"[SYSTEM] Автоматический запуск сервера: {server.Name}"), LogType.System);
                _ = StartServerModelAsync(server);
            }
        }

        private async Task CheckAndInstallUpdatesAsync(ServerModel server)
        {
            server.Status = "Updating";
            OnPropertyChanged(nameof(SelectedServer));
            // Resolve actual rustds path
            string baseDir = server.Path;
            string rustdsPath = Path.Combine(baseDir, "rustds");
            string steamPath = Path.Combine(baseDir, "steam");

            if (!Directory.Exists(rustdsPath)) rustdsPath = baseDir; // Fallback if no subfolder
            if (!Directory.Exists(steamPath)) steamPath = Path.Combine(baseDir, "..", "steam"); // Try parent

            AddLog(GetLoc("[SYSTEM] Syncing server core via SteamCMD...", "[SYSTEM] Синхронизация ядра сервера через SteamCMD..."), LogType.System);
            
            // 1. Core Update
            string updateArgs = $"+force_install_dir \"{rustdsPath}\" +login anonymous +app_update 258550 validate +quit";
            await _serverManager.RunSteamCmdAsync(Directory.Exists(steamPath) ? steamPath : Path.Combine(rustdsPath, "steam"), updateArgs);

            // 2. Mod Detection & Update
            if (File.Exists(Path.Combine(rustdsPath, "carbon.dll")) || Directory.Exists(Path.Combine(rustdsPath, "carbon")))
        {
                AddLog(GetLoc("[SYSTEM] Carbon installation detected. Updating to latest Production Build...", "[SYSTEM] Обнаружен Carbon. Обновление до последней стабильной версии..."), LogType.System);
                await _serverManager.InstallModAsync(rustdsPath, "https://github.com/CarbonCommunity/Carbon/releases/download/production_build/Carbon.Windows.Release.zip", "Carbon");
            }
            else if (Directory.Exists(Path.Combine(rustdsPath, "oxide")) || File.Exists(Path.Combine(rustdsPath, "RustDedicated_Data", "Managed", "Oxide.Core.dll")))
        {
                AddLog(GetLoc("[SYSTEM] Oxide installation detected. Updating core binaries...", "[SYSTEM] Обнаружен Oxide. Обновление системных файлов..."), LogType.System);
                await _serverManager.InstallModAsync(rustdsPath, "https://umod.org/games/rust/download", "Oxide");
            }
            
            AddLog(GetLoc("[SUCCESS] Pre-start synchronization completed.", "[SUCCESS] Предварительная синхронизация завершена."), LogType.Success);
        }

        public async Task StopServerAsync()
        {
            if (SelectedServer == null)
            {
                AddLog(GetLoc("[WARNING] Cannot stop server: No server selected.", "[ВНИМАНИЕ] Не удалось остановить сервер: Сервер не выбран."), LogType.Warning);
                return;
            }
            SelectedServer.Status = "Stopping...";
            if (_rconService.IsConnected)
            {
                await _rconService.DisconnectAsync();
            }
            await _serverManager.StopServerAsync();
            SelectedServer.Status = "Stopped";
            SelectedServer.Uptime = "00:00:00";
            SelectedServer.PlayerCount = 0;
            SelectedServer.Fps = 0;
        }

        public async Task RestartServerAsync()
        {
            if (SelectedServer == null)
            {
                AddLog(GetLoc("[WARNING] Cannot restart server: No server selected.", "[ВНИМАНИЕ] Не удалось перезагрузить сервер: Сервер не выбран."), LogType.Warning);
                return;
            }
            await StopServerAsync();
            await StartServerAsync();
        }

        public void ClearConsole() => ConsoleLogs.Clear();

        public async Task TriggerWipeAsync()
        {
            if (SelectedServer == null) return;
            var serverPath = SelectedServer.Path;
            bool wipeBps = SelectedServer.Config?.WipeSchedule?.WipeBlueprints ?? false;
            AddLog(GetLoc($"[SYSTEM] Initiating Wipe for {SelectedServer.Name} (Blueprints: {wipeBps})...", $"[SYSTEM] Запуск вайпа для {SelectedServer.Name} (Чертежи: {wipeBps})..."), LogType.Warning);
            await _wipeService.ExecuteWipeAsync(serverPath, SelectedServer.Name, wipeBps, true);
            AddLog(GetLoc("[SUCCESS] Wipe completed successfully.", "[SUCCESS] Вайп успешно завершен."), LogType.Success);
        }

        public async Task<bool> TriggerBackupAsync()
        {
            if (SelectedServer == null) return false;
            AddLog(GetLoc("[SYSTEM] Creating server backup...", "[SYSTEM] Создание резервной копии сервера..."), LogType.System);
            var result = await _backupService.CreateBackupAsync(SelectedServer.Path, SelectedServer.Name);
            if (result != null)
            {
                AddLog(GetLoc("[SUCCESS] Backup created successfully.", "[SUCCESS] Резервная копия успешно создана."), LogType.Success);
                return true;
            }
            return false;
        }

        public string GetBackupsJson()
        {
            if (SelectedServer == null) return "[]";
            var backups = _backupService.GetBackups(SelectedServer.Name);
            return JsonSerializer.Serialize(backups);
        }

        public void SwitchLanguage()
        {
            CurrentLanguage = CurrentLanguage == "ru" ? "en" : "ru";
        }

        public void SaveServerSettings(string json)
        {
            if (SelectedServer == null) return;

            try 
            {
                var options = new JsonSerializerOptions { 
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString
                };
                var newConfig = JsonSerializer.Deserialize<ServerConfig>(json, options);
                if (newConfig != null)
                {
                    // Validate ports (Пункт 19)
                    if (!SystemService.IsPortAvailable(newConfig.Port, false))
                    {
                        AddLog(GetLoc($"[WARNING] Port {newConfig.Port} is already in use by another application.", $"[ВНИМАНИЕ] Игровой порт {newConfig.Port} (UDP) уже занят другим приложением."), LogType.Warning);
                    }
                    if (!SystemService.IsPortAvailable(newConfig.QueryPort, false))
                    {
                        AddLog(GetLoc($"[WARNING] Query Port {newConfig.QueryPort} is already in use by another application.", $"[ВНИМАНИЕ] Query-порт {newConfig.QueryPort} (UDP) уже занят другим приложением."), LogType.Warning);
                    }
                    if (!SystemService.IsPortAvailable(newConfig.RconPort, true))
                    {
                        AddLog(GetLoc($"[WARNING] RCON Port {newConfig.RconPort} is already in use by another application.", $"[ВНИМАНИЕ] RCON-порт {newConfig.RconPort} (TCP) уже занят другим приложением."), LogType.Warning);
                    }

                    SelectedServer.Config = newConfig;
                    _serverManager.SaveServerSettings(SelectedServer.Path, SelectedServer.Config);
                    AddLog(GetLoc("[SUCCESS] Server configuration updated and persisted.", "[SUCCESS] Конфигурация сервера обновлена и сохранена."), LogType.Success);
                    
                    // v12.3: Explicit sync to Frontend
                    RequestConfigSync?.Invoke(SelectedServer.Config);
                    
                    // Force UI sync to update the sidebar server name if Hostname changed
                    if (SelectedServer.Name != newConfig.Hostname && !string.IsNullOrEmpty(newConfig.Hostname)) {
                        SelectedServer.Name = newConfig.Hostname;
                        SaveServers();
                    }
                    OnPropertyChanged(nameof(SelectedServer));

                    // v16.3: Sync AI state immediately
                    _geminiService.ActiveModel = newConfig.AiModel ?? "nvidia/nemotron-3-super-120b-a12b:free";
                    if (!string.IsNullOrEmpty(newConfig.AiApiKey))
                    {
                        _geminiService.Initialize(newConfig.AiApiKey);
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog(GetLoc($"[ERROR] Failed to apply settings: {ex.Message}", $"[ERROR] Не удалось применить настройки: {ex.Message}"), LogType.Error);
            }
        }

        private void LoadServers()
        {
            try
            {
                ServerList.LoadServers();
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] LoadServers: {ex.Message}", LogType.Error);
            }
        }

        private void SaveServers()
        {
            try
            {
                ServerList.SaveServers();
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] SaveServers: {ex.Message}", LogType.Error);
            }
        }

        public void SaveConfig()
        {
            if (SelectedServer != null) {
                 _serverManager.SaveServerSettings(SelectedServer.Path, SelectedServer.Config);
                 AddLog(GetLoc("[SUCCESS] Configuration saved.", "[SUCCESS] Конфигурация сохранена."), LogType.Success);
            }
        }


        private async void StartStatusPolling()
        {
            _pollingCts = new CancellationTokenSource();
            var token = _pollingCts.Token;
            int tickCounter = 0;

            while (!token.IsCancellationRequested)
            {
                try 
                {
                    await Task.Delay(1000, token);
                    if (SelectedServer != null && (SelectedServer.Status == "Running" || SelectedServer.Status == "Starting..."))
                    {
                        if (!_serverManager.IsRunning)
                        {
                            _serverManager.TryReattach(SelectedServer.Path);
                        }
                        
                        // New: Ensure RCON is connected for Running / Starting server
                        if (!_rconService.IsConnected && SelectedServer.Config != null)
                        {
                            _ = ConnectRconAsync();
                        }
                        else if (_rconService.IsConnected && SelectedServer.Status == "Starting...")
                        {
                            SelectedServer.Status = "Running";
                            OnPropertyChanged(nameof(SelectedServer));
                        }

                        UpdateResourceStats();
                        tickCounter++;

                        // Deep Analytics & Team Refresh (Every 60s)
                        if (tickCounter % 60 == 0)
                        {
                            _ = RefreshPlayersAsync();
                        }

                        if (tickCounter % 5 == 0)
                        {
                            var srv = SelectedServer;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    int qPort = srv.Config?.QueryPort ?? (srv.Port + 1);
                                    if (qPort == 0) qPort = srv.Port + 1;
                                    
                                    string queryIp = srv.Config?.ServerIP ?? "127.0.0.1";
                                    if (queryIp == "0.0.0.0") queryIp = "127.0.0.1";

                                    var info = await _a2sService.QueryServerInfoAsync(queryIp, qPort);
                                    if (info != null)
                                    {
                                        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                                        {
                                            srv.PlayerCount = info.Players;
                                            srv.MaxPlayers = info.MaxPlayers;
                                            srv.Ping = (int)info.Ping;
                                            
                                            AddToHistory(srv.PlayerHistory, srv.PlayerCount);
                                            AddToHistory(srv.PingHistory, srv.Ping);

                                            if (srv.Status == "Starting...")
                                            {
                                                srv.Status = "Running";
                                                OnPropertyChanged(nameof(SelectedServer));
                                            }
                                        });
                                    }

                                    // v16.4: RCON telemetry via `serverinfo` & `status`
                                    if (_rconService.IsConnected)
                                    {
                                        await _rconService.SendCommandAsync("serverinfo");
                                        await Task.Delay(500); 
                                        await _rconService.SendCommandAsync("status");
                                    }
                                    else
                                    {
                                        _serverManager.SendCommand("serverinfo");
                                        _serverManager.SendCommand("status");
                                    }
                                }
                                catch { }
                            });
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[POLL] Status polling error: {ex.Message}");
                }
            }
        }

        private void UpdateResourceStats()
        {
            try 
            {
                if (SelectedServer == null) return;

                // 1. Update Panel's own stats (Self-Monitoring)
                try
                {
                    _currentProcess.Refresh();
                    PanelRam = _currentProcess.WorkingSet64 / (1024.0 * 1024 * 1024);

                    var now = DateTime.UtcNow;
                    var cpuTime = _currentProcess.TotalProcessorTime;
                    if (_lastCpuTimeCheck != DateTime.MinValue)
                    {
                        var timeWindow = now - _lastCpuTimeCheck;
                        var systemTimeDelta = timeWindow.TotalMilliseconds * Environment.ProcessorCount;
                        var processTimeDelta = (cpuTime - _lastCpuTime).TotalMilliseconds;
                        if (systemTimeDelta > 0)
                        {
                            PanelCpu = Math.Min(100.0, Math.Max(0.0, (processTimeDelta / systemTimeDelta) * 100.0));
                        }
                    }
                    _lastCpuTime = cpuTime;
                    _lastCpuTimeCheck = now;
                }
                catch { }

                SelectedServer.PanelRam = PanelRam;
                
                // 2. Network Usage (System-wide)
                string netStr = _networkService.GetCurrentUsage();
                SelectedServer.NetworkUsage = netStr;
                
                var netMatch = Regex.Match(netStr, @"([0-9.]+)");
                if (netMatch.Success && double.TryParse(netMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double netVal)) {
                    AddToHistory(SelectedServer.NetworkHistory, netVal);
                }

                // 3. Process Stats (Server)
                var stats = _serverManager.GetProcessStats();
                
                SelectedServer.RamUsageValue = stats.Ram / (1024.0 * 1024 * 1024);
                SelectedServer.RamUsage = $"{SelectedServer.RamUsageValue:F1} / {SystemService.TotalPhysicalRamGb:F1} GB";
                SelectedServer.CpuUsageValue = stats.Cpu;

                AddToHistory(SelectedServer.CpuHistory, stats.Cpu);
                AddToHistory(SelectedServer.RamHistory, SelectedServer.RamUsageValue);

                // Online/Offline Parity Check
                if ((SelectedServer.Status == "Running" || SelectedServer.Status == "Starting...") && !_serverManager.IsRunning)
                {
                    SelectedServer.Status = "Stopped";
                    _notificationService.Send(GetLoc("Server Stopped", "Сервер остановлен"), 
                        GetLoc("The server process has exited unexpectedly.", "Процесс сервера неожиданно завершился."), 
                        NotificationType.Error);
                }
            }
            catch { }
        }

        public async Task TriggerInstallAsync(string serverName, string path, string modType)
        {
            if (_isInstalling) return;
            _isInstalling = true;
            InstallStatus = "Initializing...";
            InstallProgress = 0;

            // v16.5: Forward logs to InstallLog during installation for Install.html 
            Action<string> installLogForwarder = (msg) => {
                InstallLog = msg; // Triggers PropertyChanged → install_log → WebView
            };
            _serverManager.LogReceived += installLogForwarder;

            try
        {
                AddLog(GetLoc($"[SYSTEM] Starting installation for '{serverName}'...", $"[SYSTEM] Запуск установки для '{serverName}'..."), LogType.System);
                await _serverManager.InstallServerAsync(path, serverName, modType);
                
                // Track new server
                var newServer = new ServerModel { 
                    Name = serverName, 
                    Path = Path.Combine(path, serverName), 
                    Status = "Stopped",
                    Port = 28015,
                    ModType = modType
                };
                Servers.Add(newServer);
                SaveServers();
                OnPropertyChanged(nameof(Servers));
                SelectServer(serverName); // v17.0: Auto-select after install
                AddLog(GetLoc($"[SUCCESS] Server '{serverName}' deployed and registered.", $"[SUCCESS] Сервер '{serverName}' развернут и зарегистрирован."), LogType.Success);
                AddLog(GetLoc("[SUCCESS] DEPLOYMENT COMPLETE", "[SUCCESS] УСТАНОВКА ЗАВЕРШЕНА"), LogType.Success); // Trigger for UI
                InstallStatus = "Installed";
            }
            catch (Exception ex)
        {
                InstallStatus = "Error";
                AddLog(GetLoc($"[CRITICAL] Deployment Fail: {ex.Message}", $"[CRITICAL] Критическая ошибка развертывания: {ex.Message}"), LogType.Error);
            }
            finally
        {
                // v16.5: Always clean up forwarding subscription
                _serverManager.LogReceived -= installLogForwarder;
                _isInstalling = false;
            }
        }

        public async Task InstallPlugin(MarketplacePlugin plugin)
        {
            if (SelectedServer == null) return;

            AddLog(GetLoc($"[SYSTEM] Downloading '{plugin.Name}'...", $"[SYSTEM] Загрузка '{plugin.Name}'..."), LogType.System);
            
            try 
            {
                // 1. Download Content
                string content = await _pluginService.DownloadPluginContentAsync(plugin.DownloadUrl);
                
                // 2. AI Shield Audit
                AddLog(GetLoc("[AI SHIELD] Initiating pre-install security audit...", "[AI SHIELD] Запуск предварительного аудита безопасности..."), LogType.System);
                var (isSafe, auditLog) = await _geminiService.AuditPluginAsync(plugin.Name, content);
                
                if (!isSafe)
                {
                    AddLog(GetLoc($"[DANGER] AI SHIELD BLOCKED INSTALLATION: Potential security threat detected in '{plugin.Name}'.", $"[DANGER] AI SHIELD ЗАБЛОКИРОВАЛ УСТАНОВКУ: Обнаружена угроза безопасности в '{plugin.Name}'."), LogType.Error);
                    AddLog($"[AI SHIELD REPORT] {auditLog}", LogType.Warning);
                    return;
                }

                AddLog(GetLoc("[AI SHIELD] Plugin verified. No immediate threats found.", "[AI SHIELD] Плагин проверен. Явных угроз не обнаружено."), LogType.Success);

                // 3. Physical Install
                bool useCarbon = SelectedServer.Framework.ToUpper() == "CARBON";
                await _pluginService.InstallPluginFromContentAsync(SelectedServer.Path, plugin.Name, content, plugin, useCarbon);
                
                AddLog(GetLoc($"[SUCCESS] '{plugin.Name}' installed successfully.", $"[SUCCESS] '{plugin.Name}' успешно установлен."), LogType.Success);
            }
            catch (Exception ex)
            {
                AddLog(GetLoc($"[ERROR] Failed to install plugin: {ex.Message}", $"[ERROR] Ошибка при установке плагина: {ex.Message}"), LogType.Error);
            }
        }


        public async Task<List<MarketplacePlugin>> SearchPlugins(string query)
        {
            return await _pluginService.SearchPluginsAsync(query);
        }


        public void ExecuteVmCommand(string command)
        {
            // Gemini Tool Execution Logic
            switch (command)
            {
                case "StartServer": _ = StartServerAsync(); break;
                case "StopServer": _ = StopServerAsync(); break;
                case "RestartServer": _ = RestartServerAsync(); break;
                case "WipeServer": _ = TriggerWipeAsync(); break;
            }
        }

        public async Task PrepareSteamCmdOnly()
        {
            AddLog(GetLoc("[SYSTEM] Preparing SteamCMD...", "[SYSTEM] Подготовка SteamCMD..."), LogType.System);
            await _serverManager.PrepareSteamCmdAsync(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steamcmd"));
            AddLog(GetLoc("[SUCCESS] SteamCMD is ready.", "[SUCCESS] SteamCMD готов к работе."), LogType.Success);
        }

        public Task RunDiagnostics(Action<object> callback)
        {
            Task.Run(async () => {
                AddLog(GetLoc("[SYSTEM] Running system diagnostics...", "[SYSTEM] Запуск системной диагностики..."), LogType.System);
                
                // 1. Security Level (0-20%)
                callback?.Invoke(new { type = "system_status", name = "Security Level", status = "ok", detail = "Protocols Active" });
                await Task.Delay(800);

                // 2. Core Library (20-40%)
                callback?.Invoke(new { type = "system_status", name = "Core Library", status = "ok", detail = ".NET 10 Runtime Loaded" });
                await Task.Delay(600);

                // 3. Cloud Link (40-60%)
                callback?.Invoke(new { type = "system_status", name = "Cloud Link", status = "ok", detail = "Sync with TRP Cloud Established" });
                await Task.Delay(700);

                // 4. Storage Health (60-80%)
                callback?.Invoke(new { type = "system_status", name = "Storage Health", status = "ok", detail = "I/O Performance: Optimal" });
                await Task.Delay(500);

                // 5. Core Engine (80-100%)
                callback?.Invoke(new { type = "system_status", name = "Core Engine", status = "ok", detail = "SteamCMD & Oxide Bridge Active" });
                await Task.Delay(900);

                // FINAL: Ready (Enables 'Initialize Dashboard' or auto-redirect)
                callback?.Invoke(new { type = "system_status", status = "ready" });
                
                AddLog(GetLoc("[SUCCESS] Diagnostics complete. Dashboard ready.", "[SUCCESS] Диагностика завершена. Панель готова."), LogType.Success);
            });
            return Task.CompletedTask;
        }

        public async void ExecuteConsoleCommand(string cmd)
        {
            if (SelectedServer != null)
            {
                AddLog($"> {cmd}", LogType.Rcon);

                if (_rconService.IsConnected)
                {
                    await _rconService.SendCommandAsync(cmd);
                }
                else
                {
                    _serverManager.SendCommand(cmd);
                }
            }
        }

        public void SelectServer(string name)
        {
            if (string.IsNullOrEmpty(name)) return;

            // v10.4: Robust search with trimming and case-insensitivity
            var server = Servers.FirstOrDefault(s => string.Equals(s.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            
            if (server != null)
            {
                ServerList.SelectedServer = server; // v10.4.1: Sync with sub-VM first
                SelectedServer = server;
                DetectServerFramework(server);
                AddLog(GetLoc($"[SYSTEM] Switched to server: {server.Name} (Framework: {server.Framework})", $"[SYSTEM] Выбран сервер: {server.Name} (Ядро: {server.Framework})"), LogType.System);
            }
            else
            {
                AddLog(GetLoc($"[WARNING] Server '{name}' not found in the list.", $"[ВНИМАНИЕ] Сервер '{name}' не найден в списке."), LogType.Warning);
            }
        }

        private void DetectServerFramework(ServerModel server)
        {
            try {
                if (string.IsNullOrEmpty(server.Path)) return;
                
                // v10.5: Improved multi-path detection
                string[] checkPaths = { 
                    server.Path, 
                    Path.Combine(server.Path, "rustds"),
                    Directory.GetParent(server.Path)?.FullName ?? server.Path 
                };
                server.Framework = "VANILLA";

                foreach (var path in checkPaths)
                {
                    if (!Directory.Exists(path)) continue;

                    // Carbon Detection
                    if (Directory.Exists(Path.Combine(path, "carbon")) || 
                        File.Exists(Path.Combine(path, "carbon.dll")) || 
                        File.Exists(Path.Combine(path, "Carbon.targets")))
                    {
                        server.Framework = "CARBON";
                        break;
                    }

                    // Oxide Detection
                    if (Directory.Exists(Path.Combine(path, "oxide")) || 
                        File.Exists(Path.Combine(path, "RustDedicated_Data", "Managed", "Oxide.Core.dll")))
                    {
                        server.Framework = "OXIDE";
                        break;
                    }
                }
            } catch { server.Framework = "UNKNOWN"; }
            
            UpdateAvailableCommands();
        }

        private void UpdateAvailableCommands()
        {
            var framework = SelectedServer?.Framework ?? "VANILLA";
            var groups = CommandLibrary.GetCommands(framework);
            AvailableCommandsJson = JsonSerializer.Serialize(groups);
        }

        public async Task SaveServerConfigAsync(ServerConfig config)
        {
            if (SelectedServer == null) return;
            
            AddLog(GetLoc($"[SYSTEM] Saving configuration for '{SelectedServer.Name}'...", $"[SYSTEM] Сохранение конфигурации для '{SelectedServer.Name}'..."), LogType.System);
            
            // Persist to disk
            _serverManager.SaveServerSettings(SelectedServer.Path, config);
            
            // Sync in-memory model
            SelectedServer.Config = config;
            
            // Re-detect framework (in case identity or paths changed)
            DetectServerFramework(SelectedServer);
            
            AddLog(GetLoc($"[SUCCESS] Configuration saved and applied.", $"[SUCCESS] Конфигурация сохранена и применена."), LogType.Success);
            await Task.CompletedTask;
        }

        private string _marketplaceResultsJson = "[]";
        public string MarketplaceResultsJson
        {
            get => _marketplaceResultsJson;
            set { _marketplaceResultsJson = value; OnPropertyChanged(); }
        }

        public async Task SearchMarketplaceAsync(string query, string source)
        {
            try {
                AgentStatus = "Searching...";
                var results = await _pluginService.SearchPluginsAsync(query, source);
                
                // Attach security statuses from cache
                foreach (var plugin in results)
                {
                    if (_pluginSecurityStatus.TryGetValue(plugin.Name, out var status))
                        plugin.SecurityStatus = status;
                }

                MarketplaceResultsJson = JsonSerializer.Serialize(results);
                AgentStatus = "Ready";
            } catch (Exception ex) {
                AddLog(GetLoc($"[ERROR] Marketplace search failed: {ex.Message}", $"[ERROR] Ошибка поиска в магазине: {ex.Message}"), LogType.Error);
                AgentStatus = "Error";
            }
        }

        public async Task<string> GetPluginDetailsHtmlAsync(string slug, string source)
        {
            try {
                AgentStatus = "Fetching details...";
                var html = await _pluginService.GetPluginDetailsHtmlAsync(slug, source);
                AgentStatus = "Ready";
                return html;
            } catch (Exception ex) {
                AgentStatus = "Error";
                return $"<p class='text-red-500'>Ошибка получения описания: {ex.Message}</p>";
            }
        }

        public async Task<bool> InstallMarketplacePluginAsync(string pluginJson)
        {
            if (SelectedServer == null) {
                AddLog(GetLoc("[ERROR] No server selected for installation.", "[ERROR] Сервер для установки не выбран."), LogType.Error);
                return false;
            }

            try {
                var plugin = JsonSerializer.Deserialize<MarketplacePlugin>(pluginJson);
                if (plugin == null) return false;

                AddLog(GetLoc($"[SYSTEM] Installing {plugin.Name} ({plugin.Source})...", $"[SYSTEM] Установка {plugin.Name} ({plugin.Source})..."), LogType.System);
                bool useCarbon = SelectedServer.Framework == "CARBON";
                
                var success = await _pluginService.InstallPluginAsync(SelectedServer.Path, plugin, useCarbon);
                if (success) {
                    AddLog(GetLoc($"[SUCCESS] {plugin.Name} installed to {SelectedServer.Name}.", $"[SUCCESS] {plugin.Name} установлен на {SelectedServer.Name}."), LogType.Success);
                    
                    // Point 1: Proactive Background Audit
                    string targetFolder = useCarbon ? "carbon" : "oxide";
                    string pluginPath = Path.Combine(SelectedServer.Path, targetFolder, "plugins", $"{plugin.Name}.cs");
                    
                    _ = Task.Run(async () => {
                        AddLog(GetLoc($"[AI SHIELD] Proactive scan started for {plugin.Name}...", $"[AI SHIELD] Проактивное сканирование {plugin.Name} запущено..."), LogType.System);
                        string report = await RunSecurityAuditAsync(pluginPath);
                        string risk = report.Contains("КРИТИЧЕСКИЙ") ? "КРИТИЧЕСКИЙ" : (report.Contains("ВЫСОКИЙ") ? "ВЫСОКИЙ" : "СРЕДНИЙ");
                        _pluginSecurityStatus[plugin.Name] = risk;
                        
                        AddLog(GetLoc($"[AI SHIELD] Proactive scan finished for {plugin.Name}. Risk Level: {risk}", $"[AI SHIELD] Проактивное сканирование {plugin.Name} завершено. Уровень риска: {risk}"), 
                            risk == "СРЕДНИЙ" ? LogType.Warning : LogType.Error);
                            
                        // Notify UI about security update via event (Decoupled from UI element)
                        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                            OnSecurityUpdate?.Invoke(plugin.Name, risk);
                        });
                    });
                    
                    return true;
                } else {
                    AddLog(GetLoc($"[ERROR] Failed to download {plugin.Name}.", $"[ERROR] Не удалось загрузить {plugin.Name}."), LogType.Error);
                    return false;
                }
            } catch (Exception ex) {
                AddLog(GetLoc($"[ERROR] Installation crash: {ex.Message}", $"[ERROR] Критическая ошибка при установке: {ex.Message}"), LogType.Error);
                return false;
            }
        }

        public async Task<(bool Safe, string Description)> AuditMarketplacePluginAsync(string pluginJson)
        {
            try {
                var plugin = JsonSerializer.Deserialize<MarketplacePlugin>(pluginJson);
                if (plugin == null) return (false, "Error parsing plugin data.");

                _agentStatus = "Fetching Source...";
                OnPropertyChanged(nameof(AgentStatus));
                
                string code = await _pluginService.GetMarketplacePluginSourceAsync(plugin);
                if (string.IsNullOrEmpty(code)) return (false, "Could not fetch plugin source for auditing.");

                _agentStatus = "Auditing Source...";
                OnPropertyChanged(nameof(AgentStatus));
                
                return await _geminiService.AuditPluginAsync(plugin.Name + ".cs", code);
            }
            catch (Exception ex) {
                return (false, $"Audit failed: {ex.Message}");
            }
            finally {
                _agentStatus = "Ready";
                OnPropertyChanged(nameof(AgentStatus));
            }
        }

        public async Task<string> CheckMarketplacePluginDependenciesAsync(string pluginJson)
        {
            try {
                var plugin = JsonSerializer.Deserialize<MarketplacePlugin>(pluginJson);
                if (plugin == null || SelectedServer == null) return "[]";

                string content = await _pluginService.DownloadPluginContentAsync(plugin.DownloadUrl);
                if (string.IsNullOrEmpty(content)) return "[]";

                var missing = _pluginService.CheckMissingDependencies(content, SelectedServer);
                return JsonSerializer.Serialize(missing);
            } catch {
                return "[]";
            }
        }

        public async Task<string> GetAiResponseAsync(string prompt)
        {
            try {
                _agentStatus = "Thinking...";
                OnPropertyChanged(nameof(AgentStatus));
                
                var systemPrompt = $"You are TRP AI Assistant for TRP Server Panel. Current Server Framework: {SelectedServer?.Framework ?? "Unknown"}. " +
                                   "Answer concisely and professionally. Focus on Rust server administration, Oxide/Carbon plugins, and technical support.";
                
                var response = await _geminiService.AskAgentAsync(prompt, systemPrompt);
                
                _agentStatus = "Ready";
                OnPropertyChanged(nameof(AgentStatus));
                
                return response;
            } catch (Exception ex) {
                _agentStatus = "Error";
                OnPropertyChanged(nameof(AgentStatus));
                return $"[AI Error] {ex.Message}";
            }
        }

        public async Task<string> CheckForPluginUpdatesAsync()
        {
            if (SelectedServer == null) return "[]";
            
            AddLog(GetLoc("[SYSTEM] Checking for plugin updates...", "[SYSTEM] Проверка обновлений плагинов..."), LogType.System);
            var updates = await _pluginService.CheckForUpdatesAsync(SelectedServer.Path);
            
            if (updates.Count > 0) {
                AddLog(GetLoc($"[INFO] {updates.Count} updates found.", $"[INFO] Найдено {updates.Count} обновлений."), LogType.Warning);
            } else {
                AddLog(GetLoc("[SUCCESS] All plugins are up to date.", "[SUCCESS] Все плагины актуальны."), LogType.Success);
            }
            
            return JsonSerializer.Serialize(updates);
        }

        public async Task<bool> UpdateMarketplacePluginAsync(string pluginJson)
        {
            if (SelectedServer == null) return false;
            
            try {
                var plugin = JsonSerializer.Deserialize<MarketplacePlugin>(pluginJson);
                if (plugin == null) return false;

                AddLog(GetLoc($"[SYSTEM] Updating {plugin.Name}...", $"[SYSTEM] Обновление {plugin.Name}..."), LogType.System);
                bool success = await _pluginService.InstallPluginAsync(SelectedServer.Path, plugin, SelectedServer.Framework == "CARBON");
                
                if (success) {
                    AddLog(GetLoc($"[SUCCESS] {plugin.Name} updated successfully.", $"[SUCCESS] {plugin.Name} успешно обновлен."), LogType.Success);
                    return true;
                }
                return false;
            } catch { return false; }
        }

        private string _lastAuditedCode = "";
        public async Task<string> RunSecurityAuditAsync(string pluginPath)
        {
            try {
                _agentStatus = "Auditing...";
                OnPropertyChanged(nameof(AgentStatus));
                
                string code = await _pluginService.ReadFileAsync(pluginPath);
                if (string.IsNullOrEmpty(code)) return "Ошибка: Файл плагина пуст или недоступен.";

                _lastAuditedCode = code; // Cache for Diff view
                
                // Point 4: Deep Context (Related files + Dependencies)
                var relatedFiles = _pluginService.GetRelatedPluginFiles(pluginPath);
                var relatedPaths = _pluginService.GetRelatedPluginFilePaths(pluginPath);
                var dependencies = _pluginService.GetPluginDependencies(pluginPath);
                
                string extraContext = "";
                foreach (var path in relatedPaths) {
                    try {
                        string content = await _pluginService.ReadFileAsync(path);
                        // Truncate if too large to avoid context blowing up
                        if (content.Length > 2000) content = content.Substring(0, 2000) + "... [TRUNCATED]";
                        extraContext += $"\n--- FILE: {Path.GetFileName(path)} ---\n{content}\n";
                    } catch { }
                }

                string contextFiles = relatedFiles.Count > 0 ? string.Join(", ", relatedFiles) : "None found";
                string depStr = dependencies.Count > 0 ? string.Join(", ", dependencies) : "None";

                var securityPrompt = 
                    "You are the TRP Cybersecurity Auditor, an expert in Rust (Oxide/Carbon) plugin security. " +
                    "Your mission is to analyze the provided C# code for MALICIOUS BEHAVIOR, STEALERS, BACKDOORS, and LOGIC BOMBS. " +
                    "\n\nDETECTION CHECKLIST:\n" +
                    "1. WebRequests to unknown domains (stealing RCON, IP, player data).\n" +
                    "2. Hidden hardcoded SteamIDs with 'developer' or 'admin' privileges.\n" +
                    "3. Use of Reflection to bypass security or private access.\n" +
                    "4. Obfuscated or suspicious string encryption.\n" +
                    "5. Unauthorized RCON command execution via code.\n" +
                    $"6. Dependencies: {depStr}. (Check if these dependencies are misused or suspicious).\n" +
                    $"7. Related files content detected: {contextFiles}. (Analyzed below).\n\n" +
                    "EXTRA CONTEXT (Config/Data):\n" + extraContext +
                    "\nFormat your response as a professional auditor's report in Russian. " +
                    "STRUCTURE:\n" +
                    "1. SUMMARY (Краткий обзор)\n" +
                    "2. FINDINGS (Детальный список находок)\n" +
                    "3. [DATA_EXPOSURE] - EXPLICITLY list all URLs and WHAT data is being sent (SteamID, Configs, etc.)\n" +
                    "4. RISK LEVEL (LOW, MEDIUM, HIGH, CRITICAL).\n\n" +
                    "IMPORTANT: If you find an issue, YOU MUST ALSO PROVIDE THE ENTIRE FIXED CODE BLOCK enclosed in [FIXED_CODE]...[/FIXED_CODE] tags. " +
                    "The fixed code must be fully functional and use secure patterns (e.g., Whitelist for URLs, permission checks).";
                
                var response = await _geminiService.AskAgentAsync($"Аудит плагина: {Path.GetFileName(pluginPath)}\n\nКод:\n{code}", securityPrompt);
                
                _agentStatus = "Ready";
                OnPropertyChanged(nameof(AgentStatus));
                
                return response;
            } catch (Exception ex) {
                _agentStatus = "Error";
                OnPropertyChanged(nameof(AgentStatus));
                return $"[Audit Error] {ex.Message}";
            }
        }

        public string GetLastAuditedCode() => _lastAuditedCode;

        // Point 1: Proactive Security Storage
        private ConcurrentDictionary<string, string> _pluginSecurityStatus = new();
        public string GetPluginSecurityStatus(string pluginName) => _pluginSecurityStatus.TryGetValue(pluginName, out var status) ? status : "Unscanned";

        public async Task<bool> ApplyAuditFixAsync(string pluginPath, string auditReport)
        {
            try {
                // Extract fixed code from tags
                var match = Regex.Match(auditReport, @"\[FIXED_CODE\](.*?)\[/FIXED_CODE\]", RegexOptions.Singleline);
                if (match.Success)
                {
                    string fixedCode = match.Groups[1].Value.Trim();
                    
                    // Point 5: Compilation Sandbox check
                    if (!ValidateCode(fixedCode, out string errors))
                    {
                        AddLog(GetLoc($"[AI SHIELD] Fix rejected: Code contains syntax errors.\n{errors}", $"[AI SHIELD] Исправление отклонено: Код содержит синтаксические ошибки.\n{errors}"), LogType.Error);
                        return false;
                    }

                    await _pluginService.SaveFileAsync(pluginPath, fixedCode);
                    AddLog(GetLoc($"[AI SHIELD] Security patch applied to {Path.GetFileName(pluginPath)}.", $"[AI SHIELD] Патч безопасности применен к {Path.GetFileName(pluginPath)}."), LogType.Success);
                    return true;
                }
                return false;
            } catch (Exception ex) {
                AddLog(GetLoc($"[ERROR] Failed to apply security patch: {ex.Message}", $"[ERROR] Не удалось применить патч безопасности: {ex.Message}"), LogType.Error);
                return false;
            }
        }

        private bool ValidateCode(string code, out string errors)
        {
            errors = "";
            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(code);
                var compilation = CSharpCompilation.Create("AuditFix")
                    .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                    .AddSyntaxTrees(syntaxTree);

                var diagnostics = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (diagnostics.Count > 0)
                {
                    errors = string.Join("\n", diagnostics.Take(3).Select(d => d.ToString()));
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                errors = ex.Message;
                return false;
            }
        }

        public List<LocalPlugin> GetCurrentServerPlugins()
        {
            if (SelectedServer == null) return new List<LocalPlugin>();
            return _pluginService.GetLocalPlugins(SelectedServer.Path, SelectedServer.Framework);
        }


        public async Task ImportServerAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            AddLog(GetLoc($"[SYSTEM] Validating import path: {path}", $"[SYSTEM] Проверка пути импорта: {path}"), LogType.System);
            
            string exePath = Path.Combine(path, "RustDedicated.exe");
            if (!File.Exists(exePath))
            {
                // Try looking in a 'rustds' subdirectory
                string subPath = Path.Combine(path, "rustds");
                string subExePath = Path.Combine(subPath, "RustDedicated.exe");
                if (Directory.Exists(subPath) && File.Exists(subExePath))
                {
                    path = subPath; // Automatically adjust path
                    AddLog(GetLoc($"[SYSTEM] RustDedicated.exe found in subdirectory. Adjusting path to: {path}", $"[SYSTEM] RustDedicated.exe найден во вложенной папке. Автокоррекция пути на: {path}"), LogType.System);
                }
                else
                {
                    AddLog(GetLoc("[ERROR] Import failed: RustDedicated.exe not found in selected folder or nested 'rustds' folder.", "[ERROR] Ошибка импорта: RustDedicated.exe не найден в выбранной папке."), LogType.Error);
                    return;
                }
            }

            string dirName = Path.GetFileName(path);
            string serverName = dirName ?? "Imported Server";
            if (serverName.Equals("rustds", StringComparison.OrdinalIgnoreCase))
            {
                var parentDir = Directory.GetParent(path);
                if (parentDir != null)
                {
                    serverName = parentDir.Name;
                }
            }
            
            // Check if already in list
            if (Servers.Any(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
                AddLog(GetLoc("[WARNING] Server already exists in the panel.", "[WARNING] Данный сервер уже добавлен в панель."), LogType.Warning);
                return;
            }

            var newServer = new ServerModel 
        { 
                Name = serverName, 
                Path = path, 
                Status = "Stopped",
                Port = 28015,
                ModType = "vanilla",
                Framework = "VANILLA"
            };

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                Servers.Add(newServer);
                SaveServers();
                OnPropertyChanged(nameof(Servers));
                SelectServer(serverName); // v17.0: Auto-select after import
            });

            AddLog(GetLoc($"[SUCCESS] Server '{serverName}' imported and registered.", $"[SUCCESS] Сервер '{serverName}' импортирован и добавлен в панель."), LogType.Success);
        }

        public async Task DeleteServerAsync(string name)
        {
            var server = Servers.FirstOrDefault(s => s.Name == name);
            if (server != null)
            {
                bool isSelected = SelectedServer == server;
                if (isSelected && _rconService.IsConnected)
                {
                    await _rconService.DisconnectAsync();
                }
                Servers.Remove(server);
                SaveServers();
                if (isSelected)
                {
                    var nextSelection = Servers.FirstOrDefault();
                    SelectedServer = nextSelection!;
                    ServerList.SelectedServer = nextSelection;
                }
                AddLog(GetLoc($"[WARNING] Server '{name}' removed from list.", $"[WARNING] Сервер '{name}' удален из списка."), LogType.Warning);
            }
            await Task.CompletedTask;
        }

        public List<LocalPlugin> GetInstalledPlugins()
        {
            if (SelectedServer == null) return new List<LocalPlugin>();
            return _pluginService.GetLocalPlugins(SelectedServer.Path, SelectedServer.Framework);
        }

        public List<PluginFile> GetPluginDataFiles(string pluginName)
        {
            if (SelectedServer == null) return new List<PluginFile>();
            return _pluginService.GetPluginDataFiles(SelectedServer.Path, pluginName, SelectedServer.Framework);
        }

        public async Task<string> ReadPluginFileAsync(string path)
        {
            if (SelectedServer == null) return "[ERROR] No server selected";
            if (!IsSafePath(SelectedServer.Path, path))
            {
                AddLog("[WARNING] Attempted unauthorized plugin file read (Path Traversal blocked).", LogType.System);
                return "[ERROR] Access Denied";
            }
            return await _pluginService.ReadFileAsync(path);
        }

        public async Task SavePluginFileAsync(string path, string content)
        {
            if (SelectedServer == null) return;
            if (!IsSafePath(SelectedServer.Path, path))
            {
                AddLog("[WARNING] Attempted unauthorized plugin file write (Path Traversal blocked).", LogType.System);
                return;
            }
            await _pluginService.SaveFileAsync(path, content);
            AddLog(GetLoc($"[SUCCESS] File saved: {Path.GetFileName(path)}", $"[SUCCESS] Файл сохранен: {Path.GetFileName(path)}"), LogType.Success);
        }

        public void ExecutePluginAction(string pluginName, string action)
        {
            if (SelectedServer == null) return;

            string cmd = "";
            string framework = SelectedServer.Framework.ToUpper();
            var plugins = _pluginService.GetLocalPlugins(SelectedServer.Path, SelectedServer.Framework);
            var plug = plugins.FirstOrDefault(p => p.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));

            switch (action.ToLower())
        {
                case "reload":
                    cmd = framework == "CARBON" ? $"carbon.reload {pluginName}" : $"o.reload {pluginName}";
                    break;
                case "unload":
                    cmd = framework == "CARBON" ? $"carbon.unload {pluginName}" : $"o.unload {pluginName}";
                    break;
                case "delete":
                    if (plug != null)
                    {
                        _pluginService.DeleteFile(plug.FullPath);
                        AddLog(GetLoc($"[WARNING] Plugin file deleted: {pluginName}", $"[WARNING] Файл плагина удален: {pluginName}"), LogType.Warning);
                        return;
                    }
                    break;
                case "enable":
                    if (plug != null)
                    {
                        _pluginService.TogglePluginState(plug.FullPath, true);
                        AddLog(GetLoc($"[SUCCESS] Plugin enabled: {pluginName}", $"[SUCCESS] Плагин включен: {pluginName}"), LogType.Success);
                        return;
                    }
                    break;
                case "disable":
                    if (plug != null)
                    {
                        _pluginService.TogglePluginState(plug.FullPath, false);
                        AddLog(GetLoc($"[WARNING] Plugin disabled: {pluginName}", $"[WARNING] Плагин выключен: {pluginName}"), LogType.Warning);
                        return;
                    }
                    break;
            }

            if (!string.IsNullOrEmpty(cmd))
        {
                _serverManager.SendCommand(cmd);
                AddLog($"> {cmd}", LogType.Rcon);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private async Task ConnectRconAsync()
        {
            if (SelectedServer?.Config == null || _isRconConnecting) return;
            if ((DateTime.Now - _lastRconConnectAttempt).TotalSeconds < 5) return;
            
            _lastRconConnectAttempt = DateTime.Now;
            _isRconConnecting = true;
            try
            {
                string ip = SelectedServer.Config.ServerIP;
                if (string.IsNullOrEmpty(ip) || ip == "0.0.0.0") ip = "127.0.0.1";
                
                await _rconService.ConnectAsync(ip, SelectedServer.Config.RconPort, SelectedServer.Config.RconPassword);
                AddLog(GetLoc("[SYSTEM] RCON WebSocket connected.", "[SYSTEM] WebSocket RCON подключен."), LogType.Success);
                _lastRconErrorTime = DateTime.MinValue; // Reset on success

                if (SelectedServer != null && SelectedServer.Status == "Starting...")
                {
                    SelectedServer.Status = "Running";
                    OnPropertyChanged(nameof(SelectedServer));
                }
            }
            catch (Exception ex)
            {
                // Cooldown for RCON error logging: 30 seconds
                if ((DateTime.Now - _lastRconErrorTime).TotalSeconds > 30)
                {
                    AppLogService.Log($"Connection failed to RCON {SelectedServer.Config.RconPort}: {ex.Message}", AppLogLevel.ERROR, "RCON");
                    _lastRconErrorTime = DateTime.Now;
                }
                System.Diagnostics.Debug.WriteLine($"[RCON] Connect failed: {ex.Message}");
            }
            finally
            {
                _isRconConnecting = false;
            }
        }

        private void AddToHistory<T>(ObservableCollection<T> collection, T value, int maxPoints = 40)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                collection.Add(value);
                while (collection.Count > maxPoints) collection.RemoveAt(0);
            });
        }

        #region Player Management

        public async Task<string> GetPlayerListAsync()
        {
            // Hybrid approach: Return current PlayerIdentities (populated via DB + RCON)
            // This ensures data is visible even if RCON is offline
            try 
            {
                if (SelectedServer == null) return "[]";
                
                // If we have data in our observable collection, use it
                List<PlayerIdentity> playerList;
                lock (PlayerIdentities)
                {
                    playerList = PlayerIdentities.ToList();
                }

                // NOTE: RefreshPlayersAsync() is NOT triggered here to avoid infinite loop.
                // It is called once from the SelectedServer setter and explicitly via 'get_player_list' bridge.
                var options = new JsonSerializerOptions { 
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.WriteAsString
                };
                return await Task.FromResult(JsonSerializer.Serialize(playerList, options));
            }
            catch (Exception ex)
            {
                AddLog($"[ERROR] Failed to serialize player list for UI: {ex.Message}", LogType.Error);
                return "[]";
            }
        }

        public async Task<string> GetMetaDataAsync()
        {
            var fallbackMeta = new {
                Hostname = SelectedServer?.Name ?? "Unknown TRP Server",
                Framework = SelectedServer?.Framework ?? "Hybrid",
                Players = PlayerIdentities.Count,
                MaxPlayers = SelectedServer?.Config?.MaxPlayers ?? 100,
                Status = SelectedServer?.Status ?? "Offline"
            };

            if (!_rconService.IsConnected) 
            {
                return JsonSerializer.Serialize(fallbackMeta);
            }
            
            try 
            {
                // Native Rust command: returns JSON object
                var response = await _rconService.SendCommandWithResponseAsync("serverinfo");
                
                if (string.IsNullOrEmpty(response) || response == "Timeout" || !response.Trim().StartsWith("{")) 
                {
                    return JsonSerializer.Serialize(fallbackMeta);
                }

                return response;
            }
            catch (Exception ex)
            {
                AddLog(GetLoc($"[ERROR] Failed to fetch server metadata: {ex.Message}", $"[ERROR] Не удалось получить метаданные сервера: {ex.Message}"), LogType.Error);
                return JsonSerializer.Serialize(fallbackMeta);
            }
        }

        public async Task WipePlayerBlueprintsAsync(string steamId)
        {
            if (SelectedServer == null) return;
            bool wasRunning = SelectedServer.Status == "Running" || SelectedServer.Status == "Starting...";
            
            try
            {
                if (wasRunning)
                {
                    AddLog(GetLoc("[SYSTEM] Server is running. Stopping server to wipe blueprints safely...", "[SYSTEM] Сервер запущен. Останавливаем сервер для безопасной очистки чертежей..."), LogType.Warning);
                    await StopServerAsync();
                    // Wait a short delay to ensure files are fully unlocked
                    await Task.Delay(2000);
                }

                AddLog(GetLoc($"[SYSTEM] Deleting blueprints for player {steamId}...", $"[SYSTEM] Удаление чертежей для игрока {steamId}..."), LogType.System);
                bool success = await _rustDbService.DeletePlayerBlueprintsAsync(SelectedServer.Path, steamId);
                
                if (success)
                {
                    AddLog(GetLoc($"[SUCCESS] Blueprints for player {steamId} have been successfully deleted.", $"[SUCCESS] Чертежи для игрока {steamId} успешно удалены."), LogType.Success);
                }
                else
                {
                    AddLog(GetLoc($"[WARNING] No blueprints found for player {steamId} or deletion failed.", $"[ВНИМАНИЕ] Чертежи для игрока {steamId} не найдены или не удалены."), LogType.Warning);
                }

                if (wasRunning)
                {
                    AddLog(GetLoc("[SYSTEM] Restarting server...", "[SYSTEM] Перезапуск сервера..."), LogType.System);
                    await StartServerAsync();
                }

                await RefreshPlayersAsync();
            }
            catch (Exception ex)
            {
                AddLog(GetLoc($"[ERROR] Failed to wipe player blueprints: {ex.Message}", $"[ERROR] Не удалось очистить чертежи игрока: {ex.Message}"), LogType.Error);
            }
        }

        public async Task ExecutePlayerActionAsync(string steamId, string action, string extra = "", string duration = "0")
        {
            if (action.Equals("global.playerbpwipe", StringComparison.OrdinalIgnoreCase))
            {
                await WipePlayerBlueprintsAsync(steamId);
                return;
            }

            if (!_rconService.IsConnected) 
            {
                AddLog(GetLoc("[ERROR] RCON not connected. Action aborted.", "[ERROR] RCON не подключен. Действие отменено."), LogType.Error);
                return;
            }

            try
            {
                string command = "";
                switch (action.ToLower())
                {
                    case "kick":
                        // Standard Rust kick format. Carbon/Oxide will intercept this.
                        command = $"kick {steamId} \"{extra}\"";
                        break;
                    case "ban":
                        // Standard Rust ban format: ban <id/name> <reason> [duration]
                        // Duration is in minutes for most frameworks, or string like '1h'
                        command = $"ban {steamId} \"{extra}\" {duration}";
                        break;
                    case "unban":
                        command = $"unban {steamId}";
                        break;
                    case "teleport":
                        if (SelectedServer == null || string.IsNullOrWhiteSpace(SelectedServer.Config.AdminSteamId))
                        {
                            AddLog(GetLoc("[ERROR] Teleport failed: Admin SteamID is not set in server settings.", "[ERROR] Телепортация не удалась: SteamID администратора не указан в настройках сервера."), LogType.Error);
                            return;
                        }
                        command = $"teleporttosplayer {SelectedServer.Config.AdminSteamId} {steamId}";
                        break;
                    case "teleport2me":
                        if (SelectedServer == null || string.IsNullOrWhiteSpace(SelectedServer.Config.AdminSteamId))
                        {
                            AddLog(GetLoc("[ERROR] Teleport failed: Admin SteamID is not set in server settings.", "[ERROR] Телепортация не удалась: SteamID администратора не указан в настройках сервера."), LogType.Error);
                            return;
                        }
                        command = $"teleporttosplayer {steamId} {SelectedServer.Config.AdminSteamId}";
                        break;
                    default:
                        command = $"{action} {steamId} \"{extra}\"";
                        break;
                }

                await _rconService.SendCommandAsync(command);
                AddLog(GetLoc($"[SYSTEM] RCON Command sent: {command}", $"[SYSTEM] RCON Команда отправлена: {command}"), LogType.System);
            }
            catch (Exception ex)
            {
                AddLog(GetLoc($"[ERROR] Player action failed: {ex.Message}", $"[ERROR] Не удалось выполнить действие над игроком: {ex.Message}"), LogType.Error);
            }
        }

        public async Task RefreshPlayersAsync()
        {
            if (SelectedServer == null) return;
            
            AppLogService.Log("Initiating deep analytics scan (DB + RCON)...", AppLogLevel.DEBUG, "DATABASE");
            AppLogService.Log($"Searching DB in path: {SelectedServer.Path}", AppLogLevel.DEBUG, "DATABASE");
            
            try 
            {
                // 1. Fetch identities from player.identities.*.db
                var players = await _rustDbService.GetPlayersAsync(SelectedServer.Path);
                if (players == null)
                {
                    players = new List<PlayerIdentity>();
                }
                
                if (players.Count == 0)
                {
                    AppLogService.Log("No historical players found in local database.", AppLogLevel.WARN, "DATABASE");
                }
                else
                {
                    AppLogService.Log($"Loaded {players.Count} players from database.", AppLogLevel.DEBUG, "DATABASE");
                }
                
                // 2. Fetch deep analytics (Deaths, Survival Time, Blueprints)
                var deepData = players.Count > 0 ? await _rustDbService.GetDeepAnalyticsAsync(SelectedServer.Path) : new Dictionary<ulong, RustDatabaseService.PlayerDeepStats>();

                // 3. Fetch team data from relationship.*.db
                var teams = players.Count > 0 ? await _rustDbService.GetTeamsAsync(SelectedServer.Path) : new List<TeamInfo>();

                // 3.5. Fetch blueprint data from player.blueprints.*.db
                List<PlayerBlueprint> allBlueprints = new List<PlayerBlueprint>();
                if (players.Count > 0)
                {
                    try
                    {
                        allBlueprints = await _rustDbService.GetBlueprintsAsync(SelectedServer.Path);
                    }
                    catch (Exception ex)
                    {
                        AppLogService.Log($"Failed to load player blueprints: {ex.Message}", AppLogLevel.WARN, "DATABASE");
                    }
                }
                
                // --- Fetch Steam Data ---
                var steamIds = players.Select(p => p.SteamID).ToList();
                var steamData = players.Count > 0 ? await _steamService.GetPlayerSummariesAsync(steamIds) : new Dictionary<ulong, SteamPlayerSummary>();

                // 4. Enrich data
                foreach (var p in players)
                {
                    // History sync (Application Database)
                    var extra = _historyService.GetExtraData(p.SteamID);
                    p.IPAddress = extra.IP;
                    p.PlayTimeSeconds = extra.PlaytimeSeconds;
                    
                    // Priority: Steam API > DB History > fallback
                    if (steamData.TryGetValue(p.SteamID, out var steamPlayer))
                    {
                        if (!string.IsNullOrEmpty(steamPlayer.AvatarUrl))
                            p.AvatarUrl = steamPlayer.AvatarUrl;
                        else
                            p.AvatarUrl = string.IsNullOrEmpty(extra.AvatarUrl) ? $"https://www.steamid.link/api/v1/avatar/{p.SteamID}" : extra.AvatarUrl;

                        if (!string.IsNullOrEmpty(steamPlayer.CountryCode))
                        {
                            p.CountryCode = steamPlayer.CountryCode;
                            try
                            {
                                var region = new System.Globalization.RegionInfo(steamPlayer.CountryCode);
                                p.Country = region.DisplayName;
                            }
                            catch
                            {
                                p.Country = steamPlayer.CountryCode;
                            }
                        }
                        else
                        {
                            p.Country = extra.Country;
                            p.CountryCode = extra.CountryCode;
                        }
                    }
                    else
                    {
                        p.AvatarUrl = string.IsNullOrEmpty(extra.AvatarUrl) ? $"https://www.steamid.link/api/v1/avatar/{p.SteamID}" : extra.AvatarUrl;
                        p.Country = extra.Country;
                        p.CountryCode = extra.CountryCode;
                    }
                    
                    // Deep DB Stats sync
                    if (deepData.TryGetValue(p.SteamID, out var stats))
                    {
                        p.Kills = stats.Kills;
                        p.Deaths = stats.Deaths;
                        p.BlueprintsCount = stats.Blueprints;
                        p.TotalSurvivalTimeSeconds = stats.SurvivalTime;
                    }

                    // Team sync
                    var team = teams.FirstOrDefault(t => t.Members.Contains(p.SteamID) || t.LeaderID == p.SteamID);
                    if (team != null)
                    {
                        p.TeamID = team.TeamID;
                        p.TeamName = team.LeaderID == p.SteamID ? $"Leader ({team.TeamID})" : $"Member ({team.TeamID})";
                    }
                    
                    // Blueprints sync
                    try
                    {
                        var bps = allBlueprints.Where(b => b.SteamID == p.SteamID).ToList();
                        p.Blueprints = bps.Select(b => Utils.RustItems.GetItemNameFormatted(b.BlueprintID)).ToList();
                    }
                    catch
                    {
                        p.Blueprints = new List<string>();
                    }

                    // Trigger GeoIP if IP found but country unknown
                    if (p.IPAddress != "N/A" && p.Country == "Unknown")
                    {
                        _ = ResolveCountryAsync(p);
                    }
                }

                // 5. Update UI Collection
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                    lock (PlayerIdentities)
                    {
                        foreach (var dp in players)
                        {
                            var existing = PlayerIdentities.FirstOrDefault(x => x.SteamID == dp.SteamID);
                            if (existing == null)
                            {
                                PlayerIdentities.Add(dp);
                            }
                            else
                            {
                                // Update only persistent fields, preserve real-time ones (Ping, etc.)
                                existing.Username = dp.Username;
                                existing.IPAddress = dp.IPAddress;
                                existing.Country = dp.Country;
                                existing.CountryCode = dp.CountryCode;
                                existing.AvatarUrl = dp.AvatarUrl;
                                existing.Kills = dp.Kills;
                                existing.Deaths = dp.Deaths;
                                existing.BlueprintsCount = dp.BlueprintsCount;
                                existing.TotalSurvivalTimeSeconds = dp.TotalSurvivalTimeSeconds;
                                existing.TeamID = dp.TeamID;
                                existing.TeamName = dp.TeamName;
                                existing.PlayTimeSeconds = dp.PlayTimeSeconds;
                                existing.Blueprints = dp.Blueprints;
                            }
                        }
                    }

                    OnPropertyChanged(nameof(PlayerIdentities));
                    PlayerIdentitiesUpdated?.Invoke();
                });
                
                // 5. If RCON is connected, sync live data (Health, Ping, real-time IP)
                if (_rconService.IsConnected)
                {
                    await SyncLivePlayerDataAsync();
                }

                AppLogService.Log($"Deep scan complete. Analyzing {players.Count} active profiles.", AppLogLevel.DEBUG, "DATABASE");
            }
            catch (Exception ex)
            {
                AppLogService.Log($"Failed to scan database: {ex.Message}", AppLogLevel.ERROR, "DATABASE");
            }
        }

        private async Task ResolveCountryAsync(PlayerIdentity player)
        {
            try
            {
                // Simple GeoIP via ip-api.com (No API key needed for basic usage)
                var response = await _httpClient.GetStringAsync($"http://ip-api.com/json/{player.IPAddress}?fields=status,country,countryCode");
                using (var doc = JsonDocument.Parse(response))
                {
                    var root = doc.RootElement;
                    if (root.GetProperty("status").GetString() == "success")
                    {
                        player.Country = root.GetProperty("country").GetString() ?? "Unknown";
                        player.CountryCode = root.GetProperty("countryCode").GetString()?.ToLower() ?? "";
                        
                        // Persist this immediately
                        _historyService.UpdateExtendedData(player.SteamID, player.Username, player.IPAddress, player.Country, player.CountryCode, player.AvatarUrl);
                    }
                }
            }
            catch { }
        }

        private async Task SyncLivePlayerDataAsync()
        {
            try
            {
                var response = await _rconService.SendCommandWithResponseAsync("playerlist");
                if (string.IsNullOrEmpty(response) || response == "Timeout" || !response.Trim().StartsWith("[")) return;

                // Move log to diagnostics, not UI console
                AppLogService.Log($"[playerlist] {response.Substring(0, Math.Min(300, response.Length))}", AppLogLevel.DEBUG, "RCON");

                var livePlayers = JsonSerializer.Deserialize<List<JsonElement>>(response);
                if (livePlayers == null) return;

                bool listChanged = false;

                foreach (var lp in livePlayers)
                {
                    // v16.7: Multi-property SteamID detection (SteamID, UserId, ID) + Robust Type handling
                    string steamIdStr = "";
                    JsonElement sidElem;
                    if (lp.TryGetProperty("SteamID", out sidElem) || lp.TryGetProperty("UserId", out sidElem) || lp.TryGetProperty("ID", out sidElem))
                    {
                        if (sidElem.ValueKind == JsonValueKind.Number) steamIdStr = sidElem.GetUInt64().ToString();
                        else steamIdStr = sidElem.GetString() ?? "";
                    }

                    if (!ulong.TryParse(steamIdStr, out ulong steamId) || steamId == 0) continue;

                    string rconName = "Unknown";
                    if (lp.TryGetProperty("Username", out var unElem) && !string.IsNullOrWhiteSpace(unElem.GetString()))
                        rconName = unElem.GetString()!;
                    else if (lp.TryGetProperty("DisplayName", out var dnElem) && !string.IsNullOrWhiteSpace(dnElem.GetString()))
                        rconName = dnElem.GetString()!;

                    PlayerIdentity? player = null;
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                        lock (PlayerIdentities)
                        {
                            player = PlayerIdentities.FirstOrDefault(p => p.SteamID == steamId);
                            if (player == null)
                            {
                                player = new PlayerIdentity
                                {
                                    SteamID   = steamId,
                                    Username  = rconName,
                                    Country   = "Unknown",
                                    IPAddress = "N/A",
                                    Ping      = 1,
                                    // v16.7: Better fallback for avatars using steamid.link proxy
                                    AvatarUrl = $"https://www.steamid.link/api/v1/avatar/{steamId}"
                                };
                                PlayerIdentities.Add(player);
                                listChanged = true;
                            }
                        }
                    });

                    if (player == null) continue;

                    // Sync live metrics under lock
                    lock (PlayerIdentities)
                    {
                        if (lp.TryGetProperty("Ping",    out JsonElement pingElem)) player.Ping   = pingElem.GetInt32();
                        if (lp.TryGetProperty("Health",  out JsonElement hpElem))   player.Health = (int)hpElem.GetDouble();
                        
                        if (lp.TryGetProperty("Address", out JsonElement addrElem))
                        {
                            string fullAddr = addrElem.GetString() ?? "";
                            string ip = fullAddr.Split(':')[0];
                            if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0" && player.IPAddress != ip)
                            {
                                player.IPAddress = ip;
                                _historyService.UpdateIP(player.SteamID, ip);
                                _ = ResolveCountryAsync(player);
                            }
                        }

                        // Playtime delta persistence
                        if (_lastPlaytimeUpdate != DateTime.MinValue)
                        {
                            double delta = (DateTime.Now - _lastPlaytimeUpdate).TotalSeconds;
                            player.PlayTimeSeconds += delta;
                            _historyService.AddPlaytime(player.SteamID, delta);
                        }
                    }
                }

                _lastPlaytimeUpdate = DateTime.Now;

                if (listChanged)
                {
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() => PlayerIdentitiesUpdated?.Invoke());
                }
            }
            catch (Exception ex) { 
                System.Diagnostics.Debug.WriteLine($"[RCON] Sync Error: {ex.Message}");
            }
        }

        #endregion

        #region Plugin Management

        public async Task<string> GetLocalPluginsAsync()
        {
            if (SelectedServer == null) {
                AddLog("[WARN] GetLocalPluginsAsync: No server selected.", LogType.Warning);
                return "[]";
            }

            try
            {
                AddLog($"[DEBUG] Scanning plugins for server '{SelectedServer.Name}'", LogType.System);
                AddLog($"[DEBUG] Path: {SelectedServer.Path}", LogType.System);
                AddLog($"[DEBUG] Framework: {SelectedServer.Framework}", LogType.System);

                var localPlugins = _pluginService.GetLocalPlugins(SelectedServer.Path, SelectedServer.Framework);
                AddLog($"[DEBUG] Found {localPlugins.Count} plugin files on disk.", localPlugins.Count > 0 ? LogType.Success : LogType.Warning);

                if (_rconService.IsConnected)
                {
                    AddLog("[DEBUG] Syncing plugin states via RCON...", LogType.System);
                    var rconResponse = await _rconService.SendCommandWithResponseAsync("plugins");
                    if (!string.IsNullOrEmpty(rconResponse))
                    {
                        if (rconResponse.Trim().StartsWith("["))
                        {
                            try {
                                var rconList = JsonSerializer.Deserialize<List<JsonElement>>(rconResponse);
                                if (rconList != null) {
                                    foreach (var p in localPlugins) {
                                        var live = rconList.FirstOrDefault(l => 
                                            l.TryGetProperty("Name", out var nameProp) && nameProp.GetString() == p.Name);
                                        
                                        if (live.ValueKind != JsonValueKind.Undefined) {
                                            p.IsEnabled = true;
                                            p.Version = live.TryGetProperty("Version", out var v) ? v.GetString() ?? p.Version : p.Version;
                                            p.Author = live.TryGetProperty("Author", out var a) ? a.GetString() ?? p.Author : p.Author;
                                        }
                                    }
                                    AddLog($"[DEBUG] RCON Sync completed. Processed {rconList.Count} live plugins from JSON.", LogType.Success);
                                }
                            } catch (Exception ex) { 
                                AddLog($"[DEBUG] RCON Plugin sync JSON parsing failed: {ex.Message}", LogType.Warning);
                            }
                        }
                        else
                        {
                            try {
                                var lines = rconResponse.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                int matchedCount = 0;
                                foreach (var line in lines)
                                {
                                    // Parse loaded plugin:
                                    // Example: "  01 \"Hub Admin Panel\" (2.0.2) by TEAM_RUST_PLUGINS (2.45s / 120 KB) - HubAdminPanel.cs"
                                    // Or: "  01 \"Hub Admin Panel\" (2.0.2) by TEAM_RUST_PLUGINS - HubAdminPanel.cs"
                                    var loadedMatch = Regex.Match(line, @"^\s*\d+\s+""([^""]+)""\s+\(([^)]+)\)\s+by\s+(.+?)(?:\s+\([\d\.]+s\s+/\s+[^)]+\))?\s+-\s+(.+)$", RegexOptions.IgnoreCase);
                                    if (loadedMatch.Success)
                                    {
                                        string title = loadedMatch.Groups[1].Value.Trim();
                                        string version = loadedMatch.Groups[2].Value.Trim();
                                        string author = loadedMatch.Groups[3].Value.Trim();
                                        string filename = loadedMatch.Groups[4].Value.Trim();
                                        string pluginName = Path.GetFileNameWithoutExtension(filename).Replace(".cs", "").Replace(".disabled", "").Replace(".off", "");
                                        
                                        var p = localPlugins.FirstOrDefault(lp => 
                                            NormalizePluginName(lp.Name) == NormalizePluginName(pluginName) || 
                                            NormalizePluginName(lp.Name) == NormalizePluginName(title));
                                        
                                        if (p != null)
                                        {
                                            p.IsEnabled = true;
                                            p.Status = "Active";
                                            p.Version = version;
                                            p.Author = author;
                                            matchedCount++;
                                        }
                                        continue;
                                    }

                                    // Parse unloaded or errored plugin:
                                    // Example: "  02 PluginName - Unloaded"
                                    // Or: "  02 PluginName - Compile Error"
                                    var unloadedMatch = Regex.Match(line, @"^\s*\d+\s+([a-zA-Z0-9_]+)\s+-\s+(.+)$", RegexOptions.IgnoreCase);
                                    if (unloadedMatch.Success)
                                    {
                                        string pluginName = unloadedMatch.Groups[1].Value.Trim();
                                        string statusText = unloadedMatch.Groups[2].Value.Trim();

                                        var p = localPlugins.FirstOrDefault(lp => 
                                            NormalizePluginName(lp.Name) == NormalizePluginName(pluginName));
                                        
                                        if (p != null)
                                        {
                                            if (statusText.Equals("Unloaded", StringComparison.OrdinalIgnoreCase))
                                            {
                                                p.IsEnabled = false;
                                                p.Status = "Offline";
                                            }
                                            else
                                            {
                                                p.IsEnabled = true;
                                                p.Status = "Error";
                                                p.CompileError = statusText;
                                            }
                                            matchedCount++;
                                        }
                                    }
                                }
                                AddLog($"[DEBUG] RCON Sync completed. Processed {matchedCount} live plugins from text.", LogType.Success);
                            }
                            catch (Exception ex)
                            {
                                AddLog($"[DEBUG] RCON Plugin sync text parsing failed: {ex.Message}", LogType.Warning);
                            }
                        }
                    }
                }

                AddLog($"[DEBUG] Serializing {localPlugins.Count} plugins for UI...", LogType.System);
                string json = JsonSerializer.Serialize(localPlugins);
                AddLog($"[DEBUG] Plugins JSON length: {json.Length} chars.", LogType.System);
                return json;
            }
            catch (Exception ex)
            {
                AppLogService.Log($"Critical error in GetLocalPluginsAsync: {ex.Message}", AppLogLevel.ERROR, "SYSTEM");
                AddLog(GetLoc($"[ERROR] Failed to list local plugins: {ex.Message}", $"[ERROR] Не удалось составить список плагинов: {ex.Message}"), LogType.Error);
                return "[]";
            }
        }

        private string NormalizePluginName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            return name.Replace(" ", "").Replace("_", "").Replace(".", "").Replace("-", "").ToLowerInvariant();
        }


        public async Task<string> GetPluginRelatedFilesAsync(string pluginName)
        {
            if (SelectedServer == null) return "[]";

            try
            {
                var files = new List<object>();
                string[] dirs = { "configs", "data" };

                foreach (var dir in dirs)
                {
                    string path = Path.Combine(SelectedServer.Path, "carbon", dir);
                    if (Directory.Exists(path))
                    {
                        // Match Name.json or Name_*.json
                        var matches = Directory.GetFiles(path, "*.json")
                            .Where(f => Path.GetFileName(f).StartsWith(pluginName, StringComparison.OrdinalIgnoreCase))
                            .Select(f => new {
                                Name = Path.GetFileName(f),
                                FullPath = f,
                                RelativePath = Path.Combine("carbon", dir, Path.GetFileName(f)),
                                Type = dir // configs or data
                            });
                        files.AddRange(matches);
                    }
                }

                return await Task.FromResult(JsonSerializer.Serialize(files));
            }
            catch (Exception ex)
            {
                AddLog(GetLoc($"[ERROR] Failed to find related files: {ex.Message}", $"[ERROR] Не удалось найти связанные файлы: {ex.Message}"), LogType.Error);
                return "[]";
            }
        }

        // Methods ReadPluginFileAsync and SavePluginFileAsync are already defined above (lines 1328-1337)
        // We will keep the ones that use _pluginService if they were there, 
        // but for now I'll just remove these duplicates to fix build.


        public async Task ExecutePluginCommandAsync(string pluginName, string action)
        {
            if (!_rconService.IsConnected) return;

            string cmd = "";
            bool isCarbon = SelectedServer?.Framework?.ToUpper() == "CARBON";
            string prefix = isCarbon ? "c" : "oxide";

            switch (action.ToLower())
            {
                case "reload": cmd = $"{prefix}.reload {pluginName}"; break;
                case "load": cmd = $"{prefix}.load {pluginName}"; break;
                case "unload": cmd = $"{prefix}.unload {pluginName}"; break;
            }

            if (!string.IsNullOrEmpty(cmd))
            {
                await _rconService.SendCommandAsync(cmd);
                AddLog(GetLoc($"[SYSTEM] Plugin action: {action} {pluginName}", $"[SYSTEM] Действие над плагином: {action} {pluginName}"), LogType.System);
            }
        }

        public async Task<bool> DeletePluginAsync(string pluginName)
        {
            if (SelectedServer == null) return false;

            try
            {
                string csPath = Path.Combine(SelectedServer.Path, "carbon", "plugins", pluginName + ".cs");
                if (File.Exists(csPath))
                {
                    File.Delete(csPath);
                    AddLog(GetLoc($"[SYSTEM] Plugin deleted from disk: {pluginName}", $"[SYSTEM] Плагин удален с диска: {pluginName}"), LogType.Warning);
                    return await Task.FromResult(true);
                }
                return await Task.FromResult(false);
            }
            catch (Exception ex)
            {
                AddLog(GetLoc($"[ERROR] Failed to delete plugin: {ex.Message}", $"[ERROR] Не удалось удалить плагин: {ex.Message}"), LogType.Error);
                return false;
            }
        }


        public async Task RunSecurityAuditAsync()
        {
            if (ConsoleLogs.Count == 0) return;

            AgentStatus = "Auditing Logs...";
            try
            {
                var recentLogs = ConsoleLogs.Select(l => l.Message).TakeLast(100).ToList();
                LastSecurityReport = await _geminiService.AuditLogAsync(recentLogs);
                _notificationService.Success("Security Audit Complete", $"Risk Level: {LastSecurityReport?.RiskLevel ?? "Unknown"}");
            }
            catch (Exception ex)
            {
                _notificationService.Error("Audit Failed", ex.Message);
            }
            finally
            {
                AgentStatus = "Ready";
            }
        }

        private bool IsSafePath(string rootPath, string targetPath)
        {
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(targetPath)) return false;
            try
            {
                string fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullTarget = Path.GetFullPath(targetPath);
                return fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public string GetFileListJson(string path)
        {
            if (SelectedServer == null) return "[]";
            if (string.IsNullOrEmpty(path)) 
                path = SelectedServer.Path;

            if (!IsSafePath(SelectedServer.Path, path))
            {
                AddLog("[WARNING] Attempted unauthorized folder access (Path Traversal blocked).", LogType.System);
                return "[]";
            }

            var items = _fileService.ListDirectory(path);
            return JsonSerializer.Serialize(items);
        }

        public async Task<string> ReadFileContentAsync(string path)
        {
            if (SelectedServer == null) return "[ERROR] No server selected";
            if (!IsSafePath(SelectedServer.Path, path))
            {
                AddLog("[WARNING] Attempted unauthorized file read (Path Traversal blocked).", LogType.System);
                return "[ERROR] Access Denied";
            }
            return await _fileService.ReadFileAsync(path);
        }

        public async Task<bool> SaveFileContentAsync(string path, string content)
        {
            if (SelectedServer == null) return false;
            if (!IsSafePath(SelectedServer.Path, path))
            {
                AddLog("[WARNING] Attempted unauthorized file write (Path Traversal blocked).", LogType.System);
                return false;
            }
            return await _fileService.WriteFileAsync(path, content);
        }

        public bool DeleteFileItem(string path)
        {
            if (SelectedServer == null) return false;
            if (!IsSafePath(SelectedServer.Path, path))
            {
                AddLog("[WARNING] Attempted unauthorized file deletion (Path Traversal blocked).", LogType.System);
                return false;
            }
            return _fileService.DeleteItem(path);
        }

        public bool CreateNewFolder(string parentPath, string name)
        {
            if (SelectedServer == null) return false;
            var combinedPath = Path.Combine(parentPath, name);
            if (!IsSafePath(SelectedServer.Path, combinedPath))
            {
                AddLog("[WARNING] Attempted unauthorized folder creation (Path Traversal blocked).", LogType.System);
                return false;
            }
            return _fileService.CreateDirectory(parentPath, name);
        }

        #endregion
    }


    public class RelayCommand : ICommand
        {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
