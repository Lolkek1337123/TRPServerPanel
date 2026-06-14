using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Linq;
using System.Text.RegularExpressions;
using TRPServerPanel.Models;

namespace TRPServerPanel.Services
{
    public class MarketplacePlugin
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Slug { get; set; } = ""; // Internal slug for uMod or RepoPath for GitHub
        public string Author { get; set; } = "uMod";
        public string Version { get; set; } = "1.0.0";
        public string Description { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string Source { get; set; } = "uMod"; // uMod, TRP, GitHub, Codefling
        public bool IsInstalled { get; set; } = false;
        public string LocalVersion { get; set; } = "";
        public bool HasUpdate => !string.IsNullOrEmpty(LocalVersion) && LocalVersion != Version;

        // v5.0: Security Audit Status
        public string SecurityStatus { get; set; } = "Unknown"; // Safe, Risky, Critical, Unknown
        public string SecurityReport { get; set; } = "";
    }

    public class InstalledPluginMetadata
    {
        public string FileName { get; set; } = "";
        public string RemoteSource { get; set; } = "";
        public string RemoteId { get; set; } = "";
        public string LastInstalledVersion { get; set; } = "";
    }

    public class LocalPlugin
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "Unknown";
        public string Author { get; set; } = "Unknown";
        public string FullPath { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
        public List<string> Dependencies { get; set; } = new();
        public List<string> MissingDependencies { get; set; } = new();
        public string Status { get; set; } = "Active";
        public string CompileError { get; set; } = "";
    }

