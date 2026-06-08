#pragma warning disable CS8600, CS8601, CS8602, CS8604
using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;
using TRPServerPanel.Models;
using System.Linq;

namespace TRPServerPanel.Services
{
    public class RustDatabaseService
    {
        public class PlayerDeepStats
        {
            public int Kills { get; set; }
            public int Deaths { get; set; }
            public double SurvivalTime { get; set; }
            public int Blueprints { get; set; }
        }

        private static readonly Dictionary<string, (DateTime LastWriteTime, string SnapshotPath)> _snapshotCache = new();
        private static readonly SemaphoreSlim _snapshotSemaphore = new(1, 1);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _queryCache = new();

        public RustDatabaseService()
        {
            try
            {
                string cacheDir = Path.Combine(Path.GetTempPath(), "TRP_DatabaseCache");
                if (Directory.Exists(cacheDir))
                {
                    foreach (var file in Directory.GetFiles(cacheDir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        private static SqliteCommand CreateCommandWithTimeout(SqliteConnection connection)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandTimeout = 15;
            return cmd;
        }

        private async Task<T> ExecuteWithSnapshotAsync<T>(string dbPath, Func<SqliteConnection, Task<T>> action, T defaultValue = default)
        {
            string snapshotPath = null;
            try
            {
                await _snapshotSemaphore.WaitAsync();
                try
                {
                    var fileInfo = new FileInfo(dbPath);
                    if (!fileInfo.Exists) return defaultValue;

                    DateTime currentWriteTime = fileInfo.LastWriteTimeUtc;

                    if (_snapshotCache.TryGetValue(dbPath, out var cached) && cached.LastWriteTime == currentWriteTime && File.Exists(cached.SnapshotPath))
                    {
                        snapshotPath = cached.SnapshotPath;
                    }
                    else
                    {
                        string cacheDir = Path.Combine(Path.GetTempPath(), "TRP_DatabaseCache");
                        Directory.CreateDirectory(cacheDir);
                        
                        snapshotPath = Path.Combine(cacheDir, $"snap_{Guid.NewGuid():N}.db");
                        
                        // Bulletproof copy using FileShare.ReadWrite to bypass SQLite exclusive locks from the active Rust server
                        using (var source = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var dest = new FileStream(snapshotPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await source.CopyToAsync(dest);
                        }

                        // Copy WAL file if it exists
                        string walPath = dbPath + "-wal";
                        if (File.Exists(walPath))
                        {
                            try
                            {
                                using (var source = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                using (var dest = new FileStream(snapshotPath + "-wal", FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    await source.CopyToAsync(dest);
                                }
                            }
                            catch { }
                        }

                        // Copy SHM file if it exists
                        string shmPath = dbPath + "-shm";
                        if (File.Exists(shmPath))
                        {
                            try
                            {
                                using (var source = new FileStream(shmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                using (var dest = new FileStream(snapshotPath + "-shm", FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    await source.CopyToAsync(dest);
                                }
                            }
                            catch { }
                        }

                        // Cleanup old snapshot
                        if (cached.SnapshotPath != null)
                        {
                            try { if (File.Exists(cached.SnapshotPath)) File.Delete(cached.SnapshotPath); } catch { }
                            try { if (File.Exists(cached.SnapshotPath + "-wal")) File.Delete(cached.SnapshotPath + "-wal"); } catch { }
                            try { if (File.Exists(cached.SnapshotPath + "-shm")) File.Delete(cached.SnapshotPath + "-shm"); } catch { }
                        }

                        _snapshotCache[dbPath] = (currentWriteTime, snapshotPath);
                    }
                }
                finally
                {
                    _snapshotSemaphore.Release();
                }

                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = snapshotPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString();

                using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync();
                    
                    // Enable WAL (Write-Ahead Logging) for better performance and concurrency
                    using (var walCmd = CreateCommandWithTimeout(connection))
                    {
                        walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=10000;";
                        await walCmd.ExecuteNonQueryAsync();
                    }

                    return await action(connection);
                }
            }
            catch (SqliteException sqliteEx) when (sqliteEx.SqliteErrorCode == 11 || sqliteEx.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase))
            {
                _snapshotSemaphore.Wait();
                try
                {
                    if (_snapshotCache.TryGetValue(dbPath, out var cached))
                    {
                        _snapshotCache.Remove(dbPath);
                        if (File.Exists(cached.SnapshotPath))
                        {
                            try { File.Delete(cached.SnapshotPath); } catch { }
                        }
                    }
                }
                finally
                {
                    _snapshotSemaphore.Release();
                }

                LogError($"CRITICAL: SQLite Database Corruption detected on: {dbPath}. Invalidated cache.", sqliteEx);
                
                if (snapshotPath != null && File.Exists(snapshotPath))
                {
                    try { File.Delete(snapshotPath); } catch { }
                }

                return defaultValue;
            }
            catch (Exception ex)
            {
                string extraInfo = "";
                try
                {
                    if (File.Exists(dbPath))
                    {
                        var fi = new FileInfo(dbPath);
                        extraInfo = $" (Exists: True, Size: {fi.Length} bytes, LastWrite: {fi.LastWriteTimeUtc:u})";
                    }
                    else
                    {
                        extraInfo = " (Exists: False)";
                    }
                }
                catch { }

                LogError($"ExecuteWithSnapshotAsync failed on DB: {dbPath}{extraInfo}", ex);
                if (snapshotPath != null && File.Exists(snapshotPath))
                {
                    bool isCached = false;
                    _snapshotSemaphore.Wait();
                    try
                    {
                        isCached = _snapshotCache.Values.Any(c => c.SnapshotPath == snapshotPath);
                    }
                    finally
                    {
                        _snapshotSemaphore.Release();
                    }
                    
                    if (!isCached)
                    {
                        try { File.Delete(snapshotPath); } catch { }
                    }
                }
                return defaultValue;
            }
        }

        public async Task<Dictionary<ulong, PlayerDeepStats>> GetDeepAnalyticsAsync(string serverPath)
        {
            string deathsDb = GetLatestDbFile(serverPath, "player.deaths.*.db");
            string bpDb = GetLatestDbFile(serverPath, "player.blueprints.*.db");

            DateTime deathsWrite = !string.IsNullOrEmpty(deathsDb) && File.Exists(deathsDb) ? File.GetLastWriteTimeUtc(deathsDb) : DateTime.MinValue;
            DateTime bpWrite = !string.IsNullOrEmpty(bpDb) && File.Exists(bpDb) ? File.GetLastWriteTimeUtc(bpDb) : DateTime.MinValue;

            string cacheKey = $"analytics_{serverPath}_{deathsWrite.Ticks}_{bpWrite.Ticks}";

            if (_queryCache.TryGetValue(cacheKey, out var cached))
            {
                return (Dictionary<ulong, PlayerDeepStats>)cached;
            }

            var results = new Dictionary<ulong, PlayerDeepStats>();
            
            // 1. Process Deaths and Survival Time
            if (!string.IsNullOrEmpty(deathsDb))
            {
                await ExecuteWithSnapshotAsync<bool>(deathsDb, async (conn) => 
                {
                    var cmd = CreateCommandWithTimeout(conn);
                    bool isNewFormat = await TableExistsAsync(conn, "data");
                    if (isNewFormat)
                    {
                        cmd.CommandText = "SELECT userid, COUNT(*), SUM(died - born) FROM data GROUP BY userid";
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                if (ulong.TryParse(reader.GetString(0), out ulong id))
                                {
                                    results[id] = new PlayerDeepStats { 
                                        Deaths = reader.GetInt32(1),
                                        SurvivalTime = reader.IsDBNull(2) ? 0 : reader.GetDouble(2)
                                    };
                                }
                            }
                        }
                    }
                    else if (await TableExistsAsync(conn, "deaths"))
                    {
                        cmd.CommandText = "SELECT victim_id, COUNT(*) FROM deaths GROUP BY victim_id";
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                if (ulong.TryParse(reader.GetString(0), out ulong id))
                                    results[id] = new PlayerDeepStats { Deaths = reader.GetInt32(1) };
                            }
                        }
                    }
                    return true;
                });
            }

            // 2. Process Blueprints
            if (!string.IsNullOrEmpty(bpDb))
            {
                await ExecuteWithSnapshotAsync<bool>(bpDb, async (conn) => 
                {
                    var cmd = CreateCommandWithTimeout(conn);
                    if (await TableExistsAsync(conn, "data"))
                    {
                        cmd.CommandText = "SELECT userid, info FROM data";
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                if (ulong.TryParse(reader.GetString(0), out ulong id))
                                {
                                    if (!results.ContainsKey(id)) results[id] = new PlayerDeepStats();
                                    
                                    byte[] blob = null;
                                    if (!reader.IsDBNull(1))
                                    {
                                        blob = (byte[])reader.GetValue(1);
                                    }
                                    
                                    var bpIds = ParseBlueprintBlob(blob);
                                    results[id].Blueprints = bpIds.Count;
                                }
                            }
                        }
                    }
                    return true;
                });
            }

            _queryCache[cacheKey] = results;
            return results;
        }

        private async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName)
        {
            var cmd = CreateCommandWithTimeout(conn);
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name";
            cmd.Parameters.AddWithValue("@name", tableName);
            return await cmd.ExecuteScalarAsync() != null;
        }


        private string GetLatestDbFile(string serverPath, string pattern)
        {
            try
            {
                pattern = pattern.Replace(".*.db", "*.db");
                // v3.6: Dynamic identity detection
                var rootDirs = new[] { "server", "rustds/server" };
                foreach (var rd in rootDirs)
                {
                    string rootPath = Path.Combine(serverPath, rd.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    if (Directory.Exists(rootPath))
                    {
                        // 1. Try 'rustserver' first (Legacy/Default)
                        string defaultPath = Path.Combine(rootPath, "rustserver");
                        if (Directory.Exists(defaultPath))
                        {
                            var latest = Directory.GetFiles(defaultPath, pattern).OrderByDescending(f => f).FirstOrDefault();
                            if (!string.IsNullOrEmpty(latest)) return latest;
                        }

                        // 2. Scan all subdirectories (Custom identities)
                        foreach (var subDir in Directory.GetDirectories(rootPath))
                        {
                            var latest = Directory.GetFiles(subDir, pattern).OrderByDescending(f => f).FirstOrDefault();
                            if (!string.IsNullOrEmpty(latest)) 
                            {
                                AppLogService.Log($"Found latest DB in custom identity {Path.GetFileName(subDir)}: {latest}", AppLogLevel.DEBUG, "DATABASE");
                                return latest;
                            }
                        }
                    }
                }

                // Fallback: search anywhere in serverPath if not found in standard paths
                AppLogService.Log($"DB {pattern} not found in standard paths. Falling back to recursive search in {serverPath}", AppLogLevel.DEBUG, "DATABASE");
                var files = Directory.GetFiles(serverPath, pattern, SearchOption.AllDirectories);
                var result = files.OrderByDescending(f => f).FirstOrDefault() ?? string.Empty;
                if (!string.IsNullOrEmpty(result)) AppLogService.Log($"Found DB via recursive search: {result}", AppLogLevel.DEBUG, "DATABASE");
                return result;
            }
            catch (Exception ex)
            {
                LogError($"Error searching for DB file with pattern {pattern}", ex);
                return string.Empty;
            }
        }

        private List<string> GetAllDbFiles(string serverPath, string pattern)
        {
            pattern = pattern.Replace(".*.db", "*.db");
            var files = new List<string>();
            try
            {
                var rootDirs = new[] { "server", "rustds/server" };
                foreach (var rd in rootDirs)
                {
                    string rootPath = Path.Combine(serverPath, rd.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    if (Directory.Exists(rootPath))
                    {
                        // 1. Try 'rustserver' first
                        string defaultPath = Path.Combine(rootPath, "rustserver");
                        if (Directory.Exists(defaultPath))
                        {
                            var matches = Directory.GetFiles(defaultPath, pattern);
                            files.AddRange(matches);
                        }

                        // 2. Scan all subdirectories (Custom identities)
                        foreach (var subDir in Directory.GetDirectories(rootPath))
                        {
                            var matches = Directory.GetFiles(subDir, pattern);
                            files.AddRange(matches);
                        }
                    }
                }

                // Fallback: search recursively
                if (files.Count == 0)
                {
                    var matches = Directory.GetFiles(serverPath, pattern, SearchOption.AllDirectories);
                    files.AddRange(matches);
                }
            }
            catch (Exception ex)
            {
                LogError($"Error searching for DB files with pattern {pattern}", ex);
            }

            return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f).ToList();
        }

        public async Task<List<PlayerIdentity>> GetPlayersAsync(string serverPath)
        {
            var dbPaths = GetAllDbFiles(serverPath, "player.identities.*.db");
            if (dbPaths.Count == 0) return new List<PlayerIdentity>();

            var cacheParts = dbPaths.Select(p => $"{p}_{File.GetLastWriteTimeUtc(p).Ticks}");
            string cacheKey = $"players_{string.Join("|", cacheParts)}";

            if (_queryCache.TryGetValue(cacheKey, out var cached))
            {
                return (List<PlayerIdentity>)cached;
            }

            var playersMap = new Dictionary<ulong, PlayerIdentity>();

            // Process databases in reverse order (oldest first, so newer databases overwrite older ones)
            foreach (var dbPath in dbPaths.AsEnumerable().Reverse())
            {
                if (!File.Exists(dbPath)) continue;

                var dbPlayers = new List<PlayerIdentity>();
                var result = await ExecuteWithSnapshotAsync<List<PlayerIdentity>>(dbPath, async (connection) =>
                {
                    var playersList = new List<PlayerIdentity>();
                    if (!await TableExistsAsync(connection, "data"))
                    {
                        return playersList;
                    }

                    var command = CreateCommandWithTimeout(connection);
                    
                    // Check if lastSeen column exists
                    bool hasLastSeen = false;
                    using (var schemaCmd = CreateCommandWithTimeout(connection))
                    {
                        schemaCmd.CommandText = "PRAGMA table_info(data)";
                        using (var reader = await schemaCmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                if (reader.GetString(1).Equals("lastSeen", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasLastSeen = true;
                                    break;
                                }
                            }
                        }
                    }

                    command.CommandText = hasLastSeen 
                        ? "SELECT userid, username, lastSeen FROM data ORDER BY lastSeen DESC"
                        : "SELECT userid, username FROM data";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            string sidStr = reader.GetValue(0)?.ToString() ?? "0";
                            if (!ulong.TryParse(sidStr, out ulong sid)) sid = 0;

                            var identity = new PlayerIdentity
                            {
                                SteamID = sid,
                                Username = reader.IsDBNull(1) ? "Unknown" : reader.GetString(1),
                                LastSeen = hasLastSeen ? DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).DateTime : DateTime.Now
                            };
                            playersList.Add(identity);
                        }
                    }
                    return playersList;
                }, dbPlayers);

                foreach (var p in result)
                {
                    if (p.SteamID == 0) continue;
                    playersMap[p.SteamID] = p;
                }
            }

