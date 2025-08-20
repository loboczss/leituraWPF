using leituraWPF.Models;
using leituraWPF.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using Key = System.Windows.Input.Key;

namespace leituraWPF
{
    public partial class LoginWindow : Window, INotifyPropertyChanged
    {
        private readonly FuncionarioService _funcService;
        private readonly BackupUploaderService _backup;
        private IDictionary<string, Funcionario> _funcionarios = new Dictionary<string, Funcionario>();
        private bool _isLoading;
        private string _statusMessage = string.Empty;

        public Funcionario? FuncionarioLogado { get; private set; }

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged(nameof(IsLoading));
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    OnPropertyChanged(nameof(StatusMessage));
                    TxtSummary.Text = value;
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public LoginWindow(FuncionarioService funcService, BackupUploaderService backup)
        {
            InitializeComponent();
            _funcService = funcService ?? throw new ArgumentNullException(nameof(funcService));
            _backup = backup ?? throw new ArgumentNullException(nameof(backup));
            DataContext = this;

            _backup.CountersChanged += (pend, sent) =>
            {
                Dispatcher.Invoke(() =>
                {
                    long total = pend + sent;
                    long percent = total > 0 ? (sent * 100 / total) : 0;
                    if (StatusBorder != null)
                        StatusBorder.Visibility = Visibility.Visible;
                    if (TxtBackupStatus != null)
                        TxtBackupStatus.Text = $"Backup: {sent}/{total} ({percent}%)";
                });
            };

            Dispatcher.Invoke(() =>
            {
                long total = _backup.PendingCount + _backup.UploadedCountSession;
                long percent = total > 0 ? (_backup.UploadedCountSession * 100 / total) : 0;
                StatusBorder.Visibility = Visibility.Visible;
                TxtBackupStatus.Text = $"Backup: {_backup.UploadedCountSession}/{total} ({percent}%)";
            });
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            BtnLogin.IsEnabled = false;
            StatusMessage = "Carregando funcionários...";
            var statusBorder = FindName("StatusBorder") as FrameworkElement;
            if (statusBorder != null)
                statusBorder.Visibility = Visibility.Visible;

            await AnimateWindowEntrance();
            await LoadFuncionariosAsync();
            await LoadStatsAsync();
            TxtMatricula.Focus();
        }

