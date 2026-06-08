using System.Windows;
using System.Windows.Input;

namespace TRPServerPanel.Views
{
    public partial class InstallWindow : Window
    {
        private int _currentStep = 1;

        public InstallWindow()
        {
            InitializeComponent();
        }

        private void Header_Border_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left) this.DragMove();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 1)
            {
                Step1.Visibility = Visibility.Collapsed;
                Step2.Visibility = Visibility.Visible;
                NextButton.Content = "ЗАВЕРШИТЬ";
                _currentStep = 2;
            }
            else
            {
                this.Close();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