            var combinedPlayers = playersMap.Values.OrderByDescending(p => p.LastSeen).ToList();
            _queryCache[cacheKey] = combinedPlayers;
            return combinedPlayers;
        }

        public async Task<List<TeamInfo>> GetTeamsAsync(string serverPath)
        {
            var dbPath = GetLatestDbFile(serverPath, "relationship.*.db");
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return new List<TeamInfo>();

            DateTime lastWrite = File.GetLastWriteTimeUtc(dbPath);
            string cacheKey = $"teams_{dbPath}_{lastWrite.Ticks}";

            if (_queryCache.TryGetValue(cacheKey, out var cached))
            {
                return (List<TeamInfo>)cached;
            }

            var teams = new List<TeamInfo>();
            var result = await ExecuteWithSnapshotAsync<List<TeamInfo>>(dbPath, async (connection) =>
            {
                var command = CreateCommandWithTimeout(connection);
                
                bool hasTeamsTable = false;
                using (var checkCmd = CreateCommandWithTimeout(connection))
                {
                    checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='teams'";
                    hasTeamsTable = await checkCmd.ExecuteScalarAsync() != null;
                }

                if (!hasTeamsTable) 
                {
                    AppLogService.Log("No 'teams' table found in relationship database. Skipping team sync.", AppLogLevel.DEBUG, "DATABASE");
                    return teams;
                }

                command.CommandText = "SELECT teamid, leaderid FROM teams";

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        teams.Add(new TeamInfo {
                            TeamID = (ulong)reader.GetInt64(0),
                            LeaderID = (ulong)reader.GetInt64(1)
                        });
                    }
                }

                foreach(var team in teams)
                {
                    var mCommand = CreateCommandWithTimeout(connection);
                    mCommand.CommandText = "SELECT userid FROM members WHERE teamid = @tid";
                    mCommand.Parameters.AddWithValue("@tid", (long)team.TeamID);
                    
                    using (var mReader = await mCommand.ExecuteReaderAsync())
                    {
                        while (await mReader.ReadAsync())
                        {
                            team.Members.Add((ulong)mReader.GetInt64(0));
                        }
                    }
                }

                return teams;
            }, teams);

            _queryCache[cacheKey] = result;
            return result;
        }


        public async Task<List<PlayerBlueprint>> GetBlueprintsAsync(string serverPath)
        {
            var dbPath = GetLatestDbFile(serverPath, "player.blueprints.*.db");
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return new List<PlayerBlueprint>();

            DateTime lastWrite = File.GetLastWriteTimeUtc(dbPath);
            string cacheKey = $"blueprints_{dbPath}_{lastWrite.Ticks}";

            if (_queryCache.TryGetValue(cacheKey, out var cached))
            {
                return (List<PlayerBlueprint>)cached;
            }

            var blueprints = new List<PlayerBlueprint>();
            var result = await ExecuteWithSnapshotAsync<List<PlayerBlueprint>>(dbPath, async (connection) =>
            {
                if (!await TableExistsAsync(connection, "data"))
                {
                    return blueprints;
                }

                var command = CreateCommandWithTimeout(connection);
                command.CommandText = "SELECT userid, info FROM data";

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        string userIdStr = reader.GetString(0);
                        if (!ulong.TryParse(userIdStr, out ulong steamId)) continue;

                        byte[] blob = null;
                        if (!reader.IsDBNull(1))
                        {
                            blob = (byte[])reader.GetValue(1);
                        }

                        var bpIds = ParseBlueprintBlob(blob);
                        foreach (var bpId in bpIds)
                        {
                            blueprints.Add(new PlayerBlueprint
                            {
                                SteamID = steamId,
                                BlueprintID = bpId
                            });
                        }
                    }
                }
                return blueprints;
            }, blueprints);

            _queryCache[cacheKey] = result;
            return result;
        }

        private static List<int> ParseBlueprintBlob(byte[] blob)
        {
            var ids = new List<int>();
            if (blob == null || blob.Length == 0) return ids;

            int pos = 0;
            while (pos < blob.Length)
            {
                byte key = blob[pos++];
                int wireType = key & 7;
                int fieldNum = key >> 3;

                if (fieldNum == 3 && wireType == 0) // Tag 3 (24), Varint
                {
                    ulong val = 0;
                    int shift = 0;
                    while (pos < blob.Length)
                    {
                        byte b = blob[pos++];
                        val |= (ulong)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                    }
                    ids.Add((int)val);
                }
                else if (wireType == 0) // Varint (skip)
                {
                    while (pos < blob.Length && (blob[pos++] & 0x80) != 0) { }
                }
                else if (wireType == 1) // 64-bit
                {
                    pos += 8;
                }
                else if (wireType == 2) // Length-delimited
                {
                    ulong len = 0;
                    int shift = 0;
                    while (pos < blob.Length)
                    {
                        byte b = blob[pos++];
                        len |= (ulong)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                    }
                    pos += (int)len;
                }
                else if (wireType == 5) // 32-bit
                {
                    pos += 4;
                }
                else
                {
                    break; // Unknown wire type, abort to avoid loop
                }
            }
            return ids;
        }

        public async Task<bool> DeletePlayerBlueprintsAsync(string serverPath, string steamId)
        {
            try
            {
                if (!ulong.TryParse(steamId, out ulong userId)) return false;

                var dbPaths = GetAllDbFiles(serverPath, "player.blueprints.*.db");
                if (dbPaths.Count == 0) return false;

                bool anyDeleted = false;
                foreach (var dbPath in dbPaths)
                {
                    if (!File.Exists(dbPath)) continue;

                    string connStr = $"Data Source={dbPath};Mode=ReadWrite;";
                    using (var connection = new SqliteConnection(connStr))
                    {
                        await connection.OpenAsync();
                        using (var cmd = CreateCommandWithTimeout(connection))
                        {
                            cmd.CommandText = "DELETE FROM data WHERE userid = @userid";
                            cmd.Parameters.AddWithValue("@userid", (long)userId);
                            int rows = await cmd.ExecuteNonQueryAsync();
                            if (rows > 0)
                            {
                                anyDeleted = true;
                            }
                        }
                    }
                }

                // Invalidate query caches since database changed
                _queryCache.Clear();

                return anyDeleted;
            }
            catch (Exception ex)
            {
                LogError($"Failed to delete player blueprints for SteamID {steamId}", ex);
                return false;
            }
        }

        private void LogError(string message, Exception ex)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "database_errors.log");
                string logEntry = $"[{DateTime.Now:G}] ERROR: {message}. Exception: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}";
                File.AppendAllText(logPath, logEntry);
                Console.WriteLine($"[DB ERROR] {message}: {ex.Message}");
            }
            catch { }
        }
    }
}

