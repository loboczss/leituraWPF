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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfMessageBox = System.Windows.MessageBox;

namespace leituraWPF
{
    public partial class MainWindow : Window, ILogSink, IProgress<double>
    {
        private readonly TokenService _tokenService;
        private readonly GraphDownloader _downloader;
        private readonly JsonReaderService _jsonReader;
        private readonly RenamerService _renamer = new RenamerService();
        private readonly InstallationRenamerService _installRenamer = new InstallationRenamerService();
        private readonly InstalacaoService _instalacao = new InstalacaoService();
        private readonly BackupUploaderService _backup;

        private readonly AtualizadorService _atualizador = new AtualizadorService();
        private bool _checkedUpdateAtStartup = false;

        private string _sourceFolderPath = string.Empty;
        private readonly string _downloadsDir;

        private readonly ObservableCollection<LogEntry> _logItems = new();
        private List<ClientRecord> _cacheRecords = new();
        private readonly Funcionario? _funcionario;

        public MainWindow(Funcionario? funcionario = null)
        {
            InitializeComponent();

            _funcionario = funcionario;
            if (_funcionario != null)
            {
                LblUsuario.Text = $"Usuário: {_funcionario.Nome}";
            }

            _downloadsDir = Path.Combine(AppContext.BaseDirectory, "downloads");
            Directory.CreateDirectory(_downloadsDir);

            GridLog.ItemsSource = _logItems;

            _tokenService = new TokenService(Program.Config);
            _downloader = new GraphDownloader(Program.Config, _tokenService, this, this);
            _jsonReader = new JsonReaderService(this);
            _backup = new BackupUploaderService(Program.Config, _tokenService);

            _renamer.FileReadyForBackup += async p => await _backup.EnqueueAsync(p);
            _installRenamer.FileReadyForBackup += async p => await _backup.EnqueueAsync(p);

            _backup.StatusChanged += msg => Log($"[BACKUP] {msg}");
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
                        ? $"Última sync: {t:dd/MM HH:mm}" : "Última sync: —";
                });
            };

            _backup.Start();

            // No carregamento: checa atualização (com timeout) e prepara o cache local
            this.Loaded += MainWindow_Loaded;
        }

        public void RunManualSync()
        {
            _ = _backup.ForceRunOnceAsync();
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            // roda só uma vez
            if (!_checkedUpdateAtStartup)
            {
                _checkedUpdateAtStartup = true;
                // Checa atualização sem travar UI
                _ = CheckUpdatesOnStartupAsync();
            }

            // Prepara cache local para buscas/renomeação offline
            await EnsureLocalCacheAsync();
        }

        private async Task CheckUpdatesOnStartupAsync()
        {
            try
            {
                var (localV, remoteV) = await _atualizador.GetVersionsAsync();
                if (remoteV <= localV) return; // já está atualizado

                // Prompt com timeout de 60s: se não responder, atualiza
                var prompt = new UpdatePromptWindow(localV, remoteV, timeoutSeconds: 60)
                {
                    Owner = this
                };
                var result = prompt.ShowDialog();

                // result == true (atualizar agora) / result == null (auto-timeout) ⇒ atualiza
                if (result != false)
                {
                    var zip = await _atualizador.DownloadLatestReleaseAsync(preferNameContains: null);
                    if (zip == null)
                    {
                        Log("[WARN] Release encontrado, mas sem asset .zip para baixar.");
                        return;
                    }

                    var bat = _atualizador.CreateUpdateBatch(zip);

                    // dispara o .bat e encerra para permitir cópia/substituição
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
                // segue a vida; não quebra o startup
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
                }
            });
        }

        /* ---- Handlers ---- */
        private void btnSelecionarOrigem_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new VistaFolderBrowserDialog
            {
                Description = "Escolha a pasta com os arquivos crus (con/c0n, inv, bat, imagens...)",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
                Multiselect = false
            };

            if (dlg.ShowDialog(this) == true)
            {
                _sourceFolderPath = dlg.SelectedPath;
                txtOrigem.Text = _sourceFolderPath;
                SetStatus("Origem definida.");
            }
        }

        private async void btnExecutar_Click(object sender, RoutedEventArgs e)
        {
            btnExecutar.IsEnabled = false;
            ClearLog();
            SetStatus("Sincronizando com SharePoint...");
            Report(0);

            try
            {
                var downloaded = await _downloader.DownloadMatchingJsonAsync(
                    _downloadsDir,
                    extraQueries: new[] { "Instalacao_AC" } // baixa também instalação
                );

                var stats = SyncStatsService.Load();
                stats.Downloaded += downloaded.Count;
                SyncStatsService.Save(stats);

                SetStatus($"Download finalizado. {downloaded.Count} arquivo(s).");
                SetStatus("Atualizando cache local (manutenção)...");
                await EnsureLocalCacheAsync(forceReload: true);
                Log("[OK] Sincronização concluída.");
            }
            catch (Exception ex)
            {
                Log($"[FATAL] {ex.Message}");
                SetStatus("Falha.");
            }
            finally
            {
                Report(100);
                btnExecutar.IsEnabled = true;
            }
        }

        private async void btnProcessar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await EnsureLocalCacheAsync();

                var uf = GetSelectedUf();
                var raw = (txtNumos.Text ?? "").Trim();
                if (string.IsNullOrEmpty(raw))
                {
                    WpfMessageBox.Show("Preencha o NumOS (obrigatório) antes de processar.",
                                       "NumOS obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning);
                    txtNumos.Focus();
                    return;
                }

                // ===== Caso especial: "0" → fluxo de INSTALAÇÃO via Fallback (com progresso) =====
                if (raw == "0")
                {
                    SetStatus("Atualizando arquivo de instalação...");
                    progress.Visibility = Visibility.Visible;
                    progress.IsIndeterminate = true;

                    // Baixa/atualiza "Instalacao_AC"
                    await _downloader.DownloadMatchingJsonAsync(_downloadsDir, extraQueries: new[] { "Instalacao_AC" });

                    // Abre o fallback permitindo ID livre; FallbackWindow executa a renomeação e mostra progresso
                    var rotas = _cacheRecords.Select(r => r.Rota).Where(s => !string.IsNullOrWhiteSpace(s));
                    var fb = new FallbackWindow($"{uf}0", rotas, uf, _cacheRecords, _renamer, _sourceFolderPath, allowAnyId: true)
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

                // ===== Caminho NORMAL: manutenção =====
                var numosFull = BuildFullNumos(); // ex.: "AC202400012345"
                var record = _cacheRecords.FirstOrDefault(r =>
                    string.Equals(r.NumOS, numosFull, StringComparison.OrdinalIgnoreCase));

                if (record == null)
                {
                    Log($"[WARN] NumOS \"{numosFull}\" não encontrado no cache. Abrindo fallback.");

                    // Fallback "assistido" (ele mesmo renomeia com progresso)
                    var rotas = _cacheRecords.Select(r => r.Rota).Where(s => !string.IsNullOrWhiteSpace(s));
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

                // Achou o record no cache → renomeia aqui mesmo
                if (!await EnsureSourceFolderHasFilesAsync()) return;

                btnProcessar.IsEnabled = false;
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
            try
            {
                var destino = _installRenamer.LastDestination;
                if (string.IsNullOrWhiteSpace(destino))
                    destino = _renamer.LastDestination;

                if (!string.IsNullOrWhiteSpace(destino) && Directory.Exists(destino))
                    System.Diagnostics.Process.Start("explorer.exe", destino);
            }
            catch { /* noop */ }
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
                        Multiselect = false
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

        private async void BtnSyncAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnSyncAll.IsEnabled = false;
                UploadBar.Visibility = Visibility.Visible;
                UploadBar.IsIndeterminate = true;
                TxtSyncStatus.Text = "Sincronizando...";
                await _backup.ForceRunOnceAsync();
                TxtSyncStatus.Text = $"Pendentes: {_backup.PendingCount} | Enviados (sessão): {_backup.UploadedCountSession}";
                TxtLastUpdate.Text = _backup.LastRunUtc is { } t ? $"Última sync: {t:dd/MM HH:mm}" : "Última sync: —";
            }
            catch (Exception ex)
            {
                Log($"[WARN] Sync manual falhou: {ex.Message}");
            }
            finally
            {
                UploadBar.IsIndeterminate = false;
                UploadBar.Visibility = Visibility.Collapsed;
                BtnSyncAll.IsEnabled = true;
            }
        }

        /* ---- Logging ---- */
        public void Log(string message)
        {
            string tipo = "INFO"; string emoji = "ℹ️";
            var up = (message ?? "").ToUpperInvariant();
            if (up.Contains("[FATAL]")) { tipo = "CRITICAL"; emoji = "🛑"; }
            else if (up.Contains("[ERRO]") || up.Contains("[ERROR]")) { tipo = "ERROR"; emoji = "❌"; }
            else if (up.Contains("[WARN]")) { tipo = "WARN"; emoji = "⚠️"; }
            else if (up.Contains("[OK]") || up.Contains("[INFO]")) { tipo = "INFO"; emoji = "✅"; }

            void Append()
            {
                _logItems.Add(new LogEntry { Hora = DateTime.Now, Tipo = tipo, Emoji = emoji, Mensagem = message });
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
    }
}
