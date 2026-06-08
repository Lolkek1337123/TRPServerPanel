// TRPName: TRPServerPanel
// Author: TEAM_RUST_PLUGINS
// Changelog:
// - v17.0.1: Added automated backup of old system configurations before loading.
// - v17.0.0: Unified server persistence with encryption.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TRPServerPanel.Models;
using TRPServerPanel.Utils;
using System.Linq;

namespace TRPServerPanel.Services
{
    public class ServerManager
    {
        private Process? _serverProcess;
        public Process? ServerProcess => _serverProcess;
        private readonly HttpClient _httpClient;
        private JobObject? _jobObject;
        public string ServerPath { get; private set; } = string.Empty;

        // CPU Monitoring fields
        private DateTime _lastCpuCheckTime = DateTime.MinValue;
        private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
        private double _currentCpuUsage = 0;

        public ServerManager(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public bool IsRunning => _serverProcess != null && !_serverProcess.HasExited;

        public event Action<string>? LogReceived;
        public event Action<string, double>? ProgressChanged;

        private string GetSteamCmdCachePath()
        {
            var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TRPServerPanel", "Cache", "SteamCMD");
            Directory.CreateDirectory(cachePath);
            return cachePath;
        }

        public async Task InstallServerAsync(string path, string serverName = "New TRP Server", string modType = "vanilla")
        {
            // v6.5.1: Create a subfolder with server name to keep structure clean
            var finalPath = Path.Combine(path, serverName);
            ServerPath = finalPath;

            LogReceived?.Invoke($"[SYSTEM] Verifying target directory: {finalPath}");
            if (!Directory.Exists(finalPath)) Directory.CreateDirectory(finalPath);

            var steamPath = Path.Combine(finalPath, "steam");
            var serverFilesPath = Path.Combine(finalPath, "rustds");

            LogReceived?.Invoke("[SYSTEM] Creating workspace structure (steam/ & rustds/)...");
            Directory.CreateDirectory(steamPath);
            Directory.CreateDirectory(serverFilesPath);

            try
            {
                // 1. Prepare SteamCMD in 'steam' folder
                LogReceived?.Invoke("[SYSTEM] Synchronizing SteamCMD maintenance core. Please wait...");
                await PrepareSteamCmdAsync(steamPath);

                // 2. Install Rust Core into 'rustds'
                LogReceived?.Invoke("[SYSTEM] Initiating Rust Dedicated Server deployment (AppID 258550)...");
                LogReceived?.Invoke("[SYSTEM] This may take several minutes depending on your internet connection.");

                var installArgs = $"+force_install_dir \"{serverFilesPath}\" +login anonymous +app_update 258550 validate +quit";
                await RunSteamCmdAsync(steamPath, installArgs);

                // Verification: Ensure the server core actually exists
                LogReceived?.Invoke("[SYSTEM] Validating core executable integrity...");
                string exePath = Path.Combine(serverFilesPath, "RustDedicated.exe");
                if (!File.Exists(exePath))
                {
                    throw new Exception("SteamCMD finished but RustDedicated.exe is missing. Deployment internal failure.");
                }
                LogReceived?.Invoke("[SUCCESS] Rust Core binaries verified successfully.");

                // 3. Install Mods if requested (into rustds)
                if (modType.ToLower() == "oxide")
                {
                    LogReceived?.Invoke("[SYSTEM] Mod selection: Oxide/uMod. Initiating injection...");
                    await InstallModAsync(serverFilesPath, "https://umod.org/games/rust/download", "Oxide");
                }
                else if (modType.ToLower() == "carbon")
                {
                    LogReceived?.Invoke("[SYSTEM] Mod selection: Carbon. Initiating injection...");
                    await InstallModAsync(serverFilesPath, "https://github.com/CarbonCommunity/Carbon/releases/download/production_build/Carbon.Windows.Release.zip", "Carbon");
                }

                // 4. Generate Professional Maintenance Scripts
                LogReceived?.Invoke("[SYSTEM] Generating environment configurations and launch scripts...");
                CreateDefaultConfigs(finalPath, serverFilesPath, serverName, modType);

                // 5. Deep Verification (v6.6.1)
                LogReceived?.Invoke("[SYSTEM] Initiating Post-Deployment Security & Integrity Check...");
                VerifyServerInstallation(finalPath, modType);

                LogReceived?.Invoke("[SUCCESS] FULL DEPLOYMENT COMPLETED! SERVER IS READY.");
                ProgressChanged?.Invoke("Installation Complete", 100);
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[CRITICAL ERROR] INSTALLATION FAILED: {ex.Message}");
                ProgressChanged?.Invoke("Error", 0);
                throw;
            }
        }

        public async Task PrepareSteamCmdAsync(string targetPath)
        {
            var cachePath = GetSteamCmdCachePath();
            var cachedExe = Path.Combine(cachePath, "steamcmd.exe");

            if (!File.Exists(cachedExe))
            {
                LogReceived?.Invoke("[SYSTEM] Downloading core maintenance tools...");
                var zipPath = Path.Combine(cachePath, "steamcmd.zip");
                var response = await _httpClient.GetAsync("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip");
                response.EnsureSuccessStatusCode();

                using (var fs = new FileStream(zipPath, FileMode.Create))
                {
                    await response.Content.CopyToAsync(fs);
                }

                if (new FileInfo(zipPath).Length < 1000) throw new Exception("Downloaded tool archive is empty or invalid.");

                ZipFile.ExtractToDirectory(zipPath, cachePath, System.Text.Encoding.UTF8, true);
                File.Delete(zipPath);
            }

            LogReceived?.Invoke("[SYSTEM] Synchronizing maintenance core...");
            foreach (var file in Directory.GetFiles(cachePath))
            {
                File.Copy(file, Path.Combine(targetPath, Path.GetFileName(file)), true);
            }
        }

        public async Task InstallModAsync(string serverPath, string url, string modName)
        {
            LogReceived?.Invoke($"[MOD] Injecting {modName} framework from {url}...");
            var zipPath = Path.Combine(Path.GetTempPath(), $"{modName.ToLower()}_{Guid.NewGuid()}.zip");

            try
            {
                // Set User-Agent to avoid blocks from GitHub
                if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
                    _httpClient.DefaultRequestHeaders.Add("User-Agent", "TRP-Server-Panel");

                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using (var fs = new FileStream(zipPath, FileMode.Create))
                {
                    await response.Content.CopyToAsync(fs);
                }

                var fileInfo = new FileInfo(zipPath);
                if (fileInfo.Length < 10000)
                    throw new Exception($"{modName} archive is too small ({fileInfo.Length} bytes). Download likely failed.");

                LogReceived?.Invoke($"[MOD] Extracting {modName} assets to server core...");

                // v7.2: Ensure target directory is not locked by previous instances
                EnsureFilesUnlocked(serverPath);

                using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read, System.Text.Encoding.UTF8))
                {
                    foreach (var entry in archive.Entries)
                    {
                        var destPath = Path.Combine(serverPath, entry.FullName);
                        var fullDestPath = Path.GetFullPath(destPath);
                        var fullServerPath = Path.GetFullPath(serverPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        if (!fullDestPath.StartsWith(fullServerPath, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException($"[ERROR] Safe extraction validation failed: entry '{entry.FullName}' attempts to escape server root.");
                        }

                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                        {
                            Directory.CreateDirectory(fullDestPath);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(fullDestPath)!);

                        // RETRY LOGIC (v7.3)
                        int retries = 3;
                        bool success = false;
                        while (retries > 0 && !success)
                        {
                            try
                            {
                                entry.ExtractToFile(destPath, true);
                                success = true;
                            }
                            catch (IOException)
                            {
                                retries--;
                                if (retries > 0)
                                {
                                    LogReceived?.Invoke($"[WARNING] File {entry.Name} is locked. Retrying in 1s...");
                                    Task.Delay(1000).Wait();
                                }
                                else throw;
                            }
                        }
                    }
                }
                LogReceived?.Invoke($"[SUCCESS] {modName} integrated successfully.");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[ERROR] {modName} injection failed: {ex.Message}");
                throw; // Rethrow to stop success message
            }
            finally
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
            }
        }

