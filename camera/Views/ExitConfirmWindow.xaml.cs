using System.Windows;

namespace camera.Views
{
    public partial class ExitConfirmWindow : Window
    {
        public enum ExitChoice
        {
            Cancel,
            MinimizeToTray,
            ExitCompletely
        }

        public ExitChoice Choice { get; private set; } = ExitChoice.Cancel;
        public bool RememberChoice => RememberChoiceCheckBox.IsChecked == true;

        public ExitConfirmWindow()
        {
            InitializeComponent();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExitChoice.MinimizeToTray;
            DialogResult = true;
            Close();
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExitChoice.ExitCompletely;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExitChoice.Cancel;
            DialogResult = false;
            Close();
        }
    }
}
