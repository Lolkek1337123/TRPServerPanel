using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace TRPServerPanel.Services
{
    public class FileItem
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string Extension { get; set; } = string.Empty;
    }

    public class FileService
    {
        private bool IsSafePath(string rootPath, string targetPath)
        {
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(targetPath)) return false;
            try
            {
                string fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullTarget = Path.GetFullPath(targetPath);
                return fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public List<FileItem> ListDirectory(string path, string? rootPath = null)
        {
            if (rootPath != null && !IsSafePath(rootPath, path)) return new List<FileItem>();
            if (!Directory.Exists(path)) return new List<FileItem>();

            var items = new List<FileItem>();
            var di = new DirectoryInfo(path);

            foreach (var dir in di.GetDirectories())
            {
                items.Add(new FileItem
                {
                    Name = dir.Name,
                    FullPath = dir.FullName,
                    IsDirectory = true,
                    LastModified = dir.LastWriteTime,
                    Extension = ""
                });
            }

            foreach (var file in di.GetFiles())
            {
                items.Add(new FileItem
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    Size = file.Length,
                    LastModified = file.LastWriteTime,
                    Extension = file.Extension.ToLower()
                });
            }

            return items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name).ToList();
        }

        public async Task<string> ReadFileAsync(string path, string? rootPath = null)
        {
            if (rootPath != null && !IsSafePath(rootPath, path)) return "[ERROR] Access Denied";
            if (!File.Exists(path)) return string.Empty;
            // Prevent reading massive binaries
            var info = new FileInfo(path);
            if (info.Length > 2 * 1024 * 1024) return "[ERROR] File too large to preview (>2MB)";

            return await File.ReadAllTextAsync(path);
        }

        public async Task<bool> WriteFileAsync(string path, string content, string? rootPath = null)
        {
            if (rootPath != null && !IsSafePath(rootPath, path)) return false;
            try
            {
                await File.WriteAllTextAsync(path, content);
                return true;
            }
            catch { return false; }
        }

        public bool DeleteItem(string path, string? rootPath = null)
        {
            if (rootPath != null && !IsSafePath(rootPath, path)) return false;
            try
            {
                if (Directory.Exists(path))
                {
                    SafeDeleteDirectory(path);
                    return true;
                }
                if (File.Exists(path))
                {
                    SafeDeleteFile(path);
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        private void SafeDeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;

            // Remove Read-Only attributes recursively
            try
            {
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch { }
                }

                var dirs = Directory.GetDirectories(path, "*", SearchOption.AllDirectories);
                foreach (var dir in dirs)
                {
                    try
                    {
                        var di = new DirectoryInfo(dir);
                        di.Attributes &= ~FileAttributes.ReadOnly;
                    }
                    catch { }
                }
            }
            catch { }

            // Try to delete with retries
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Directory.Delete(path, true);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }

            Directory.Delete(path, true);
        }

        private void SafeDeleteFile(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            catch { }

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.Delete(path);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
            File.Delete(path);
        }

        public bool CreateDirectory(string parentPath, string name, string? rootPath = null)
        {
            var newPath = Path.Combine(parentPath, name);
            if (rootPath != null && !IsSafePath(rootPath, newPath)) return false;
            try
            {
                Directory.CreateDirectory(newPath);
                return true;
            }
            catch { return false; }
        }
    }
}
