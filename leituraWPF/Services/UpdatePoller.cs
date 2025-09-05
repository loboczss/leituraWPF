// Services/UpdatePoller.cs
using System;
using System.Threading;
using System.Threading.Tasks;
// Evitar colisões com WPF:
// (não use "using System.Windows;" direto aqui)
using WpfApp = System.Windows.Application;
using WpfWindow = System.Windows.Window;
using ThreadingTimer = System.Threading.Timer;

namespace leituraWPF.Services
{
    // ================================
    // Contratos usados pelo Poller
    // ================================
    public interface IUpdateService
    {
        Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);
        Task<UpdatePerformResult> PerformUpdateAsync(CancellationToken ct = default);
    }

    public sealed class UpdateCheckResult
    {
        public bool Success;
        public string Message;
        public bool RemoteFetchSuccessful;
        public bool UpdateAvailable;
        public Version LocalVersion;
        public Version RemoteVersion;
    }

    public sealed class UpdatePerformResult
    {
        public bool Success;
        public string Message;
        public bool RemoteFetchSuccessful;
    }

    /// <summary>
    /// Verificador periódico de atualização com backoff exponencial quando offline.
    /// - One-shot timer: reprograma o próximo tick conforme sucesso/falha.
    /// - Sucesso (online): intervalo base.
    /// - Falha (offline): base*2^n até o teto, com jitter ±10%.
    /// - Sem reentrância.
    /// - Usa ownerResolver para abrir o UpdatePromptWindow com Owner correto.
    /// </summary>
    public sealed class UpdatePoller : IDisposable
    {
        private readonly IUpdateService _service;
        private readonly TimeSpan _baseInterval;
        private readonly TimeSpan _maxInterval;
        private readonly Func<WpfWindow> _ownerResolver;

        private readonly ThreadingTimer _timer;
        private int _isChecking;        // 0 = livre / 1 = rodando
        private int _failureCount;      // falhas consecutivas (para backoff)
        private volatile bool _disposed;

        /// <param name="service">Serviço de atualização utilizado pelo poller.</param>
        /// <param name="ownerResolver">Resolve a janela dona do prompt (Login ou Main).</param>
        /// <param name="baseInterval">Intervalo base (padrão 10 minutos).</param>
        /// <param name="maxInterval">Intervalo máximo (padrão 1 hora).</param>
        /// <param name="initialDelay">Atraso inicial antes da 1ª checagem.</param>
        public UpdatePoller(
            IUpdateService service,
            Func<WpfWindow> ownerResolver = null,
            TimeSpan? baseInterval = null,
            TimeSpan? maxInterval = null,
            TimeSpan? initialDelay = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _ownerResolver = ownerResolver;
            _baseInterval = baseInterval ?? TimeSpan.FromMinutes(10);
            _maxInterval = maxInterval ?? TimeSpan.FromHours(1);

            // One-shot: só agenda o próximo quando terminar a rodada atual
            _timer = new ThreadingTimer(TimerCallback, state: null,
                dueTime: initialDelay ?? TimeSpan.Zero,
                period: Timeout.InfiniteTimeSpan);
        }

        private void TimerCallback(object state)
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _isChecking, 1) == 1) return; // sem reentrância

            _ = CheckAndMaybeUpdateAsync()
                .ContinueWith(_ => Interlocked.Exchange(ref _isChecking, 0));
        }

        private async Task CheckAndMaybeUpdateAsync()
        {
            bool offlineFailure = false;

            try
            {
                // 1) Checa servidor / versão
                var check = await _service.CheckForUpdatesAsync().ConfigureAwait(false);

                if (!check.RemoteFetchSuccessful)
                {
                    offlineFailure = true; // rede/servidor falhou
                    return;
                }

                // 2) Sem update → só reagenda
                if (!check.UpdateAvailable)
                {
                    return;
                }

                // 3) Pede confirmação ao usuário na UI (dispatcher WPF)
                var dispatcher = WpfApp.Current?.Dispatcher;
                if (dispatcher == null) return;

                bool wantsUpdate = await dispatcher.InvokeAsync(() =>
                {
                    WpfWindow owner = null;
                    try
                    {
                        owner = _ownerResolver?.Invoke();
                        if (owner != null && !owner.IsVisible) owner = null;
                    }
                    catch { /* ignore */ }

                    var win = new UpdatePromptWindow(
                        check.LocalVersion ?? new Version(0, 0),
                        check.RemoteVersion ?? new Version(0, 0),
                        timeoutSeconds: 60);

                    if (owner != null) win.Owner = owner;

                    bool? dlg = win.ShowDialog();
                    return dlg == true;
                });

                if (!wantsUpdate) return;

                // 4) Executa a atualização (abre o AtualizaAPP.exe na subpasta "AtualizaAPP")
                var update = await _service.PerformUpdateAsync().ConfigureAwait(false);
                if (!update.Success)
                {
                    offlineFailure = !update.RemoteFetchSuccessful;
                    return;
                }

                // 5) Fecha o app para que o AtualizaAPP.exe prossiga
                await dispatcher.InvokeAsync(() => WpfApp.Current.Shutdown());
            }
            catch
            {
                // Qualquer exceção aqui conta como falha para backoff
                offlineFailure = true;
            }
            finally
            {
                if (!_disposed)
                    ScheduleNext(offlineFailure);
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
                // backoff exponencial com teto e jitter
                _failureCount = Math.Min(_failureCount + 1, 10);
                double minutes = _baseInterval.TotalMinutes * Math.Pow(2, _failureCount - 1);
                if (minutes > _maxInterval.TotalMinutes) minutes = _maxInterval.TotalMinutes;
                next = TimeSpan.FromMinutes(ApplyJitter(minutes, 0.10));
            }

            try { _timer.Change(next, Timeout.InfiniteTimeSpan); } catch { /* ignore */ }
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
