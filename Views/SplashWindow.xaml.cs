using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace TRPServerPanel.Views
{
    public partial class SplashWindow : Window
    {
        private const string SplashHtml = @"
        <!DOCTYPE html>
        <html lang='en'>
        <head>
            <meta charset='UTF-8'>
            <script src=""https://cdn.tailwindcss.com""></script>
            <link href=""https://fonts.googleapis.com/css2?family=Outfit:wght@400;600;800&family=JetBrains+Mono:wght@500;700&family=Press+Start+2P&display=swap"" rel=""stylesheet"">
            <link href=""https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.0.0/css/all.min.css"" rel=""stylesheet"">
            <style>
                :root {
                    --pixel-grid: url(""data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='4' height='4' viewBox='0 0 4 4'%3E%3Cpath fill='%23ff3e3e' fill-opacity='0.15' d='M1 3h1v1H1V3zm2-2h1v1H3V1z'/%3E%3C/svg%3E"");
                    --pixel-size: 4px 4px;
                }
                body {
                    font-family: 'Inter', sans-serif;
                    background-color: #050505;
                    color: #ffffff;
                    margin: 0;
                    overflow: hidden;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    height: 100vh;
                    width: 100vw;
                    animation: crt-flicker 0.2s infinite;
                }

                @keyframes crt-flicker {
                    0% { opacity: 0.99; }
                    5% { opacity: 0.98; }
                    10% { opacity: 0.99; }
                    100% { opacity: 1; }
                }

                /* SVG Pixel Grid */
                body::before {
                    content: '';
                    position: fixed;
                    inset: 0;
                    background-image: var(--pixel-grid);
                    background-size: var(--pixel-size);
                    pointer-events: none;
                    z-index: 0;
                    opacity: 0.3;
                }

                /* Сканлайны (пикселизация) */
                body::after {
                    content: "" "";
                    position: fixed;
                    inset: 0;
                    background: linear-gradient(rgba(18, 16, 16, 0) 50%, rgba(0, 0, 0, 0.15) 50%), 
                                linear-gradient(90deg, rgba(255, 0, 0, 0.03), rgba(0, 255, 0, 0.01), rgba(0, 0, 255, 0.03));
                    background-size: 100% 3px, 3px 100%;
                    z-index: 9999;
                    pointer-events: none;
                    opacity: 0.4;
                }

                .pixel-font {
                    font-family: 'Press Start 2P', cursive;
                }

                /* Glass Panel Container */
                .glass-panel {
                    background: rgba(10, 5, 5, 0.95);
                    border: 1px solid rgba(255, 0, 0, 0.15);
                    border-radius: 0px;
                    width: calc(100% - 16px);
                    height: calc(100% - 16px);
                    padding: 16px;
                    display: flex;
                    flex-direction: column;
                    justify-content: space-between;
                    position: relative;
                    z-index: 10;
                    box-shadow: 4px 4px 0px rgba(0, 0, 0, 0.8), inset 0 0 20px rgba(255, 0, 0, 0.02);
                }

                .glass-panel::before {
                    content: """";
                    position: absolute; inset: 0;
                    background-image: var(--pixel-grid);
                    background-size: var(--pixel-size);
                    pointer-events: none; opacity: 0.15;
                    z-index: 0;
                }

                /* Typography & Gradients */
                .title-gradient {
                    background: linear-gradient(135deg, #ffffff 0%, #a1a1aa 100%);
                    -webkit-background-clip: text;
                    -webkit-text-fill-color: transparent;
                    background-clip: text;
                }

                /* Progress Bar */
                .percentage {
                    font-family: 'JetBrains Mono', monospace;
                    font-size: 28px;
                    font-weight: 700;
                    color: #ffffff;
                    letter-spacing: -0.05em;
                    line-height: 1;
                }

                .progress-rail {
                    width: 100%;
                    height: 3px;
                    background: rgba(255, 255, 255, 0.08);
                    border-radius: 999px;
                    overflow: hidden;
                    position: relative;
                    margin-top: 8px;
                }

                .progress-fill {
                    width: 0%;
                    height: 100%;
                    background: linear-gradient(90deg, #990000, #ff0000);
                    border-radius: 999px;
                    transition: width 0.5s cubic-bezier(0.4, 0, 0.2, 1);
                    position: relative;
                    box-shadow: 0 0 10px rgba(255, 0, 0, 0.4);
                }
                
                .progress-fill::after {
                    content: '';
                    position: absolute;
                    top: 0; right: 0; bottom: 0; width: 30px;
                    background: linear-gradient(90deg, transparent, rgba(255,255,255,0.8));
                    border-radius: 999px;
                }

                /* Mini Tiles */
                .tiles-container {
                    display: flex;
                    flex-wrap: wrap;
                    gap: 6px;
                    justify-content: center;
                }

                .tile {
                    background: rgba(20, 5, 5, 0.8);
                    border: 1px solid rgba(255, 0, 0, 0.1);
                    border-radius: 0px;
                    padding: 8px 10px;
                    width: calc(50% - 3px); /* 2 per row for 4 tiles */
                    min-width: 120px;
                    display: flex;
                    flex-direction: column;
                    transition: all 0.3s ease;
                    position: relative;
                    overflow: hidden;
                }

                .tile::before {
                    content: '';
                    position: absolute;
                    top: 0; left: 0; width: 100%; height: 100%;
                    background: linear-gradient(180deg, rgba(255,255,255,0.03) 0%, transparent 100%);
                    opacity: 0;
                    transition: opacity 0.3s;
                }

                .tile.active {
                    background: rgba(255, 255, 255, 0.04);
                    border-color: rgba(255, 255, 255, 0.1);
                }
                .tile.active::before { opacity: 1; }

                .tile-header {
                    display: flex;
                    align-items: center;
                    gap: 6px;
                    margin-bottom: 4px;
                }

                .tile-icon {
                    font-size: 9px;
                    color: rgba(255, 255, 255, 0.3);
                    transition: color 0.3s;
                }

                .tile-title {
                    font-size: 8px;
                    font-weight: 600;
                    color: rgba(255, 255, 255, 0.4);
                    text-transform: uppercase;
                    letter-spacing: 0.05em;
                    transition: color 0.3s;
                }

                .tile-detail {
                    font-family: 'Inter', sans-serif;
                    font-size: 9px;
                    font-weight: 500;
                    color: rgba(255, 255, 255, 0.25);
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    transition: color 0.3s;
                    display: flex;
                    align-items: center;
                }

                .indicator {
                    width: 5px;
                    height: 5px;
                    border-radius: 50%;
                    background: rgba(255, 255, 255, 0.2);
                    display: inline-block;
                    margin-right: 5px;
                    transition: all 0.3s;
                }

                /* Status Colors */
                .tile.active .tile-title { color: rgba(255, 255, 255, 0.8); }
                .tile.active .tile-detail { color: rgba(255, 255, 255, 0.6); }
                
                .tile.green { border-color: rgba(16, 185, 129, 0.2); background: rgba(16, 185, 129, 0.03); }
                .tile.yellow { border-color: rgba(245, 158, 11, 0.2); background: rgba(245, 158, 11, 0.03); }
                .tile.red { border-color: rgba(239, 68, 68, 0.2); background: rgba(239, 68, 68, 0.03); }
                
                .tile.green .tile-icon { color: #10b981; }
                .tile.yellow .tile-icon { color: #f59e0b; }
                .tile.red .tile-icon { color: #ef4444; }

                .indicator.green { background: #10b981; box-shadow: 0 0 8px rgba(16,185,129,0.8); }
                .indicator.yellow { background: #f59e0b; box-shadow: 0 0 8px rgba(245,158,11,0.8); }
                .indicator.red { background: #ef4444; box-shadow: 0 0 8px rgba(239,68,68,0.8); }

                    i.fas, i.fa-solid, i.fab { text-shadow: 2px 2px 0px rgba(0,0,0,0.8); }`n            </style>
        </head>
        <body>

            <div class='glass-panel'>
                <!-- TOP ROW: Header -->
                <div class='flex justify-between items-start'>
                    <div>
                        <h1 class='text-sm pixel-font text-red-600 drop-shadow-md tracking-widest mt-0.5 relative z-10'>TRP ORCHESTRATOR</h1>
                        <div class='text-[8px] font-semibold text-white/30 tracking-widest uppercase mt-0.5'>System Initialization // Kernel Optimized</div>
                    </div>
                    <div class='text-[9px] font-bold text-white/20 px-2 py-0.5 bg-white/5  border border-white/5'>
                        STABLE
                    </div>
                </div>

                <!-- MIDDLE: Progress -->
                <div class='w-full px-2 mt-2 mb-3'>
                    <div class='flex justify-between items-end mb-1'>
                        <span class='text-[10px] font-medium text-white/50 tracking-wide' id='loading-status'>Booting sequence...</span>
                        <span id='percent' class='percentage pixel-font'>0%</span>
                    </div>
                    <div class='progress-rail'>
                        <div id='bar' class='progress-fill'></div>
                    </div>
                </div>

                <!-- BOTTOM: Mini Tiles -->
                <div class='tiles-container'>
                    <!-- 0 Security -->
                    <div class='tile' id='card0'>
                        <div class='tile-header'>
                            <i class='fas fa-shield-halved tile-icon'></i>
                            <div class='tile-title'>Security</div>
                        </div>
                        <div class='tile-detail'><span class='indicator' id='dot0'></span><span id='val0'>Waiting...</span></div>
                    </div>

                    <!-- 1 Library -->
                    <div class='tile' id='card1'>
                        <div class='tile-header'>
                            <i class='fas fa-microchip tile-icon'></i>
                            <div class='tile-title'>Library</div>
                        </div>
                        <div class='tile-detail'><span class='indicator' id='dot1'></span><span id='val1'>Waiting...</span></div>
                    </div>

                    <!-- 2 Cloud -->
                    <div class='tile' id='card2'>
                        <div class='tile-header'>
                            <i class='fas fa-cloud tile-icon'></i>
                            <div class='tile-title'>Cloud</div>
                        </div>
                        <div class='tile-detail'><span class='indicator' id='dot2'></span><span id='val2'>Waiting...</span></div>
                    </div>

                    <!-- 3 Storage -->
                    <div class='tile' id='card3'>
                        <div class='tile-header'>
                            <i class='fas fa-database tile-icon'></i>
                            <div class='tile-title'>Storage</div>
                        </div>
                        <div class='tile-detail'><span class='indicator' id='dot3'></span><span id='val3'>Waiting...</span></div>
                    </div>
                </div>
            </div>

            <script>
                function updateStatus(id, percent, detail, status) {
                    document.getElementById('percent').innerText = percent + '%';
                    document.getElementById('bar').style.width = percent + '%';
                    
                    if (id !== undefined && id >= 0 && id <= 3) {
                        const card = document.getElementById('card' + id);
                        const dot = document.getElementById('dot' + id);
                        const val = document.getElementById('val' + id);
                        
                        card.className = 'tile active ' + status; // add 'green', 'yellow', 'red'
                        dot.className = 'indicator ' + status;
                        val.innerText = detail;

                        document.getElementById('loading-status').innerText = detail + '...';
                    }
                    if (percent >= 100) {
                        document.getElementById('loading-status').innerText = 'System Ready';
                    }
                }
            </script>
        </body>
        </html>";

        public SplashWindow()
        {
            InitializeComponent();
            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            try
            {
                var env = await App.GetSharedEnvironmentAsync();
                await SplashBrowser.EnsureCoreWebView2Async(env);
                
                SplashBrowser.NavigateToString(SplashHtml);
                
                // Wait for UI to render
                await Task.Delay(100);
                
                await RunDiagnosticsAsync();
            }
            catch (Exception ex)
            {
                App.ShowFatalError(ex, "SplashWindow Initialization");
                LocalFallback.Visibility = Visibility.Visible;
            }
        }

        private async Task RunDiagnosticsAsync()
        {
            var system = new Services.SystemService();
            
            // 1. Security Check
            await UpdateUi(0, 15, "Initializing...", "yellow");
            var admin = system.CheckAdmin();
            await Task.Delay(150);
            await UpdateUi(0, 25, admin.IsOk ? "ADMIN OK" : "USER ACCESS", admin.IsOk ? "green" : "yellow");

            // 2. Library Check
            await UpdateUi(1, 40, "Loading .NET...", "yellow");
            var env_sys = system.CheckEnvironment();
            await Task.Delay(150);
            await UpdateUi(1, 50, ".NET 10 OK", "green");

            // 3. Cloud Check
            await UpdateUi(2, 60, "Syncing...", "yellow");
            var cloud = system.CheckInternet();
            await Task.Delay(150);
            await UpdateUi(2, 75, cloud.IsOk ? "CLOUD LINK" : "OFFLINE", cloud.IsOk ? "green" : "red");

            // 4. Storage Check
            await UpdateUi(3, 85, "I/O Scan...", "yellow");
            var storage = system.CheckDiskSpace();
            await Task.Delay(150);
            await UpdateUi(3, 100, storage.IsOk ? "STORAGE OK" : "LOW SPACE", storage.IsOk ? "green" : "yellow");

            await Task.Delay(300);
            this.DialogResult = true;
            this.Close();
        }

        private async Task UpdateUi(int id, int percent, string detail, string status)
        {
            string js = $"updateStatus({id}, {percent}, '{detail}', '{status}')";
            await SplashBrowser.CoreWebView2.ExecuteScriptAsync(js);
        }
    }
}
