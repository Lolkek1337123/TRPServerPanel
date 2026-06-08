using System;
using System.IO;
using System.Threading.Tasks;
using TRPServerPanel.Models;

namespace TRPServerPanel.Services
{
    public class WipeService
    {
        private readonly ServerManager _serverManager;
        private readonly BackupService _backupService;

        public WipeService(ServerManager serverManager, BackupService backupService)
        {
            _serverManager = serverManager ?? throw new ArgumentNullException(nameof(serverManager));
            _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        }

        public async Task<bool> ExecuteWipeAsync(string serverPath, string serverName, bool wipeBlueprints, bool createBackup = true)
        {
            try
            {
                // Load config to determine server identity folder
                var config = _serverManager.LoadServerConfig(serverPath);
                string identity = config?.Identity ?? "rustserver";
                if (string.IsNullOrEmpty(identity)) identity = "rustserver";

                // 1. Backup before wipe
                if (createBackup)
                {
                    await _backupService.CreateBackupAsync(serverPath, serverName);
                }

                // 2. Stop Server
                await _serverManager.StopServerAsync();

                // 3. Wipe Files
                // Procedural Maps (root directory)
                var files = Directory.GetFiles(serverPath, "proceduralmap.*", SearchOption.TopDirectoryOnly);
                foreach (var file in files) 
                {
                    try { File.Delete(file); } catch { }
                }

                // Save files in identity directory
                var saveDir = Path.Combine(serverPath, "server", identity);
                if (Directory.Exists(saveDir))
                {
                    // Also delete procedural maps in the identity folder if they exist there
                    var identityProcedurals = Directory.GetFiles(saveDir, "proceduralmap.*", SearchOption.TopDirectoryOnly);
                    foreach (var file in identityProcedurals)
                    {
                        try { File.Delete(file); } catch { }
                    }

                    var saves = Directory.GetFiles(saveDir, "*.sav");
                    foreach (var s in saves)
                    {
                        try { File.Delete(s); } catch { }
                    }
                    
                    var allFiles = Directory.GetFiles(saveDir, "*.*");
                    foreach (var f in allFiles)
                    {
                        string ext = Path.GetExtension(f).ToLower();
                        string fileName = Path.GetFileName(f).ToLower();
                        
                        // Check if it is a SQLite database or journal file (.db, .db-wal, .db-shm)
                        bool isDbFile = ext == ".db" || ext == ".db-wal" || ext == ".db-shm" || fileName.EndsWith(".db-wal") || fileName.EndsWith(".db-shm");
                        if (isDbFile)
                        {
                            if (!wipeBlueprints && fileName.StartsWith("player.blueprints.", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            
                            try
                            {
                                File.Delete(f);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[WIPE WARNING] Could not delete database file {f}: {ex.Message}");
                            }
                        }
                    }
                }

                // 4. Update Seed
                if (config != null)
                {
                    config.Seed = new Random().Next(1, 2147483647);
                    _serverManager.SaveServerSettings(serverPath, config);
                    
                    // 5. Start Server
                    await _serverManager.StartServerAsync(serverPath, config);
                }
                
                return true;
            }
            catch { return false; }
        }
    }
}
