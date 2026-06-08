using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using TRPServerPanel.Models;
using TRPServerPanel.Services;

namespace TRPServerPanel.ViewModels
{
    public class ServerManagerViewModel : BaseViewModel
    {
        private readonly ServerManager _serverManager;
        private ObservableCollection<ServerModel> _servers = new();
        private ServerModel? _selectedServer;

        public ObservableCollection<ServerModel> Servers
        {
            get => _servers;
            set => SetProperty(ref _servers, value);
        }

        public ServerModel? SelectedServer
        {
            get => _selectedServer;
            set
            {
                if (SetProperty(ref _selectedServer, value))
                {
                    if (value != null)
                    {
                        value.Config = _serverManager.LoadServerConfig(value.Path);
                    }
                    OnServerSelected?.Invoke(value);
                }
            }
        }

        public event Action<ServerModel?>? OnServerSelected;

        public ServerManagerViewModel(ServerManager serverManager)
        {
            _serverManager = serverManager;
            LoadServers();
        }

        public void LoadServers()
        {
            var list = _serverManager.LoadServerList();
            foreach (var s in list)
            {
                if (_serverManager.IsServerProcessRunning(s.Path))
                {
                    s.Status = "Running";
                    _serverManager.TryReattach(s.Path);
                }
                else
                {
                    s.Status = "Stopped";
                }
            }
            Servers = new ObservableCollection<ServerModel>(list);
            if (SelectedServer == null && Servers.Count > 0)
            {
                SelectedServer = Servers[0];
            }
        }

        public void SaveServers()
        {
            _serverManager.SaveServerList(Servers.ToList());
        }

        public async Task DeleteServerAsync(ServerModel server)
        {
            if (server == null) return;
            Servers.Remove(server);
            SaveServers();
            if (SelectedServer == server) SelectedServer = Servers.FirstOrDefault();
            await Task.CompletedTask;
        }

        public void AddServer(ServerModel server)
        {
            Servers.Add(server);
            SaveServers();
            SelectedServer = server;
        }
    }
}
