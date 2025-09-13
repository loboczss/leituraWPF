// Program.cs
using leituraWPF.Services;
using leituraWPF.Utils;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

// Aliases
using WpfApp = System.Windows.Application;
using WpfWindow = System.Windows.Window;

namespace leituraWPF
{
    public static class Program
    {
        public static AppConfig Config { get; private set; } = new AppConfig();

        // References used for graceful shutdown
        public static BackupUploaderService? BackupInstance { get; private set; }
        public static UpdatePoller? UpdatePollerInstance { get; private set; }
        public static TrayService? TrayInstance { get; private set; }
        private static int _updatePromptShowing;

        [STAThread]
        public static void Main()
        {
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
                using var evt = EventWaitHandle.OpenExisting("leituraWPF_SHOW_EVENT");
                evt.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            catch
            {
            }

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
            }
        }

        private static void RunApplication()
        {
            try { StartupService.ConfigureStartup(); } catch { }

            EnsureVersionFile();
            LoadConfiguration();

            var tokenService = new TokenService(Config);
            var funcService = new FuncionarioService(Config, tokenService);

            BackupUploaderService? backup = null;
            UpdatePoller? updatePoller = null;
            TrayService? tray = null;
            EventWaitHandle? showEvent = null;
            CancellationTokenSource? appCts = null;

            try
            {
                backup = new BackupUploaderService(Config, tokenService);
                BackupInstance = backup;

                // Inicia imediatamente o serviço de upload para que
                // funcione desde a tela de login
                backup.Start();

                // Carrega os arquivos pendentes em segundo plano sem
                // bloquear a inicialização da aplicação
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await backup.LoadPendingFromBaseDirsAsync();
                    }
                    catch
                    {
                    }
                });

                var app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                appCts = new CancellationTokenSource();

                // Pre-download da lista de funcionários para evitar travamentos na tela de login
                try
                {
                    var baseDir = AppContext.BaseDirectory;
                    var jsonPath = Path.Combine(baseDir, "funcionarios.json");
                    var needsDownload = !File.Exists(jsonPath) ||
                                        DateTime.Now.Subtract(File.GetLastWriteTime(jsonPath)).TotalDays > 1;

                    if (needsDownload)
                    {
                        using var downloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        try
                        {
                            funcService
                                .DownloadJsonAsync(baseDir, downloadCts.Token)
                                .GetAwaiter()
                                .GetResult();
                        }
                        catch
                        {
                            // falha silenciosa; a tela de login tentará novamente se necessário
                        }
                    }
                }
                catch
                {
                    // ignora erros de pré-carregamento
                }

                var login = new LoginWindow(funcService, backup);

                // Poller com serviço externo de atualização
                updatePoller = CreateUpdatePoller(login);
                UpdatePollerInstance = updatePoller;

                var loginResult = login.ShowDialog();
                if (loginResult != true || login.FuncionarioLogado == null)
                {
                    return;
                }

                app.ShutdownMode = ShutdownMode.OnMainWindowClose;

                var main = new MainWindow(login.FuncionarioLogado, backup);
                app.MainWindow = main;

                showEvent = SetupShowWindowSystem(main, app);

                tray = new TrayService(
                    showWindow: () => ShowMainWindow(main, app),
                    sync: () => SafeExecute(() => main.RunManualSync()),
                    exit: () => SafeExecute(ShutdownApplication)
                );
                TrayInstance = tray;

                main.Show();
                app.Run();
            }
            finally
            {
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
            IUpdateService svc = new ExternalUpdateService();

            return new UpdatePoller(
                service: svc,
                ownerResolver: () => GetCurrentVisibleWindow(login),
                baseInterval: TimeSpan.FromMinutes(5),
                maxInterval: TimeSpan.FromMinutes(5),
                initialDelay: TimeSpan.Zero
            );
        }

        private static WpfWindow? GetCurrentVisibleWindow(LoginWindow login)
        {
            try
            {
                if (WpfApp.Current?.MainWindow?.IsVisible == true)
                    return WpfApp.Current.MainWindow;
                if (login?.IsVisible == true)
                    return login;
                return null;
            }
            catch { return null; }
        }

        public static bool TryShowUpdatePrompt(Version local, Version remote, WpfWindow owner = null, int timeoutSeconds = 30)
        {
            if (Interlocked.Exchange(ref _updatePromptShowing, 1) == 1)
                return false;

            try
            {
                var win = new UpdatePromptWindow(local, remote, timeoutSeconds);
                if (owner != null) win.Owner = owner;
                bool? dlg = win.ShowDialog();
                return dlg == true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _updatePromptShowing, 0);
            }
        }

        private static EventWaitHandle SetupShowWindowSystem(MainWindow main, App app)
        {
            try
            {
                var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "leituraWPF_SHOW_EVENT");

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

                            if (app.Dispatcher.HasShutdownStarted)
                                break;
                        }
                    }
                    catch
                    {
                    }
                });

                return showEvent;
            }
            catch
            {
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
                    }
                });
            }
            catch
            {
            }
        }

        public static void ShutdownApplication()
        {
            try
            {
                WpfApp.Current?.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (WpfApp.Current.MainWindow is MainWindow mw)
                            mw.ForceClose();
                        else
                            WpfApp.Current.MainWindow?.Close();
                    }
                    catch { }
                });
            }
            catch { }

            SafeDispose(UpdatePollerInstance);
            UpdatePollerInstance = null;
            SafeDispose(TrayInstance);
            TrayInstance = null;
            SafeDispose(BackupInstance);
            BackupInstance = null;

            try { WpfApp.Current?.Shutdown(); } catch { }

            try
            {
                var current = Process.GetCurrentProcess();
                foreach (var proc in Process.GetProcessesByName(current.ProcessName))
                {
                    if (proc.Id != current.Id)
                    {
                        try { proc.Kill(); } catch { }
                    }
                }
            }
            catch { }

            try
            {
                // garante encerramento mesmo se threads em segundo plano permanecerem ativas
                Environment.Exit(0);
            }
            catch
            {
                try { Process.GetCurrentProcess().Kill(); } catch { }
            }
        }

        private static void EnsureVersionFile()
        {
            try
            {
                var asmVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (asmVersion == null)
                    return;

                var current = $"v{asmVersion}";
                var path = Path.Combine(AppContext.BaseDirectory, "version.txt");

                var existing = File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
                if (existing != current)
                    File.WriteAllText(path, current);
            }
            catch
            {
            }
        }

        private static void SafeExecute(Action action)
        {
            try { action?.Invoke(); } catch { }
        }

        private static void SafeDispose(IDisposable? disposable)
        {
            try { disposable?.Dispose(); } catch { }
        }

        private static void LogError(string message)
        {
            try
            {
                Debug.WriteLine($"[ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
                var logPath = Path.Combine(AppContext.BaseDirectory, "error.log");
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
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
            catch { }
        }
    }
}
