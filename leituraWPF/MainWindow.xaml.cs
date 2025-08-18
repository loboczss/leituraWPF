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
using WpfMessageBox = System.Windows.MessageBox;
using System.Windows.Input;

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

        private string _sourceFolderPath = string.Empty;
        private readonly string _downloadsDir;

        private readonly ObservableCollection<LogEntry> _logItems = new();
        private List<ClientRecord> _cacheRecords = new();

        public MainWindow()
        {
            InitializeComponent();

            _downloadsDir = Path.Combine(AppContext.BaseDirectory, "downloads");
            Directory.CreateDirectory(_downloadsDir);

            GridLog.ItemsSource = _logItems;

            _tokenService = new TokenService(Program.Config);
            _downloader = new GraphDownloader(Program.Config, _tokenService, this, this);
            _jsonReader = new JsonReaderService(this);

            Loaded += async (_, __) => await EnsureLocalCacheAsync();
        }

        /* ---- UI helpers ---- */
        private void SetStatus(string s) { if (Dispatcher.CheckAccess()) txtStatus.Text = s; else Dispatcher.Invoke(() => txtStatus.Text = s); }
        private void SetResumo(string s) { if (Dispatcher.CheckAccess()) txtResumo.Text = s; else Dispatcher.Invoke(() => txtResumo.Text = s); }
        private void ClearLog() { if (Dispatcher.CheckAccess()) { _logItems.Clear(); txtLog.Clear(); } else Dispatcher.Invoke(ClearLog); }

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
                var downloaded = await _downloader.DownloadMatchingJsonAsync(_downloadsDir,
                    extraQueries: new[] { "Instalacao_AC" }); // baixa também instalação
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

                    // Se o fallback renomeou com sucesso, apenas atualiza UI aqui
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

                // Se achou o record no cache, renomeia aqui mesmo (fluxo "antigo")
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
                MessageBox.Show("A pasta de origem não contém arquivos. Selecione arquivos para continuar.",
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
