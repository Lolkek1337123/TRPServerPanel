using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TRPServerPanel.Models
{
    public class ServerModel : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private ServerStatus _statusEnum = ServerStatus.Stopped;
        private string _statusColor = "#808080";
        private string _ramUsage = "0.0 / 0.0 GB";
        private int _fps = 0;
        private int _entities = 0;
        private int _playerCount = 0;
        private int _maxPlayers = 0;
        private int _ping = 0;
        private string _path = string.Empty;
        private string _framework = "VANILLA";
        private string _uptime = "00:00:00";
        private int _port = 28015;
        private string _modType = "vanilla";
        private ServerConfig _config = new();
        private string _networkUsage = "0.0 MB/s";
        private double _cpuUsageValue = 0.0;
        private double _ramUsageValue = 0.0;
        private double _panelRam = 0.0;
        private DateTime? _uptimeStart;

        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public ServerStatus StatusEnum 
        { 
            get => _statusEnum; 
            set 
            { 
                if (SetProperty(ref _statusEnum, value))
                {
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(IsRunning));
                }
            } 
        }

        private string _status = "Stopped";

        public string Status 
        { 
            get => _status;
            set 
            {
                if (SetProperty(ref _status, value ?? "Stopped"))
                {
                    var newEnum = _status.Replace("...", "") switch {
                        "Starting" => ServerStatus.Starting,
                        "Restarting" => ServerStatus.Restarting,
                        "Running" => ServerStatus.Running,
                        "Stopped" => ServerStatus.Stopped,
                        "Stopping" => ServerStatus.Stopping,
                        "Updating" => ServerStatus.Updating,
                        "Error" => ServerStatus.Error,
                        string s when s.Contains("Checking") || s.Contains("Downloading") || s.Contains("Update") || s.Contains("Install") => ServerStatus.Updating,
                        _ => ServerStatus.Stopped
                    };
                    
                    if (_statusEnum != newEnum)
                    {
                        _statusEnum = newEnum;
                        OnPropertyChanged(nameof(StatusEnum));
                        OnPropertyChanged(nameof(IsRunning));
                    }
                }
            }
        }

        public string StatusColor { get => _statusColor; set => SetProperty(ref _statusColor, value); }
        public bool IsRunning => StatusEnum == ServerStatus.Running;

        public string RamUsage { get => _ramUsage; set => SetProperty(ref _ramUsage, value); }
        public string NetworkUsage { get => _networkUsage; set => SetProperty(ref _networkUsage, value); }
        public double CpuUsageValue { get => _cpuUsageValue; set => SetProperty(ref _cpuUsageValue, value); }
        public double RamUsageValue { get => _ramUsageValue; set => SetProperty(ref _ramUsageValue, value); }
        public double PanelRam { get => _panelRam; set => SetProperty(ref _panelRam, value); }
        public int Fps { get => _fps; set => SetProperty(ref _fps, value); }
        public int Entities { get => _entities; set => SetProperty(ref _entities, value); }
        public int PlayerCount { get => _playerCount; set { if (SetProperty(ref _playerCount, value)) OnPropertyChanged(nameof(PlayersRatio)); } }
        public int MaxPlayers { get => _maxPlayers; set { if (SetProperty(ref _maxPlayers, value)) OnPropertyChanged(nameof(PlayersRatio)); } }
        public int Ping { get => _ping; set => SetProperty(ref _ping, value); }
        public string PlayersRatio => $"{PlayerCount}/{MaxPlayers}";

        public string Path { get => _path; set => SetProperty(ref _path, value); }
        public string Framework { get => _framework; set => SetProperty(ref _framework, value); }
        public int Port { get => _port; set => SetProperty(ref _port, value); }
        public string ModType { get => _modType; set => SetProperty(ref _modType, value); }
        public ServerConfig Config { get => _config; set => SetProperty(ref _config, value); }
        public string Uptime { get => _uptime; set => SetProperty(ref _uptime, value); }

        public System.Collections.ObjectModel.ObservableCollection<double> RamHistory { get; set; } = new();
        public System.Collections.ObjectModel.ObservableCollection<double> CpuHistory { get; set; } = new();
        public System.Collections.ObjectModel.ObservableCollection<double> NetworkHistory { get; set; } = new();
        public System.Collections.ObjectModel.ObservableCollection<int> FpsHistory { get; set; } = new();
        public System.Collections.ObjectModel.ObservableCollection<int> PlayerHistory { get; set; } = new();
        public System.Collections.ObjectModel.ObservableCollection<int> EntitiesHistory { get; set; } = new();
        public System.Collections.ObjectModel.ObservableCollection<int> PingHistory { get; set; } = new();
        
        public DateTime? UptimeStart { get => _uptimeStart; set => SetProperty(ref _uptimeStart, value); }
        
        public DateTime? WipeDate { get; set; } = DateTime.Now.AddDays(7);
        public bool IsSteamConnected { get; set; } = true;
        public bool IsVacSecure { get; set; } = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value)) return false;
            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
