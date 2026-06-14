using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace TRPServerPanel.Views
{
    public partial class UModLoginWindow : Window
    {
        public bool IsSuccess { get; private set; }

        public UModLoginWindow()
        {
            InitializeComponent();
            _ = InitializeWebViewAsync();
        }

        private async System.Threading.Tasks.Task InitializeWebViewAsync()
        {
            try
            {
                var env = await App.GetSharedEnvironmentAsync();
                await LoginWebView.EnsureCoreWebView2Async(env);
                
                LoginWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                LoginWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                
                LoginWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                
                // Navigate to uMod login
                LoginWebView.CoreWebView2.Navigate("https://umod.org/login");
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка инициализации: {ex.Message}";
            }
        }

        private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (LoginWebView.CoreWebView2 == null) return;
            
            var url = LoginWebView.CoreWebView2.Source;
            if (url.Contains("umod.org") && !url.Contains("/login") && !url.Contains("steamcommunity.com"))
            {
                StatusText.Text = "Авторизация успешно выполнена! Нажмите 'Сохранить куки и закрыть'.";
                SaveButton.IsEnabled = true;
            }
            else
            {
                StatusText.Text = "Выполните вход через Steam...";
                SaveButton.IsEnabled = false;
            }
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (LoginWebView.CoreWebView2 == null) return;

            try
            {
                StatusText.Text = "Сохранение сессии...";
                var cookieManager = LoginWebView.CoreWebView2.CookieManager;
                var rawCookies = await cookieManager.GetCookiesAsync("https://umod.org");
                
                var cookies = new List<SavedCookie>();
                foreach (var rc in rawCookies)
                {
                    cookies.Add(new SavedCookie
                    {
                        Name = rc.Name,
                        Value = rc.Value,
                        Domain = rc.Domain,
                        Path = rc.Path
                    });
                }

                var appDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppData");
                if (!Directory.Exists(appDataDir))
                {
                    Directory.CreateDirectory(appDataDir);
                }

                var cookiePath = Path.Combine(appDataDir, "umod_cookies.json");
                var json = JsonSerializer.Serialize(cookies, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(cookiePath, json);

                IsSuccess = true;
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка сохранения куки: {ex.Message}";
            }
        }
    }

    public class SavedCookie
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Path { get; set; } = "";
    }
}
