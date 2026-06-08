using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;
using System.Timers;
using TRPServerPanel.Models;
using TRPServerPanel.Services;
using TRPServerPanel.Utils;

namespace TRPServerPanel.ViewModels
{
    public class ConsoleViewModel : BaseViewModel
    {
        private readonly RconService _rconService;
        private readonly ServerManager _serverManager;
        private readonly ObservableCollectionBatch<LogEntry> _consoleLogs;
        private readonly ConcurrentQueue<LogEntry> _logBuffer = new();
        private readonly System.Timers.Timer _logFlushTimer;
        private readonly object _logLock = new();
        private readonly HashSet<string> _logDedupSet = new();
        private readonly Queue<(string Msg, DateTime Ts)> _logDedupBuffer = new();
        private bool _isCapturingCompilerError = false;

        private string _currentCommandText = "";
        private string _selectedLogFilter = "ALL";
        private string _searchText = "";

        public ObservableCollectionBatch<LogEntry> ConsoleLogs => _consoleLogs;

        public event Action<IEnumerable<LogEntry>>? OnLogBatchReceived;

        public ConsoleViewModel(RconService rconService, ServerManager serverManager)
        {
            _rconService = rconService;
            _serverManager = serverManager;
            _consoleLogs = new ObservableCollectionBatch<LogEntry>();
            
            BindingOperations.EnableCollectionSynchronization(_consoleLogs, _logLock);

            _rconService.OnMessageReceived += (msg) => HandleLogReceived(msg, LogType.Rcon);
            _serverManager.LogReceived += (msg) => HandleLogReceived(msg, LogType.System);

            _logFlushTimer = new System.Timers.Timer(250);
            _logFlushTimer.Elapsed += (s, e) => FlushLogBuffer();
            _logFlushTimer.AutoReset = true;
            _logFlushTimer.Enabled = true;
        }

        public string CurrentCommandText
        {
            get => _currentCommandText;
            set => SetProperty(ref _currentCommandText, value);
        }

        public string SelectedLogFilter
        {
            get => _selectedLogFilter;
            set { if (SetProperty(ref _selectedLogFilter, value)) OnPropertyChanged(nameof(FilteredLogs)); }
        }

        public string SearchText
        {
            get => _searchText;
            set { if (SetProperty(ref _searchText, value)) OnPropertyChanged(nameof(FilteredLogs)); }
        }

        public IEnumerable<LogEntry> FilteredLogs
        {
            get
            {
                var logs = SelectedLogFilter == "ALL" ? _consoleLogs : _consoleLogs.Where(l => l.Type.ToString().ToUpper() == SelectedLogFilter);
                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    logs = logs.Where(l => l.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
                }
                return logs;
            }
        }

        public void AddLog(string msg, LogType type)
        {
            _logBuffer.Enqueue(new LogEntry { Message = msg, Type = type, Time = DateTime.Now });
        }

