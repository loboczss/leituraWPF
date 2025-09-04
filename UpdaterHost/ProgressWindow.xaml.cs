using System.Windows;

namespace UpdaterHost
{
    public partial class ProgressWindow : Window
    {
        public ProgressWindow()
        {
            InitializeComponent();
        }

        public void SetStatus(string message)
        {
            StatusText.Text = message;
        }
    }
}

