using System.Collections.Generic;

namespace TRPServerPanel.Models
{
    public class SecurityReport
    {
        public string RiskLevel { get; set; } = "Low"; // Low, Medium, High, Critical
        public string Summary { get; set; } = "";
        public List<SecurityFinding> Findings { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
        public string RawResponse { get; set; } = "";
    }

    public class SecurityFinding
    {
        public string Type { get; set; } = "Detection";
        public string Description { get; set; } = "";
        public string Severity { get; set; } = "Low";
        public string RawData { get; set; } = "";
    }
}
