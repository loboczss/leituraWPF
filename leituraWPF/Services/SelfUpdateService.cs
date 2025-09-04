// Services/SelfUpdateService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace leituraWPF.Services
{
    /// <summary>
    /// Atualizador totalmente gerenciado (sem cmd/powershell).
    /// Baixa release do GitHub, prepara staging e chama o UpdaterHost.exe.
    /// </summary>
    public class SelfUpdateService : IUpdateService
    {
        private const string ApiUrl = "https://api.github.com/repos/loboczss/leituraWPF/releases/latest";
        private const string AppProductName = "leituraWPF";
        private const string AppExeName = "leituraWPF.exe";
        private const string UpdaterHostName = "UpdaterHost.exe";
        private const int MaxRetryAttempts = 3;
        private const int TimeoutSeconds = 60;

        private static string InstallDir => AppDomain.CurrentDomain.BaseDirectory;

        public const string UpdateSuccessMarkerFile = "update_success.flag";
        public const string UpdateErrorMarkerFile = "update_error.flag";
        public const string UpdateLogFile = "update.log";

        public static string UpdateSuccessMarkerPath => Path.Combine(InstallDir, UpdateSuccessMarkerFile);
        public static string UpdateErrorMarkerPath => Path.Combine(InstallDir, UpdateErrorMarkerFile);
        public static string UpdateLogPath => Path.Combine(InstallDir, UpdateLogFile);

        private readonly ILogger _logger;

        public SelfUpdateService(ILogger logger = null)
        {
            _logger = logger ?? new DefaultLogger();
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
        {
            var r = new UpdateCheckResult();
            try
            {
                _logger.LogInfo("Verificando atualizações (gerenciado)...");

                CleanupMarkerFiles();

                var tuple = await GetVersionsWithRetryAsync(ct);
                var local = tuple.Local;
                var remote = tuple.Remote;
                var ok = tuple.Ok;

                r.LocalVersion = local;
                r.RemoteVersion = remote;
                r.RemoteFetchSuccessful = ok;

                if (!ok)
                {
                    r.Message = "Falha ao consultar o servidor de atualizações";
                    _logger.LogWarning(r.Message);
                    return r;
                }

                r.UpdateAvailable = remote > local;
                r.Success = true;

                if (r.UpdateAvailable)
                    _logger.LogInfo($"Atualização disponível: {local} → {remote}");
                else
                    _logger.LogInfo($"Versão atual ({local}) já é a mais recente.");

                return r;
            }
            catch (OperationCanceledException)
            {
                r.Message = "Operação cancelada";
                _logger.LogWarning(r.Message);
                return r;
            }
            catch (Exception ex)
            {
                r.Message = ex.Message;
                _logger.LogError("Erro ao verificar atualizações", ex);
                return r;
            }
        }

        public async Task<UpdatePerformResult> PerformUpdateAsync(CancellationToken ct = default)
        {
            var r = new UpdatePerformResult();
            string zipPath = null;
            string stagingDir = null;

            try
            {
                _logger.LogInfo("Iniciando atualização (gerenciado, sem shell)...");

                var check = await CheckForUpdatesAsync(ct);
                if (!check.Success || !check.UpdateAvailable)
                {
                    r.Success = check.Success;
                    r.RemoteFetchSuccessful = check.RemoteFetchSuccessful;
                    r.Message = check.Message ?? "Sem atualização disponível.";
                    return r;
                }

                var validation = ValidateUpdateEnvironment();
                if (!validation.Success)
                {
                    r.Message = validation.Message;
                    return r;
                }

                _logger.LogInfo("Baixando release...");
                zipPath = await DownloadWithValidationAsync(ct);

                _logger.LogInfo("Preparando staging a partir do ZIP...");
                stagingDir = PrepareStagingDirectoryFromZip(zipPath);

                var updaterPath = Path.Combine(InstallDir, UpdaterHostName);
                if (!File.Exists(updaterPath))
                    throw new InvalidOperationException($"'{UpdaterHostName}' não encontrado em {InstallDir}. Compile/copiar o projeto UpdaterHost junto ao app.");

                var args = BuildUpdaterArgs(
                    installDir: InstallDir,
                    stagingDir: stagingDir,
                    appExeName: AppExeName,
                    parentPid: Process.GetCurrentProcess().Id,
                    successFlag: UpdateSuccessMarkerPath,
                    errorFlag: UpdateErrorMarkerPath,
                    logPath: UpdateLogPath,
                    oldVersion: check.LocalVersion?.ToString() ?? string.Empty,
                    newVersion: check.RemoteVersion?.ToString() ?? string.Empty,
                    createShortcut: true,
                    shortcutName: "CompillerLog.lnk"
                );

                _logger.LogInfo("Disparando UpdaterHost...");
                var psi = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = args,
                    UseShellExecute = true,
                    WorkingDirectory = InstallDir
                };

                var p = Process.Start(psi);
                if (p == null)
                    throw new InvalidOperationException("Falha ao iniciar UpdaterHost.");

                // Aguarda brevemente para detectar encerramento imediato
                await Task.Delay(1000, ct);
                if (!p.HasExited)
                {
                    // Aguarda até que o UpdaterHost esteja pronto para receber
                    // entrada (janela inicializada). Caso contrário, consideramos
                    // que houve falha ao inicializar e abortamos a atualização.
                    try
                    {
                        if (!p.WaitForInputIdle(5000))
                        {
                            if (p.HasExited)
                            {
                                var code = p.ExitCode;
                                p.Dispose();
                                throw new InvalidOperationException($"UpdaterHost finalizado prematuramente (código {code}).");
                            }
                            throw new InvalidOperationException("UpdaterHost não ficou pronto a tempo.");
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        if (p.HasExited)
                        {
                            var code = p.ExitCode;
                            p.Dispose();
                            throw new InvalidOperationException($"UpdaterHost finalizado prematuramente (código {code}).");
                        }
                        throw;
                    }
                }
                else if (p.ExitCode != 0)
                {
                    // Saiu imediatamente com erro
                    var code = p.ExitCode;
                    p.Dispose();
                    throw new InvalidOperationException($"UpdaterHost finalizado prematuramente (código {code}).");
                }
                else
                {
                    // Saiu com sucesso rápido → provavelmente relançou elevado
                    _logger.LogInfo("UpdaterHost reiniciado para elevação.");
                }

                _logger.LogInfo("UpdaterHost iniciado. Feche o app para permitir a troca segura dos arquivos.");
                r.Success = true;
                r.RemoteFetchSuccessful = true;
                r.Message = "Atualização iniciada.";
                return r;
            }
            catch (OperationCanceledException)
            {
                r.Message = "Atualização cancelada.";
                _logger.LogWarning(r.Message);
                return r;
            }
            catch (Exception ex)
            {
                r.Message = ex.Message;
                _logger.LogError("Erro durante a atualização", ex);
                return r;
            }
            finally
            {
                try { if (zipPath != null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                // stagingDir é limpo pelo UpdaterHost ao final (sucesso/rollback)
            }
        }

        // ====== internos ======

        private async Task<(Version Local, Version Remote, bool Ok)> GetVersionsWithRetryAsync(CancellationToken ct)
        {
            var local = GetLocalVersionSafely();
            for (int i = 1; i <= MaxRetryAttempts; i++)
            {
                try
                {
                    using (var http = CreateHttp())
                    {
                        var json = await http.GetStringAsync(ApiUrl, ct);
                        if (string.IsNullOrWhiteSpace(json))
                            throw new InvalidOperationException("Resposta vazia do servidor");

                        var obj = JObject.Parse(json);
                        var tag = obj["tag_name"]?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(tag))
                            throw new InvalidOperationException("tag_name não encontrada");

                        var remote = ParseVersionFromTag(tag);
                        if (remote == null)
                            throw new InvalidOperationException($"Não foi possível parsear a versão: {tag}");

                        _logger.LogInfo($"Local: {local} | Remota: {remote}");
                        return (local, remote, true);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Tentativa {i}/{MaxRetryAttempts} falhou: {ex.Message}");
                    if (i == MaxRetryAttempts)
                    {
                        _logger.LogError("Falha final ao obter versão remota.", ex);
                        return (local, local, false);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)), ct);
                }
            }
            return (local, local, false);
        }

        private Version GetLocalVersionSafely()
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null) return v;
            }
            catch { }

            var candidates = new[]
            {
                Path.Combine(InstallDir, $"{AppProductName}.dll"),
                Path.Combine(InstallDir, AppExeName),
                Path.Combine(InstallDir, $"{AppProductName}.exe")
            };
            foreach (var c in candidates.Where(File.Exists))
            {
                try
                {
                    var v = AssemblyName.GetAssemblyName(c).Version;
                    if (v != null) return v;
                }
                catch { }
            }
            return new Version(0, 0, 0, 0);
        }

        private async Task<string> DownloadWithValidationAsync(CancellationToken ct)
        {
            for (int i = 1; i <= MaxRetryAttempts; i++)
            {
                try
                {
                    using (var http = CreateHttp())
                    {
                        var json = await http.GetStringAsync(ApiUrl, ct);
                        var obj = JObject.Parse(json);
                        var assets = (JArray)obj["assets"];
                        if (assets == null || assets.Count == 0)
                            throw new InvalidOperationException("Nenhum asset no release.");

                        var asset = assets.FirstOrDefault(a =>
                        {
                            var name = a?["name"]?.ToString() ?? "";
                            return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                        });
                        if (asset == null)
                            throw new InvalidOperationException("Nenhum .zip encontrado.");

                        var url = asset["browser_download_url"]?.ToString();
                        var nameFile = asset["name"]?.ToString();
                        var sizeStr = asset["size"]?.ToString();

                        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(nameFile))
                            throw new InvalidOperationException("Asset inválido.");

                        long expected = 0;
                        long.TryParse(sizeStr, out expected);
                        var tempZip = Path.Combine(Path.GetTempPath(), $"{AppProductName}_upd_{Guid.NewGuid():N}.zip");

                        var bytes = await http.GetByteArrayAsync(url, ct);
                        if (expected > 0 && bytes.LongLength != expected)
                            throw new InvalidOperationException($"Tamanho inesperado. Esperado {expected}, baixado {bytes.LongLength}");

                        File.WriteAllBytes(tempZip, bytes);

                        using (var z = ZipFile.OpenRead(tempZip))
                        {
                            if (z.Entries == null || z.Entries.Count == 0)
                            {
                                File.Delete(tempZip);
                                throw new InvalidOperationException("ZIP vazio/corrompido.");
                            }
                        }
                        return tempZip;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Download tentativa {i}/{MaxRetryAttempts} falhou: {ex.Message}");
                    if (i == MaxRetryAttempts) throw;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)), ct);
                }
            }
            return null;
        }

        private (bool Success, string Message) ValidateUpdateEnvironment()
        {
            try
            {
                if (!Directory.Exists(InstallDir))
                    return (false, "Diretório de instalação não encontrado.");

                var test = Path.Combine(InstallDir, $"wtest_{Guid.NewGuid():N}.tmp");
                try { File.WriteAllText(test, "ok"); File.Delete(test); }
                catch { /* sem permissão agora; UpdaterHost tentará elevar */ }

                var drive = new DriveInfo(Path.GetPathRoot(InstallDir) ?? "C:");
                if (drive.AvailableFreeSpace < 100 * 1024 * 1024)
                    return (false, "Espaço em disco insuficiente (<100MB).");

                return (true, "ok");
            }
            catch (Exception ex) { return (false, $"Falha na validação: {ex.Message}"); }
        }

        private string PrepareStagingDirectoryFromZip(string zipPath)
        {
            var stagingRoot = Path.Combine(Path.GetTempPath(), $"{AppProductName}_staging_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingRoot);
            ZipFile.ExtractToDirectory(zipPath, stagingRoot);

            var sub = Directory.GetDirectories(stagingRoot);
            var filesAtRoot = Directory.GetFiles(stagingRoot);
            if (sub.Length == 1 && filesAtRoot.Length == 0)
                return sub[0];

            return stagingRoot;
        }

        private string BuildUpdaterArgs(string installDir, string stagingDir, string appExeName,
            int parentPid, string successFlag, string errorFlag, string logPath,
            string oldVersion, string newVersion,
            bool createShortcut, string shortcutName)
        {
            var sb = new StringBuilder();
            void A(string k, string v) { sb.Append(' ').Append(k).Append(' ').Append(EscapeArg(v)); }
            void B(string k, int v) { sb.Append(' ').Append(k).Append(' ').Append(v); }
            void C(string k, bool v) { sb.Append(' ').Append(k).Append(' ').Append(v ? "true" : "false"); }

            A("--install", installDir);
            A("--staging", stagingDir);
            A("--exe", appExeName);
            B("--pid", parentPid);
            A("--success", successFlag);
            A("--error", errorFlag);
            A("--log", logPath);
            A("--oldVersion", oldVersion);
            A("--newVersion", newVersion);
            C("--shortcut", createShortcut);
            A("--shortcutName", shortcutName);

            return sb.ToString();
        }

        private static string EscapeArg(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            bool needQuotes = s.IndexOf(' ') >= 0 || s.IndexOf('\t') >= 0 || s.IndexOf('\"') >= 0 || s.EndsWith("\\");
            if (!needQuotes) return s;

            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in s)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    sb.Append(new string('\\', backslashes * 2 + 1));
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    sb.Append(new string('\\', backslashes));
                    backslashes = 0;
                    sb.Append(c);
                }
            }
            sb.Append(new string('\\', backslashes * 2));
            sb.Append('"');
            return sb.ToString();
        }

        private HttpClient CreateHttp()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var http = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            http.DefaultRequestHeaders.Add("User-Agent", $"{AppProductName}/1.0 (Windows NT 10.0; Win64; x64)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
            http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("deflate");
            return http;
        }

        private void CleanupMarkerFiles()
        {
            try
            {
                var files = new[] { UpdateSuccessMarkerPath, UpdateErrorMarkerPath };
                foreach (var f in files) if (File.Exists(f)) File.Delete(f);
            }
            catch (Exception ex) { _logger.LogWarning($"Falha ao limpar flags: {ex.Message}"); }
        }

        private static Version ParseVersionFromTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return new Version(0, 0, 0, 0);
            var s = new string(tagName.Trim().TrimStart('v', 'V')
                          .TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            Version v; return Version.TryParse(s, out v) ? v : new Version(0, 0, 0, 0);
        }
    }
}
