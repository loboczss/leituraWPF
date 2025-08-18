// Services/RenamerService.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualBasic; // para InputBox
using leituraWPF.Models;

namespace leituraWPF.Services
{
    /// <summary>
    /// Renomeia e move arquivos segundo as regras fornecidas, sem logging.
    /// Usa os dados do ClientRecord (derivados dos JSON baixados).
    /// </summary>
    public class RenamerService
    {
        #region Constantes / caches
        private const string RaizOneEng = "ONE ENGENHARIA INDUSTRIA E COMERCIO LTDA";

        private static readonly HashSet<string> ImgExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg"
        };

        // "" = sem extensão (alguns controladores vêm sem extensão)
        private static readonly HashSet<string> ControlInvBatExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".csv",".txt", ".xls", ".xlsx", string.Empty
        };

        private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Distinct()
            .ToArray();
        #endregion

        public string LastDestination { get; private set; } = string.Empty;

        public event Action<string>? FileReadyForBackup;

        #region Helpers
        private static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return string.Concat(input.Split(InvalidFileChars, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string CreateSafeDir(string path)
        {
            Directory.CreateDirectory(path); // idempotente
            return path;
        }

        private static bool SameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

        private static void MoveOverwrite(string src, string dst)
        {
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }
        #endregion

        #region Pasta base OneDrive
        public string ResolveBaseDir(string uf)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var raiz = Path.Combine(home, RaizOneEng);

            bool isMt = string.Equals(uf, "MT", StringComparison.OrdinalIgnoreCase);
            string mask = isMt ? "ONE Engenharia - LOGIN_W_{0:D3}_R_MT"
                               : "ONE Engenharia - Clientes PC ONE {0:D3}";

            var candidatas = Enumerable.Range(1, 100)
                                       .Select(n => Path.Combine(raiz, string.Format(mask, n)))
                                       .Where(Directory.Exists)
                                       .ToArray();

            if (candidatas.Length == 1) return candidatas[0];

            if (candidatas.Length == 0)
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var fallback = Path.Combine(docs, "OrganizadorArquivos");
                Directory.CreateDirectory(fallback);
                return fallback;
            }

            int escolha = 0;
            while (escolha < 1 || escolha > 100)
            {
                var prompt = isMt
                    ? "Digite 1–100 para LOGIN_W_*_R_MT:"
                    : "Digite 1–100 para Clientes PC ONE *:";

                int.TryParse(
                    Interaction.InputBox(prompt, "Número da pasta", "1"),
                    out escolha);
            }

            var destino = Path.Combine(raiz, string.Format(mask, escolha));
            Directory.CreateDirectory(destino);
            return destino;
        }
        #endregion

        #region Classificação
        private (List<string> Controllers, string? Inv, string? Bat, List<string> Images)
            ClassifyFiles(IEnumerable<string> files)
        {
            var controllers = new List<string>();
            string? inv = null, bat = null;
            var images = new List<string>();

            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var ext = (Path.GetExtension(file) ?? string.Empty).ToLowerInvariant();

                // 1) Imagens
                if (ImgExts.Contains(ext))
                {
                    images.Add(file);
                    continue;
                }

                // 2) Controladores (con/c0n) – pode não ter extensão
                if (fileName.StartsWith("con", StringComparison.OrdinalIgnoreCase) ||
                    fileName.StartsWith("c0n", StringComparison.OrdinalIgnoreCase))
                {
                    if (ControlInvBatExts.Contains(ext)) controllers.Add(file);
                    continue;
                }

                // 3) Inversor (inv*)
                if (inv == null && fileName.StartsWith("inv", StringComparison.OrdinalIgnoreCase))
                {
                    if (ControlInvBatExts.Contains(ext)) inv = file;
                    continue;
                }

                // 4) Bateria (bat*)
                if (bat == null && fileName.StartsWith("bat", StringComparison.OrdinalIgnoreCase))
                {
                    if (ControlInvBatExts.Contains(ext)) bat = file;
                }
            }

            return (controllers, inv, bat, images);
        }
        #endregion

        #region RenameAsync
        /// <summary>
        /// Renomeia/move arquivos de <paramref name="sourceFolder"/> para a pasta de destino,
        /// usando dados de <paramref name="record"/>. Sem logs externos.
        /// </summary>
        public Task RenameAsync(
            string sourceFolder,
            ClientRecord record,
            IProgress<double>? progress = null)
        {
            return Task.Run(() =>
            {
                void Report(double v) => progress?.Report(v);

                if (record == null) throw new ArgumentNullException(nameof(record));
                if (string.IsNullOrWhiteSpace(sourceFolder)) throw new ArgumentNullException(nameof(sourceFolder));

                // Empresa e TipoDesigfi vêm do JSON e alimentam as regras
                string sistema = (record.Empresa ?? string.Empty).ToUpperInvariant();
                string tipoSistema = record.TipoDesigfi ?? string.Empty;
                bool isSistema160 = string.Equals(tipoSistema, "SIGFI160", StringComparison.OrdinalIgnoreCase);

                Report(0);

                // 1) Validação da origem
                if (!Directory.Exists(sourceFolder) || !Directory.EnumerateFiles(sourceFolder).Any())
                {
                    // Mostra alerta visual sem travar o programa
                    MessageBox.Show(
                        "A pasta de origem não contém arquivos. Selecione arquivos para continuar.",
                        "Aviso",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return; // Sai do método sem travar o app
                }


                // 2) Estrutura de destino
                string root = ResolveBaseDir(record.UF);
                string rotaDir = CreateSafeDir(Path.Combine(root, Sanitize(record.Rota)));
                string clienteDir = CreateSafeDir(Path.Combine(rotaDir,
                    Sanitize($"{record.NumOS}_{record.IdSigfi}_{record.Tipo}")));
                LastDestination = clienteDir;

                // 3) Verifica destino vazio
                if (Directory.EnumerateFileSystemEntries(clienteDir).Any())
                {
                    MessageBox.Show("A pasta de destino já contém arquivos.",
                                    "Destino Não Vazio",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    throw new OperationCanceledException("Destino não vazio.");
                }

                Report(10);

                // 4) Classificação
                var files = Directory.EnumerateFiles(sourceFolder).ToList();
                var (controllers, inv, bat, images) = ClassifyFiles(files);

                // 5) Valida nº de controladores conforme sistema
                int reqCtrl;
                if (sistema == "INTELBRAS")
                {
                    reqCtrl = 1;
                    if (controllers.Count != 1)
                        throw new FileNotFoundException("[INTELBRAS] Requerido: 1 controlador.");
                }
                else if (sistema == "HOPPECKE" && isSistema160)
                {
                    reqCtrl = 2;
                    if (controllers.Count != 2)
                        throw new FileNotFoundException("[HOPPECKE 160] Requerido: 2 controladores.");
                }
                else
                {
                    reqCtrl = 1; // genérico
                    if (controllers.Count < 1)
                        throw new FileNotFoundException("Requerido: pelo menos 1 controlador.");
                }

                // 6) Nome base
                string nomeBase = Sanitize(string.Join("_", new[]
                {
                    record.UC,
                    record.Tipo != "PREVENTIVA" ? record.NumOcorrencia : record.Obra,
                    record.NomeCliente,
                    record.NumOS,
                    record.IdSigfi
                }));

                // 7) Progresso (80% do total após 10%)
                int totalSteps = controllers.Count + (inv != null ? 1 : 0) + (bat != null ? 1 : 0) + images.Count + 2;
                totalSteps = Math.Max(totalSteps, 1);
                int done = 0;
                void Step() => Report(10 + (++done * 80.0 / totalSteps));

                // 8) Função mover/renomear
                void MoveRen(string src, string suf)
                {
                    var ext = Path.GetExtension(src);
                    var dst = Path.Combine(clienteDir, Sanitize($"{nomeBase}{suf}{ext}"));

                    if (SameVolume(src, dst))
                        MoveOverwrite(src, dst);
                    else
                    {
                        File.Copy(src, dst, true);
                        File.Delete(src);
                    }

                    try { FileReadyForBackup?.Invoke(dst); } catch { /* ignore */ }

                    Step();
                }

                // 9) Controladores (_CON ou _CON1/_CON2)
                for (int i = 0; i < controllers.Count; i++)
                {
                    var suf = (sistema == "HOPPECKE" && isSistema160)
                              ? (i == 0 ? "_CON1" : "_CON2")
                              : "_CON";
                    MoveRen(controllers[i], suf);
                }

                // 10) Inversor e bateria
                if (inv != null) MoveRen(inv, "_INV");
                if (bat != null) MoveRen(bat, "_BAT");

                // 11) Imagens (PRINT001, PRINT002…)
                for (int i = 0; i < images.Count; i++)
                    MoveRen(images[i], $"_PRINT{i + 1:D3}");

                Report(100);
            });
        }
        #endregion

        // (Opcional) Enumerar bases existentes — útil se quiser mostrar numa combo
        public static IEnumerable<string> EnumerarPastasBase()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var raiz = Path.Combine(home, RaizOneEng);

            IEnumerable<string> Enumerar(string mask) =>
                Enumerable.Range(1, 100)
                          .Select(n => Path.Combine(raiz, string.Format(mask, n)))
                          .Where(Directory.Exists);

            foreach (var dir in Enumerar("ONE Engenharia - Clientes PC ONE {0:D3}"))
                yield return dir;

            foreach (var dir in Enumerar("ONE Engenharia - LOGIN_W_{0:D3}_R_MT"))
                yield return dir;

            var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                    "OrganizadorArquivos");
            if (Directory.Exists(docs))
                yield return docs;
        }
    }
}
