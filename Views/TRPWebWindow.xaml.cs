using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using TRPServerPanel.Models;
using TRPServerPanel.Services;
using TRPServerPanel.ViewModels;
using Microsoft.Win32;

namespace TRPServerPanel.Views
{
    public partial class TRPWebWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly string _targetUrl;
        private bool _isSelectingFolder = false;

        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern int SendMessage(IntPtr h, int msg, int wp, int lp);
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };

        private static readonly JsonSerializerOptions _encOpts = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.WriteAsString
        };

        public TRPWebWindow(string url)
        {
            InitializeComponent();
            _vm = (MainViewModel)App.ServiceProvider.GetRequiredService(typeof(MainViewModel));
            _targetUrl = url;

            // Independent window — no Owner so user can freely switch to main window via taskbar / Alt+Tab
            this.ShowInTaskbar = true;

            // Push events
            _vm.PlayerIdentitiesUpdated += OnPlayerIdentitiesUpdated;
            AppLogService.OnLogAdded += OnAppLogAdded;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Console.OnLogBatchReceived += OnLogBatchReceived;

            this.Closed += (_, __) =>
            {
                _vm.PlayerIdentitiesUpdated -= OnPlayerIdentitiesUpdated;
                AppLogService.OnLogAdded -= OnAppLogAdded;
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.Console.OnLogBatchReceived -= OnLogBatchReceived;
            };

            InitPageAsync();
        }

        // ─── Push events ────────────────────────────────────────────────────

        private void OnPlayerIdentitiesUpdated()
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (PageBrowser?.CoreWebView2 == null) return;
                List<Models.PlayerIdentity> snapshot;
                lock (_vm.PlayerIdentities) { snapshot = _vm.PlayerIdentities.ToList(); }
                string payload = JsonSerializer.Serialize(snapshot, _encOpts);
                var resp = new { type = "player_list_update", payload };
                PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp, _encOpts));
            });
        }

        private void OnAppLogAdded(AppLogEntry entry)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (PageBrowser?.CoreWebView2 == null) return;
                var resp = new { type = "app_logs_sync", entry };
                PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp, _jsonOpts));
            });
        }

        private void OnVmPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (PageBrowser?.CoreWebView2 == null) return;
                if (e.PropertyName == nameof(MainViewModel.InstallStatus) ||
                    e.PropertyName == nameof(MainViewModel.InstallProgress))
                {
                    var d = new { type = "install_update", status = _vm.InstallStatus, progress = _vm.InstallProgress };
                    PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(d));
                }
                if (e.PropertyName == nameof(MainViewModel.AgentStatus))
                {
                    var d = new { type = "agent_status", status = _vm.AgentStatus };
                    PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(d));
                }
                if (e.PropertyName == nameof(MainViewModel.CurrentLanguage))
                {
                    var d = new { type = "language_updated", lang = _vm.CurrentLanguage };
                    PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(d, _jsonOpts));
                }
            });
        }

        private void OnLogBatchReceived(System.Collections.Generic.IEnumerable<Models.LogEntry> batch)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (PageBrowser?.CoreWebView2 == null) return;
                var logs = batch.Select(l => new { message = l.Message, level = l.Type.ToString() }).ToList();
                var d = new { type = "batch_log", logs };
                PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(d, _jsonOpts));
            });
        }

        // ─── WebView2 init ───────────────────────────────────────────────────

        private async void InitPageAsync()
        {
            try
            {
                var env = await App.GetSharedEnvironmentAsync();
                await PageBrowser.EnsureCoreWebView2Async(env);

                string res = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
                PageBrowser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "trp.app", res, CoreWebView2HostResourceAccessKind.Allow);

                PageBrowser.Source = new Uri(_targetUrl);
                PageBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                PageBrowser.CoreWebView2.Settings.IsZoomControlEnabled = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TRPWebWindow] Init error: {ex.Message}");
            }
        }

        // ─── Navigation ──────────────────────────────────────────────────────

        private void PageBrowser_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // Show loader
            var loader = (FrameworkElement?)this.FindName("LoadingOverlay");
            if (loader != null) loader.Visibility = Visibility.Visible;

            // If navigating back to Dashboard → close this window
            if (e.Uri.Contains("Dashboard.html", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                Dispatcher.BeginInvoke(() =>
                {
                    this.Close();
                    foreach (Window w in System.Windows.Application.Current.Windows)
                        if (w is MainWindow m) { m.Activate(); break; }
                });
            }
        }

        private void PageBrowser_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var loader = (FrameworkElement?)this.FindName("LoadingOverlay");
            if (loader != null) { loader.Visibility = Visibility.Collapsed; loader.IsHitTestVisible = false; }
        }

        // ─── Window state ────────────────────────────────────────────────────

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState != WindowState.Minimized)
            {
                PageBrowser.Visibility = Visibility.Collapsed;
                PageBrowser.Visibility = Visibility.Visible;
            }
        }

        // ─── Bridge ──────────────────────────────────────────────────────────

        private void PageBrowser_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                var msg = JsonDocument.Parse(json).RootElement;

                string action = "";
                if (msg.TryGetProperty("action", out var a)) action = a.GetString() ?? "";
                else if (msg.TryGetProperty("type", out var t)) action = t.GetString() ?? "";

                try {
                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge_debug.log");
                    File.AppendAllText(logPath, $"[{DateTime.Now}] WEB_WINDOW RECV: action='{action}' json='{json}'\n");
                } catch { }

                if (string.IsNullOrEmpty(action)) return;

                switch (action)
                {
                    case "js_error":
                        if (msg.TryGetProperty("message", out var jm))
                        {
                            string mText = jm.GetString() ?? "";
                            string sText = msg.TryGetProperty("source", out var js) ? js.GetString() ?? "" : "";
                            int line = msg.TryGetProperty("lineno", out var jl) ? jl.GetInt32() : 0;
                            string stack = msg.TryGetProperty("error", out var je) ? je.GetString() ?? "" : "";
                            _vm.AddLog($"[JS ERROR] {mText} at {sText}:{line}\nStack: {stack}", LogType.Error);
                            try {
                                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge_debug.log");
                                File.AppendAllText(logPath, $"[{DateTime.Now}] JS EXCEPTION: message='{mText}' source='{sText}' line={line} stack='{stack}'\n");
                            } catch { }
                        }
                        break;

                    // ── Window controls ──────────────────────────────────
                    case "minimize":
                        Dispatcher.BeginInvoke(() => this.WindowState = WindowState.Minimized);
                        break;
                    case "maximize":
                        Dispatcher.BeginInvoke(() =>
                            this.WindowState = this.WindowState == WindowState.Maximized
                                ? WindowState.Normal : WindowState.Maximized);
                        break;
                    case "close": case "quit":
                        Dispatcher.BeginInvoke(() => this.Close());
                        break;
                    // Bring main (Dashboard) window to front without closing this page
                    case "focus_main_window":
                        Dispatcher.BeginInvoke(() =>
                        {
                            foreach (Window w in System.Windows.Application.Current.Windows)
                                if (w is MainWindow m) { m.Activate(); m.Focus(); break; }
                        });
                        break;
                    case "drag":
                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                if (this.WindowState == WindowState.Maximized) this.WindowState = WindowState.Normal;
                                
                                var helper = new WindowInteropHelper(this);
                                ReleaseCapture();
                                SendMessage(helper.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                            }
                            catch { }
                        });
                        break;

                    // ── Players ──────────────────────────────────────────
                    case "get_player_list":
                        _ = Dispatcher.InvokeAsync(async () =>
                        {
                            if (PageBrowser?.CoreWebView2 == null) return;
                            await _vm.RefreshPlayersAsync();
                            string pJson = await _vm.GetPlayerListAsync();
                            var r = new { type = "player_list_update", payload = pJson };
                            PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(r, _encOpts));
                        });
                        break;

                    case "get_server_metadata":
                        _ = Dispatcher.InvokeAsync(async () =>
                        {
                            if (PageBrowser?.CoreWebView2 == null) return;
                            string mJson = await _vm.GetMetaDataAsync();
                            var r = new { type = "server_metadata_update", payload = mJson };
                            PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(r, _encOpts));
                        });
                        break;

                    case "player_kick":
                        if (msg.TryGetProperty("steamId", out var kId) && msg.TryGetProperty("reason", out var kR))
                            _ = _vm.ExecutePlayerActionAsync(kId.GetString()!, "kick", kR.GetString()!);
                        break;

                    case "player_ban":
                        if (msg.TryGetProperty("steamId", out var bId) && msg.TryGetProperty("reason", out var bR))
                        {
                            string dur = msg.TryGetProperty("duration", out var dv) ? dv.GetString()! : "0";
                            _ = _vm.ExecutePlayerActionAsync(bId.GetString()!, "ban", bR.GetString()!, dur);
                        }
                        break;

                    case "player_action":
                        if (msg.TryGetProperty("steamId", out var aId) && msg.TryGetProperty("action", out var aAct) && msg.TryGetProperty("extra", out var aExtra))
                        {
                            _ = _vm.ExecutePlayerActionAsync(aId.GetString()!, aAct.GetString()!, aExtra.GetString()!);
                        }
                        break;

                    case "mute":
                    case "unmute":
                    case "teleport":
                    case "teleport2me":
                    case "global.playerbpwipe":
                        if (msg.TryGetProperty("steamId", out var bpSteamId))
                        {
                            string extra = msg.TryGetProperty("extra", out var bpExtra) ? bpExtra.GetString() ?? "" : "";
                            _ = _vm.ExecutePlayerActionAsync(bpSteamId.GetString()!, action, extra);
                        }
                        break;

                    // ── Console / Commands ───────────────────────────────
                    case "command":
                        if (msg.TryGetProperty("name", out var cn))
                        {
                            string? name = cn.GetString();
                            if (name == "ConsoleCommand" && msg.TryGetProperty("payload", out var cp))
                                _vm.ExecuteConsoleCommand(cp.GetString() ?? "");
                            else
                                _vm.ExecuteVmCommand(name ?? "");
                        }
                        break;

                    case "sync_logs":
                        Dispatcher.BeginInvoke(() =>
                        {
                            var logsJson = _vm.Console.GetConsoleLogsJson();
                            var m2 = new { type = "log_sync", data = logsJson };
                            PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(m2));
                        });
                        break;

                    // ── Plugins ──────────────────────────────────────────
                    case "get_local_plugins":
                    case "search_plugins":
                        _ = Dispatcher.InvokeAsync(async () =>
                        {
                            try
                            {
                                if (PageBrowser?.CoreWebView2 == null) return;
                                if (msg.TryGetProperty("query", out var qp) && msg.TryGetProperty("source", out var sp))
                                {
                                    await _vm.SearchMarketplaceAsync(qp.GetString() ?? "", sp.GetString() ?? "umod");
                                    var r = new { type = "search_results", plugins = _vm.MarketplaceResultsJson };
                                    PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(r, _jsonOpts));
                                }
                                else
                                {
                                    string pJson = await _vm.GetLocalPluginsAsync();
                                    var r = new { type = "local_plugins_results", plugins = JsonSerializer.Deserialize<JsonElement>(pJson) };
                                    PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(r, _jsonOpts));
                                }
                            }
                            catch (Exception ex) { _vm.AddLog($"[BRIDGE ERROR] {ex.Message}", LogType.Error); }
                        });
                        break;

                    case "plugin_action":
                        if (msg.TryGetProperty("pluginName", out var pan) && msg.TryGetProperty("pluginAction", out var paa))
                            _ = _vm.ExecutePluginCommandAsync(pan.GetString()!, paa.GetString()!);
                        break;

                    case "delete_plugin":
                        if (msg.TryGetProperty("pluginName", out var dpn))
                            _ = _vm.DeletePluginAsync(dpn.GetString()!);
                        break;

                    case "get_plugin_data":
                        if (msg.TryGetProperty("pluginName", out var pdn))
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                string fJson = await _vm.GetPluginRelatedFilesAsync(pdn.GetString()!);
                                var r = new { type = "plugin_data_results", files = JsonSerializer.Deserialize<JsonElement>(fJson) };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                            });
                        break;

                    case "read_plugin_file":
                        if (msg.TryGetProperty("path", out var rfp))
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                string c = await _vm.ReadPluginFileAsync(rfp.GetString()!);
                                var r = new { type = "file_content_results", content = c, path = rfp.GetString() };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                            });
                        break;

                    case "save_plugin_file":
                        if (msg.TryGetProperty("path", out var sfp) && msg.TryGetProperty("content", out var sfc))
                            _ = _vm.SavePluginFileAsync(sfp.GetString()!, sfc.GetString()!);
                        break;

                    case "market_install":
                        if (msg.TryGetProperty("plugin", out var mip))
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                bool ok = await _vm.InstallMarketplacePluginAsync(mip.GetRawText());
                                var r = new { type = "install_status", success = ok };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                            });
                        break;

                    case "market_check_updates":
                        _ = Dispatcher.InvokeAsync(async () =>
                        {
                            string upd = await _vm.CheckForPluginUpdatesAsync();
                            var r = new { type = "search_results", plugins = upd };
                            PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                        });
                        break;

                    case "market_update":
                        if (msg.TryGetProperty("plugin", out var mup))
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                bool ok = await _vm.UpdateMarketplacePluginAsync(mup.GetRawText());
                                var r = new { type = "install_status", success = ok };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                            });
                        break;

                    case "start_plugin_audit":
                        if (msg.TryGetProperty("payload", out var aup))
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                string rep = await _vm.RunSecurityAuditAsync(aup.GetString() ?? "");
                                if (PageBrowser?.CoreWebView2 == null) return;
                                var r = new
                                {
                                    type = "audit_report", report = rep,
                                    originalCode = _vm.GetLastAuditedCode(),
                                    hasFix = rep.Contains("РИСК: ВЫСОКИЙ") || rep.Contains("КРИТИЧЕСКИЙ"),
                                    riskLevel = rep.Contains("КРИТИЧЕСКИЙ") ? "КРИТИЧЕСКИЙ" : "СРЕДНИЙ"
                                };
                                PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                            });
                        break;

                    case "apply_audit_fix":
                        if (msg.TryGetProperty("path", out var fxp) && msg.TryGetProperty("report", out var fxr))
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                bool ok = await _vm.ApplyAuditFixAsync(fxp.GetString()!, fxr.GetString()!);
                                var r = new { type = "audit_fix_result", success = ok };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                            });
                        break;

                    case "get_plugin_details":
                        if (msg.TryGetProperty("slug", out var gdSlug) && msg.TryGetProperty("source", out var gdSource))
                        {
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                string details = await _vm.GetPluginDetailsHtmlAsync(gdSlug.GetString()!, gdSource.GetString()!);
                                var r = new { type = "plugin_details_result", details = details, slug = gdSlug.GetString() };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                            });
                        }
                        break;

                    case "umod_login":
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            var loginWin = new UModLoginWindow();
                            loginWin.Owner = this;
                            bool? res = loginWin.ShowDialog();
                            var r = new { type = "umod_login_result", success = res == true };
                            PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                        });
                        break;

                    // ── File Explorer ────────────────────────────────────
                    case "get_files":
                        if (msg.TryGetProperty("path", out var pathProp))
                        {
                            string path = pathProp.GetString() ?? "";
                            _ = Dispatcher.InvokeAsync(() => {
                                string json = _vm.GetFileListJson(path);
                                var resp = new { type = "file_list", path = path, items = json };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;

                    case "read_file":
                        if (msg.TryGetProperty("path", out var readPathProp))
                        {
                            string path = readPathProp.GetString() ?? "";
                            _ = Dispatcher.InvokeAsync(async () => {
                                string content = await _vm.ReadFileContentAsync(path);
                                var resp = new { type = "file_content", path = path, content = content };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;

                    case "save_file":
                        if (msg.TryGetProperty("path", out var savePathProp) && msg.TryGetProperty("content", out var contentProp))
                        {
                            string path = savePathProp.GetString() ?? "";
                            string content = contentProp.GetString() ?? "";
                            _ = Dispatcher.InvokeAsync(async () => {
                                bool success = await _vm.SaveFileContentAsync(path, content);
                                var resp = new { type = "save_status", path = path, success = success };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;

                    case "delete_file":
                        if (msg.TryGetProperty("path", out var delPathProp))
                        {
                            string path = delPathProp.GetString() ?? "";
                            _ = Dispatcher.InvokeAsync(() => {
                                bool success = _vm.DeleteFileItem(path);
                                var resp = new { type = "delete_status", path = path, success = success };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;

                    case "create_folder":
                        if (msg.TryGetProperty("parent", out var parentProp) && msg.TryGetProperty("name", out var nameProp))
                        {
                            string parent = parentProp.GetString() ?? "";
                            string name = nameProp.GetString() ?? "";
                            _ = Dispatcher.InvokeAsync(() => {
                                bool success = _vm.CreateNewFolder(parent, name);
                                var resp = new { type = "create_status", success = success };
                                PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                            });
                        }
                        break;

                    // ── Backups ──────────────────────────────────────────
                    case "get_backups":
                        _ = Dispatcher.InvokeAsync(() => {
                            string json = _vm.GetBackupsJson();
                            var resp = new { type = "backup_list", items = json };
                            PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                        });
                        break;

                    case "trigger_backup":
                        _ = Dispatcher.InvokeAsync(async () => {
                            bool success = await _vm.TriggerBackupAsync();
                            var resp = new { type = "backup_status", success = success };
                            PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(resp));
                        });
                        break;

                    // ── Settings ──────────────────────────────────────────
                    case "get_server_config":
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (PageBrowser?.CoreWebView2 == null || _vm.SelectedServer == null) return;
                            var r = new { type = "force_config_sync", payload = _vm.SelectedServer.Config };
                            PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(r, _jsonOpts));
                        });
                        break;

                    case "save_server_settings":
                        if (msg.TryGetProperty("payload", out var ssp))
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                try
                                {
                                    var cfg = JsonSerializer.Deserialize<ServerConfig>(ssp.GetRawText());
                                    if (cfg != null) await _vm.SaveServerConfigAsync(cfg);
                                }
                                catch (Exception ex) { _vm.AddLog($"[ERROR] Config parse: {ex.Message}", LogType.Error); }
                            });
                        break;

                    case "save_system_settings":
                        if (msg.TryGetProperty("payload", out var sysp))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(sysp.GetRawText());
                                bool autorun = doc.RootElement.GetProperty("autorun").GetBoolean();
                                string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                                string appPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true);
                                if (key != null)
                                {
                                    if (autorun) key.SetValue("TRPServerPanel", $"\"{appPath}\"");
                                    else key.DeleteValue("TRPServerPanel", false);
                                }
                            }
                            catch { }
                        }
                        break;

                    // ── App Logs ──────────────────────────────────────────
                    case "get_app_logs":
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (PageBrowser?.CoreWebView2 == null) return;
                            var logs = AppLogService.Logs.ToList();
                            var r = new { type = "app_logs_initial", logs };
                            PageBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(r, _jsonOpts));
                        });
                        break;

                    // ── Install ───────────────────────────────────────────
                    case "install":
                        if (msg.TryGetProperty("path", out var ip))
                        {
                            string path = ip.GetString() ?? @"C:\TRP_Server";
                            string sName = msg.TryGetProperty("server_name", out var sn) ? sn.GetString()! : "My_Rust_Server";
                            string mType = msg.TryGetProperty("mod_type", out var mt) ? mt.GetString()! : "vanilla";
                            _ = Dispatcher.InvokeAsync(async () => await _vm.TriggerInstallAsync(sName, path, mType));
                        }
                        break;

                    case "download_steamcmd":
                        _ = Dispatcher.InvokeAsync(async () => await _vm.PrepareSteamCmdOnly());
                        break;

                    case "select_folder":
                        if (_isSelectingFolder) return;
                        _isSelectingFolder = true;
                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                using var dlg = new System.Windows.Forms.FolderBrowserDialog
                                {
                                    Description = "Select Server Directory",
                                    UseDescriptionForTitle = true,
                                    ShowNewFolderButton = true
                                };
                                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                                {
                                    var r = new { type = "folder_selected", path = dlg.SelectedPath };
                                    PageBrowser?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(r));
                                }
                            }
                            finally { _isSelectingFolder = false; }
                        });
                        break;

                    // ── AI ────────────────────────────────────────────────
                    case "ai_ask":
                        if (msg.TryGetProperty("payload", out var aip))
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                string resp = await _vm.GetAiResponseAsync(aip.GetString() ?? "");
                                _vm.AddLog($"[TRP AI]: {resp}", LogType.Success);
                            });
                        break;

                    case "setLanguage":
                        if (msg.TryGetProperty("lang", out var lp))
                            _vm.CurrentLanguage = lp.GetString() ?? "ru";
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TRPWebWindow Bridge] {ex.Message}");
            }
        }
    }
}
