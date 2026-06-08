using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TRPServerPanel.Models;
using TRPServerPanel.Services;

namespace TRPServerPanel.ViewModels
{
    public class MarketplaceViewModel : BaseViewModel
    {
        private readonly PluginService _pluginService;
        private readonly GeminiService _geminiService;
        private string _searchText = "";
        private bool _isBusy;
        private List<MarketplacePlugin> _pluginResults = new();

        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public List<MarketplacePlugin> PluginResults
        {
            get => _pluginResults;
            set => SetProperty(ref _pluginResults, value);
        }

        public MarketplaceViewModel(PluginService pluginService, GeminiService geminiService)
        {
            _pluginService = pluginService;
            _geminiService = geminiService;
        }

        public async Task SearchAsync(string query)
        {
            IsBusy = true;
            try
            {
                PluginResults = await _pluginService.SearchPluginsAsync(query);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<(bool Safe, string Report)> AuditPluginAsync(MarketplacePlugin plugin)
        {
            var content = await _pluginService.DownloadPluginContentAsync(plugin.DownloadUrl);
            return await _geminiService.AuditPluginAsync(plugin.Name, content);
        }

        public async Task<bool> InstallPluginAsync(string serverPath, MarketplacePlugin plugin, bool useCarbon)
        {
            var content = await _pluginService.DownloadPluginContentAsync(plugin.DownloadUrl);
            return await _pluginService.InstallPluginFromContentAsync(serverPath, plugin.Name, content, plugin, useCarbon);
        }
    }
}
