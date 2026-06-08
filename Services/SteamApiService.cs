using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TRPServerPanel.Services
{
    public class SteamPlayerSummary
    {
        public ulong SteamId { get; set; }
        public string PersonaName { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public string CountryCode { get; set; } = string.Empty;
    }

    public class SteamApiService
    {
        // TODO: Move this to global settings or UI config if needed. 
        public static string SteamApiKey { get; set; } = ""; 
        
        private readonly HttpClient _httpClient;
        
        // Cache directory inside Resources to be accessible via WebView2 VirtualHost
        private readonly string _avatarCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Cache", "Avatars");
        private readonly string _dataCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppData", "steam_data_cache.json");
        private Dictionary<ulong, CachedSteamData> _dataCache = new();

        public class CachedSteamData
        {
            public SteamPlayerSummary Summary { get; set; } = new();
            public DateTime CachedAt { get; set; }
        }

        public SteamApiService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            
            // Ensure directory exists
            try { 
                Directory.CreateDirectory(_avatarCacheDir); 
                Directory.CreateDirectory(Path.GetDirectoryName(_dataCachePath) ?? "");
                LoadDataCache();
            } catch { }
        }

        private void LoadDataCache()
        {
            try
            {
                if (File.Exists(_dataCachePath))
                {
                    var json = File.ReadAllText(_dataCachePath);
                    _dataCache = JsonSerializer.Deserialize<Dictionary<ulong, CachedSteamData>>(json) ?? new();
                }
            }
            catch { _dataCache = new(); }
        }

        private void SaveDataCache()
        {
            try
            {
                var json = JsonSerializer.Serialize(_dataCache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_dataCachePath, json);
            }
            catch { }
        }

        private async Task<string> EnsureAvatarCachedAsync(ulong steamId, string remoteUrl)
        {
            if (string.IsNullOrEmpty(remoteUrl)) return "";

            string localFile = Path.Combine(_avatarCacheDir, $"{steamId}.jpg");
            string localUrl = $"http://trp.app/Cache/Avatars/{steamId}.jpg";

            if (File.Exists(localFile))
            {
                if (new FileInfo(localFile).Length > 0)
                    return localUrl;
            }

            try
            {
                var imageBytes = await _httpClient.GetByteArrayAsync(remoteUrl);
                await File.WriteAllBytesAsync(localFile, imageBytes);
                return localUrl;
            }
            catch (Exception ex)
            {
                // Fallback to remote URL if caching fails
                AppLogService.Log($"Failed to cache avatar for {steamId}: {ex.Message}", AppLogLevel.DEBUG, "STEAM");
                return remoteUrl; 
            }
        }

        public async Task<Dictionary<ulong, SteamPlayerSummary>> GetPlayerSummariesAsync(IEnumerable<ulong> steamIds)
        {
            var results = new Dictionary<ulong, SteamPlayerSummary>();
            var ids = steamIds.Distinct().ToList();

            if (ids.Count == 0)
                return results;

            // 1. Check Cache First (Fresh for 24 hours)
            var now = DateTime.UtcNow;
            var missingIds = new List<ulong>();

            foreach (var id in ids)
            {
                if (_dataCache.TryGetValue(id, out var cached) && (now - cached.CachedAt).TotalHours < 24)
                {
                    results[id] = cached.Summary;
                }
                else
                {
                    missingIds.Add(id);
                }
            }

            if (missingIds.Count == 0) return results;

            // If API key is not set, use the XML profile fallback (No API Key required)
            if (string.IsNullOrEmpty(SteamApiKey))
            {
                AppLogService.Log($"Steam API Cache miss. Falling back to XML scraping for {missingIds.Count} players...", AppLogLevel.DEBUG, "STEAM");
                var fetched = await GetPlayerSummariesXmlFallbackAsync(missingIds);
                foreach (var pair in fetched)
                {
                    _dataCache[pair.Key] = new CachedSteamData { Summary = pair.Value, CachedAt = now };
                    results[pair.Key] = pair.Value;
                }
                SaveDataCache();
                return results;
            }

            // Steam API limits to 100 steam IDs per request
            int batchSize = 100;
            for (int i = 0; i < missingIds.Count; i += batchSize)
            {
                var batch = missingIds.Skip(i).Take(batchSize).ToList();
                string idsParam = string.Join(",", batch);
                string url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/?key={SteamApiKey}&steamids={idsParam}";

                try
                {
                    var response = await _httpClient.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(response);
                    
                    if (doc.RootElement.TryGetProperty("response", out var respElement) && 
                        respElement.TryGetProperty("players", out var playersElement))
                    {
                        foreach (var playerElement in playersElement.EnumerateArray())
                        {
                            if (playerElement.TryGetProperty("steamid", out var sidElement) && 
                                ulong.TryParse(sidElement.GetString(), out ulong sid))
                            {
                                var summary = new SteamPlayerSummary { SteamId = sid };
                                
                                if (playerElement.TryGetProperty("personaname", out var nameElement))
                                    summary.PersonaName = nameElement.GetString() ?? "";
                                    
                                if (playerElement.TryGetProperty("avatarfull", out var avatarElement))
                                {
                                    string remoteAvatar = avatarElement.GetString() ?? "";
                                    summary.AvatarUrl = await EnsureAvatarCachedAsync(sid, remoteAvatar);
                                }
                                    
                                if (playerElement.TryGetProperty("loccountrycode", out var countryElement))
                                    summary.CountryCode = countryElement.GetString() ?? "";

                                results[sid] = summary;
                                _dataCache[sid] = new CachedSteamData { Summary = summary, CachedAt = now };
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogService.Log($"Steam Web API Error: {ex.Message}", AppLogLevel.ERROR, "STEAM");
                }
            }

            SaveDataCache();
            return results;
        }

        private async Task<Dictionary<ulong, SteamPlayerSummary>> GetPlayerSummariesXmlFallbackAsync(List<ulong> ids)
        {
            var results = new Dictionary<ulong, SteamPlayerSummary>();
            using var semaphore = new SemaphoreSlim(15); // Max 15 concurrent requests to avoid rate limits
            
            var tasks = ids.Select(async sid => 
            {
                await semaphore.WaitAsync();
                try
                {
                    string url = $"https://steamcommunity.com/profiles/{sid}?xml=1";
                    string xml = await _httpClient.GetStringAsync(url);

                    var summary = new SteamPlayerSummary { SteamId = sid };

                    // Extract AvatarFull
                    var avatarMatch = Regex.Match(xml, @"<avatarFull><!\[CDATA\[(.*?)\]\]></avatarFull>");
                    if (avatarMatch.Success) 
                    {
                        string remoteAvatar = avatarMatch.Groups[1].Value;
                        summary.AvatarUrl = await EnsureAvatarCachedAsync(sid, remoteAvatar);
                    }

                    // Extract PersonaName (steamID tag in XML)
                    var nameMatch = Regex.Match(xml, @"<steamID><!\[CDATA\[(.*?)\]\]></steamID>");
                    if (nameMatch.Success) summary.PersonaName = nameMatch.Groups[1].Value;

                    // Extract Location/Country
                    var locMatch = Regex.Match(xml, @"<location><!\[CDATA\[(.*?)\]\]></location>");
                    if (locMatch.Success) summary.CountryCode = locMatch.Groups[1].Value; // Usually full country name, but works as fallback

                    lock (results)
                    {
                        results[sid] = summary;
                    }
                }
                catch
                {
                    // Ignore individual profile failures (e.g. private or deleted profiles)
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            return results;
        }
    }
}
