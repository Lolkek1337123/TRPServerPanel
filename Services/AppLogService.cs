using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;
using TRPServerPanel.Models;

namespace TRPServerPanel.Services
{
    public enum AppLogLevel
    {
        DEBUG,
        INFO,
        WARN,
        ERROR
    }

    public class AppLogEntry : LogEntry
    {
        public AppLogLevel Level { get; set; }
        public string Source { get; set; } = string.Empty;
    }

    public static class AppLogService
    {
        private static readonly ConcurrentQueue<AppLogEntry> _logBuffer = new();
        private static readonly ObservableCollection<AppLogEntry> _logs = new();
        private static readonly object _lock = new();
        private const int MaxLogs = 1000;
        private static readonly BlockingCollection<string> _fileWriteQueue = new();

        public static AppLogLevel MinLogLevel { get; set; } = AppLogLevel.INFO;
        public static ObservableCollection<AppLogEntry> Logs => _logs;

        public static event Action<AppLogEntry>? OnLogAdded;

        static AppLogService()
        {
            BindingOperations.EnableCollectionSynchronization(_logs, _lock);
            StartFileWriteWorker();
        }

        private static void StartFileWriteWorker()
        {
            var thread = new System.Threading.Thread(FileWriteWorker)
            {
                IsBackground = true,
                Name = "AppLogServiceFileWriter"
            };
            thread.Start();
        }

        private static void FileWriteWorker()
        {
            var logFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log");
            foreach (var line in _fileWriteQueue.GetConsumingEnumerable())
            {
                try
                {
                    System.IO.File.AppendAllText(logFile, line);
                }
                catch { }
            }
        }

        public static void Log(string message, AppLogLevel level = AppLogLevel.INFO, string source = "APP")
        {
            System.Diagnostics.Debug.WriteLine($"[{level}] [{source}] {message}");

            if (level < MinLogLevel)
            {
                return;
            }

            var entry = new AppLogEntry
            {
                Time = DateTime.Now,
                Message = message,
                Level = level,
                Source = source
            };

            lock (_lock)
            {
                _logs.Add(entry);
                if (_logs.Count > MaxLogs)
                {
                    _logs.RemoveAt(0);
                }
            }

            OnLogAdded?.Invoke(entry);

            _fileWriteQueue.Add($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] [{level}] [{source}] {message}{Environment.NewLine}");
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _logs.Clear();
            }
        }
    }
}
