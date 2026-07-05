using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TRPServerPanel.Services
{
    public class BackupMetadata
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
        public string ServerName { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public long SizeBytes { get; set; }
        public string FilePath { get; set; } = "";
        public string Type { get; set; } = "Full";
    }

    public class BackupService
    {
        private readonly string _backupsRoot;

        public BackupService()
        {
            _backupsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TRPServerPanel", "Backups");
            if (!Directory.Exists(_backupsRoot)) Directory.CreateDirectory(_backupsRoot);
        }

        public async Task<BackupMetadata?> CreateBackupAsync(string serverPath, string serverName)
        {
            try
            {
                var serverId = serverName.Replace(" ", "_");
                var serverBackupDir = Path.Combine(_backupsRoot, serverId);
                if (!Directory.Exists(serverBackupDir)) Directory.CreateDirectory(serverBackupDir);

                var meta = new BackupMetadata { ServerName = serverName };
                var fileName = $"backup_{meta.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
                var filePath = Path.Combine(serverBackupDir, fileName);
                meta.FilePath = filePath;

                await Task.Run(() =>
                {
                    using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create, System.Text.Encoding.UTF8);
                    var activeServerRoot = Directory.Exists(Path.Combine(serverPath, "rustds"))
                        ? Path.Combine(serverPath, "rustds")
                        : serverPath;

                    var foldersToBackup = new[] { "oxide", "carbon", "server" };
                    var files = new List<string>();

                    foreach (var folder in foldersToBackup)
                    {
                        var targetFolder = Path.Combine(activeServerRoot, folder);
                        if (Directory.Exists(targetFolder))
                        {
                            files.AddRange(Directory.GetFiles(targetFolder, "*.*", SearchOption.AllDirectories)
                                .Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && !f.Contains(".zip")));
                        }
                    }

                    var mainCfg = Path.Combine(serverPath, "trp_config.json");
                    if (File.Exists(mainCfg)) files.Add(mainCfg);

                    foreach (var file in files)
                    {
                        try
                        {
                            var relativePath = Path.GetRelativePath(serverPath, file);
                            zip.CreateEntryFromFile(file, relativePath);
                        }
                        catch { /* Skip locked files */ }
                    }
                });

                meta.SizeBytes = new FileInfo(filePath).Length;
                
                // Save metadata
                var metaPath = Path.Combine(serverBackupDir, "metadata.json");
                List<BackupMetadata> allMetas = new();
                if (File.Exists(metaPath))
                {
                    var json = await File.ReadAllTextAsync(metaPath);
                    allMetas = JsonSerializer.Deserialize<List<BackupMetadata>>(json) ?? new();
                }
                allMetas.Add(meta);
                await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(allMetas, new JsonSerializerOptions { WriteIndented = true }));

                return meta;
            }
            catch { return null; }
        }

        public List<BackupMetadata> GetBackups(string serverName)
        {
            var serverId = serverName.Replace(" ", "_");
            var metaPath = Path.Combine(_backupsRoot, serverId, "metadata.json");
            if (File.Exists(metaPath))
            {
                var json = File.ReadAllText(metaPath);
                return JsonSerializer.Deserialize<List<BackupMetadata>>(json) ?? new();
            }
            return new List<BackupMetadata>();
        }
    }
}
