namespace TRPServerPanel.Models
{
    public class ServerConfig
    {
        // 1. IDENTITY & BRANDING
        public string Hostname { get; set; } = "RUST DEV LAB";
        public string Description { get; set; } = "Professional Rust Server Orchestrator";
        public string Url { get; set; } = "https://teamrustplugins.com";
        public string HeaderImage { get; set; } = "";
        public string Identity { get; set; } = "rustserver";

        // 2. NETWORK
        public int Port { get; set; } = 28015;
        public int QueryPort { get; set; } = 28016;
        public int RconPort { get; set; } = 28017;
        public string RconPassword { get; set; } = "admin";
        public string ServerIP { get; set; } = "0.0.0.0";
        public bool RconWeb { get; set; } = true;
        public string AdminSteamId { get; set; } = "";

        // 3. WORLD & MAP
        public int WorldSize { get; set; } = 3000;
        public int Seed { get; set; } = 12345;
        public string MapLevel { get; set; } = "Procedural Map";
        public int MaxPlayers { get; set; } = 100;
        public int Tickrate { get; set; } = 30;

        // 4. GAMEPLAY
        public bool PvpEnabled { get; set; } = true;
        public bool Stability { get; set; } = true;
        public bool Radiation { get; set; } = true;
        public int SaveInterval { get; set; } = 300;
        public bool GlobalChat { get; set; } = true;
        public bool Upkeep { get; set; } = true;
        public double DecayScale { get; set; } = 1.0;
        public bool InstantCraft { get; set; } = false;
        public int MaxTeamSize { get; set; } = 8;
        public double HeliLifespan { get; set; } = 45; // in minutes
        public double DayLength { get; set; } = 45; // in minutes
        public double NightLength { get; set; } = 15; // in minutes

        // 5. SECURITY & ANTIHACK
        public bool Secure { get; set; } = true; // VAC
        public int AntiHackLevel { get; set; } = 2;
        public bool ProxyConnections { get; set; } = false;
        public int MaxConnectionsPerIP { get; set; } = 5;

        // 6. ENGINE & AI
        public string AdditionalArgs { get; set; } = "-batchmode -nographics -silent-crashes";
        public string AiModel { get; set; } = "nvidia/nemotron-3-super-120b-a12b:free";
        public string AiApiKey { get; set; } = "";
        public string LevelUrl { get; set; } = "";

        // 7. MAINTENANCE & AUTOMATION
        public WipeSchedule WipeSchedule { get; set; } = new WipeSchedule();
        public bool AutoStart { get; set; } = false;
    }
}