        private async Task LoadFuncionariosAsync()
        {
            try
            {
                IsLoading = true;
                BtnLogin.IsEnabled = false;

                var baseDir = AppContext.BaseDirectory;
                try
                {
                    await _funcService.DownloadCsvAsync(baseDir);
                }
                catch
                {
                    // ignorado: falha de rede
                }

                var csvPath = Path.Combine(baseDir, "funcionarios.csv");
                if (!File.Exists(csvPath))
                {
                    System.Windows.MessageBox.Show("Arquivo de funcionários não disponível e não foi possível baixá-lo.",
                                    "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    Close();
                    return;
                }

                _funcionarios = await _funcService.LoadFuncionariosAsync(csvPath);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Erro ao carregar funcionários: {ex.Message}",
                                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
            finally
            {
                IsLoading = false;
                BtnLogin.IsEnabled = true;
            }
        }

        private async Task LoadStatsAsync()
        {
            try
            {
                IsLoading = true;

                // Simula operação assíncrona se necessário
                await Task.Run(() =>
                {
                    var stats = SyncStatsService.Load();
                    Dispatcher.Invoke(() =>
                    {
                        StatusMessage = $"Enviados: {stats.Uploaded} | Baixados: {stats.Downloaded}";
                        // Mostra o status border
                        var statusBorder = FindName("StatusBorder") as FrameworkElement;
                        if (statusBorder != null)
                            statusBorder.Visibility = Visibility.Visible;
                    });
                });
            }
            catch (Exception ex)
            {
                StatusMessage = "Erro ao carregar estatísticas";
                // Log do erro se necessário
                System.Diagnostics.Debug.WriteLine($"Erro ao carregar stats: {ex.Message}");

                // Mostra o status border mesmo com erro
                var statusBorder = FindName("StatusBorder") as FrameworkElement;
                if (statusBorder != null)
                    statusBorder.Visibility = Visibility.Visible;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            await ProcessLoginAsync(sender);
        }

        private async Task ProcessLoginAsync(object sender = null)
        {
            if (IsLoading) return;

            try
            {
                IsLoading = true;
                StatusMessage = "Verificando matrícula...";

                // Desabilita o botão temporariamente
                var button = sender as System.Windows.Controls.Button;
                if (button != null) button.IsEnabled = false;

                // Remove zeros à esquerda da matrícula digitada
                var matricula = (TxtMatricula.Text ?? string.Empty).Trim().TrimStart('0');

                if (string.IsNullOrWhiteSpace(matricula))
                {
                    await ShowErrorMessageAsync("Por favor, digite sua matrícula.");
                    return;
                }

                // Simula verificação assíncrona (pode ser útil para verificação em BD)
                await Task.Delay(500); // Pequeno delay para mostrar o loading

                if (_funcionarios.TryGetValue(matricula, out var func))
                {
                    FuncionarioLogado = func;
                    StatusMessage = $"Bem-vindo, {func.Nome}!";

                    // Pequeno delay para mostrar a mensagem de sucesso
                    await Task.Delay(800);

                    DialogResult = true;
                    Close();
                }
                else
                {
                    await ShowErrorMessageAsync("Matrícula não encontrada.");
                    TxtMatricula.SelectAll();
                    TxtMatricula.Focus();
                }
            }
            catch (Exception ex)
            {
                await ShowErrorMessageAsync($"Erro durante o login: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
                // Reabilita o botão
                var button = sender as System.Windows.Controls.Button;
                if (button != null) button.IsEnabled = true;
            }
        }

        private async Task ShowErrorMessageAsync(string message)
        {
            StatusMessage = message;

            // Animação de shake no campo de matrícula
            await AnimateShake(TxtMatricula);

            // Limpa a mensagem após alguns segundos
            await Task.Delay(3000);
            if (StatusMessage == message) // Só limpa se não mudou
            {
                var stats = SyncStatsService.Load();
                StatusMessage = $"Enviados: {stats.Uploaded} | Baixados: {stats.Downloaded}";
            }
        }

        private async Task AnimateWindowEntrance()
        {
            // Para Window, apenas animamos a opacidade e posição
            Opacity = 0;

            // Move a janela ligeiramente para baixo
            var originalTop = Top;
            Top += 20;

            var opacityAnimation = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var positionAnimation = new DoubleAnimation(originalTop, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
            };

            BeginAnimation(OpacityProperty, opacityAnimation);
            BeginAnimation(TopProperty, positionAnimation);

            await Task.Delay(400);
        }

        private async Task AnimateShake(FrameworkElement element)
        {
            var originalMargin = element.Margin;

            // Animação de shake usando mudanças na margem
            var shakeStoryboard = new Storyboard();
            var shakeAnimation = new ThicknessAnimationUsingKeyFrames();

            shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(originalMargin, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
            shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(originalMargin.Left - 5, originalMargin.Top, originalMargin.Right + 5, originalMargin.Bottom), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
            shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(originalMargin.Left + 5, originalMargin.Top, originalMargin.Right - 5, originalMargin.Bottom), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
            shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(originalMargin.Left - 3, originalMargin.Top, originalMargin.Right + 3, originalMargin.Bottom), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
            shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(originalMargin.Left + 3, originalMargin.Top, originalMargin.Right - 3, originalMargin.Bottom), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
            shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(originalMargin, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));

            Storyboard.SetTarget(shakeAnimation, element);
            Storyboard.SetTargetProperty(shakeAnimation, new PropertyPath(FrameworkElement.MarginProperty));

            shakeStoryboard.Children.Add(shakeAnimation);
            shakeStoryboard.Begin();

            await Task.Delay(250);
        }

        // Método para permitir arrastar a janela
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // Ignora exceção se a janela não puder ser movida
                }
            }
        }

        // Método para fechar a janela com animação
        private async void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            await AnimateWindowExit();
            Close();
        }

        private async Task AnimateWindowExit()
        {
            var opacityAnimation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var positionAnimation = new DoubleAnimation(Top + 15, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            BeginAnimation(OpacityProperty, opacityAnimation);
            BeginAnimation(TopProperty, positionAnimation);

            await Task.Delay(250);
        }

        // Sobrescrever método KeyDown para suportar Enter e Escape
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !IsLoading)
            {
                _ = ProcessLoginAsync(); // Sem sender quando chamado pelo teclado
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _ = AnimateWindowExit();
                Close();
                e.Handled = true;
            }

            base.OnKeyDown(e);
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Cleanup
        protected override void OnClosed(EventArgs e)
        {
            PropertyChanged = null;
            base.OnClosed(e);
        }
    }
}