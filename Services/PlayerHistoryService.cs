using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Timers;

namespace TRPServerPanel.Services
{
    public class PlayerExtraData
    {
        public ulong SteamID { get; set; }
        public string Username { get; set; } = string.Empty;
        public string IP { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string CountryCode { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public double PlaytimeSeconds { get; set; }
        public DateTime LastSeen { get; set; } = DateTime.Now;
        public int TotalKills { get; set; } = 0;
        public int TotalDeaths { get; set; } = 0;
    }

    public class PlayerHistoryService : IDisposable
    {
        private readonly string _historyPath;
        private readonly Dictionary<ulong, PlayerExtraData> _cache = new();
        private readonly object _cacheLock = new();
        private readonly System.Timers.Timer _saveTimer;
        private bool _isDirty;
        private bool _disposed;

        public PlayerHistoryService()
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TRPServerPanel");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
            _historyPath = Path.Combine(appData, "player_history_v2.json");
            LoadHistory();

            // Set up non-blocking periodic saving
            _saveTimer = new System.Timers.Timer(5000); // Check and save every 5 seconds if dirty
            _saveTimer.Elapsed += (s, e) => SaveHistory(force: false);
            _saveTimer.AutoReset = true;
            _saveTimer.Enabled = true;
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(_historyPath))
                {
                    var json = File.ReadAllText(_historyPath);
                    lock (_cacheLock)
                    {
                        var data = JsonSerializer.Deserialize<Dictionary<ulong, PlayerExtraData>>(json);
                        if (data != null)
                        {
                            _cache.Clear();
                            foreach (var kvp in data)
                            {
                                _cache[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HISTORY ERROR] Failed to load history: {ex.Message}");
            }
        }

        public void UpdateExtendedData(ulong steamId, string username, string ip, string country, string countryCode, string avatarUrl)
        {
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(steamId, out var data))
                {
                    data = new PlayerExtraData { SteamID = steamId };
                    _cache[steamId] = data;
                }

                data.Username = username;
                if (!string.IsNullOrEmpty(ip)) data.IP = ip;
                if (!string.IsNullOrEmpty(country)) data.Country = country;
                if (!string.IsNullOrEmpty(countryCode)) data.CountryCode = countryCode;
                if (!string.IsNullOrEmpty(avatarUrl)) data.AvatarUrl = avatarUrl;
                data.LastSeen = DateTime.Now;
                
                _isDirty = true;
            }
        }

        public void UpdateIP(ulong steamId, string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "N/A") return;

            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(steamId, out var data))
                {
                    data = new PlayerExtraData { SteamID = steamId };
                    _cache[steamId] = data;
                }
                
                if (data.IP != ip)
                {
                    data.IP = ip;
                    _isDirty = true;
                }
            }
        }

        public void AddPlaytime(ulong steamId, double seconds)
        {
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(steamId, out var data))
                {
                    data = new PlayerExtraData { SteamID = steamId };
                    _cache[steamId] = data;
                }
                data.PlaytimeSeconds += seconds;
                _isDirty = true;
            }
        }

        public void UpdateStats(ulong steamId, int kills, int deaths)
        {
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(steamId, out var data))
                {
                    data = new PlayerExtraData { SteamID = steamId };
                    _cache[steamId] = data;
                }

                data.TotalKills = kills;
                data.TotalDeaths = deaths;
                _isDirty = true;
            }
        }

        public PlayerExtraData GetExtraData(ulong steamId)
        {
            lock (_cacheLock)
            {
                return _cache.TryGetValue(steamId, out var data) ? data : new PlayerExtraData { SteamID = steamId };
            }
        }

        public void SaveHistory(bool force = false)
        {
            Dictionary<ulong, PlayerExtraData> cacheCopy;
            
            lock (_cacheLock)
            {
                if (!_isDirty && !force) return;
                
                // Clone dictionary for thread-safe serialization
                cacheCopy = new Dictionary<ulong, PlayerExtraData>(_cache);
                _isDirty = false;
            }

            try
            {
                var json = JsonSerializer.Serialize(cacheCopy, new JsonSerializerOptions { WriteIndented = true });
                string tempPath = _historyPath + ".tmp";
                
                // Atomic save using temporary file
                File.WriteAllText(tempPath, json);
                if (File.Exists(_historyPath))
                {
                    File.Delete(_historyPath);
                }
                File.Move(tempPath, _historyPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HISTORY ERROR] Failed to save history: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _saveTimer.Stop();
                _saveTimer.Dispose();
            }
            catch { }

            // Ensure all final changes are persisted synchronously during dispose/shutdown
            SaveHistory(force: true);
        }
    }
}
