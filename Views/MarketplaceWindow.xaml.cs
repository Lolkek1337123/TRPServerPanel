using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace TRPServerPanel.Views
{
    public partial class MarketplaceWindow : Window
    {
        public MarketplaceWindow()
        {
            InitializeComponent();
            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            try
            {
                await MarketBrowser.EnsureCoreWebView2Async(null);
                // Custom User Agent if needed
                MarketBrowser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Ошибка инициализации WebView2: " + ex.Message);
            }
        }

        private void MarketBrowser_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        private void MarketBrowser_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    }
}
