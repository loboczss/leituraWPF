// Services/UpdatePoller.cs
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
// NÃO usar "using System.Windows;" aqui para evitar colisões de nomes.
// Em vez disso, usamos aliases:
using WpfApp = System.Windows.Application;
using WpfWindow = System.Windows.Window;
using WpfShutdownMode = System.Windows.ShutdownMode;
using ThreadingTimer = System.Threading.Timer;

namespace leituraWPF.Services
{
    /// <summary>
    /// Verificador periódico de atualização com backoff exponencial quando offline.
    /// - One-shot timer: reprograma o próximo tick conforme sucesso/falha.
    /// - Sucesso (online): intervalo base (ex.: 10 min).
    /// - Falha (offline): 10m -> 20m -> 40m -> 60m (teto), com jitter ±10%.
    /// - Evita reentrância.
    /// - Usa ownerResolver para abrir o UpdatePromptWindow com dono correto (Login ou Main).
    /// </summary>
    public sealed class UpdatePoller : IDisposable
    {
        private readonly AtualizadorService _service;
        private readonly TimeSpan _baseInterval;
        private readonly TimeSpan _maxInterval;
        private readonly Func<WpfWindow> _ownerResolver;

        private readonly ThreadingTimer _timer;
        private int _isChecking;        // 0 livre / 1 checando
        private int _failureCount;      // falhas consecutivas (offline)
        private volatile bool _disposed;

        /// <param name="service">Instância do AtualizadorService.</param>
        /// <param name="ownerResolver">
        /// Função que retorna a Window dona do prompt (ex.: retorna Login enquanto visível; depois Main).
        /// Pode ser null; nesse caso, a janela abre sem Owner.
        /// </param>
        /// <param name="baseInterval">Intervalo-base entre checagens (padrão 10 minutos).</param>
        /// <param name="maxInterval">Intervalo máximo no backoff (padrão 1 hora).</param>
        /// <param name="initialDelay">Atraso inicial antes da primeira checagem (padrão 0s).</param>
        public UpdatePoller(
            AtualizadorService service,
            Func<WpfWindow> ownerResolver = null,
            TimeSpan? baseInterval = null,
            TimeSpan? maxInterval = null,
            TimeSpan? initialDelay = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _ownerResolver = ownerResolver;
            _baseInterval = baseInterval ?? TimeSpan.FromMinutes(10);
            _maxInterval = maxInterval ?? TimeSpan.FromHours(1);

            // One-shot: dispara após initialDelay; cada rodada reprograma o próximo tick
            _timer = new ThreadingTimer(TimerCallback, state: null,
                dueTime: initialDelay ?? TimeSpan.Zero,
                period: Timeout.InfiniteTimeSpan);
        }

        private void TimerCallback(object state)
        {
            if (_disposed) return;

            // Evita sobreposição
            if (Interlocked.Exchange(ref _isChecking, 1) == 1) return;

            _ = CheckAndMaybeUpdateAsync()
                .ContinueWith(_ => Interlocked.Exchange(ref _isChecking, 0));
        }

        private async Task CheckAndMaybeUpdateAsync()
        {
            bool offlineFailure = false;
            try
            {
                // 1) Consultar versões + status de rede
                var (localVer, remoteVer, remoteOk) = await _service.GetVersionsWithStatusAsync().ConfigureAwait(false);

                if (!remoteOk)
                {
                    offlineFailure = true; // offline/erro de consulta
                    return;
                }

                // 2) Sem atualização? agenda próximo ciclo base
                if (remoteVer == null || remoteVer <= localVer)
                {
                    return;
                }

                // 3) Pergunta ao usuário na UI
                var dispatcher = WpfApp.Current?.Dispatcher;
                if (dispatcher == null) return;

                bool wantsUpdate = await dispatcher.InvokeAsync(() =>
                {
                    // Resolve o dono: MainWindow se visível; senão tenta Login; senão null
                    WpfWindow owner = null;
                    try
                    {
                        owner = _ownerResolver?.Invoke();
                        if (owner != null && !owner.IsVisible) owner = null;
                    }
                    catch { /* ignore */ }

                    var win = new UpdatePromptWindow(localVer, remoteVer, timeoutSeconds: 60);
                    if (owner != null) win.Owner = owner;

                    bool? dlg = win.ShowDialog();
                    return dlg == true;
                });

                if (!wantsUpdate) return;

                // 4) Baixa asset e roda update
                string zipPath = await _service.DownloadLatestReleaseAsync(preferNameContains: null).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(zipPath)) return;

                string batPath = _service.CreateUpdateBatch(zipPath);

                var psi = new ProcessStartInfo
                {
                    FileName = batPath,
                    UseShellExecute = true
                    // Se necessário elevar permissões: Verb = "runas"
                };
                try
                {
                    var proc = Process.Start(psi);
                    if (proc == null)
                        throw new InvalidOperationException("Falha ao iniciar processo de atualização.");
                }
                catch (Exception ex)
                {
                    await dispatcher.InvokeAsync(() =>
                        System.Windows.MessageBox.Show(
                            $"Erro ao iniciar atualização: {ex.Message}",
                            "Atualização",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error));
                    offlineFailure = true;
                    return;
                }

                // 5) Fecha o app
                await dispatcher.InvokeAsync(() => WpfApp.Current.Shutdown());
            }
            catch
            {
                // Qualquer erro aqui conta como falha para backoff
                offlineFailure = true;
            }
            finally
            {
                // Reagenda próximo tick (mesmo durante a tela de login)
                if (!_disposed)
                {
                    ScheduleNext(offlineFailure);
                }
            }
        }

        private void ScheduleNext(bool wasFailure)
        {
            TimeSpan next;
            if (!wasFailure)
            {
                _failureCount = 0;
                next = _baseInterval;
            }
            else
            {
                // 10m * 2^(n-1), com teto _maxInterval e jitter ±10%
                _failureCount = Math.Min(_failureCount + 1, 10);
                double minutes = _baseInterval.TotalMinutes * Math.Pow(2, _failureCount - 1);
                if (minutes > _maxInterval.TotalMinutes) minutes = _maxInterval.TotalMinutes;
                next = TimeSpan.FromMinutes(ApplyJitter(minutes, 0.10));
            }

            try
            {
                _timer.Change(next, Timeout.InfiniteTimeSpan);
            }
            catch
            {
                // ignore
            }
        }

        private static double ApplyJitter(double value, double jitterFraction)
        {
            // jitter multiplicativo [1-j, 1+j]
            var seed = unchecked(Environment.TickCount * 31 + Thread.CurrentThread.ManagedThreadId);
            var rnd = new Random(seed);
            double low = 1.0 - jitterFraction, high = 1.0 + jitterFraction;
            return value * (low + (high - low) * rnd.NextDouble());
        }

        public void Dispose()
        {
            _disposed = true;
            try { _timer?.Dispose(); } catch { /* ignore */ }
        }
    }
}