    public class PluginFile
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Content { get; set; } = "";
    }

    public class UModManifestPlugin
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
        [JsonPropertyName("icon")]
        public string Icon { get; set; } = "";
        [JsonPropertyName("tags")]
        public string Tags { get; set; } = "";
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
        [JsonPropertyName("games")]
        public List<string> Games { get; set; } = new();
    }

    public class PluginService
    {
        private readonly HttpClient _httpClient;
        private const string UModApi = "https://umod.org/plugins/search.json?categories[]=rust&sort=relevance";

        private Dictionary<string, UModManifestPlugin>? _umodManifestCache = null;
        private DateTime _manifestLastDownloaded = DateTime.MinValue;

        private List<MarketplacePlugin> _trpTierPlugins = new List<MarketplacePlugin>();

        private Dictionary<string, InstalledPluginMetadata> _installedMetadata = new();
        private readonly string _metadataPath;

        public PluginService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            
            _metadataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppData", "installed_plugins.json");
            LoadMetadata();
        }

        private void LoadMetadata()
        {
            try
            {
                if (File.Exists(_metadataPath))
                {
                    var json = File.ReadAllText(_metadataPath);
                    _installedMetadata = JsonSerializer.Deserialize<Dictionary<string, InstalledPluginMetadata>>(json) ?? new();
                }
            }
            catch { _installedMetadata = new(); }
        }

        private void SaveMetadata()
        {
            try
            {
                string dir = Path.GetDirectoryName(_metadataPath) ?? "";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_installedMetadata, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_metadataPath, json);
            }
            catch { }
        }

        private string? ExtractSlugFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                var uri = new Uri(url);
                var segments = uri.Segments;
                if (segments.Length > 0)
                {
                    return segments[^1].TrimEnd('/');
                }
            }
            catch { }
            return null;
        }

        private async Task EnsureUModManifestLoadedAsync()
        {
            if (_umodManifestCache != null && (DateTime.Now - _manifestLastDownloaded).TotalHours < 12)
            {
                return;
            }

            var appDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppData");
            if (!Directory.Exists(appDataDir)) Directory.CreateDirectory(appDataDir);
            var manifestPath = Path.Combine(appDataDir, "umod_manifest.json");

            bool fileExists = File.Exists(manifestPath);
            bool isFresh = fileExists && (DateTime.Now - File.GetLastWriteTime(manifestPath)).TotalHours < 24;

            if (isFresh)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(manifestPath);
                    _umodManifestCache = JsonSerializer.Deserialize<Dictionary<string, UModManifestPlugin>>(json);
                    if (_umodManifestCache != null && _umodManifestCache.Count > 0)
                    {
                        _manifestLastDownloaded = File.GetLastWriteTime(manifestPath);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLogService.Log($"[PLUGINS] Error reading cached uMod manifest: {ex.Message}", AppLogLevel.WARN, "SYSTEM");
                }
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://assets.umod.org/plugins/manifest.json");
                request.Headers.Add("User-Agent", App.UserAgent);
                
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, UModManifestPlugin>>(json);
                    if (parsed != null && parsed.Count > 0)
                    {
                        _umodManifestCache = parsed;
                        _manifestLastDownloaded = DateTime.Now;
                        await File.WriteAllTextAsync(manifestPath, json);
                        AppLogService.Log($"[PLUGINS] uMod manifest loaded and cached successfully. Count: {parsed.Count}", AppLogLevel.INFO, "SYSTEM");
                        return;
                    }
                }
                else
                {
                    AppLogService.Log($"[PLUGINS] Failed to fetch uMod manifest: {response.StatusCode}", AppLogLevel.WARN, "SYSTEM");
                }
            }
            catch (Exception ex)
            {
                AppLogService.Log($"[PLUGINS] Error fetching uMod manifest: {ex.Message}", AppLogLevel.WARN, "SYSTEM");
            }

            if (_umodManifestCache == null && fileExists)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(manifestPath);
                    _umodManifestCache = JsonSerializer.Deserialize<Dictionary<string, UModManifestPlugin>>(json);
                    if (_umodManifestCache != null)
                    {
                        _manifestLastDownloaded = File.GetLastWriteTime(manifestPath);
                    }
                }
                catch { }
            }
        }

        public async Task<List<MarketplacePlugin>> SearchPluginsAsync(string query, string source = "All")
        {
            var results = new List<MarketplacePlugin>();

            // 1. TRP Source
            if (source == "All" || source == "TRP")
            {
                results.AddRange(_trpTierPlugins.Where(p => string.IsNullOrEmpty(query) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            }

            // 2. uMod Source
            if (source == "All" || source == "uMod")
            {
                try
                {
                    await EnsureUModManifestLoadedAsync();
                    if (_umodManifestCache != null)
                    {
                        var umodResults = new List<(MarketplacePlugin Plugin, int Score)>();
                        foreach (var kvp in _umodManifestCache)
                        {
                            var key = kvp.Key;
                            var val = kvp.Value;
                            
                            bool isCompatible = val.Games != null && val.Games.Any(g => g.Equals("rust", StringComparison.OrdinalIgnoreCase) || g.Equals("universal", StringComparison.OrdinalIgnoreCase));
                            if (!isCompatible) continue;

                            var name = val.Title;
                            if (string.IsNullOrEmpty(name)) name = key;

                            var slug = ExtractSlugFromUrl(val.Url) ?? key.ToLower();

                            int score = 0;
                            if (string.IsNullOrEmpty(query))
                            {
                                score = 1;
                            }
                            else
                            {
                                if (name.Equals(query, StringComparison.OrdinalIgnoreCase) || key.Equals(query, StringComparison.OrdinalIgnoreCase))
                                {
                                    score = 100;
                                }
                                else if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase) || key.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                                {
                                    score = 80;
                                    if (name.Contains(" ", StringComparison.Ordinal)) score -= 5; // penalize multi-word a bit to favor exact single word starts
                                }
                                else if (name.Contains(query, StringComparison.OrdinalIgnoreCase) || key.Contains(query, StringComparison.OrdinalIgnoreCase))
                                {
                                    score = 50;
                                }
                                else if (val.Description != null && val.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                                {
                                    score = 20;
                                }
                                else if (val.Tags != null && val.Tags.Contains(query, StringComparison.OrdinalIgnoreCase))
                                {
                                    score = 10;
                                }
                            }

                            if (score > 0)
                            {
                                umodResults.Add((new MarketplacePlugin
                                {
                                    Name = name,
                                    Slug = slug,
                                    Author = "uMod",
                                    Version = "1.0.0",
                                    Description = val.Description ?? "",
                                    IconUrl = val.Icon ?? "",
                                    DownloadUrl = $"https://umod.org/plugins/{slug}/download",
                                    Source = "uMod"
                                }, score));
                            }
                        }

                        results.AddRange(umodResults.OrderByDescending(r => r.Score).ThenBy(r => r.Plugin.Name).Select(r => r.Plugin));
                    }
                }
                catch (Exception ex)
                {
                    AppLogService.Log($"uMod Search Error: {ex.Message}\n{ex.StackTrace}", AppLogLevel.ERROR, "PLUGINS");
                }
            }

            // 3. GitHub Source (Searching for Rust Plugins)
            if (source == "All" || source == "GitHub")
            {
                try
                {
                    var ghUrl = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(query)}+topic:rust-plugin";
                    var response = await _httpClient.GetStringAsync(ghUrl);
                    var doc = JsonDocument.Parse(response);

                    if (doc.RootElement.TryGetProperty("items", out var items))
                    {
                        foreach (var repo in items.EnumerateArray())
                        {
                            var owner = repo.GetProperty("owner").GetProperty("login").GetString();
                            var name = repo.GetProperty("name").GetString() ?? "Unknown";
                            var fullPath = $"{owner ?? "Unknown"}/{name}";
                            
                            results.Add(new MarketplacePlugin
                            {
                                Name = name,
                                Slug = fullPath,
                                Author = owner ?? "GitHub",
                                Version = "Latest", // Need individual check for tags
                                Description = repo.GetProperty("description").GetString() ?? "",
                                IconUrl = repo.GetProperty("owner").GetProperty("avatar_url").GetString() ?? "",
                                DownloadUrl = $"https://api.github.com/repos/{fullPath}/contents", // Simplified for now
                                Source = "GitHub"
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogService.Log($"GitHub Search Error: {ex.Message}\n{ex.StackTrace}", AppLogLevel.ERROR, "PLUGINS");
                }
            }

            // --- Post-Process: Mark Installed ---
            foreach (var res in results)
            {
                var meta = _installedMetadata.Values.FirstOrDefault(m => m.RemoteId == res.Slug && m.RemoteSource == res.Source);
                if (meta != null)
                {
                    res.IsInstalled = true;
                    res.LocalVersion = meta.LastInstalledVersion;
                }
            }

            return results;
        }

        private string GetUModCookiesHeader()
        {
            try
            {
                var cookiePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppData", "umod_cookies.json");
                if (File.Exists(cookiePath))
                {
                    var json = File.ReadAllText(cookiePath);
                    var cookies = JsonSerializer.Deserialize<List<Views.SavedCookie>>(json);
                    if (cookies != null && cookies.Count > 0)
                    {
                        return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogService.Log($"Error reading uMod cookies: {ex.Message}", AppLogLevel.WARN, "PLUGINS");
            }
            return "";
        }
        public async Task<string> GetPluginDetailsHtmlAsync(string slug, string source)
        {
            if (source == "GitHub")
            {
                try
                {
                    var url = $"https://api.github.com/repos/{slug}/readme";
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("User-Agent", App.UserAgent);
                    
                    var response = await _httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        var jsonStr = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(jsonStr);
                        if (doc.RootElement.TryGetProperty("content", out var contentProp))
                        {
                            var base64 = contentProp.GetString() ?? "";
                            base64 = base64.Replace("\n", "").Replace("\r", "");
                            var bytes = Convert.FromBase64String(base64);
                            return System.Text.Encoding.UTF8.GetString(bytes);
                        }
                    }
                }
                catch (Exception ex)
                {
                    return $"Ошибка загрузки README с GitHub: {ex.Message}";
                }
                return "README не найден.";
            }
            else // uMod
            {
                try
                {
                    var url = $"https://umod.org/plugins/{slug}";
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    var cookies = GetUModCookiesHeader();
                    if (!string.IsNullOrEmpty(cookies))
                    {
                        request.Headers.Add("Cookie", cookies);
                    }
                    request.Headers.Add("User-Agent", App.UserAgent);

                    var response = await _httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        var html = await response.Content.ReadAsStringAsync();
                        
                        var docHtml = "";
                        var docMatch = Regex.Match(html, @"<(article|section|div)[^>]*?class=""[^""]*?documentation[^""]*?""[^>]*?>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        if (docMatch.Success)
                        {
                            docHtml = docMatch.Groups[2].Value;
                        }
                        
                        if (string.IsNullOrEmpty(docHtml))
                        {
                            var docIdMatch = Regex.Match(html, @"<([a-z0-9]+)[^>]*?id=""documentation""[^>]*?>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                            if (docIdMatch.Success)
                            {
                                docHtml = docIdMatch.Groups[2].Value;
                            }
                        }

                        if (string.IsNullOrEmpty(docHtml))
                        {
                            var fallbackMatch = Regex.Match(html, @"<section[^>]*?class=""[^""]*?section[^""]*?""[^>]*?>(.*?)</section>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                            if (fallbackMatch.Success)
                            {
                                docHtml = fallbackMatch.Groups[1].Value;
                            }
                        }

                        if (!string.IsNullOrEmpty(docHtml))
                        {
                            docHtml = docHtml.Replace("href=\"/", "href=\"https://umod.org/");
                            docHtml = docHtml.Replace("src=\"/", "src=\"https://umod.org/");
                            docHtml = docHtml.Replace("href='/", "href='https://umod.org/");
                            docHtml = docHtml.Replace("src='/", "src='https://umod.org/");
                            
                            docHtml = Regex.Replace(docHtml, @"<img\s+", "<img referrerpolicy=\"no-referrer\" ", RegexOptions.IgnoreCase);
                            
                            return docHtml;
                        }
                    }
                    else
                    {
                        return $"Не удалось загрузить страницу uMod: {response.StatusCode}. Попробуйте авторизоваться.";
                    }
                }
                catch (Exception ex)
                {
                    return $"Ошибка загрузки документации: {ex.Message}";
                }
                return "Не удалось загрузить документацию.";
            }
        }

        public async Task<string> DownloadPluginContentAsync(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (url.Contains("umod.org", StringComparison.OrdinalIgnoreCase))
            {
                var cookies = GetUModCookiesHeader();
                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                }
            }
            request.Headers.Add("User-Agent", App.UserAgent);
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        public async Task<string> GetMarketplacePluginSourceAsync(MarketplacePlugin plugin)
        {
            if (string.IsNullOrEmpty(plugin.DownloadUrl)) return "";
            return await DownloadPluginContentAsync(plugin.DownloadUrl);
        }

        private string GetActualServerPath(string serverPath)
        {
            if (string.IsNullOrEmpty(serverPath)) return serverPath;
            string rustds = Path.Combine(serverPath, "rustds");
            return Directory.Exists(rustds) ? rustds : serverPath;
        }

        public async Task<bool> InstallPluginFromContentAsync(string serverPath, string pluginName, string content, MarketplacePlugin plugin, bool useCarbon = false)
        {
            try
            {
                var actualPath = GetActualServerPath(serverPath);
                var targetFolder = useCarbon ? "carbon" : "oxide";
                var pluginsDir = Path.Combine(actualPath, targetFolder, "plugins");
                
                if (!Directory.Exists(pluginsDir)) Directory.CreateDirectory(pluginsDir);

                var fileName = $"{pluginName}.cs";
                var filePath = Path.Combine(pluginsDir, fileName);
                
                // Create backup if exists
                if (File.Exists(filePath))
                {
                    var backupPath = filePath + ".old";
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    File.Move(filePath, backupPath);
                }

                await File.WriteAllTextAsync(filePath, content, new System.Text.UTF8Encoding(false));
                
                // v5.0: Detect Dependencies
                var deps = DetectDependencies(content);
                if (deps.Any())
                {
                    AppLogService.Log($"[PLUGINS] Detected {deps.Count} dependencies for {pluginName}: {string.Join(", ", deps)}", AppLogLevel.WARN, "SYSTEM");
                    // We can also store this in metadata
                }

                // Update metadata
                _installedMetadata[fileName] = new InstalledPluginMetadata
                {
                    FileName = fileName,
                    RemoteSource = plugin.Source,
                    RemoteId = plugin.Slug,
                    LastInstalledVersion = plugin.Version
                };
                SaveMetadata();
                
                return true;
            }
            catch (Exception ex)
            {
                AppLogService.Log($"Plugin Install Error: {ex.Message}", AppLogLevel.ERROR, "PLUGINS");
                return false; 
            }
        }

        public async Task<bool> InstallPluginAsync(string serverPath, MarketplacePlugin plugin, bool useCarbon = false)
        {
            try
            {
                var content = await DownloadPluginContentAsync(plugin.DownloadUrl);
                return await InstallPluginFromContentAsync(serverPath, plugin.Name, content, plugin, useCarbon);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Plugin Direct Install Error: {ex.Message}");
                return false; 
            }
        }

        public async Task<List<MarketplacePlugin>> CheckForUpdatesAsync(string serverPath)
        {
            var updateResults = new List<MarketplacePlugin>();
            
            // For now, let's focus on uMod updates as they have the best API
            var umodPlugins = _installedMetadata.Values.Where(m => m.RemoteSource == "uMod").ToList();
            if (!umodPlugins.Any()) return updateResults;

            try
            {
                // We can query multiple plugins at once if we had a bulk API, 
                // but uMod's search with slugs works well.
                foreach (var meta in umodPlugins)
                {
                    var searchUrl = $"{UModApi}&query={Uri.EscapeDataString(meta.RemoteId)}";
                    var response = await _httpClient.GetStringAsync(searchUrl);
                    var doc = JsonDocument.Parse(response);

                    if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                    {
                        var latest = data[0];
                        var latestVer = latest.GetProperty("latest_release_version").GetString() ?? "1.0.0";
                        
                        if (latestVer != meta.LastInstalledVersion)
                        {
                            updateResults.Add(new MarketplacePlugin
                            {
                                Name = Path.GetFileNameWithoutExtension(meta.FileName),
                                Slug = meta.RemoteId,
                                Source = "uMod",
                                Version = latestVer,
                                LocalVersion = meta.LastInstalledVersion
                            });
                        }
                    }
                }
            }
            catch { }

            return updateResults;
        }

        // --- Local Management ---

        private string NormalizePluginName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            return name.Replace(" ", "").Replace("_", "").Replace(".", "").Replace("-", "").ToLowerInvariant();
        }

        private string[] SafeReadAllLines(string path)
        {
            try
            {
                if (!File.Exists(path)) return Array.Empty<string>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    var lines = new List<string>();
                    while (!sr.EndOfStream)
                    {
                        var line = sr.ReadLine();
                        if (line != null) lines.Add(line);
                    }
                    return lines.ToArray();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SafeReadAllLines failed for {path}: {ex.Message}");
                try { return File.ReadAllLines(path); } catch { return Array.Empty<string>(); }
            }
        }

        private Dictionary<string, (string Status, string Error)> ParseCompilerLogs(string actualPath, string framework)
        {
            var result = new Dictionary<string, (string Status, string Error)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (framework.Equals("CARBON", StringComparison.OrdinalIgnoreCase))
                {
                    string logFile = Path.Combine(actualPath, "carbon", "logs", "Carbon.Core.log");
                    if (File.Exists(logFile))
                    {
                        var lines = SafeReadAllLines(logFile);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            var line = lines[i];
                            if (line.Contains("[ERRO] Failed compiling '"))
                            {
                                var match = Regex.Match(line, @"Failed compiling '([^']+)'");
                                if (match.Success)
                                {
                                    string pluginFile = match.Groups[1].Value;
                                    string pluginName = Path.GetFileNameWithoutExtension(pluginFile);
                                    
                                    var errors = new List<string>();
                                    int j = i + 1;
                                    while (j < lines.Length && !lines[j].Contains("Failed compiling '") && (lines[j].Contains("[ERRO]") || lines[j].Trim().StartsWith("(") || lines[j].Trim().StartsWith("1.") || lines[j].Trim().StartsWith("2.") || lines[j].Trim().StartsWith("3.")))
                                    {
                                        string errLine = lines[j];
                                        int bracketIdx = errLine.IndexOf(']');
                                        if (bracketIdx != -1 && bracketIdx + 1 < errLine.Length)
                                        {
                                            string content = errLine.Substring(bracketIdx + 1).Trim();
                                            if (content.StartsWith("[ERRO]")) content = content.Substring(6).Trim();
                                            errors.Add(content);
                                        }
                                        else
                                        {
                                            errors.Add(errLine.Trim());
                                        }
                                        j++;
                                    }
                                    
                                    result[NormalizePluginName(pluginName)] = ("Error", string.Join("\n", errors));
                                    i = j - 1;
                                }
                            }
                            else if (line.Contains("[INFO] Loaded plugin "))
                            {
                                var match = Regex.Match(line, @"Loaded plugin ([a-zA-Z0-9_]+) v");
                                if (match.Success)
                                {
                                    string pluginName = match.Groups[1].Value;
                                    result[NormalizePluginName(pluginName)] = ("Active", "");
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Oxide Compiler & Runtime Logs Support
                    string logsDir = Path.Combine(actualPath, "oxide", "logs");
                    if (Directory.Exists(logsDir))
                    {
                        // 1. Parse compiler logs for compilation errors
                        var compilerLogs = Directory.GetFiles(logsDir, "compiler_*.txt")
                            .Concat(Directory.GetFiles(logsDir, "oxide.compiler_*.log"))
                            .Distinct()
                            .ToList();
                        var latestCompilerLog = compilerLogs.OrderByDescending(f => f).FirstOrDefault();
                        if (latestCompilerLog != null)
                        {
                            try
                            {
                                var lines = SafeReadAllLines(latestCompilerLog);
                                for (int i = 0; i < lines.Length; i++)
                                {
                                    var line = lines[i];
                                    if (line.Contains("[Error]"))
                                    {
                                        var match = Regex.Match(line, @"\[Error\]\s+([a-zA-Z0-9_]+)\s+compiler\s+error:");
                                        if (!match.Success)
                                        {
                                            match = Regex.Match(line, @"\[Error\]\s+([^:]+):");
                                        }

                                        if (match.Success)
                                        {
                                            string pluginName = match.Groups[1].Value.Trim();
                                            var errors = new List<string> { line.Substring(line.IndexOf("[Error]") + 7).Trim() };
                                            int j = i + 1;
                                            while (j < lines.Length && !lines[j].Contains("[Error]") && !lines[j].Contains("[Info]") && !string.IsNullOrWhiteSpace(lines[j]))
                                            {
                                                errors.Add(lines[j].Trim());
                                                j++;
                                            }
                                            result[NormalizePluginName(pluginName)] = ("Error", string.Join("\n", errors));
                                            i = j - 1;
                                        }
                                    }
                                    else if (line.Contains("Failed to compile"))
                                    {
                                        var match = Regex.Match(line, @"Failed to compile\s+([a-zA-Z0-9_\-\s]+)\.cs\s*-\s*(.*)", RegexOptions.IgnoreCase);
                                        if (match.Success)
                                        {
                                            string pluginName = match.Groups[1].Value.Trim();
                                            string errorMsg = match.Groups[2].Value.Trim();
                                            result[NormalizePluginName(pluginName)] = ("Error", errorMsg);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to parse compiler log file: {ex.Message}");
                            }
                        }

                        // 2. Parse main oxide runtime logs for load success and runtime init errors
                        var runtimeLogs = Directory.GetFiles(logsDir, "oxide_*.txt").ToList();
                        var latestRuntimeLog = runtimeLogs.OrderByDescending(f => f).FirstOrDefault();
                        if (latestRuntimeLog != null)
                        {
                            try
                            {
                                var lines = SafeReadAllLines(latestRuntimeLog);
                                for (int i = 0; i < lines.Length; i++)
                                {
                                    var line = lines[i];
                                    if (line.Contains("Failed to initialize plugin"))
                                    {
                                        var match = Regex.Match(line, @"Failed to initialize plugin\s+'([^'\s]+)(?:\s+v[^']+)?'\s*\((.*)\)", RegexOptions.IgnoreCase);
                                        if (match.Success)
                                        {
                                            string pluginName = match.Groups[1].Value.Trim();
                                            string errorMsg = match.Groups[2].Value.Trim();
                                            
                                            var errors = new List<string> { errorMsg };
                                            int j = i + 1;
                                            while (j < lines.Length && !Regex.IsMatch(lines[j], @"^\d{2}:\d{2}") && !string.IsNullOrWhiteSpace(lines[j]))
                                            {
                                                errors.Add(lines[j].Trim());
                                                j++;
                                            }
                                            result[NormalizePluginName(pluginName)] = ("Error", string.Join("\n", errors));
                                            i = j - 1;
                                        }
                                    }
                                    else if (line.Contains("Loaded plugin"))
                                    {
                                        var match = Regex.Match(line, @"Loaded plugin\s+([a-zA-Z0-9_\-\s]+)(?:\s+v\d+(\.\d+)*)?", RegexOptions.IgnoreCase);
                                        if (match.Success)
                                        {
                                            string pluginName = match.Groups[1].Value.Trim();
                                            string normKey = NormalizePluginName(pluginName);
                                            if (!result.ContainsKey(normKey))
                                            {
                                                result[normKey] = ("Active", "");
                                            }
                                        }
                                    }
                                    else if (line.Contains("Unloaded plugin"))
                                    {
                                        var match = Regex.Match(line, @"Unloaded plugin\s+([a-zA-Z0-9_\-\s]+)", RegexOptions.IgnoreCase);
                                        if (match.Success)
                                        {
                                            string pluginName = match.Groups[1].Value.Trim();
                                            result[NormalizePluginName(pluginName)] = ("Offline", "Unloaded by server.");
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to parse runtime log file: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse compiler logs: {ex.Message}");
            }
            return result;
        }

        public List<LocalPlugin> GetLocalPlugins(string serverPath, string framework)
        {
            var plugins = new List<LocalPlugin>();
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugin_debug.log");
            
            try
            {
                File.AppendAllText(logPath, $"\n[{DateTime.Now}] SCAN START: Path='{serverPath}', Framework='{framework}'\n");

                if (string.IsNullOrEmpty(serverPath)) {
                    File.AppendAllText(logPath, $"[{DateTime.Now}] SCAN ABORT: serverPath is null or empty\n");
                    return plugins;
                }

                var actualPath = GetActualServerPath(serverPath);
                File.AppendAllText(logPath, $"[{DateTime.Now}] ACTUAL PATH: {actualPath}\n");

                string relPath = framework.ToUpper() == "CARBON" ? "carbon/plugins" : "oxide/plugins";
                string fullPath = Path.GetFullPath(Path.Combine(actualPath, relPath));
                File.AppendAllText(logPath, $"[{DateTime.Now}] INITIAL TARGET: {fullPath}\n");

                // Robust check: if the primary folder doesn't exist, try auto-detection
                if (!Directory.Exists(fullPath))
                {
                    File.AppendAllText(logPath, $"[{DateTime.Now}] FOLDER NOT FOUND: {fullPath}. Swapping to auto-detect...\n");
                    
                    string carbonPath = Path.GetFullPath(Path.Combine(actualPath, "carbon", "plugins"));
                    string oxidePath = Path.GetFullPath(Path.Combine(actualPath, "oxide", "plugins"));

                    if (Directory.Exists(carbonPath)) { 
                        fullPath = carbonPath; 
                        framework = "CARBON";
                        File.AppendAllText(logPath, $"[{DateTime.Now}] AUTO-DETECTED: CARBON found at {carbonPath}\n");
                    }
                    else if (Directory.Exists(oxidePath)) { 
                        fullPath = oxidePath; 
                        framework = "OXIDE";
                        File.AppendAllText(logPath, $"[{DateTime.Now}] AUTO-DETECTED: OXIDE found at {oxidePath}\n");
                    }
                    else { 
                        File.AppendAllText(logPath, $"[{DateTime.Now}] SCAN FAILED: No carbon/plugins or oxide/plugins found in {actualPath}\n");
                        return plugins; 
                    }
                }

                File.AppendAllText(logPath, $"[{DateTime.Now}] SCANNING FILES IN: {fullPath}\n");
                var files = Directory.GetFiles(fullPath, "*.cs*", SearchOption.TopDirectoryOnly);
                File.AppendAllText(logPath, $"[{DateTime.Now}] FILES FOUND: {files.Length}\n");

                var compilerStates = ParseCompilerLogs(actualPath, framework);

                foreach (var file in files)
                {
                    try {
                        var info = new FileInfo(file);
                        string fileName = Path.GetFileName(file);
                        string pluginName = Path.GetFileNameWithoutExtension(file).Replace(".cs", "").Replace(".disabled", "").Replace(".off", "");
                        
                        var plugin = new LocalPlugin
                        {
                            Name = pluginName,
                            FullPath = file,
                            IsEnabled = !fileName.EndsWith(".disabled") && !fileName.EndsWith(".off")
                        };

                        string normalizedName = NormalizePluginName(pluginName);

                        if (!plugin.IsEnabled)
                        {
                            plugin.Status = "Offline";
                        }
                        else if (compilerStates.TryGetValue(normalizedName, out var state))
                        {
                            plugin.Status = state.Status;
                            plugin.CompileError = state.Error;
                        }
                        else
                        {
                            plugin.Status = "Active";
                        }

                        // Simple Regex to extract [Info("Name", "Author", "Version")]
                        try {
                            var lines = File.ReadLines(file).Take(30).ToList();
                            string content = string.Join(" ", lines);
                            var match = Regex.Match(content, @"\[Info\s*\(\s*""[^""]*""\s*,\s*""([^""]*)""\s*,\s*""([^""]*)""", RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                plugin.Author = match.Groups[1].Value;
                                plugin.Version = match.Groups[2].Value;
                            }
                        } catch { }

                        // Smart Dependency Resolver
                        plugin.Dependencies = GetPluginDependencies(file);
                        
                        // Check missing
                        foreach (var dep in plugin.Dependencies)
                        {
                            if (!files.Any(f => Path.GetFileNameWithoutExtension(f).Equals(dep, StringComparison.OrdinalIgnoreCase)))
                            {
                                plugin.MissingDependencies.Add(dep);
                            }
                        }

                        plugins.Add(plugin);
                        File.AppendAllText(logPath, $"[{DateTime.Now}] ADDED: {plugin.Name} (Enabled: {plugin.IsEnabled}, Status: {plugin.Status})\n");
                    } catch (Exception ex) { File.AppendAllText(logPath, $"[{DateTime.Now}] ENTRY ERROR: {ex.Message}\n"); }
                }

                File.AppendAllText(logPath, $"[{DateTime.Now}] SCAN COMPLETE: Total {plugins.Count} plugins.\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, $"[{DateTime.Now}] CRITICAL SCAN ERROR: {ex.Message}\n");
                AppLogService.Log($"Error searching plugins: {ex.Message}", AppLogLevel.ERROR, "PLUGINS");
            }

            return plugins;
        }

        public List<PluginFile> GetPluginDataFiles(string serverPath, string pluginName, string framework)
        {
            var dataFiles = new List<PluginFile>();
            var actualPath = GetActualServerPath(serverPath);
            string relPath = framework.ToUpper() == "CARBON" ? "carbon/data" : "oxide/data";
            string fullPath = Path.Combine(actualPath, relPath);

            if (!Directory.Exists(fullPath)) return dataFiles;

            var files = Directory.GetFiles(fullPath, pluginName + "*.json");
            foreach (var file in files)
            {
                dataFiles.Add(new PluginFile { Name = Path.GetFileName(file), FullPath = file });
            }
            return dataFiles;
        }

        public List<string> GetRelatedPluginFiles(string pluginFullPath)
        {
            var related = new List<string>();
            try
            {
                string pluginName = Path.GetFileNameWithoutExtension(pluginFullPath);
                string? pluginsDir = Path.GetDirectoryName(pluginFullPath);
                if (string.IsNullOrEmpty(pluginsDir)) return related;

                string? frameworkDir = Path.GetDirectoryName(pluginsDir);
                if (string.IsNullOrEmpty(frameworkDir)) return related;

                // Check Config
                string configDir = Path.Combine(frameworkDir, "config");
                if (Directory.Exists(configDir))
                {
                    var files = Directory.GetFiles(configDir, pluginName + ".json");
                    related.AddRange(files.Select(f => "Config: " + Path.GetFileName(f)));
                }

                // Check Data
                string dataDir = Path.Combine(frameworkDir, "data");
                if (Directory.Exists(dataDir))
                {
                    var files = Directory.GetFiles(dataDir, pluginName + "*.json");
                    related.AddRange(files.Select(f => "Data: " + Path.GetFileName(f)));
                }
            }
            catch { }
            return related;
        }

        public List<string> GetRelatedPluginFilePaths(string pluginFullPath)
        {
            var paths = new List<string>();
            try
            {
                string pluginName = Path.GetFileNameWithoutExtension(pluginFullPath);
                string? pluginsDir = Path.GetDirectoryName(pluginFullPath);
                if (string.IsNullOrEmpty(pluginsDir)) return paths;

                string? frameworkDir = Path.GetDirectoryName(pluginsDir);
                if (string.IsNullOrEmpty(frameworkDir)) return paths;

                // Check Config
                string configDir = Path.Combine(frameworkDir, "config");
                if (Directory.Exists(configDir))
                {
                    paths.AddRange(Directory.GetFiles(configDir, pluginName + ".json"));
                }

                // Check Data
                string dataDir = Path.Combine(frameworkDir, "data");
                if (Directory.Exists(dataDir))
                {
                    paths.AddRange(Directory.GetFiles(dataDir, pluginName + "*.json"));
                }
            }
            catch { }
            return paths;
        }



        public async Task<string> ReadFileAsync(string path)
        {
            if (!File.Exists(path)) return "";
            return await File.ReadAllTextAsync(path);
        }

        public async Task SaveFileAsync(string path, string content)
        {
            await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(false));
        }

        public void DeleteFile(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public void TogglePluginState(string path, bool enable)
        {
            if (!File.Exists(path)) return;
            
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileName(path);
            
            if (enable && (name.EndsWith(".disabled") || name.EndsWith(".off")))
            {
                string newName = name.Replace(".disabled", "").Replace(".off", "");
                File.Move(path, Path.Combine(dir, newName));
            }
            else if (!enable && !name.EndsWith(".disabled") && !name.EndsWith(".off"))
            {
                File.Move(path, path + ".disabled");
            }
        }
        public List<string> GetPluginDependencies(string pluginFullPath)
        {
            var dependencies = new List<string>();
            try
            {
                if (!File.Exists(pluginFullPath)) return dependencies;

                // We only need the first 100 lines for dependency declarations
                var lines = File.ReadLines(pluginFullPath).Take(100).ToList();
                var content = string.Join("\n", lines);

                // 1. Oxide/Carbon "Requires" comments: // Requires: PluginName
                var requiresMatches = Regex.Matches(content, @"//\s*Requires:\s*([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
                foreach (Match match in requiresMatches)
                {
                    if (match.Groups.Count > 1) dependencies.Add(match.Groups[1].Value);
                }

                // 2. [PluginReference] attributes
                // [PluginReference] Plugin ImageLibrary;
                // [PluginReference] Carbon.Plugins.Plugin MyDep;
                var refMatches = Regex.Matches(content, @"\[PluginReference\]\s*(?:[a-zA-Z0-9\.]+\s+)?([a-zA-Z0-9]+)\s+[a-zA-Z0-9]+", RegexOptions.IgnoreCase);
                foreach (Match match in refMatches)
                {
                    if (match.Groups.Count > 1) 
                    {
                        var depName = match.Groups[1].Value;
                        if (!dependencies.Contains(depName, StringComparer.OrdinalIgnoreCase))
                            dependencies.Add(depName);
                    }
                }
            }
            catch { }
            return dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        public List<string> CheckMissingDependencies(string code, ServerModel server)
        {
            var detected = DetectDependencies(code);
            if (server == null || string.IsNullOrEmpty(server.Path)) return detected;

            var installed = GetLocalPlugins(server.Path, server.Framework).Select(p => p.Name).ToList();
            return detected.Where(d => !installed.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        private List<string> DetectDependencies(string code)
        {
            var deps = new List<string>();
            try
            {
                // Oxide style: // Requires: PluginName
                var reqMatches = Regex.Matches(code, @"//\s*Requires:\s*([a-zA-Z0-9]+)");
                foreach (Match m in reqMatches) if (m.Groups.Count > 1) deps.Add(m.Groups[1].Value);

                // C# attribute style: [PluginReference] private Plugin PluginName;
                var attrMatches = Regex.Matches(code, @"\[PluginReference\]\s*(?:private|protected|public)?\s*(?:Plugin|RustPlugin)\s+([a-zA-Z0-9]+)");
                foreach (Match m in attrMatches) if (m.Groups.Count > 1) deps.Add(m.Groups[1].Value);
            }
            catch { }
            return deps.Distinct().ToList();
        }
    }
}
