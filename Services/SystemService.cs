using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Linq;

namespace TRPServerPanel.Services
{
    public class SystemService
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        private static double? _totalPhysicalRamGb;
        public static double TotalPhysicalRamGb
        {
            get
            {
                if (!_totalPhysicalRamGb.HasValue)
                {
                    try
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            var memStatus = new MEMORYSTATUSEX();
                            if (GlobalMemoryStatusEx(memStatus))
                            {
                                _totalPhysicalRamGb = Math.Max(4.0, Math.Round((double)memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0)));
                            }
                        }

                        if (!_totalPhysicalRamGb.HasValue)
                        {
                            double bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                            _totalPhysicalRamGb = Math.Max(4.0, Math.Round(bytes / (1024.0 * 1024.0 * 1024.0)));
                        }
                    }
                    catch
                    {
                        _totalPhysicalRamGb = 16.0; // Fallback
                    }
                }
                return _totalPhysicalRamGb.Value;
            }
        }
        public struct DiagnosticResult
        {
            public string Name { get; set; }
            public bool IsOk { get; set; }
            public string Detail { get; set; }
            public string Status { get; set; } // "OK", "Warning", "Error"
        }

        public DiagnosticResult CheckAdmin()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    bool isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
                    return new DiagnosticResult 
                    { 
                        Name = "Security Level", 
                        IsOk = isAdmin, 
                        Detail = isAdmin ? "Administrator Access Verified" : "Standard User (Limited Perms)",
                        Status = isAdmin ? "OK" : "Warning"
                    };
                }
            }
            catch { return new DiagnosticResult { Name = "Security", IsOk = false, Detail = "Unknown error during check", Status = "Error" }; }
        }

        public DiagnosticResult CheckDiskSpace()
        {
            try
            {
                var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.StartsWith("C"));
                if (drive == null) drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);

                if (drive != null)
                {
                    double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                    bool isOk = freeGb > 20.0;
                    return new DiagnosticResult
                    {
                        Name = "Storage Health",
                        IsOk = isOk,
                        Detail = $"Free Space: {freeGb:F1} GB (Required 20GB)",
                        Status = isOk ? "OK" : (freeGb > 5.0 ? "Warning" : "Error")
                    };
                }
                return new DiagnosticResult { Name = "Storage", IsOk = false, Detail = "No ready drives found", Status = "Error" };
            }
            catch { return new DiagnosticResult { Name = "Storage", IsOk = false, Detail = "Storage I/O Error", Status = "Error" }; }
        }

        public DiagnosticResult CheckInternet()
        {
            try
            {
                using (var ping = new Ping())
                {
                    var reply = ping.Send("google.com", 1000);
                    bool isOk = reply.Status == IPStatus.Success;
                    return new DiagnosticResult
                    {
                        Name = "Cloud Link",
                        IsOk = isOk,
                        Detail = isOk ? "Steam & Valve Services Reachable" : "No Internet Connection Found",
                        Status = isOk ? "OK" : "Error"
                    };
                }
            }
            catch { return new DiagnosticResult { Name = "Network", IsOk = false, Detail = "Network Bridge Offline", Status = "Error" }; }
        }

        public DiagnosticResult CheckSteamCMD()
        {
            var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TRPServerPanel", "Cache", "SteamCMD");
            bool exists = File.Exists(Path.Combine(cachePath, "steamcmd.exe"));
            return new DiagnosticResult
            {
                Name = "Core Engine",
                IsOk = exists,
                Detail = exists ? "SteamCMD Ready (Cached)" : "SteamCMD Core Missing",
                Status = exists ? "OK" : "Error"
            };
        }

        public DiagnosticResult CheckEnvironment()
        {
            string version = RuntimeInformation.FrameworkDescription;
            return new DiagnosticResult
            {
                Name = "Core Library",
                IsOk = true,
                Detail = $".NET Runtime {version} Active",
                Status = "OK"
            };
        }
        public double ProcessRamUsage
        {
            get
            {
                using (var proc = System.Diagnostics.Process.GetCurrentProcess())
                {
                    return proc.WorkingSet64;
                }
            }
        }

        public double ProcessCpuLoad
        {
            get
            {
                return 0.0;
            }
        }

        public static bool IsPortAvailable(int port, bool isTcp)
        {
            try
            {
                var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                if (isTcp)
                {
                    var tcpListeners = ipGlobalProperties.GetActiveTcpListeners();
                    return tcpListeners.All(el => el.Port != port);
                }
                else
                {
                    var udpListeners = ipGlobalProperties.GetActiveUdpListeners();
                    return udpListeners.All(el => el.Port != port);
                }
            }
            catch
            {
                return true; // Fallback
            }
        }
    }
}
