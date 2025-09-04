using System.Windows;

namespace leituraWPF.Views
{
    public partial class UpdateProgressWindow : Window
    {
        public UpdateProgressWindow()
        {
            InitializeComponent();
        }

        public void SetStatus(string message, double? progress = null)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = message;
                if (progress.HasValue)
                {
                    ProgressBar.IsIndeterminate = false;
                    ProgressBar.Value = progress.Value;
                }
                else
                {
                    ProgressBar.IsIndeterminate = true;
                }
            });
        }
    }
}

