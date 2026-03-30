using System.Windows;

namespace VideoPlayer
{
    public partial class OpenUrlDialog : Window
    {
        public string Url => UrlTextBox.Text.Trim();

        public OpenUrlDialog()
        {
            InitializeComponent();
            UrlTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                MessageBox.Show("Please enter a URL.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
