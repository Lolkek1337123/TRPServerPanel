using System;
using System.Windows;

namespace TRPServerPanel.Views
{
    public partial class CrashReportWindow : Window
    {
        private string _rawDetails;

        public CrashReportWindow(string brief, string details)
        {
            InitializeComponent();
            ShortMessage.Text = brief;
            FullTrace.Text = details;
            _rawDetails = details;
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(_rawDetails);
                System.Windows.MessageBox.Show("Error details copied to clipboard.", "SUCCESS", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
