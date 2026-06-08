using System.Windows.Media;

namespace TRPServerPanel.Views
{
    public class PlayerMock
    {
        public string Name { get; set; } = "";
        public string SteamID { get; set; } = "";
        public string Ping { get; set; } = "";
    }

    public class PluginMock
    {
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public string Status { get; set; } = "";
        public string StatusColor { get; set; } = "#2000FF00";
    }
}