        private void HandleLogReceived(string log, LogType defaultType)
        {
            if (string.IsNullOrWhiteSpace(log)) return;

            string cleanLog = log.Trim();
            if (cleanLog == "[]" || cleanLog == "{}" || cleanLog == ">>" || cleanLog == ">") return;

            // v16.7: Filter out Telemetry & Status Spam
            if (cleanLog.StartsWith("{") && (cleanLog.Contains("Hostname") || cleanLog.Contains("EntityCount") || cleanLog.Contains("Framerate"))) return;
            if (cleanLog.StartsWith("hostname:") || cleanLog.StartsWith("version:") || cleanLog.StartsWith("map :")) return;
            if (cleanLog.Contains("id name ping connected addr owner")) return;
            if (System.Text.RegularExpressions.Regex.IsMatch(cleanLog, @"^\d+\s+fps,\s+\d+\s+ents")) return;
            if (cleanLog.Contains("players :") && cleanLog.Contains("max)")) return;

            string compareBasis = System.Text.RegularExpressions.Regex.Replace(
                cleanLog, 
                @"^(?:\[?(?:\d{2}/\d{2}(?:/\d{4})?\s+)?\d{1,2}:\d{2}:\d{2}\]?[:\s]*)", 
                ""
            ).Trim();
            // Remove ANSI escape codes (e.g. colors like \x1B[31m) and normalize for comparison
            string cleanCompare = System.Text.RegularExpressions.Regex.Replace(compareBasis, @"\x1B\[[0-9;]*[a-zA-Z]", "").Trim();
            cleanCompare = System.Text.RegularExpressions.Regex.Replace(cleanCompare, @"\s+", " ").ToLowerInvariant();

            lock (_logLock)
            {
                while (_logDedupBuffer.Count > 0 && (DateTime.Now - _logDedupBuffer.Peek().Ts).TotalMilliseconds > 3000)
                {
                    var old = _logDedupBuffer.Dequeue();
                    _logDedupSet.Remove(old.Msg);
                }

                if (_logDedupSet.Contains(cleanCompare)) return;
                
                _logDedupBuffer.Enqueue((cleanCompare, DateTime.Now));
                _logDedupSet.Add(cleanCompare);
            }

            var type = defaultType;
            string upperLog = log.ToUpperInvariant();

            if (upperLog.Contains("ERROR WHILE COMPILING") || upperLog.Contains("FAILED COMPILING"))
            {
                _isCapturingCompilerError = true;
                type = LogType.Compiler;
            }
            else if (_isCapturingCompilerError && (log.StartsWith(" ") || System.Text.RegularExpressions.Regex.IsMatch(log.TrimStart(), @"^\d+\.")))
            {
                type = LogType.Compiler;
            }
            else if (upperLog.Contains("ERROR CS") || (upperLog.Contains("LINE") && upperLog.Contains("COLUMN") && upperLog.Contains("(")))
            {
                type = LogType.Compiler;
            }
            else if (!string.IsNullOrWhiteSpace(log))
            {
                _isCapturingCompilerError = false;
            }

            if (type != LogType.Compiler && defaultType != LogType.Rcon)
            {
                if (upperLog.Contains("[ERROR]") || upperLog.Contains("ERROR:") || upperLog.Contains("EXCEPTION:") || upperLog.Contains("FAILED TO CALL HOOK"))
                    type = LogType.Error;
                else if (upperLog.Contains("[SUCCESS]"))
                    type = LogType.Success;
                else if (upperLog.Contains("[WARNING]") || upperLog.Contains("WARN:"))
                    type = LogType.Warning;
                else if (upperLog.Contains("[CHAT]"))
                    type = LogType.Chat;
                else if (upperLog.Contains("[OXIDE]") || upperLog.Contains("[CARBON]"))
                    type = LogType.Oxide;
            }

            AddLog(log, type);
        }

        private void FlushLogBuffer()
        {
            if (_logBuffer.IsEmpty) return;

            var batch = new List<LogEntry>();
            while (_logBuffer.TryDequeue(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0) return;

            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => {
                lock (_logLock)
                {
                    _consoleLogs.AddRange(batch);
                    // v16.7: Reduce limit to 500 entries to prevent WebView2/WPF performance degradation
                    if (_consoleLogs.Count > 500)
                    {
                        _consoleLogs.RemoveRange(0, _consoleLogs.Count - 500);
                    }
                }
                OnLogBatchReceived?.Invoke(batch);
                OnPropertyChanged(nameof(FilteredLogs));
            });
        }

        public void ClearConsole()
        {
            lock (_logLock)
            {
                _consoleLogs.Clear();
                _logDedupSet.Clear();
            }
            OnPropertyChanged(nameof(FilteredLogs));
        }

        public async Task ExecuteCommandAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

            string sanitized = SanitizeRconCommand(command);
            if (string.IsNullOrWhiteSpace(sanitized)) return;

            if (sanitized.Length > 512)
            {
                AddLog("[ERROR] Command exceeds 512 character limit.", LogType.Error);
                return;
            }

            if (_rconService.IsConnected)
            {
                await _rconService.SendCommandAsync(sanitized);
                AddLog($"> {sanitized}", LogType.Rcon);
            }
            else if (_serverManager.IsRunning)
            {
                _serverManager.SendCommand(sanitized);
                AddLog($"> {sanitized}", LogType.Rcon);
            }
            else
            {
                AddLog("[ERROR] No active connection to server.", LogType.Error);
            }
        }

        private string SanitizeRconCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (char c in command)
            {
                if (c >= 32)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Trim();
        }
        public string GetConsoleLogsJson()
        {
            lock (_logLock)
            {
                var logs = _consoleLogs.TakeLast(500).Select(l => new {
                    message = l.Message,
                    level = l.Type.ToString()
                });
                return System.Text.Json.JsonSerializer.Serialize(logs);
            }
        }
    }
}
