using leituraWPF.Models;
using leituraWPF.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
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
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _disposed;

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

                    // Thread-safe UI update
                    if (Dispatcher.CheckAccess())
                        TxtSummary.Text = value;
                    else
                        Dispatcher.BeginInvoke(() => TxtSummary.Text = value);
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
            _cancellationTokenSource = new CancellationTokenSource();

            _backup.CountersChanged += (pend, sent) =>
            {
                Dispatcher.Invoke(() =>
                {
                    long total = pend + sent;
                    long percent = total > 0 ? (sent * 100 / total) : 0;
                    if (StatusBorder != null)
                        StatusBorder.Visibility = Visibility.Visible;
                    if (TxtBackupProgress != null)
                        TxtBackupProgress.Text = $"Backup: {sent}/{total} ({percent}%)";
                });
            };

            Dispatcher.Invoke(() =>
            {
                long total = _backup.PendingCount + _backup.UploadedCountSession;
                long percent = total > 0 ? (_backup.UploadedCountSession * 100 / total) : 0;
                StatusBorder.Visibility = Visibility.Visible;
                TxtBackupProgress.Text = $"Backup: {_backup.UploadedCountSession}/{total} ({percent}%)";
            });

        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_disposed) return;

            try
            {
                // garante que o serviço de backup esteja ativo também na tela de login
                _backup.Start();

                BtnLogin.IsEnabled = false;
                StatusMessage = "Carregando...";

                var statusBorder = FindName("StatusBorder") as FrameworkElement;
                if (statusBorder != null)
                    statusBorder.Visibility = Visibility.Visible;

                await AnimateWindowEntrance();

                // Executar operações em paralelo quando possível
                var loadFuncTask = LoadFuncionariosAsync();
                var loadStatsTask = LoadStatsAsync();

                await Task.WhenAll(loadFuncTask, loadStatsTask);

                TxtMatricula.Focus();
            }
            catch (Exception ex)
            {
                StatusMessage = "Erro na inicialização";
                System.Diagnostics.Debug.WriteLine($"Erro Window_Loaded: {ex.Message}");
            }
        }

        private async Task LoadFuncionariosAsync()
        {
            var ct = _cancellationTokenSource?.Token ?? CancellationToken.None;

            try
            {
                IsLoading = true;
                BtnLogin.IsEnabled = false;

                var baseDir = AppContext.BaseDirectory;
                var jsonPath = Path.Combine(baseDir, "funcionarios.json");

                // Tentar atualizar sempre que houver internet disponível
                if (NetworkInterface.GetIsNetworkAvailable())
                {
                    try
                    {
                        // Timeout curto para não travar a interface
                        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        downloadCts.CancelAfter(TimeSpan.FromSeconds(10));

                        await _funcService.DownloadJsonAsync(baseDir, downloadCts.Token);
                    }
                    catch
                    {
                        // Sem internet ou falha de download – continuar com arquivo local
                    }
                }

                if (!File.Exists(jsonPath) && !ct.IsCancellationRequested)
                {
                    System.Windows.MessageBox.Show("Arquivo de funcionários não encontrado. Apenas acesso administrativo disponível.",
                                   "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                _funcionarios = await _funcService.LoadFuncionariosAsync(jsonPath, ct);

                if (_funcionarios.Count == 0 && !ct.IsCancellationRequested)
                {
                    StatusMessage = "Nenhum funcionário carregado";
                }
            }
            catch (OperationCanceledException)
            {
                // Normal quando cancelado
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    StatusMessage = "Erro ao carregar funcionários";
                    System.Diagnostics.Debug.WriteLine($"Erro LoadFuncionarios: {ex.Message}");
                }
            }
            finally
            {
                IsLoading = false;
                BtnLogin.IsEnabled = true;
            }
        }

        private async Task LoadStatsAsync()
        {
            var ct = _cancellationTokenSource?.Token ?? CancellationToken.None;

            try
            {
                // Operação rápida - não precisa de IsLoading
                var stats = await Task.Run(() => SyncStatsService.Load(), ct);

                if (!ct.IsCancellationRequested)
                {
                    StatusMessage = $"Enviados: {stats.Uploaded}";

                    var statusBorder = FindName("StatusBorder") as FrameworkElement;
                    if (statusBorder != null)
                        statusBorder.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException)
            {
                // Normal quando cancelado
            }
            catch
            {
                // Falha silenciosa - stats não são críticas
                if (!ct.IsCancellationRequested)
                {
                    StatusMessage = "Pronto";

                    var statusBorder = FindName("StatusBorder") as FrameworkElement;
                    if (statusBorder != null)
                        statusBorder.Visibility = Visibility.Visible;
                }
            }
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            await ProcessLoginAsync();
        }

        private async Task ProcessLoginAsync()
        {
            if (IsLoading || _disposed) return;

            var ct = _cancellationTokenSource?.Token ?? CancellationToken.None;
            if (ct.IsCancellationRequested) return;

            try
            {
                IsLoading = true;
                BtnLogin.IsEnabled = false;
                StatusMessage = "Verificando...";

                var matricula = (TxtMatricula.Text ?? string.Empty).Trim().TrimStart('0');

                if (string.IsNullOrWhiteSpace(matricula))
                {
                    await ShowErrorMessageAsync("Digite sua matrícula");
                    return;
                }

                // Pequeno delay para UX, mas cancelável
                try
                {
                    await Task.Delay(300, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_funcionarios.TryGetValue(matricula, out var func))
                {
                    FuncionarioLogado = func;
                    StatusMessage = $"Bem-vindo, {func.Nome}!";

                    try
                    {
                        await Task.Delay(500, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (!ct.IsCancellationRequested)
                    {
                        DialogResult = true;
                        Close();
                    }
                }
                else
                {
                    await ShowErrorMessageAsync("Matrícula não encontrada");
                    if (!ct.IsCancellationRequested)
                    {
                        TxtMatricula.SelectAll();
                        TxtMatricula.Focus();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal quando cancelado
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    await ShowErrorMessageAsync("Erro no login");
                    System.Diagnostics.Debug.WriteLine($"Erro ProcessLogin: {ex.Message}");
                }
            }
            finally
            {
                IsLoading = false;
                BtnLogin.IsEnabled = true;
            }
        }

        private async Task ShowErrorMessageAsync(string message)
        {
            var ct = _cancellationTokenSource?.Token ?? CancellationToken.None;
            if (ct.IsCancellationRequested) return;

            StatusMessage = message;

            try
            {
                // Animação de shake mais rápida
                await AnimateShake(TxtMatricula);

                // Timeout mais curto para limpar mensagem
                await Task.Delay(2000, ct);

                if (StatusMessage == message && !ct.IsCancellationRequested)
                {
                    try
                    {
                        var stats = SyncStatsService.Load();
                        StatusMessage = $"Enviados: {stats.Uploaded}";
                    }
                    catch
                    {
                        StatusMessage = "Pronto";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal quando cancelado
            }
        }

        private async Task AnimateWindowEntrance()
        {
            var ct = _cancellationTokenSource?.Token ?? CancellationToken.None;
            if (ct.IsCancellationRequested) return;

            try
            {
                Opacity = 0;
                var originalTop = Top;
                Top += 20;

                var opacityAnimation = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                var positionAnimation = new DoubleAnimation(originalTop, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
                };

                BeginAnimation(OpacityProperty, opacityAnimation);
                BeginAnimation(TopProperty, positionAnimation);

                await Task.Delay(300, ct);
            }
            catch (OperationCanceledException)
            {
                // Completar animação imediatamente se cancelado
                Opacity = 1;
            }
        }

        private async Task AnimateShake(FrameworkElement element)
        {
            var ct = _cancellationTokenSource?.Token ?? CancellationToken.None;
            if (ct.IsCancellationRequested) return;

            try
            {
                var originalMargin = element.Margin;
                var shakeStoryboard = new Storyboard();
                var shakeAnimation = new ThicknessAnimationUsingKeyFrames();

                // Animação mais simples e rápida
                shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(originalMargin, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(
                    new Thickness(originalMargin.Left - 4, originalMargin.Top, originalMargin.Right + 4, originalMargin.Bottom),
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
                shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(
                    new Thickness(originalMargin.Left + 4, originalMargin.Top, originalMargin.Right - 4, originalMargin.Bottom),
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
                shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(
                    new Thickness(originalMargin.Left - 2, originalMargin.Top, originalMargin.Right + 2, originalMargin.Bottom),
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
                shakeAnimation.KeyFrames.Add(new LinearThicknessKeyFrame(originalMargin,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));

                Storyboard.SetTarget(shakeAnimation, element);
                Storyboard.SetTargetProperty(shakeAnimation, new PropertyPath(FrameworkElement.MarginProperty));

                shakeStoryboard.Children.Add(shakeAnimation);
                shakeStoryboard.Begin();

                await Task.Delay(160, ct);
            }
            catch (OperationCanceledException)
            {
                // Normal quando cancelado
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed && !_disposed)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // Ignorar - janela não pode ser movida no momento
                }
            }
        }

        private async void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            await CloseWindowAsync();
        }

        private async Task CloseWindowAsync()
        {
            if (_disposed) return;

            try
            {
                await AnimateWindowExit();
            }
            catch
            {
                // Ignorar erros de animação
            }
            finally
            {
                Close();
            }
        }

        private async Task AnimateWindowExit()
        {
            var ct = _cancellationTokenSource?.Token ?? CancellationToken.None;

            try
            {
                var opacityAnimation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };

                var positionAnimation = new DoubleAnimation(Top + 10, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };

                BeginAnimation(OpacityProperty, opacityAnimation);
                BeginAnimation(TopProperty, positionAnimation);

                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
                // Completar animação imediatamente
                Opacity = 0;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_disposed)
            {
                e.Handled = true;
                return;
            }

            try
            {
                if (e.Key == Key.Enter && !IsLoading)
                {
                    _ = ProcessLoginAsync(); // Manter contexto da UI
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    _ = CloseWindowAsync(); // Manter contexto da UI
                    e.Handled = true;
                }

                base.OnKeyDown(e);
            }
            catch
            {
                e.Handled = true;
            }
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            try
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
            catch
            {
                // Ignorar erros de notificação
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_disposed)
            {
                _cancellationTokenSource?.Cancel();
                _disposed = true;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                if (!_disposed)
                {
                    _cancellationTokenSource?.Cancel();
                    _disposed = true;
                }

                PropertyChanged = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
            catch
            {
                // Ignorar erros de cleanup
            }
            finally
            {
                base.OnClosed(e);
            }
        }
    }
}