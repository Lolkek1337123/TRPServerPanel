using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TRPServerPanel.Views
{
    public partial class WebView2InstallerWindow : Window
    {
        private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public WebView2InstallerWindow()
        {
            InitializeComponent();
            Loaded += WebView2InstallerWindow_Loaded;
        }

        private async void WebView2InstallerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await StartInstallationAsync();
            }
            catch (OperationCanceledException)
            {
                System.Windows.MessageBox.Show("Установка была отменена пользователем.", "Отмена", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                DialogResult = false;
                Close();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Ошибка при установке WebView2: {ex.Message}", "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                DialogResult = false;
                Close();
            }
        }

        private async Task StartInstallationAsync()
        {
            // 1. Check Internet
            StatusText.Text = "Проверка интернет-соединения...";
            InstallProgressBar.Value = 5;
            
            // 2. Download Bootstrapper
            string tempDir = Path.GetTempPath();
            string tempFile = Path.Combine(tempDir, "MicrosoftEdgeWebview2Setup.exe");
            
            StatusText.Text = "Загрузка установщика WebView2 Runtime...";
            
            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromMinutes(3);
                using (var response = await httpClient.GetAsync(BootstrapperUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token))
                {
                    response.EnsureSuccessStatusCode();
                    
                    long? totalBytes = response.Content.Headers.ContentLength;
                    
                    using (var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token))
                    using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int read;
                        
                        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, read, _cts.Token);
                            totalRead += read;
                            
                            if (totalBytes.HasValue)
                            {
                                double progress = (double)totalRead / totalBytes.Value * 100.0;
                                // Limit progress bar to 80% for download phase
                                Dispatcher.Invoke(() => InstallProgressBar.Value = 5 + (progress * 0.75));
                            }
                        }
                    }
                }
            }
            
            // 3. Start Install
            StatusText.Text = "Установка компонентов WebView2 Runtime (пожалуйста, разрешите установку в окне UAC)...";
            Dispatcher.Invoke(() => InstallProgressBar.Value = 85);
            
            var startInfo = new ProcessStartInfo
            {
                FileName = tempFile,
                Arguments = "/silent /install",
                UseShellExecute = true,
                Verb = "runas"
            };
            
            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        await process.WaitForExitAsync(_cts.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Не удалось запустить установщик. Возможно, был отклонен запрос UAC или запуск заблокирован. Подробности: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
            
            StatusText.Text = "Проверка установленных компонентов...";
            Dispatcher.Invoke(() => InstallProgressBar.Value = 95);
            await Task.Delay(1000);
            
            if (App.IsWebView2RuntimeInstalled())
            {
                StatusText.Text = "Установка успешно завершена!";
                Dispatcher.Invoke(() => InstallProgressBar.Value = 100);
                await Task.Delay(500);
                DialogResult = true;
                Close();
            }
            else
            {
                throw new Exception("Установка завершена, но компоненты WebView2 по-прежнему не обнаружены.");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts.Cancel();
            DialogResult = false;
            Close();
        }
    }
}
