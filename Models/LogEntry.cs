using System;

namespace TRPServerPanel.Models
{
    public class LogEntry
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public string Message { get; set; } = string.Empty;
        public LogType Type { get; set; }

        public string FormattedTime => Time.ToString("HH:mm:ss");
    }
}