        public Task RunSteamCmdAsync(string steamCmdPath, string args)
        {
            return Task.Run(async () =>
            {
                // Register encoding provider for OEM 866 (Russian Console)
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                var encoding = System.Text.Encoding.GetEncoding(866);

                ProgressChanged?.Invoke("Checking for Updates...", 0);
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(steamCmdPath, "steamcmd.exe"),
                    Arguments = args,
                    WorkingDirectory = steamCmdPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = encoding,
                    StandardErrorEncoding = encoding,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };

                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        string msg = e.Data.Trim();
                        LogReceived?.Invoke($"[STEAMCMD] {msg}");

                        // Extract Progress: progress: 10.31 (593764678 / 5758121243)
                        var match = System.Text.RegularExpressions.Regex.Match(msg, @"progress:\s+(\d+\.\d+)");
                        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double progress))
                        {
                            ProgressChanged?.Invoke($"Downloading: {progress:F1}%", progress);
                        }
                        else if (msg.Contains("Success! App '258550' fully installed."))
                        {
                            ProgressChanged?.Invoke("Update Complete", 100);
                        }
                    }
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        LogReceived?.Invoke($"[STEAMCMD ERROR] {e.Data.Trim()}");
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync();
            });
        }

        private void CreateDefaultConfigs(string rootPath, string serverFilesPath, string serverName, string modType)
        {
            // 1. Rust Server.cfg
            var cfgPath = Path.Combine(serverFilesPath, "server", "rustserver", "cfg", "server.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);
            var serverCfg = $"server.hostname \"{serverName}\"\n" +
                            $"server.description \"TRP Professional Rust Server Deployment\"\n" +
                            "server.maxplayers 50\n" +
                            "server.worldsize 3000\n" +
                            "server.seed 123456\n" +
                            "server.saveinterval 300";
            File.WriteAllText(cfgPath, serverCfg);

            // 2. Run_DS.bat (Root)
            var runBatPath = Path.Combine(rootPath, "Run_DS.bat");
            var runBatContent = "@echo off\n" +
                               "cls\n" +
                               "echo [TRP] Starting Rust Dedicated Server Orchestrator...\n" +
                               "cd rustds\n" +
                               "RustDedicated.exe -batchmode -nographics " +
                               "+server.port 28015 " +
                               "+server.queryport 28016 " +
                               $"+server.hostname \"{serverName}\" " +
                               "+server.identity \"rustserver\" " +
                               "+server.level \"Procedural Map\" " +
                               "+server.worldsize 3000 +server.seed 123456 " +
                               "+rcon.port 28017 +rcon.password \"admin\" +rcon.web 1 " +
                               (modType.ToLower() == "oxide" || modType.ToLower() == "carbon" ? "+oxide.directory \"oxide\" " : "") +
                               "-logFile \"output.txt\"\n" +
                               "pause";
            File.WriteAllText(runBatPath, runBatContent);

            // 3. update_script.txt (Root)
            var updateScriptPath = Path.Combine(rootPath, "update_script.txt");
            var updateScriptContent = "@ShutdownOnFailedCommand 1\n" +
                                     "@NoPromptForPassword 1\n" +
                                     "login anonymous\n" +
                                     "force_install_dir ../rustds\n" +
                                     "app_update 258550 validate\n" +
                                     "quit";
            File.WriteAllText(updateScriptPath, updateScriptContent);

            // 4. update.bat (Root)
            var updateBatPath = Path.Combine(rootPath, "update.bat");
            var updateBatContent = "@echo off\n" +
                                  "echo [TRP] Starting Server Core Update...\n" +
                                  "cd steam\n" +
                                  "steamcmd.exe +runscript ../update_script.txt\n" +
                                  "pause";
            File.WriteAllText(updateBatPath, updateBatContent);
        }

        public void SaveServerSettings(string rootPath, ServerConfig config)
        {
            try
            {
                // 1. JSON Persistence (UI State)
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                string encryptedJson = SecurityHelper.Encrypt(json);
                string configPath = Path.Combine(rootPath, "trp_config.json");
                string tempPath = configPath + ".tmp";
                
                File.WriteAllText(tempPath, encryptedJson);
                if (File.Exists(configPath))
                {
                    File.Delete(configPath);
                }
                File.Move(tempPath, configPath);

                // 2. CFG Persistence (Server Logic)
                UpdateServerCfg(rootPath, config);
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[ERROR] Failed to save config: {ex.Message}");
            }
        }

        public ServerConfig LoadServerConfig(string rootPath)
        {
            try
            {
                string jsonPath = Path.Combine(rootPath, "trp_config.json");
                if (File.Exists(jsonPath))
                {
                    string encryptedJson = File.ReadAllText(jsonPath);
                    string json = SecurityHelper.Decrypt(encryptedJson);
                    return JsonSerializer.Deserialize<ServerConfig>(json) ?? new ServerConfig();
                }

                // If JSON doesn't exist, try to parse existing server.cfg (v10.1 Auto-Import)
                return ParseServerCfg(rootPath) ?? new ServerConfig();
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[ERROR] Failed to load config: {ex.Message}");
            }
            return new ServerConfig();
        }

        private void UpdateServerCfg(string rootPath, ServerConfig config)
        {
            string serverFilesPath = Path.Combine(rootPath, "rustds");
            if (!Directory.Exists(serverFilesPath)) serverFilesPath = rootPath;

            // v15.1: Strict identity folder discovery
            string identity = string.IsNullOrEmpty(config.Identity) ? "rustserver" : config.Identity;
            var cfgDir = Path.Combine(serverFilesPath, "server", identity, "cfg");
            var cfgPath = Path.Combine(cfgDir, "server.cfg");

            // v15.2: Legacy Cleanup (Delete ghost config in root)
            var legacyCfgPath = Path.Combine(serverFilesPath, "server", "cfg", "server.cfg");
            if (File.Exists(legacyCfgPath))
            {
                try
                {
                    LogReceived?.Invoke($"[SYSTEM] Migrating and purging legacy config: {legacyCfgPath}");
                    // Here we could parse and merge, but for now we just overwrite with panel settings and delete
                    File.Delete(legacyCfgPath);
                    var legacyDir = Path.GetDirectoryName(legacyCfgPath);
                    if (legacyDir != null && Directory.Exists(legacyDir) && !Directory.EnumerateFileSystemEntries(legacyDir).Any())
                        Directory.Delete(legacyDir);
                }
                catch { }
            }

            Directory.CreateDirectory(cfgDir);

            var sb = new StringBuilder();
            sb.AppendLine($"server.identity \"{identity}\"");
            sb.AppendLine($"server.hostname \"{config.Hostname}\"");
            sb.AppendLine($"server.description \"{config.Description}\"");
            sb.AppendLine($"server.url \"{config.Url}\"");
            sb.AppendLine($"server.headerimage \"{config.HeaderImage}\"");
            sb.AppendLine($"server.maxplayers {config.MaxPlayers}");
            sb.AppendLine($"server.worldsize {config.WorldSize}");
            sb.AppendLine($"server.seed {config.Seed}");
            sb.AppendLine($"server.port {config.Port}");
            sb.AppendLine($"server.queryport {config.QueryPort}");
            sb.AppendLine($"server.tickrate {config.Tickrate}");
            sb.AppendLine($"server.saveinterval {config.SaveInterval}");
            sb.AppendLine($"server.secure {(config.Secure ? "1" : "0")}");
            sb.AppendLine($"server.pvp {(config.PvpEnabled ? "1" : "0")}");
            sb.AppendLine($"server.stability {(config.Stability ? "1" : "0")}");
            sb.AppendLine($"server.radiation {(config.Radiation ? "1" : "0")}");
            sb.AppendLine($"antihack.enabled {(config.AntiHackLevel > 0 ? "1" : "0")}");
            sb.AppendLine($"chat.global {(config.GlobalChat ? "1" : "0")}");
            sb.AppendLine($"rcon.port {config.RconPort}");
            sb.AppendLine($"rcon.password \"{config.RconPassword}\"");
            sb.AppendLine($"rcon.web {(config.RconWeb ? "1" : "0")}");
            sb.AppendLine($"decay.upkeep {(config.Upkeep ? "true" : "false")}");
            sb.AppendLine($"decay.scale {config.DecayScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"craft.instant {(config.InstantCraft ? "true" : "false")}");
            sb.AppendLine($"relationshipmanager.maxteamSize {config.MaxTeamSize}");
            sb.AppendLine($"heli.lifespan {config.HeliLifespan.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"env.daylength {config.DayLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"env.nightlength {config.NightLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"server.maxconnectionsperip {config.MaxConnectionsPerIP}");
            if (!string.IsNullOrEmpty(config.LevelUrl))
                sb.AppendLine($"server.levelurl \"{config.LevelUrl}\"");

            File.WriteAllText(cfgPath, sb.ToString());
            LogReceived?.Invoke($"[SUCCESS] Server configuration synchronized at: {cfgPath}");
        }

        private ServerConfig? ParseServerCfg(string rootPath)
        {
            // HYBRID PATH DISCOVERY (v10.5)
            // Try standard 'rustds' subfolder first
            string configSubPath = Path.Combine("server", "rustserver", "cfg", "server.cfg");
            string fullCfgPath = Path.Combine(rootPath, "rustds", configSubPath);

            // If not found, try root level
            if (!File.Exists(fullCfgPath))
            {
                fullCfgPath = Path.Combine(rootPath, configSubPath);
            }

            // If still not found, try any 'server.cfg' in identity folders
            if (!File.Exists(fullCfgPath))
            {
                try
                {
                    var files = Directory.GetFiles(rootPath, "server.cfg", SearchOption.AllDirectories);
                    fullCfgPath = files.FirstOrDefault() ?? "";
                }
                catch { }
            }

            if (string.IsNullOrEmpty(fullCfgPath) || !File.Exists(fullCfgPath)) return null;

            var config = new ServerConfig();
            var lines = File.ReadAllLines(fullCfgPath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                string key = parts[0].Trim();
                string val = parts[1].Trim(' ', '\"');

                switch (key.ToLower())
                {
                    case "server.identity": config.Identity = val; break;
                    case "server.hostname": config.Hostname = val; break;
                    case "server.description": config.Description = val; break;
                    case "server.maxplayers": if (int.TryParse(val, out var mp)) config.MaxPlayers = mp; break;
                    case "server.worldsize": if (int.TryParse(val, out var ws)) config.WorldSize = ws; break;
                    case "server.seed": if (int.TryParse(val, out var sd)) config.Seed = sd; break;
                    case "server.port": if (int.TryParse(val, out var p)) config.Port = p; break;
                    case "server.queryport": if (int.TryParse(val, out var qp)) config.QueryPort = qp; break;
                    case "rcon.port": if (int.TryParse(val, out var rp)) config.RconPort = rp; break;
                    case "rcon.password": config.RconPassword = val; break;
                    case "rcon.web": config.RconWeb = (val == "1" || val.ToLower() == "true"); break;
                    case "server.tickrate": if (int.TryParse(val, out var tr)) config.Tickrate = tr; break;
                    case "server.saveinterval": if (int.TryParse(val, out var si)) config.SaveInterval = si; break;
                    case "server.url": config.Url = val; break;
                    case "server.headerimage": config.HeaderImage = val; break;
                    case "server.secure": config.Secure = (val == "1" || val.ToLower() == "true"); break;
                    case "server.pvp": config.PvpEnabled = (val == "1" || val.ToLower() == "true"); break;
                    case "server.stability": config.Stability = (val == "1" || val.ToLower() == "true"); break;
                    case "server.radiation": config.Radiation = (val == "1" || val.ToLower() == "true"); break;
                    case "antihack.enabled": config.AntiHackLevel = (val == "1" || val.ToLower() == "true") ? 2 : 0; break;
                    case "chat.global": config.GlobalChat = (val == "1" || val.ToLower() == "true"); break;
                    case "decay.upkeep": config.Upkeep = (val == "1" || val.ToLower() == "true"); break;
                    case "decay.scale": if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ds)) config.DecayScale = ds; break;
                    case "craft.instant": config.InstantCraft = (val == "1" || val.ToLower() == "true"); break;
                    case "relationshipmanager.maxteamsize": if (int.TryParse(val, out var ts)) config.MaxTeamSize = ts; break;
                    case "heli.lifespan": if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var hl)) config.HeliLifespan = hl; break;
                    case "env.daylength": if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dl)) config.DayLength = dl; break;
                    case "env.nightlength": if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var nl)) config.NightLength = nl; break;
                    case "server.maxconnectionsperip": if (int.TryParse(val, out var mc)) config.MaxConnectionsPerIP = mc; break;
                    case "server.levelurl": config.LevelUrl = val; break;
                }
            }
            return config;
        }

        public async Task StartServerAsync(string path, ServerConfig config)
        {
            if (IsRunning) return;

            string exePath = Path.Combine(path, "RustDedicated.exe");
            string serverFolderPath = path;

            if (!File.Exists(exePath))
            {
                serverFolderPath = Path.Combine(path, "rustds");
                exePath = Path.Combine(serverFolderPath, "RustDedicated.exe");
            }

            if (!File.Exists(exePath))
            {
                LogReceived?.Invoke($"[ERROR] Server core not found. Checked: {path} and {Path.Combine(path, "rustds")}");
                return;
            }

            // DYNAMIC BOOT ENGINE (v10.2)
            string identity = SanitizeArgument(string.IsNullOrEmpty(config.Identity) ? "rustserver" : config.Identity);
            string additionalArgs = SanitizeAdditionalArgs(config.AdditionalArgs);
            string serverIp = SanitizeArgument(config.ServerIP);
            string hostname = SanitizeArgument(config.Hostname);
            string mapLevel = SanitizeArgument(config.MapLevel);
            string rconPassword = SanitizeArgument(config.RconPassword);
            string levelUrl = SanitizeArgument(config.LevelUrl);

            // v15.3: Ensure Identity is FIRST to establish context
            var argBuilder = new StringBuilder();
            argBuilder.Append($"+server.identity \"{identity}\" ");
            if (!string.IsNullOrEmpty(additionalArgs))
                argBuilder.Append($"{additionalArgs} ");
            argBuilder.Append($"+server.ip {serverIp} ");
            argBuilder.Append($"+server.port {config.Port} ");
            argBuilder.Append($"+server.queryport {config.QueryPort} ");
            argBuilder.Append($"+server.hostname \"{hostname}\" ");
            argBuilder.Append($"+server.maxplayers {config.MaxPlayers} ");
            argBuilder.Append($"+server.level \"{mapLevel}\" ");
            argBuilder.Append($"+server.worldsize {config.WorldSize} ");
            argBuilder.Append($"+server.seed {config.Seed} ");
            argBuilder.Append($"+server.tickrate {config.Tickrate} ");
            argBuilder.Append($"+rcon.port {config.RconPort} ");
            argBuilder.Append($"+rcon.password \"{rconPassword}\" ");
            argBuilder.Append($"+rcon.web {(config.RconWeb ? "1" : "0")} ");
            argBuilder.Append($"+decay.upkeep {(config.Upkeep ? "true" : "false")} ");
            argBuilder.Append($"+decay.scale {config.DecayScale.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");
            argBuilder.Append($"+craft.instant {(config.InstantCraft ? "true" : "false")} ");
            argBuilder.Append($"+relationshipmanager.maxteamSize {config.MaxTeamSize} ");
            argBuilder.Append($"+heli.lifespan {config.HeliLifespan.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");
            argBuilder.Append($"+env.daylength {config.DayLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");
            argBuilder.Append($"+env.nightlength {config.NightLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");
            argBuilder.Append($"+server.maxconnectionsperip {config.MaxConnectionsPerIP} ");
            if (!string.IsNullOrEmpty(levelUrl))
                argBuilder.Append($"+server.levelurl \"{levelUrl}\" ");
            argBuilder.Append($"+oxide.directory \"oxide\" ");

            string args = argBuilder.ToString();

            CleanupProcessInterference(exePath);

            // v13.1: Expanded Port Validation (Port, QueryPort, RconPort)
            if (!IsPortAvailable(config.Port))
            {
                LogReceived?.Invoke($"[CRITICAL] Game Port {config.Port} is already in use! Initialization will fail.");
                return;
            }
            if (!IsPortAvailable(config.QueryPort))
            {
                LogReceived?.Invoke($"[CRITICAL] Query Port {config.QueryPort} is already in use! Initialization will fail.");
                return;
            }
            if (!IsPortAvailable(config.RconPort))
            {
                LogReceived?.Invoke($"[CRITICAL] RCON Port {config.RconPort} is already in use! Initialization will fail.");
                return;
            }

            // v13.2: Prevent Steamworks NullRef (steam_appid.txt)
            try
            {
                var appIdPath = Path.Combine(serverFolderPath, "steam_appid.txt");
                File.WriteAllText(appIdPath, "258550");
            }
            catch { /* Suppress if read-only */ }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = serverFolderPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _serverProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    // [HYBRID V8] Filter out OS Internal Ping errors that spam the console without affecting server health
                    if (e.Data.Contains("Ping: Error performing ICMP transmission") || e.Data.Contains("Possibly because of a timeout")) return;

                    // v10.7: Instant Online Detection
                    if (e.Data.Contains("Server startup complete"))
                    {
                        ProgressChanged?.Invoke("Online", 100);
                    }

                    // v13.3: Smart Error Recovery Detection
                    if (e.Data.Contains("InitGameServer failed"))
                    {
                        LogReceived?.Invoke("[CRITICAL ALERT] Steam Server Initialization FAILED! This usually means another program is using your ports (28015/28016). Close competing programs or change ports in Settings.");
                    }

                    LogReceived?.Invoke(e.Data);
                }
            };
            _serverProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    // Same filter for stderr
                    if (e.Data.Contains("Ping: Error performing ICMP transmission")) return;
                    LogReceived?.Invoke($"[SERVER ERROR] {e.Data}");
                }
            };
            _serverProcess.Exited += (s, e) =>
            {
                LogReceived?.Invoke("[SYSTEM] Server process has terminated.");
                _serverProcess = null;
            };

            LogReceived?.Invoke($"[SYSTEM] Launching RustDedicated.exe from {serverFolderPath}");
            ProgressChanged?.Invoke("Starting...", 0);
            _serverProcess.Start();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    if (_jobObject == null)
                    {
                        _jobObject = new JobObject();
                    }
                    _jobObject.AddProcess(_serverProcess);
                    LogReceived?.Invoke("[SYSTEM] Process bound to Job Object successfully (Auto-termination enabled).");
                }
                catch (Exception ex)
                {
                    LogReceived?.Invoke($"[WARNING] Failed to bind process to Job Object: {ex.Message}");
                }
            }

            _serverProcess.StandardInput.AutoFlush = true;
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            await Task.CompletedTask;
        }

        public async Task RestartServerAsync(string path, ServerConfig config)
        {
            LogReceived?.Invoke("[SYSTEM] Initiating server restart sequence...");
            await StopServerAsync();
            await Task.Delay(2000); // Wait for resources to free
            await StartServerAsync(path, config);
        }

        public async Task StopServerAsync()
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                LogReceived?.Invoke("[SYSTEM] Sending 'quit' command to server...");
                SendCommand("quit");

                // Wait for graceful shutdown or kill after timeout
                var stopTask = _serverProcess.WaitForExitAsync();
                var timeoutTask = Task.Delay(15000);

                if (await Task.WhenAny(stopTask, timeoutTask) == timeoutTask)
                {
                    LogReceived?.Invoke("[WARNING] Server did not exit gracefully. Terminating process...");
                    _serverProcess.Kill();
                }
            }
            _serverProcess = null;
            _jobObject?.Dispose();
            _jobObject = null;
        }

        public bool IsServerProcessRunning(string serverFolderPath)
        {
            try
            {
                var processes = Process.GetProcessesByName("RustDedicated");
                foreach (var p in processes)
                {
                    try
                    {
                        string fullPath = p.MainModule?.FileName ?? "";
                        if (string.IsNullOrEmpty(fullPath)) continue;
                        if (fullPath.StartsWith(serverFolderPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        public void TryReattach(string serverFolderPath)
        {
            if (IsRunning) return;

            try
            {
                var processes = Process.GetProcessesByName("RustDedicated");
                foreach (var p in processes)
                {
                    try
                    {
                        string fullPath = p.MainModule?.FileName ?? "";
                        if (string.IsNullOrEmpty(fullPath)) continue;

                        // Check if this process belongs to the specified path
                        if (fullPath.StartsWith(serverFolderPath, StringComparison.OrdinalIgnoreCase))
                        {
                            _serverProcess = p;
                            _serverProcess.EnableRaisingEvents = true;
                            // Re-bind events (Standard I/O might be lost for reattached processes if not redirected)
                            _serverProcess.Exited += (s, e) => { _serverProcess = null; };
                            LogReceived?.Invoke("[SYSTEM] Successfully re-attached to existing server process.");
                            break;
                        }
                    }
                    catch { /* Access denied or other process errors */ }
                }
            }
            catch { }
        }

        public void SendCommand(string command)
        {
            if (IsRunning && _serverProcess != null)
            {
                try
                {
                    _serverProcess.StandardInput.WriteLine(command);
                    _serverProcess.StandardInput.Flush();
                }
                catch (Exception ex)
                {
                    LogReceived?.Invoke($"[ERROR] Failed to send command: {ex.Message}");
                }
            }
            else
            {
                LogReceived?.Invoke("[WARNING] Cannot send command: Server is not running.");
            }
        }

        public (double Cpu, long Ram, long NetworkIO) GetProcessStats()
        {
            if (!IsRunning || _serverProcess == null) return (0, 0, 0);

            try
            {
                _serverProcess.Refresh();
                long ram = _serverProcess.WorkingSet64;

                // Calculate CPU Usage (v18.1)
                var now = DateTime.UtcNow;
                var totalProcessorTime = _serverProcess.TotalProcessorTime;

                if (_lastCpuCheckTime != DateTime.MinValue)
                {
                    var elapsed = (now - _lastCpuCheckTime).TotalMilliseconds;
                    var processorElapsed = (totalProcessorTime - _lastTotalProcessorTime).TotalMilliseconds;

                    // Divide by number of cores to get a 0-100% value relative to the whole system
                    // Or keep it 0-100% relative to a single core? Usually, UI expects 0-100% total process load.
                    _currentCpuUsage = (processorElapsed / (elapsed * Environment.ProcessorCount)) * 100.0;

                    // Clamp to 0-100
                    if (_currentCpuUsage < 0) _currentCpuUsage = 0;
                    if (_currentCpuUsage > 100) _currentCpuUsage = 100;
                }

                _lastCpuCheckTime = now;
                _lastTotalProcessorTime = totalProcessorTime;

                return (_currentCpuUsage, ram, 0);
            }
            catch { return (0, 0, 0); }
        }
        // Duplicates removed to fix CS0111

        private void VerifyServerInstallation(string rootPath, string modType)
        {
            var serverFilesPath = Path.Combine(rootPath, "rustds");
            var steamPath = Path.Combine(rootPath, "steam");

            // 1. Check Core Executables
            if (!File.Exists(Path.Combine(serverFilesPath, "RustDedicated.exe")))
                throw new Exception("Core Verification Failed: RustDedicated.exe missing.");

            if (!File.Exists(Path.Combine(steamPath, "steamcmd.exe")))
                throw new Exception("Core Verification Failed: steamcmd.exe missing.");

            // 2. Check Launch Scripts
            if (!File.Exists(Path.Combine(rootPath, "Run_DS.bat")))
                throw new Exception("Script Verification Failed: Run_DS.bat missing.");

            // 3. Check Mod Specific Files
            if (modType.ToLower() == "oxide")
            {
                if (!Directory.Exists(Path.Combine(serverFilesPath, "RustDedicated_Data", "Managed", "x64")))
                    LogReceived?.Invoke("[WARNING] Oxide structure looks unusual (x64 missing).");
            }
            else if (modType.ToLower() == "carbon")
            {
                if (!File.Exists(Path.Combine(serverFilesPath, "carbon.dll")) && !Directory.Exists(Path.Combine(serverFilesPath, "carbon")))
                    throw new Exception("Mod Verification Failed: Carbon files not found.");
            }

            LogReceived?.Invoke("[SUCCESS] Verification passed: All core files found and validated.");
        }

        public void CleanupProcessInterference(string exePath)
        {
            try
            {
                var absolutePath = Path.GetFullPath(exePath).ToLowerInvariant();
                foreach (var process in Process.GetProcessesByName("RustDedicated"))
                {
                    try
                    {
                        var processPath = process.MainModule?.FileName.ToLowerInvariant();
                        if (processPath == absolutePath)
                        {
                            LogReceived?.Invoke($"[SYSTEM] Detected interfering process ({process.Id}). Terminating...");
                            process.Kill();
                            process.WaitForExit(3000);
                        }
                    }
                    catch { /* Process might have already exited or access denied */ }
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[WARNING] Interference cleanup error: {ex.Message}");
            }
        }

        public bool IsPortAvailable(int port)
        {
            try
            {
                var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                var listeners = properties.GetActiveTcpListeners();
                foreach (var listener in listeners)
                {
                    if (listener.Port == port) return false;
                }
                return true;
            }
            catch { return true; }
        }
        private void EnsureFilesUnlocked(string serverPath)
        {
            try
            {
                var processes = Process.GetProcessesByName("RustDedicated");
                foreach (var p in processes)
                {
                    try
                    {
                        string processPath = p.MainModule?.FileName ?? string.Empty;
                        if (processPath.StartsWith(serverPath, StringComparison.OrdinalIgnoreCase))
                        {
                            LogReceived?.Invoke($"[WARNING] Detected locking process: PID {p.Id}. Forcing termination for injection...");
                            p.Kill();
                            p.WaitForExit(5000);
                        }
                    }
                    catch { /* Access denied or already exited */ }
                }
            }
            catch { /* Global process list access error */ }
        }
        private string GetServersPath()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TRPServerPanel");
            Directory.CreateDirectory(path);
            return Path.Combine(path, "servers.json");
        }

        private void BackupOldSystem()
        {
            try
            {
                var baseDirectoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "servers.json");
                var myDocumentsPath = GetServersPath();

                string sourcePath = File.Exists(baseDirectoryPath) ? baseDirectoryPath : (File.Exists(myDocumentsPath) ? myDocumentsPath : string.Empty);
                if (string.IsNullOrEmpty(sourcePath)) return;

                var backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TRPServerPanel", "Backups");
                Directory.CreateDirectory(backupDir);

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupPath = Path.Combine(backupDir, $"servers_backup_{timestamp}.json");

                File.Copy(sourcePath, backupPath, true);

                var backupFiles = Directory.GetFiles(backupDir, "servers_backup_*.json")
                                           .Select(f => new FileInfo(f))
                                           .OrderByDescending(f => f.CreationTime)
                                           .ToList();

                if (backupFiles.Count > 10)
                {
                    foreach (var oldFile in backupFiles.Skip(10))
                    {
                        oldFile.Delete();
                    }
                }
            }
            catch { }
        }

        public List<ServerModel> LoadServerList()
        {
            BackupOldSystem();
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "servers.json");
                if (!File.Exists(path))
                {
                    path = GetServersPath();
                }

                if (File.Exists(path))
                {
                    string rawContent = File.ReadAllText(path);
                    string json = rawContent;

                    // Decrypt if encrypted (does not start with JSON object or array character)
                    if (!string.IsNullOrWhiteSpace(rawContent) && !rawContent.Trim().StartsWith("[") && !rawContent.Trim().StartsWith("{"))
                    {
                        json = SecurityHelper.Decrypt(rawContent);
                    }

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                    };
                    return JsonSerializer.Deserialize<List<ServerModel>>(json, options) ?? new List<ServerModel>();
                }
            }
            catch { }
            return new List<ServerModel>();
        }

        public void SaveServerList(List<ServerModel> servers)
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "servers.json");
                var tempPath = path + ".tmp";
                string json = JsonSerializer.Serialize(servers);
                string encrypted = SecurityHelper.Encrypt(json);
                File.WriteAllText(tempPath, encrypted);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tempPath, path);
            }
            catch { }
        }


        private string SanitizeArgument(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string clean = value;
            char[] dangerous = new char[] { '|', '&', ';', '>', '<', '\r', '\n' };
            foreach (char c in dangerous)
            {
                clean = clean.Replace(c.ToString(), "");
            }
            clean = clean.Replace("\"", "\\\"");
            return clean;
        }

        private string SanitizeAdditionalArgs(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string clean = value;
            char[] dangerous = new char[] { '|', '&', ';', '>', '<', '\r', '\n' };
            foreach (char c in dangerous)
            {
                clean = clean.Replace(c.ToString(), "");
            }
            return clean;
        }
    }
}
