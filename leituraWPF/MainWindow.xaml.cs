using leituraWPF.Models;
using leituraWPF.Services;
using leituraWPF.Utils;
using Newtonsoft.Json.Linq;
using Ookii.Dialogs.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Reflection;
using System.ComponentModel;
using WpfMessageBox = System.Windows.MessageBox;

namespace leituraWPF
{
    public partial class MainWindow : Window, ILogSink, IProgress<double>
    {
        private const int MaxLogItems = 1000;

        private readonly TokenService _tokenService;
        private readonly GraphDownloader _downloader;
        private readonly JsonReaderService _jsonReader;
        private readonly RenamerService _renamer = new RenamerService();
        private readonly InstallationRenamerService _installRenamer = new InstallationRenamerService();
        private readonly BackupUploaderService _backup;

        private readonly AtualizadorService _atualizador = new AtualizadorService();
        private bool _checkedUpdateAtStartup = false;

        private string _sourceFolderPath = string.Empty;
        private readonly string _downloadsDir;

        private readonly ObservableCollection<LogEntry> _logItems = new();
        private List<ClientRecord> _cacheRecords = new();
        private readonly Funcionario? _funcionario;
        private readonly SemaphoreSlim _syncMutex = new(1, 1);
        private readonly PeriodicTimer _autoSyncTimer = new(TimeSpan.FromMinutes(10));
        private readonly CancellationTokenSource _cts = new();
        private bool _suppressLogs = false;
        private bool _allowClose = false;

        public MainWindow(Funcionario? funcionario = null, BackupUploaderService? backup = null)
        {
            InitializeComponent();

            LblVersao.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version}";

            _funcionario = funcionario;
            if (_funcionario != null)
            {
                LblUsuario.Text = $"Usuário: {_funcionario.Nome}";
            }
            _renamer.FuncionarioLogado = _funcionario;

            _downloadsDir = Path.Combine(AppContext.BaseDirectory, "downloads");
            Directory.CreateDirectory(_downloadsDir);

            // Define o caminho padrão da pasta de origem na área de trabalho
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            _sourceFolderPath = Path.Combine(desktop, "SALVAR AQUI");
            Directory.CreateDirectory(_sourceFolderPath);
            txtOrigem.Text = _sourceFolderPath;

            GridLog.ItemsSource = _logItems;

            _tokenService = new TokenService(Program.Config);
            _downloader = new GraphDownloader(Program.Config, _tokenService, this, this);
            _jsonReader = new JsonReaderService(this);
            _backup = backup ?? new BackupUploaderService(Program.Config, _tokenService);

            _renamer.FileReadyForBackup += async p => await _backup.EnqueueAsync(p);
            _installRenamer.FileReadyForBackup += async p => await _backup.EnqueueAsync(p);

            _backup.StatusChanged += msg =>
            {
                var up = msg.ToUpperInvariant();
                if (up.Contains("FALHA") || up.Contains("ERRO") || up.Contains("INDISPONÍVEL"))
                    Log(msg, true);
            };
            _backup.FileUploaded += (local, remote, bytes) =>
            {
                Log($"[UPL] {Path.GetFileName(local)} → {remote} ({bytes:n0} bytes)");
                var stats = SyncStatsService.Load();
                stats.Uploaded++;
                SyncStatsService.Save(stats);
                Dispatcher.Invoke(() =>
                {
                    TxtSyncStatus.Text = $"Enviado: {Path.GetFileName(local)}";
                });
            };
            _backup.CountersChanged += (pend, sent) =>
            {
                Dispatcher.Invoke(() =>
                {
                    TxtSyncStatus.Text = $"Pendentes: {pend} | Enviados (sessão): {sent}";
                    TxtLastUpdate.Text = _backup.LastRunUtc is { } t
                        ? $"Última sync: {t.ToLocalTime():dd/MM HH:mm}"
                        : "Última sync: —";
                });
            };

            if (backup == null)
            {
                _ = _backup.LoadPendingFromBaseDirsAsync();
                _backup.Start();
            }

