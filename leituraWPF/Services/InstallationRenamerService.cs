using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace leituraWPF.Services
{
    /// <summary>
    /// Renomeia/move arquivos de INSTALAÇÃO.
    /// Destino: {BasePorUF}\{ROTA}\{IdSigfi}_INSTALACAO
    /// Nome base: UF_NomeCliente_IdSigfi_INSTALACAO
    /// </summary>
    public sealed class InstallationRenamerService
    {
        private const string RaizOneEng = "ONE ENGENHARIA INDUSTRIA E COMERCIO LTDA";

        private static readonly HashSet<string> ImgExts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };
        private static readonly HashSet<string> ControlInvBatExts = new(StringComparer.OrdinalIgnoreCase) { ".csv", ".txt", ".xls", ".xlsx", string.Empty };
        private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct().ToArray();

        public string LastDestination { get; private set; } = string.Empty;

        public event Action<string>? FileReadyForBackup;

        private static string Sanitize(string s) =>
            string.IsNullOrWhiteSpace(s) ? string.Empty : string.Concat(s.Split(InvalidFileChars, StringSplitOptions.RemoveEmptyEntries));

        private static string CreateDir(string p) { Directory.CreateDirectory(p); return p; }

        private static bool SameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

        private static void MoveOverwrite(string src, string dst)
        {
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }

        private static string ResolveBaseDir(string uf)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var raiz = Path.Combine(home, RaizOneEng);
            bool isMt = string.Equals(uf, "MT", StringComparison.OrdinalIgnoreCase);
            string mask = isMt ? "ONE Engenharia - LOGIN_W_{0:D3}_R_MT" : "ONE Engenharia - Clientes PC ONE {0:D3}";
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
            return candidatas[0]; // heurística simples
        }

        private static (List<string> Controllers, string Inv, string Bat, List<string> Images)
            Classify(IEnumerable<string> files)
        {
            var controllers = new List<string>();
            string inv = null, bat = null; var images = new List<string>();
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var ext = (Path.GetExtension(f) ?? "").ToLowerInvariant();

                if (ImgExts.Contains(ext)) { images.Add(f); continue; }
                if (name.StartsWith("con", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("c0n", StringComparison.OrdinalIgnoreCase))
                { if (ControlInvBatExts.Contains(ext)) controllers.Add(f); continue; }
                if (inv == null && name.StartsWith("inv", StringComparison.OrdinalIgnoreCase))
                { if (ControlInvBatExts.Contains(ext)) inv = f; continue; }
                if (bat == null && name.StartsWith("bat", StringComparison.OrdinalIgnoreCase))
                { if (ControlInvBatExts.Contains(ext)) bat = f; }
            }
            return (controllers, inv, bat, images);
        }

        public async Task RenameInstallationAsync(
            string sourceFolder, string uf, string idSigfi, string rota, string nomeCliente, bool isSigfi160,
            IProgress<double> progress = null, CancellationToken ct = default)
        {
            await Task.Run(() =>
            {
                void Report(double v) => progress?.Report(v);

                if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder) || !Directory.EnumerateFiles(sourceFolder).Any())
                    throw new IOException("A pasta de origem não contém arquivos.");

                string baseDir = ResolveBaseDir(uf);
                string rotaDir = CreateDir(Path.Combine(baseDir, Sanitize(string.IsNullOrWhiteSpace(rota) ? "SEM_ROTA" : rota)));
                string destino = CreateDir(Path.Combine(rotaDir, Sanitize($"{idSigfi}_INSTALACAO")));
                LastDestination = destino;

                if (Directory.EnumerateFileSystemEntries(destino).Any())
                    throw new OperationCanceledException("A pasta de destino já contém arquivos.");

                var files = Directory.EnumerateFiles(sourceFolder).ToList();
                var (controllers, inv, bat, images) = Classify(files);

                if (isSigfi160)
                {
                    if (controllers.Count != 2)
                    {
                        System.Windows.MessageBox.Show(
                            "[INSTALAÇÃO SIGFI160] Requer 2 controladores.",
                            "Aviso",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                }
                else
                {
                    if (controllers.Count < 1)
                    {
                        System.Windows.MessageBox.Show(
                            "[INSTALAÇÃO] Requer pelo menos 1 controlador.",
                            "Aviso",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                }

                string nomeBase = Sanitize(string.Join("_", new[] { (uf ?? "").ToUpperInvariant(), nomeCliente ?? "", idSigfi ?? "", "INSTALACAO" }));

                int total = Math.Max(controllers.Count + (inv != null ? 1 : 0) + (bat != null ? 1 : 0) + images.Count, 1);
                int done = 0; void Step() => Report(10 + (++done * 90.0 / total));

                void MoveRen(string src, string suf)
                {
                    var ext = Path.GetExtension(src);
                    var dst = Path.Combine(destino, Sanitize($"{nomeBase}{suf}{ext}"));
                    if (SameVolume(src, dst)) MoveOverwrite(src, dst); else { File.Copy(src, dst, true); File.Delete(src); }
                    try { FileReadyForBackup?.Invoke(dst); } catch { /* ignore */ }
                    Step();
                }

                Report(10);

                if (isSigfi160)
                {
                    var ord = controllers.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
                    MoveRen(ord[0], "_CON1"); MoveRen(ord[1], "_CON2");
                }
                else
                {
                    foreach (var c in controllers) MoveRen(c, "_CON");
                }

                if (inv != null) MoveRen(inv, "_INV");
                if (bat != null) MoveRen(bat, "_BAT");
                for (int i = 0; i < images.Count; i++) MoveRen(images[i], $"_PRINT{i + 1:D3}");

                Report(100);
            }, ct);
        }
    }
}
