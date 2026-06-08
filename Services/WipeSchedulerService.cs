using System;
using System.Timers;
using System.Threading.Tasks;
using TRPServerPanel.Models;
using System.Collections.Generic;
using System.Linq;

namespace TRPServerPanel.Services
{
    public class WipeSchedulerService
    {
        private readonly System.Timers.Timer _timer;
        private readonly WipeService _wipeService;
        private readonly ServerManager _serverManager;
        private readonly List<ServerModel> _trackedServers = new();
        private readonly HashSet<string> _wipingServers = new();
        private readonly Dictionary<string, DateTime> _lastWipeTimes = new();
        private readonly object _lock = new();

        public event Action<string, string>? OnWipeScheduled;

        public WipeSchedulerService(WipeService wipeService, ServerManager serverManager)
        {
            _wipeService = wipeService;
            _serverManager = serverManager;
            
            _timer = new System.Timers.Timer(60000); // Check every minute
            _timer.Elapsed += async (s, e) => await CheckSchedulesAsync();
            _timer.AutoReset = true;
            _timer.Enabled = true;
        }

        public void RegisterServer(ServerModel server)
        {
            lock (_lock)
            {
                if (!_trackedServers.Any(s => s.Path == server.Path))
                {
                    _trackedServers.Add(server);
                }
            }
        }

        private async Task CheckSchedulesAsync()
        {
            var now = DateTime.Now;
            List<ServerModel> serversCopy;

            lock (_lock)
            {
                serversCopy = _trackedServers.ToList();
            }
            
            foreach (var server in serversCopy)
            {
                if (server.Config == null || server.Config.WipeSchedule == null || !server.Config.WipeSchedule.Enabled)
                    continue;

                var schedule = server.Config.WipeSchedule;
                
                bool isTime = CheckIfTime(schedule, now);
                
                if (isTime)
                {
                    lock (_lock)
                    {
                        if (_wipingServers.Contains(server.Path))
                            continue;

                        // Prevent double-wipe during the same minute block
                        if (_lastWipeTimes.TryGetValue(server.Path, out var lastWipe) && (now - lastWipe).TotalMinutes < 2)
                        {
                            continue;
                        }

                        _wipingServers.Add(server.Path);
                        _lastWipeTimes[server.Path] = now;
                    }

                    try
                    {
                        bool wipeBps = schedule.WipeBlueprints;
                        OnWipeScheduled?.Invoke(server.Name, $"Initiating scheduled wipe: {schedule.Frequency} (Blueprints: {wipeBps})");
                        await _wipeService.ExecuteWipeAsync(server.Path, server.Name, wipeBps, true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WIPE SCHEDULER ERROR] Failed scheduled wipe for {server.Name}: {ex.Message}");
                    }
                    finally
                    {
                        lock (_lock)
                        {
                            _wipingServers.Remove(server.Path);
                        }
                    }
                }
            }
        }

        private bool CheckIfTime(WipeSchedule schedule, DateTime now)
        {
            // Simple time check: HH:mm
            string currentTime = now.ToString("HH:mm");
            if (currentTime != schedule.Time) return false;

            // Check Frequency
            switch (schedule.Frequency)
            {
                case "Daily":
                    return true;
                case "Weekly":
                    return now.DayOfWeek.ToString() == schedule.DayOfWeek;
                case "Monthly":
                    return now.Day == 1; // First day of month for simplicity
                default:
                    return false;
            }
        }
    }
}
