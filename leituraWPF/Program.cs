using leituraWPF.Services;
using leituraWPF.Utils;
using System;
using System.Diagnostics;
using System.IO;
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
            using var mutex = new Mutex(true, "leituraWPF_SINGLE_INSTANCE", out bool created);
            if (!created)
            {
                try
                {
                    using var evt = EventWaitHandle.OpenExisting("leituraWPF_SHOW_EVENT");
                    evt.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    var current = Process.GetCurrentProcess();
                    var other = Process.GetProcessesByName(current.ProcessName)
                                        .FirstOrDefault(p => p.Id != current.Id);
                    if (other != null)
                    {
                        NativeMethods.ShowWindow(other.MainWindowHandle, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(other.MainWindowHandle);
                    }
                }
                return;
            }

            StartupService.ConfigureStartup();

            // Carrega configuração
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                System.Windows.MessageBox.Show(
                    "Arquivo appsettings.json não encontrado. Usando configurações padrão.",
                    "Aviso",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                var json = File.ReadAllText(path);
                Config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new AppConfig();
            }

            var tokenService = new TokenService(Config);
            using var backup = new BackupUploaderService(Config, tokenService);
            backup.LoadPendingFromBaseDirs();
            backup.Start();

            var funcService = new FuncionarioService(Config, tokenService);
            var login = new LoginWindow(funcService, backup);

            // Cria a Application ANTES do ShowDialog (dispatcher ativo para o poller abrir janelas)
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // ⬇️ Poller ATIVO já durante a tela de login:
            using var updatePoller = new UpdatePoller(
                service: new AtualizadorService(),
                ownerResolver: () =>
                {
                    // Preferir MainWindow quando visível; senão, usar a janela de login (se estiver visível)
                    if (WpfApp.Current?.MainWindow != null && WpfApp.Current.MainWindow.IsVisible)
                        return WpfApp.Current.MainWindow;

                    return login != null && login.IsVisible ? (WpfWindow)login : null;
                },
                baseInterval: TimeSpan.FromMinutes(10),
                maxInterval: TimeSpan.FromHours(1),
                initialDelay: TimeSpan.FromSeconds(5) // dá tempo da tela de login aparecer
            );

            if (login.ShowDialog() == true)
            {
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;

                var main = new MainWindow(login.FuncionarioLogado, backup);

                void ShowMainWindow()
                {
                    app.Dispatcher.Invoke(() =>
                    {
                        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                        if (!main.IsVisible) main.Show();
                        main.Activate();
                    });
                }

                using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "leituraWPF_SHOW_EVENT");
                _ = Task.Run(() =>
                {
                    while (showEvent.WaitOne())
                    {
                        ShowMainWindow();
                    }
                });

                using var tray = new TrayService(
                    showWindow: ShowMainWindow,
                    sync: () => main.RunManualSync(),
                    exit: () => app.Dispatcher.Invoke(() => main.ForceClose()));

                // Exibe a janela principal antes de iniciar o loop da aplicação
                app.MainWindow = main;
                main.Show();
                app.Run();
                // Ao sair do Run, 'using' garante Dispose do tray e do poller
            }
            // Se o login for cancelado, o poller é descartado automaticamente aqui pelo 'using'
        }
    }
}
