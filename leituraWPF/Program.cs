using leituraWPF.Services;
using leituraWPF.Utils;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

// Aliases para tirar ambiguidade de Application/Window
using WpfApp = System.Windows.Application;
using WpfWindow = System.Windows.Window;

namespace leituraWPF
{
    public static class Program
    {
        public static AppConfig Config { get; private set; } = new();

        [STAThread]
        public static void Main()
        {
            // Single instance com timeout para evitar travamentos
            using var mutex = new Mutex(true, "leituraWPF_SINGLE_INSTANCE", out bool created);
            if (!created)
            {
                HandleExistingInstance();
                return;
            }

            try
            {
                RunApplication();
            }
            catch (Exception ex)
            {
                LogFatalError(ex);
                System.Windows.MessageBox.Show("Erro crítico na inicialização. Verifique os logs.",
                               "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void HandleExistingInstance()
        {
            try
            {
                // Sinaliza a instância existente para mostrar a janela principal
                using var evt = EventWaitHandle.OpenExisting("leituraWPF_SHOW_EVENT");
                evt.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Fallback para método nativo
            }
            catch
            {
                // Ignorar outros erros de handle
            }

            // Fallback: tentar ativar janela existente via Win32
            try
            {
                var current = Process.GetCurrentProcess();
                var existing = Process.GetProcessesByName(current.ProcessName)
                                     .Where(p => p.Id != current.Id)
                                     .FirstOrDefault();

                if (existing?.MainWindowHandle != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(existing.MainWindowHandle, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(existing.MainWindowHandle);
                }
            }
            catch
            {
                // Ignorar erros de Win32
            }
        }

        private static void RunApplication()
        {
            // Configurar startup antes de tudo
            try
            {
                StartupService.ConfigureStartup();
            }
            catch
            {
                // Não crítico - continuar sem startup automático
            }

            // Carregar configuração
            LoadConfiguration();

            // Inicializar serviços core
            var tokenService = new TokenService(Config);
            var funcService = new FuncionarioService(Config, tokenService);

            BackupUploaderService? backup = null;
            UpdatePoller? updatePoller = null;
            TrayService? tray = null;
            EventWaitHandle? showEvent = null;
            CancellationTokenSource? appCts = null;

            try
            {
                // Inicializar backup service
                backup = new BackupUploaderService(Config, tokenService);

                // Carregar pendentes em background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await backup.LoadPendingFromBaseDirsAsync();
                        backup.Start();
                    }
                    catch
                    {
                        // Falha silenciosa - backup não é crítico
                    }
                });

                // Criar aplicação WPF
                var app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                // Token para cancelamento global
                appCts = new CancellationTokenSource();

                // Mostrar login
                var login = new LoginWindow(funcService, backup);

                // Configurar poller antes do login
                updatePoller = CreateUpdatePoller(login);

                // Processo de login
                var loginResult = login.ShowDialog();

                if (loginResult != true || login.FuncionarioLogado == null)
                {
                    // Login cancelado - cleanup e exit
                    return;
                }

                // Login bem-sucedido - inicializar janela principal
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;

                var main = new MainWindow(login.FuncionarioLogado, backup);
                app.MainWindow = main;

                // Configurar sistema de mostrar janela
                showEvent = SetupShowWindowSystem(main, app);

                // Configurar tray
                tray = new TrayService(
                    showWindow: () => ShowMainWindow(main, app),
                    sync: () => SafeExecute(() => main.RunManualSync()),
                    exit: () => SafeExecute(() => app.Dispatcher.BeginInvoke(() => main.ForceClose()))
                );

                // Mostrar janela e executar aplicação
                main.Show();
                app.Run();
            }
            finally
            {
                // Cleanup de recursos
                SafeDispose(appCts);
                SafeDispose(tray);
                SafeDispose(updatePoller);
                SafeDispose(backup);
                SafeDispose(showEvent);
            }
        }

        private static void LoadConfiguration()
        {
            try
            {
                string? json = null;
                var url = "https://github.com/loboczss/leituraWPF/releases/download/v1.0.0.0/appsettings.json";

                try
                {
                    using var http = new HttpClient();
                    var response = http.GetAsync(url).GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                        json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // Ignorar e tentar arquivo local
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

                    if (!File.Exists(path))
                    {
                        System.Windows.MessageBox.Show(
                            "Arquivo de configuração não encontrado. Usando padrões.",
                            "Aviso",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json))
                        return;
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                Config = JsonSerializer.Deserialize<AppConfig>(json, options) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                LogError($"Erro ao carregar configuração: {ex.Message}");
                System.Windows.MessageBox.Show(
                    "Erro na configuração. Usando padrões.",
                    "Aviso",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static UpdatePoller CreateUpdatePoller(LoginWindow login)
        {
            return new UpdatePoller(
                service: new AtualizadorService(),
                ownerResolver: () => GetCurrentVisibleWindow(login),
                baseInterval: TimeSpan.FromMinutes(15), // Intervalo maior para reduzir carga
                maxInterval: TimeSpan.FromHours(2),
                initialDelay: TimeSpan.FromSeconds(30) // Delay maior para estabilizar
            );
        }

        private static WpfWindow? GetCurrentVisibleWindow(LoginWindow login)
        {
            try
            {
                // Preferir MainWindow se visível
                if (WpfApp.Current?.MainWindow?.IsVisible == true)
                    return WpfApp.Current.MainWindow;

                // Fallback para login se visível
                if (login?.IsVisible == true)
                    return login;

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static EventWaitHandle SetupShowWindowSystem(MainWindow main, App app)
        {
            try
            {
                var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "leituraWPF_SHOW_EVENT");

                // Background task para monitorar evento
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            if (showEvent.WaitOne(TimeSpan.FromSeconds(30)))
                            {
                                ShowMainWindow(main, app);
                            }

                            // Verificar se app ainda está ativo
                            if (app.Dispatcher.HasShutdownStarted)
                                break;
                        }
                    }
                    catch
                    {
                        // Task vai terminar silenciosamente
                    }
                });

                return showEvent;
            }
            catch
            {
                // Retornar handle dummy se falhar
                return new EventWaitHandle(false, EventResetMode.AutoReset);
            }
        }

        private static void ShowMainWindow(MainWindow main, App app)
        {
            try
            {
                if (app.Dispatcher.HasShutdownStarted)
                    return;

                app.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (main.WindowState == WindowState.Minimized)
                            main.WindowState = WindowState.Normal;

                        if (!main.IsVisible)
                            main.Show();

                        main.Activate();
                        main.Focus();
                    }
                    catch
                    {
                        // Ignorar erros de UI
                    }
                });
            }
            catch
            {
                // Ignorar erros de dispatcher
            }
        }

        private static void SafeExecute(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch
            {
                // Execução segura - ignorar erros
            }
        }

        private static void SafeDispose(IDisposable? disposable)
        {
            try
            {
                disposable?.Dispose();
            }
            catch
            {
                // Dispose seguro - ignorar erros
            }
        }

        private static void LogError(string message)
        {
            try
            {
                Debug.WriteLine($"[ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");

                // Log em arquivo se necessário
                var logPath = Path.Combine(AppContext.BaseDirectory, "error.log");
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch
            {
                // Ignorar erros de log
            }
        }

        private static void LogFatalError(Exception ex)
        {
            try
            {
                var message = $"FATAL: {ex.Message}\n{ex.StackTrace}";
                Debug.WriteLine($"[FATAL] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");

                var logPath = Path.Combine(AppContext.BaseDirectory, "fatal.log");
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch
            {
                // Último recurso - não pode falhar
            }
        }
    }
}