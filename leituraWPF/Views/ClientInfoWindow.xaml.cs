using leituraWPF.Models;
using System.Windows;

namespace leituraWPF
{
    public partial class ClientInfoWindow : Window
    {
        public ClientInfoWindow(ClientRecord record)
        {
            InitializeComponent();
            DataContext = record;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
