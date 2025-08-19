// Services/UpdatePoller.cs
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace leituraWPF.Services
{
    /// <summary>
    /// Verificador periódico de atualização com backoff exponencial quando offline.
    /// - Dispara já e reprograma conforme sucesso/falha.
    /// - Sucesso (online): 10 minutos fixos.
    /// - Falha (offline): 10m -> 20m -> 40m -> 60m (cap), com jitter ~±10%.
    /// - Evita reentrância.
    /// </summary>
    public sealed class UpdatePoller : IDisposable
    {
        private readonly AtualizadorService _service;
        private readonly TimeSpan _baseInterval;
        private readonly TimeSpan _maxInterval;

        private readonly Timer _timer;
        private int _isChecking; // 0 livre / 1 checando
        private int _failureCount; // consecutivas de offline
        private volatile bool _disposed;

        public UpdatePoller(AtualizadorService service, TimeSpan? baseInterval = null, TimeSpan? maxInterval = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _baseInterval = baseInterval ?? TimeSpan.FromMinutes(10);
            _maxInterval = maxInterval ?? TimeSpan.FromHours(1);

            // One-shot: dispara agora, e a cada rodada reprograma o próximo tick.
            _timer = new Timer(TimerCallback, state: null, dueTime: TimeSpan.Zero, period: Timeout.InfiniteTimeSpan);
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
                // 1) Consultar versões e status de rede
                var (localVer, remoteVer, remoteOk) = await _service.GetVersionsWithStatusAsync().ConfigureAwait(false);

                if (!remoteOk)
                {
                    // offline / erro ao consultar GitHub → habilita backoff
                    offlineFailure = true;
                    return;
                }

                // 2) Sem atualização? agenda próximo ciclo base e sai.
                if (remoteVer == null || remoteVer <= localVer)
                {
                    return;
                }

                // 3) Pergunta ao usuário (UI thread)
                bool wantsUpdate = false;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null) return;

                wantsUpdate = await dispatcher.InvokeAsync(() =>
                {
                    var win = new UpdatePromptWindow(localVer, remoteVer, timeoutSeconds: 60)
                    {
                        Owner = Application.Current?.MainWindow
                    };
                    bool? dlg = win.ShowDialog();
                    return dlg == true;
                });

                if (!wantsUpdate) return;

                // 4) Baixa e atualiza
                string zipPath = await _service.DownloadLatestReleaseAsync(preferNameContains: null).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(zipPath)) return;

                string batPath = _service.CreateUpdateBatch(zipPath);

                var psi = new ProcessStartInfo
                {
                    FileName = batPath,
                    UseShellExecute = true
                    // Se precisar admin: Verb = "runas"
                };
                Process.Start(psi);

                // 5) Fecha o app
                await dispatcher.InvokeAsync(() => Application.Current.Shutdown());
            }
            catch
            {
                // Qualquer exceção aqui (inclusive rede) conta como falha offline para o re-agendamento.
                offlineFailure = true;
            }
            finally
            {
                // Se o app não foi encerrado (sem update), reagendar próximo tick.
                if (!_disposed && Application.Current != null && Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown)
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
                // 1,2,3,... → 10m * 2^(n-1), com teto em _maxInterval
                _failureCount = Math.Min(_failureCount + 1, 10); // trava o crescimento
                double minutes = _baseInterval.TotalMinutes * Math.Pow(2, _failureCount - 1);
                if (minutes > _maxInterval.TotalMinutes) minutes = _maxInterval.TotalMinutes;
                next = TimeSpan.FromMinutes(ApplyJitter(minutes, 0.10)); // ±10% jitter
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
            // Jitter multiplicativo: [1 - j, 1 + j]
            // Random sem estado global (seedado pelo tick + thread id)
            var seed = unchecked(Environment.TickCount * 31 + Thread.CurrentThread.ManagedThreadId);
            var rnd = new Random(seed);
            double low = 1.0 - jitterFraction;
            double high = 1.0 + jitterFraction;
            double factor = low + (high - low) * rnd.NextDouble();
            return value * factor;
        }

        public void Dispose()
        {
            _disposed = true;
            try { _timer?.Dispose(); } catch { /* ignore */ }
        }
    }
}
