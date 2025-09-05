using System;
using System.Windows;
using System.Windows.Threading;

namespace leituraWPF
{
    public partial class UpdatePromptWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private readonly int _totalSeconds;
        private int _seconds;

        public UpdatePromptWindow(Version local, Version remote, int timeoutSeconds = 30)
        {
            InitializeComponent();

            LblLocalVer.Text  = local?.ToString()  ?? "-";
            LblRemoteVer.Text = remote?.ToString() ?? "-";

            _seconds = Math.Max(5, timeoutSeconds); // pequena proteção
            _totalSeconds = _seconds;
            AutoProgressBar.Maximum = _totalSeconds;
            UpdateCountdownText();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) =>
            {
                _seconds--;
                if (_seconds <= 0)
                {
                    _timer.Stop();
                    // Auto-confirma a atualização
                    DialogResult = true; // TRUE = atualizar
                    Close();
                    return;
                }
                UpdateCountdownText();
            };
            _timer.Start();
        }

        private void UpdateCountdownText()
        {
            TxtAuto.Text = $"A atualização iniciará automaticamente em {_seconds}s...";
            AutoProgressBar.Value = _totalSeconds - _seconds;
        }

        private void BtnNow_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            DialogResult = true; // atualizar agora
            Close();
        }

        private void BtnLater_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            DialogResult = false; // não atualizar
            Close();
        }
    }
}
