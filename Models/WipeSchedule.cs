namespace TRPServerPanel.Models
{
    public class WipeSchedule
    {
        public bool Enabled { get; set; } = false;
        public string Frequency { get; set; } = "Weekly"; // Weekly, Daily, Monthly
        public string DayOfWeek { get; set; } = "Thursday"; 
        public string Time { get; set; } = "18:00";
        public bool WipeBlueprints { get; set; } = false;
        public bool AutoRestart { get; set; } = true;
    }
}
