using System;
using System.Linq;
using System.Net.NetworkInformation;

namespace TRPServerPanel.Services
{
    public class NetworkService
    {
        private long _lastBytesReceived = 0;
        private long _lastBytesSent = 0;
        private DateTime _lastUpdate = DateTime.MinValue;

        public string GetCurrentUsage()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up && 
                                i.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                long currentReceived = 0;
                long currentSent = 0;

                foreach (var ni in interfaces)
                {
                    var stats = ni.GetIPv4Statistics();
                    currentReceived += stats.BytesReceived;
                    currentSent += stats.BytesSent;
                }

                if (_lastUpdate == DateTime.MinValue)
                {
                    _lastBytesReceived = currentReceived;
                    _lastBytesSent = currentSent;
                    _lastUpdate = DateTime.Now;
                    return "0.0 MB/s";
                }

                var now = DateTime.Now;
                double seconds = (now - _lastUpdate).TotalSeconds;
                if (seconds < 0.1) return "0.0 MB/s";

                double diffRec = (currentReceived - _lastBytesReceived);
                double diffSent = (currentSent - _lastBytesSent);
                
                // Convert to MB per second
                double totalMBs = (diffRec + diffSent) / (1024.0 * 1024.0) / seconds;

                _lastBytesReceived = currentReceived;
                _lastBytesSent = currentSent;
                _lastUpdate = now;

                return $"{totalMBs:F1} MB/s";
            }
            catch
            {
                return "0.0 MB/s";
            }
        }
    }
}
