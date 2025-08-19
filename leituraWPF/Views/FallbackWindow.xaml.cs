using leituraWPF.Models;
using leituraWPF.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace leituraWPF
{
    public partial class FallbackWindow : Window
    {
        // ===== Dependências para renomeação =====
        private readonly RenamerService _renamer;
        private readonly InstallationRenamerService? _installRenamer;
        private readonly string _sourceFolder;

        // Controle para evitar buscas durante a inicialização
        private bool _carregando = true;

        // Captura dos dados
        public string ClienteEncontrado { get; private set; }
        public string OSFull { get; }
        public string Rota { get; private set; }
        public string IdSigfi { get; private set; }
        public bool Is160 => Chk160.IsChecked == true;
        public string TipoSistema => Is160 ? "SIGFI160" : "OUTRO";

        // Guarda a UF selecionada no MainWindow
        private readonly string _ufPrefixo;

        // Conjunto de registros carregados em memória (para tentar resolver cliente/rota)
        private readonly IList<ClientRecord> _records;

        // Busca auxiliar no arquivo de instalação
        private readonly InstalacaoService _instalacaoService = new InstalacaoService();

        // Modo manual (permite ID livre e escolha manual da rota)
        private readonly bool _allowAnyId;

        // Conclusão (opcional, para MainWindow saber que renomeou com sucesso)
        public bool RenamedSuccessfully { get; private set; }
        public string? LastDestination =>
            _allowAnyId && _installRenamer != null ?
                _installRenamer.LastDestination : _renamer?.LastDestination;

        public FallbackWindow(string osFull,
                              IEnumerable<string> rotas,
                              string uf,
                              IEnumerable<ClientRecord> records,
                              RenamerService renamer,
                              string sourceFolder,
                              bool allowAnyId = false,
                              InstallationRenamerService? installRenamer = null)
        {
            InitializeComponent();

            _renamer = renamer ?? throw new ArgumentNullException(nameof(renamer));
            _installRenamer = installRenamer;
            _sourceFolder = sourceFolder ?? throw new ArgumentNullException(nameof(sourceFolder));

            OSFull = osFull ?? string.Empty;
            _ufPrefixo = (uf ?? "").ToUpperInvariant();
            _allowAnyId = allowAnyId;
            _records = records?.ToList() ?? new List<ClientRecord>();

            foreach (var r in rotas.Distinct().OrderBy(x => x))
                CmbRota.Items.Add(new ComboBoxItem { Content = r });

            // Em modo manual, rota pode ser escolhida pelo usuário
            CmbRota.IsEnabled = allowAnyId;

            ClienteEncontrado = null;
            if (_allowAnyId)
                LblCliente.Content = "Modo manual - cliente não verificado.";

            Validate();
            _carregando = false;
        }

        /* ======================
           Validação / Busca
           ====================== */

        private void Validate()
        {
            bool idValido =
                !string.IsNullOrWhiteSpace(TxtIdSigfi.Text) &&
                TxtIdSigfi.Text.Length >= 5;

            if (_allowAnyId)
            {
                // precisa selecionar rota manualmente
                BtnOk.IsEnabled = idValido && CmbRota.SelectedItem != null;
            }
            else
            {
                // precisa ter resolvido um cliente
                BtnOk.IsEnabled = idValido && !string.IsNullOrEmpty(ClienteEncontrado);
            }
        }

        private async Task BuscarClientePorIdSigfiAsync(string idSigfi)
        {
            try
            {
                var record = await Task.Run(() =>
                    _records.FirstOrDefault(r =>
                        r.IdSigfi.Equals(idSigfi, StringComparison.OrdinalIgnoreCase) ||
                        (r.UF + r.IdSigfi).Equals(idSigfi, StringComparison.OrdinalIgnoreCase)));

                (string NomeCliente, string Rota)? encontrado = null;

                if (record != null)
                {
                    encontrado = (record.NomeCliente, record.Rota);
                }
                else if (_allowAnyId)
                {
                    encontrado = await Task.Run(() =>
                        _instalacaoService.BuscarPorIdSigfi(_ufPrefixo, idSigfi));
                }

                Dispatcher.Invoke(() =>
                {
                    if (encontrado != null)
                    {
                        ClienteEncontrado = encontrado.Value.NomeCliente;
                        LblCliente.Content = $"Cliente: {ClienteEncontrado}";

                        var rotaEncontrada = encontrado.Value.Rota;
                        Rota = rotaEncontrada;

                        // Seleciona rota na combo (ou adiciona se não existir)
                        bool achou = false;
                        foreach (ComboBoxItem item in CmbRota.Items)
                        {
                            if (string.Equals(item.Content?.ToString(), rotaEncontrada, StringComparison.OrdinalIgnoreCase))
                            {
                                CmbRota.SelectedItem = item;
                                achou = true;
                                break;
                            }
                        }
                        if (!achou && !string.IsNullOrEmpty(rotaEncontrada))
                        {
                            var novo = new ComboBoxItem { Content = rotaEncontrada };
                            CmbRota.Items.Add(novo);
                            CmbRota.SelectedItem = novo;
                        }
                    }
                    else
                    {
                        ClienteEncontrado = null;
                        LblCliente.Content = "Cliente não encontrado.";
                        Rota = string.Empty;
                        CmbRota.SelectedIndex = -1;
                    }

                    Validate();
                });
            }
            catch
            {
                Dispatcher.Invoke(() =>
                {
                    ClienteEncontrado = null;
                    LblCliente.Content = "Erro ao buscar cliente.";
                    Rota = string.Empty;
                    CmbRota.SelectedIndex = -1;
                    Validate();
                });
            }
        }

        private void CmbRota_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_allowAnyId)
            {
                var item = CmbRota.SelectedItem as ComboBoxItem;
                Rota = item?.Content?.ToString();
            }
            Validate();
        }

        private async void TxtIdSigfi_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_carregando) return;

            string texto = (TxtIdSigfi.Text ?? "").Trim();
            string somenteDigitos = new string(texto.Where(char.IsDigit).ToArray());
            if (somenteDigitos != texto)
            {
                TxtIdSigfi.Text = somenteDigitos;
                TxtIdSigfi.SelectionStart = somenteDigitos.Length;
                return;
            }

            texto = somenteDigitos;

            // aguarda ter pelo menos 5 dígitos
            if (texto.Length < 5)
            {
                ClienteEncontrado = null;
                LblCliente.Content = "ID SIGFI incompleto (mínimo 5 dígitos).";
                Rota = string.Empty;
                if (!_allowAnyId) CmbRota.SelectedIndex = -1;
                Validate();
                return;
            }

            string idCompleto = _ufPrefixo + texto;

            if (_allowAnyId)
            {
                // modo manual: não exige cliente, mas tenta enriquecer
                LblCliente.Content = "Buscando cliente…";
                try
                {
                    await BuscarClientePorIdSigfiAsync(idCompleto);
                }
                catch
                {
                    // silencioso
                }
                Validate();
            }
            else
            {
                // modo normal: precisa encontrar cliente
                LblCliente.Content = "Buscando cliente…";
                Validate(); // mantém OK desabilitado
                await BuscarClientePorIdSigfiAsync(idCompleto);
            }
        }

        private void Chk160_Checked(object sender, RoutedEventArgs e) => Validate();

        /* ======================
           OK / Cancelar
           ====================== */

        private ClientRecord BuildRecord()
        {
            // Prepara um ClientRecord com o mínimo necessário para o Renamer
            var rec = new ClientRecord
            {
                Rota = Rota ?? string.Empty,
                Tipo = "CORRETIVA", // evita cair na regra PREVENTIVA que usaria OBRA
                NumOS = OSFull ?? string.Empty,
                NumOcorrencia = "INST", // marcador genérico
                Obra = string.Empty,
                IdSigfi = IdSigfi ?? string.Empty,
                UC = _ufPrefixo, // usamos a UF como UC quando não houver melhor
                NomeCliente = ClienteEncontrado ?? string.Empty,
                Empresa = string.Empty,
                TipoDesigfi = TipoSistema.ToUpperInvariant(), // "SIGFI160" ou "OUTRO"
                UF = _ufPrefixo,
                NomeArquivoBase = string.Empty
            };
            return rec;
        }

        private async void Ok_Click(object sender, RoutedEventArgs e)
        {
            // validações mínimas
            if (string.IsNullOrWhiteSpace(TxtIdSigfi.Text) || TxtIdSigfi.Text.Length < 5)
            {
                System.Windows.MessageBox.Show(this, "Complete o ID SIGFI.", "Atenção", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_allowAnyId && CmbRota.SelectedItem == null)
            {
                System.Windows.MessageBox.Show(this, "Selecione a rota.", "Atenção", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Captura finais
            IdSigfi = _ufPrefixo + TxtIdSigfi.Text.Trim();
            if (_allowAnyId)
                Rota = (CmbRota.SelectedItem as ComboBoxItem)?.Content?.ToString();

            // UI: preparar progresso
            BtnOk.IsEnabled = false;
            BtnCancel.IsEnabled = false;
            RenBox.Visibility = Visibility.Visible;
            RenProgress.Value = 0;
            RenPercent.Text = "0%";

            try
            {
                var progress = new Progress<double>(v =>
                {
                    var clamped = Math.Max(0, Math.Min(100, v));
                    RenProgress.Value = clamped;
                    RenPercent.Text = $"{clamped:0}%";
                });

                if (_allowAnyId && _installRenamer != null)
                {
                    await _installRenamer.RenameInstallationAsync(
                        _sourceFolder,
                        _ufPrefixo,
                        IdSigfi,
                        Rota ?? string.Empty,
                        ClienteEncontrado ?? string.Empty,
                        Is160,
                        progress);

                    RenamedSuccessfully = true;

                    System.Windows.MessageBox.Show(this,
                        $"Arquivos processados com sucesso!\nDestino:\n{_installRenamer.LastDestination}",
                        "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var rec = BuildRecord();

                    await _renamer.RenameAsync(_sourceFolder, rec, progress);

                    RenamedSuccessfully = true;

                    System.Windows.MessageBox.Show(this,
                        $"Arquivos processados com sucesso!\nDestino:\n{_renamer.LastDestination}",
                        "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                DialogResult = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                System.Windows.MessageBox.Show(this, "Operação cancelada.", "Cancelado",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                BtnOk.IsEnabled = true;
                BtnCancel.IsEnabled = true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, $"Falha ao processar arquivos:\n{ex.Message}",
                                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnOk.IsEnabled = true;
                BtnCancel.IsEnabled = true;
            }
            finally
            {
                // mantém a barra no estado final até fechar/novo clique
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
