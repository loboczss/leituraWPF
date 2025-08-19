using leituraWPF.Models;
using leituraWPF.Services;
using System;
using System.Collections.Generic;
using System.Windows;

namespace leituraWPF
{
    public partial class LoginWindow : Window
    {
        private readonly IDictionary<string, Funcionario> _funcionarios;

        public Funcionario? FuncionarioLogado { get; private set; }

        public LoginWindow(IDictionary<string, Funcionario> funcionarios)
        {
            InitializeComponent();
            _funcionarios = funcionarios ?? throw new ArgumentNullException(nameof(funcionarios));
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var stats = SyncStatsService.Load();
            TxtSummary.Text = $"Enviados: {stats.Uploaded} | Baixados: {stats.Downloaded}";
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            var matricula = (TxtMatricula.Text ?? string.Empty).Trim();
            if (_funcionarios.TryGetValue(matricula, out var func))
            {
                FuncionarioLogado = func;
                DialogResult = true;
                Close();
            }
            else
            {
                System.Windows.MessageBox.Show("Matrícula não encontrada.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
