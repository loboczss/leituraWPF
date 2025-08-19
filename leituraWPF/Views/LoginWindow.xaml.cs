using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace leituraWPF.Views
{
    public partial class LoginWindow : Window
    {
        private readonly string _csvPath;

        public LoginWindow(string csvPath)
        {
            InitializeComponent();
            _csvPath = csvPath;
        }

        private void BtnEntrar_Click(object sender, RoutedEventArgs e)
        {
            var matricula = TxtMatricula.Text.Trim();
            if (string.IsNullOrWhiteSpace(matricula))
            {
                MessageBox.Show("Informe a matrícula.", "Atenção", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(_csvPath))
            {
                MessageBox.Show("Arquivo de funcionários não encontrado.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            bool encontrado = File.ReadLines(_csvPath)
                                   .Where(l => !string.IsNullOrWhiteSpace(l) && char.IsDigit(l[0]))
                                   .Select(l => l.Split(',')[0].Trim('\"'))
                                   .Any(code => string.Equals(code, matricula, StringComparison.OrdinalIgnoreCase));

            if (encontrado)
            {
                var main = new MainWindow();
                Application.Current.MainWindow = main;
                main.Show();
                Close();
            }
            else
            {
                MessageBox.Show("Matrícula não encontrada.", "Atenção", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