            // inicia sincronização automática a cada 10 minutos (cancelável)
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await _autoSyncTimer.WaitForNextTickAsync(_cts.Token))
                    {
                        await SyncAndBackupAsync(silent: true);
                    }
                }
                catch (OperationCanceledException) { /* janela fechando: ok */ }
                catch (ObjectDisposedException) { /* timer disposed: ok */ }
            }, _cts.Token);

            // No carregamento: checa atualização (com timeout) e prepara o cache local
            this.Loaded += MainWindow_Loaded;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _cts.Cancel();
                _autoSyncTimer.Dispose();
                // Se seu BackupUploaderService tiver Stop(), chame aqui:
                // _backup.Stop();
                // Removido cast para IDisposable (classe não implementa IDisposable)
            }
            catch { /* noop */ }
            base.OnClosed(e);
        }

        public void ForceClose()
        {
            _allowClose = true;
            Close();
        }

        public void RunManualSync()
        {
            _ = SyncAndBackupAsync();
        }

        private async Task SyncAndBackupAsync(bool silent = false)
        {
            if (!await _syncMutex.WaitAsync(0)) return;
            _suppressLogs = silent;
            try
            {
                await Dispatcher.InvokeAsync(() => btnExecutar.IsEnabled = false);
                ClearLog();
                SetStatus("Sincronizando com SharePoint...");
                Report(0);

                try
                {
                    // Aqui você decide o que baixar no sync periódico.
                    // Exemplo: deixar instalação AC/MT sempre atualizada.
                    var downloaded = await _downloader.DownloadMatchingJsonAsync(
                        _downloadsDir,
                        extraQueries: new[] { "Instalacao_AC", "Instalacao_MT" }
                    );

                    var stats = SyncStatsService.Load();
                    stats.Downloaded += downloaded.Count;
                    SyncStatsService.Save(stats);

                    SetStatus($"Download finalizado. {downloaded.Count} arquivo(s).");
                    SetStatus("Atualizando cache local (manutenção)...");
                    await EnsureLocalCacheAsync(forceReload: true);
                    if (!silent)
                        Log("[OK] Sincronização concluída.");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        TxtSyncStatus.Text = "Sincronizando...";
                        UploadBar.Visibility = Visibility.Visible;
                        UploadBar.IsIndeterminate = true;
                    });
                    await _backup.ForceRunOnceAsync();
                    if (silent)
                        Log($"[INFO] Última atualização: {DateTime.Now:dd/MM HH:mm}", force: true);
                }
                catch (Exception ex)
                {
                    Log($"[FATAL] {ex.Message}");
                    Log(ex.ToString());
                    SetStatus("Falha.");
                }
                finally
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        UploadBar.IsIndeterminate = false;
                        UploadBar.Visibility = Visibility.Collapsed;
                    });
                }
            }
            finally
            {
                Report(100);
                await Dispatcher.InvokeAsync(() => btnExecutar.IsEnabled = true);
                _syncMutex.Release();
                _suppressLogs = false;
            }
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            if (!_checkedUpdateAtStartup)
            {
                _checkedUpdateAtStartup = true;
                _ = CheckUpdatesOnStartupAsync();
            }

            // Cache de manutenção (não usado no fluxo 0)
            await EnsureLocalCacheAsync();
        }

        private async Task CheckUpdatesOnStartupAsync()
        {
            try
            {
                var (localV, remoteV) = await _atualizador.GetVersionsAsync();
                if (remoteV <= localV) return; // já está atualizado

                var prompt = new UpdatePromptWindow(localV, remoteV, timeoutSeconds: 60)
                {
                    Owner = this
                };
                var result = prompt.ShowDialog();

                if (result != false)
                {
                    var zip = await _atualizador.DownloadLatestReleaseAsync(preferNameContains: null);
                    if (zip == null)
                    {
                        Log("[WARN] Release encontrado, mas sem asset .zip para baixar.");
                        return;
                    }

                    var bat = _atualizador.CreateUpdateBatch(zip);

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/C start \"\" \"{bat}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    System.Windows.Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                Log($"[WARN] Falha ao checar/atualizar: {ex.Message}");
            }
        }

        /* ---- UI helpers ---- */
        private void SetStatus(string s)
        {
            if (Dispatcher.CheckAccess()) txtStatus.Text = s;
            else Dispatcher.Invoke(() => txtStatus.Text = s);
        }

        private void SetResumo(string s)
        {
            if (Dispatcher.CheckAccess()) txtResumo.Text = s;
            else Dispatcher.Invoke(() => txtResumo.Text = s);
        }

        private void ClearLog()
        {
            if (Dispatcher.CheckAccess())
            {
                _logItems.Clear();
                txtLog.Clear();
            }
            else
            {
                Dispatcher.Invoke(ClearLog);
            }
        }

        private string GetSelectedUf()
        {
            var content = (cboUf?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "AC";
            content = content.ToUpperInvariant();
            return (content == "AC" || content == "MT") ? content : "AC";
        }

        private string BuildFullNumos()
        {
            var uf = GetSelectedUf();
            var raw = (txtNumos.Text ?? "").Trim().ToUpperInvariant();
            if (raw.StartsWith("AC") || raw.StartsWith("MT")) raw = raw[2..];
            raw = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            return string.IsNullOrEmpty(raw) ? "" : uf + raw;
        }

        /* ---- Cache local de manutenção ---- */
        private async Task EnsureLocalCacheAsync(bool forceReload = false)
        {
            await Task.Run(() =>
            {
                try
                {
                    var files = Directory.EnumerateFiles(_downloadsDir, "*.json")
                        .Where(f => !Path.GetFileName(f).StartsWith("Instalacao_", StringComparison.OrdinalIgnoreCase))
                        .Where(f => !Path.GetFileName(f).Equals(".index.json", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var all = new List<ClientRecord>();
                    int ok = 0;

                    foreach (var file in files)
                    {
                        try
                        {
                            var arr = _jsonReader.LoadJArrayFlexible(file);
                            var recs = JsonReaderService.ParseClientRecords(arr);
                            foreach (var r in recs) r.NomeArquivoBase = Path.GetFileName(file);
                            all.AddRange(recs);
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            Log($"[ERRO] Parse '{Path.GetFileName(file)}': {ex.Message}");
                        }
                    }

                    _cacheRecords = all
                        .GroupBy(r => r.NumOS ?? "", StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList();

                    SetResumo($"Arquivos OK: {ok} | Registros: {_cacheRecords.Count}");
                }
                catch (Exception ex)
                {
                    Log($"[FATAL] Falha ao carregar cache local: {ex.Message}");
                    Log(ex.ToString());
                }
            });
        }

        /* ---- Handlers ---- */
        private void btnSelecionarOrigem_Click(object sender, RoutedEventArgs e)
        {
            btnSelecionarOrigem.IsEnabled = false;
            try
            {
                var dlg = new VistaFolderBrowserDialog
                {
                    Description = "Escolha a pasta com os arquivos crus (con/c0n, inv, bat, imagens...)",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false,
                    Multiselect = false,
                    SelectedPath = _sourceFolderPath
                };

                if (dlg.ShowDialog(this) == true)
                {
                    _sourceFolderPath = dlg.SelectedPath;
                    txtOrigem.Text = _sourceFolderPath;
                    SetStatus("Origem definida.");
                }
            }
            finally
            {
                btnSelecionarOrigem.IsEnabled = true;
            }
        }

        private async void btnExecutar_Click(object sender, RoutedEventArgs e)
        {
            btnExecutar.IsEnabled = false;
            try
            {
                await SyncAndBackupAsync();
            }
            finally
            {
                btnExecutar.IsEnabled = true;
            }
        }

        private async void btnProcessar_Click(object sender, RoutedEventArgs e)
        {
            btnProcessar.IsEnabled = false;
            try
            {
                var uf = GetSelectedUf();
                var raw = (txtNumos.Text ?? "").Trim();

                if (string.IsNullOrEmpty(raw))
                {
                    WpfMessageBox.Show("Preencha o NumOS (obrigatório) antes de processar.",
                                       "NumOS obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning);
                    txtNumos.Focus();
                    return;
                }

                // ===== FLUXO 0 → INSTALAÇÃO: ler APENAS arquivo de instalação local (sem baixar) =====
                if (raw == "0")
                {
                    SetStatus("Lendo arquivo de instalação local...");
                    progress.Visibility = Visibility.Visible;
                    progress.IsIndeterminate = true;

                    // Lê as ROTAS direto do(s) arquivo(s) de instalação (sem tocar no cache de manutenção)
                    var rotasInstalacao = await LoadRotasFromInstallationAsync(uf);

                    // Abre o fallback permitindo ID livre usando o renomeador de instalação
                    var fb = new FallbackWindow($"{uf}0",
                                                rotasInstalacao,
                                                uf,
                                                new List<ClientRecord>(), // não usamos cache de manutenção aqui
                                                _renamer,
                                                _sourceFolderPath,
                                                allowAnyId: true,
                                                installRenamer: _installRenamer)
                    {
                        Owner = this
                    };

                    progress.IsIndeterminate = false;
                    progress.Visibility = Visibility.Collapsed;

                    var ok = fb.ShowDialog() == true;
                    if (!ok)
                    {
                        SetStatus("Cancelado.");
                        return;
                    }

                    btnAbrirPasta.Visibility = Visibility.Visible;
                    SetStatus("Concluído.");
                    return;
                }

                // ===== Fluxo NORMAL (≠ 0) → MANUTENÇÃO =====
                await EnsureLocalCacheAsync(); // só carrega cache de manutenção quando != 0

                var numosFull = BuildFullNumos(); // ex.: "AC202400012345"
                var record = _cacheRecords.FirstOrDefault(r =>
                    string.Equals(r.NumOS, numosFull, StringComparison.OrdinalIgnoreCase));

                if (record == null)
                {
                    Log($"[WARN] NumOS \"{numosFull}\" não encontrado no cache. Abrindo fallback de manutenção.");

                    var rotas = _cacheRecords
                        .Select(r => r.Rota)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    var fb = new FallbackWindow(numosFull, rotas, uf, _cacheRecords, _renamer, _sourceFolderPath, allowAnyId: false)
                    {
                        Owner = this
                    };

                    var ok = fb.ShowDialog() == true;
                    if (!ok)
                    {
                        SetStatus("Cancelado.");
                        return;
                    }

                    btnAbrirPasta.Visibility = Visibility.Visible;
                    SetStatus("Concluído.");
                    return;
                }

                // Achou o record no cache → renomeia aqui mesmo (manutenção)
                if (!await EnsureSourceFolderHasFilesAsync()) return;

                SetStatus("Processando manutenção...");
                progress.IsIndeterminate = false;
                progress.Value = 0;
                progress.Visibility = Visibility.Visible;

                await _renamer.RenameAsync(
                    _sourceFolderPath,
                    record,
                    new Progress<double>(v => progress.Value = Math.Max(0, Math.Min(100, v)))
                );

                btnAbrirPasta.Visibility = Visibility.Visible;
                SetStatus("Concluído.");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Operação cancelada.");
            }
            catch (Exception ex)
            {
                SetStatus("Falha.");
                WpfMessageBox.Show($"Erro no processamento:\n{ex.Message}",
                                   "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                progress.Value = 0;
                progress.Visibility = Visibility.Collapsed;
                progress.IsIndeterminate = false;
                btnProcessar.IsEnabled = true;
            }
        }

        private void btnAbrirPasta_Click(object sender, RoutedEventArgs e)
        {
            btnAbrirPasta.IsEnabled = false;
            try
            {
                var destino = _installRenamer.LastDestination;
                if (string.IsNullOrWhiteSpace(destino))
                    destino = _renamer.LastDestination;

                if (!string.IsNullOrWhiteSpace(destino) && Directory.Exists(destino))
                    System.Diagnostics.Process.Start("explorer.exe", destino);
            }
            catch (Exception ex)
            {
                Log($"[WARN] Não foi possível abrir a pasta destino: {ex.Message}");
            }
            finally
            {
                btnAbrirPasta.IsEnabled = true;
            }
        }

        private async Task<bool> EnsureSourceFolderHasFilesAsync()
        {
            if (string.IsNullOrWhiteSpace(_sourceFolderPath) || !Directory.Exists(_sourceFolderPath))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    var dlg = new VistaFolderBrowserDialog
                    {
                        Description = "Escolha a pasta com os arquivos crus (con/c0n, inv, bat, imagens...)",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = false,
                        Multiselect = false,
                        SelectedPath = _sourceFolderPath
                    };
                    if (dlg.ShowDialog(this) == true)
                    {
                        _sourceFolderPath = dlg.SelectedPath;
                        txtOrigem.Text = _sourceFolderPath;
                    }
                });
            }

            if (!Directory.Exists(_sourceFolderPath) || !Directory.EnumerateFiles(_sourceFolderPath).Any())
            {
                WpfMessageBox.Show("A pasta de origem não contém arquivos. Selecione arquivos para continuar.",
                                   "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("Aguardando arquivos...");
                return false;
            }
            return true;
        }

        private void txtNumos_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                btnProcessar_Click(sender, e);
                e.Handled = true;
            }
        }

        /* ---- Logging ---- */
        public void Log(string message) => Log(message, false);

        public void Log(string message, bool force)
        {
            if (_suppressLogs && !force) return;
            string tipo = "INFO"; string emoji = "ℹ️";
            var up = (message ?? "").ToUpperInvariant();
            if (up.Contains("[FATAL]")) { tipo = "CRITICAL"; emoji = "🛑"; }
            else if (up.Contains("[ERRO]") || up.Contains("[ERROR]")) { tipo = "ERROR"; emoji = "❌"; }
            else if (up.Contains("[WARN]")) { tipo = "WARN"; emoji = "⚠️"; }
            else if (up.Contains("[OK]") || up.Contains("[INFO]")) { tipo = "INFO"; emoji = "✅"; }

            void Append()
            {
                _logItems.Add(new LogEntry { Hora = DateTime.Now, Tipo = tipo, Emoji = emoji, Mensagem = message });
                if (_logItems.Count > MaxLogItems)
                    _logItems.RemoveAt(0);

                txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {tipo} {message}{Environment.NewLine}");
                txtLog.ScrollToEnd();
                if (GridLog.Items.Count > 0) GridLog.ScrollIntoView(GridLog.Items[GridLog.Items.Count - 1]);
            }
            if (Dispatcher.CheckAccess()) Append(); else Dispatcher.Invoke(Append);
        }

        public void Report(double value)
        {
            if (Dispatcher.CheckAccess())
            {
                progress.Visibility = Visibility.Visible;
                progress.Value = value;
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    progress.Visibility = Visibility.Visible;
                    progress.Value = value;
                });
            }
        }

        /* ---- Helpers específicos de instalação ---- */
        private async Task<IEnumerable<string>> LoadRotasFromInstallationAsync(string uf)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var pattern = $"Instalacao_{uf}*.json";
                    var files = Directory.EnumerateFiles(_downloadsDir, pattern, SearchOption.TopDirectoryOnly).ToList();
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var file in files)
                    {
                        try
                        {
                            var arr = _jsonReader.LoadJArrayFlexible(file);
                            foreach (var obj in arr.OfType<JObject>())
                            {
                                var rota =
                                    (string?)obj["Rota"] ??
                                    (string?)obj["rota"] ??
                                    (string?)obj.SelectToken("ROTA") ??
                                    (string?)obj.SelectToken("rota.nome");

                                if (!string.IsNullOrWhiteSpace(rota))
                                    set.Add(rota.Trim());
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[WARN] Falha ao ler rotas do arquivo de instalação '{Path.GetFileName(file)}': {ex.Message}");
                        }
                    }

                    return set;
                }
                catch (Exception ex)
                {
                    Log($"[WARN] Falha ao localizar arquivos de instalação ({uf}): {ex.Message}");
                    return Enumerable.Empty<string>();
                }
            });
        }
    }
}
