using System;
using System.Collections.Generic;

namespace TRPServerPanel.Models
{
    public class PlayerIdentity
    {
        public ulong SteamID { get; set; }
        public string Username { get; set; } = "Unknown";
        public string DisplayName => Username;
        public string Nickname => Username;
        public string AvatarUrl { get; set; } = "https://files.facepunch.com/steam/profiles/0.png";
        public DateTime LastSeen { get; set; }
        public string LastSeenFormatted => LastSeen.ToString("g");
        
        // Advanced Insights
        public string IPAddress { get; set; } = "N/A";
        public string Country { get; set; } = "Unknown";
        public string CountryCode { get; set; } = string.Empty;
        public int Health { get; set; } = 0;
        public int Ping { get; set; } = 0;
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int BlueprintsCount { get; set; } = 0;
        public double TotalSurvivalTimeSeconds { get; set; } = 0;
        public double KD => Deaths == 0 ? Kills : Math.Round((double)Kills / Deaths, 2);
        
        public ulong TeamID { get; set; } = 0;
        public string TeamName { get; set; } = "No Team";

        public double PlayTimeSeconds { get; set; } = 0;

        public string PlaytimeFormatted 
        {
            get 
            {
                var t = TimeSpan.FromSeconds(PlayTimeSeconds);
                if (t.TotalDays >= 1)
                    return string.Format("{0:0}d {1:0}h", Math.Floor(t.TotalDays), t.Hours);
                return string.Format("{0:0}h {1:0}m", Math.Floor(t.TotalHours), t.Minutes);
            }
        }

        public List<string> Blueprints { get; set; } = new List<string>();

        // Extended attributes for list display
        public bool IsOnline => Ping > 0;
        public string StatusColor => IsOnline ? "#00FF00" : "#888888";
    }

    public class TeamInfo
    {
        public ulong TeamID { get; set; }
        public ulong LeaderID { get; set; }
        public List<ulong> Members { get; set; } = new List<ulong>();
    }

    public class PlayerBlueprint
    {
        public ulong SteamID { get; set; }
        public int BlueprintID { get; set; }
        public string ItemShortname { get; set; } = string.Empty;
    }
}
