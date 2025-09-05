using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace leituraWPF.Services
{
    /// <summary>
    /// Serviço de autoatualização que funciona sem elevação de privilégios
    /// </summary>
    public class SelfUpdateService : IUpdateService
    {
        private const string ApiUrl = "https://api.github.com/repos/loboczss/leituraWPF/releases/latest";
        private const string AppExeName = "leituraWPF.exe";
        private const string UpdaterExe = "UpdaterHost.exe";

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
            var result = new UpdateCheckResult
            {
                LocalVersion = GetLocalVersion()
            };

            try
            {
                _logger.LogInfo($"Verificando atualizações. Versão local: {result.LocalVersion}");

                using var http = CreateHttpClient();
                var json = await http.GetStringAsync(ApiUrl, ct).ConfigureAwait(false);
                var obj = JObject.Parse(json);
                var tag = obj["tag_name"]?.ToString();

                result.RemoteVersion = ParseVersion(tag);
                result.RemoteFetchSuccessful = true;
                result.UpdateAvailable = result.RemoteVersion > result.LocalVersion;
                result.Success = true;

                _logger.LogInfo($"Versão remota: {result.RemoteVersion}. Atualização disponível: {result.UpdateAvailable}");
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                _logger.LogError("Falha ao verificar atualizações", ex);
            }

            return result;
        }

        public async Task<UpdatePerformResult> PerformUpdateAsync(CancellationToken ct = default)
        {
            var result = new UpdatePerformResult();

            try
            {
                _logger.LogInfo("Iniciando processo de atualização...");

                // Verifica permissões ANTES de fazer qualquer coisa
                if (!CheckWritePermissions())
                {
                    result.Success = false;
                    result.Message = "Permissões insuficientes para atualizar. Execute como administrador ou mova a aplicação para uma pasta com permissões de escrita.";
                    _logger.LogError("Permissões insuficientes para atualização");
                    return result;
                }

                // Limpa arquivos de status anteriores
                CleanupStatusFiles();

                var check = await CheckForUpdatesAsync(ct).ConfigureAwait(false);
                result.RemoteFetchSuccessful = check.RemoteFetchSuccessful;

                if (!check.Success || !check.UpdateAvailable)
                {
                    result.Success = false;
                    result.Message = check.Message ?? "Nenhuma atualização disponível.";
                    _logger.LogInfo($"Atualização não necessária: {result.Message}");
                    return result;
                }

                _logger.LogInfo($"Atualizando de {check.LocalVersion} para {check.RemoteVersion}");

                // Baixa informações do release
                using var http = CreateHttpClient();
                var json = await http.GetStringAsync(ApiUrl, ct).ConfigureAwait(false);
                var obj = JObject.Parse(json);
                var assets = (JArray)obj["assets"];

                if (assets == null || assets.Count == 0)
                    throw new InvalidOperationException("Nenhum asset encontrado no release.");

                // Procura por um arquivo .zip nos assets
                string downloadUrl = null;
                string assetName = null;
                foreach (var asset in assets)
                {
                    var name = asset["name"]?.ToString();
                    var url = asset["browser_download_url"]?.ToString();

                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url) &&
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = url;
                        assetName = name;
                        _logger.LogInfo($"Asset encontrado: {name}");
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(downloadUrl))
                    throw new InvalidOperationException("Pacote de atualização (.zip) não encontrado nos assets do release.");

                _logger.LogInfo($"Baixando: {assetName} de {downloadUrl}");

                // Cria pasta temporária com nome único
                var tempDir = Path.Combine(Path.GetTempPath(), "leituraWPF_Update");
                var uniqueDir = $"{tempDir}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N[..8]}";

                Directory.CreateDirectory(uniqueDir);
                _logger.LogInfo($"Pasta temporária criada: {uniqueDir}");

                // Baixa o ZIP
                var tempZip = Path.Combine(uniqueDir, "update.zip");

                using (var httpClient = CreateHttpClient())
                {
                    var response = await httpClient.GetAsync(downloadUrl, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    _logger.LogInfo($"Iniciando download: {totalBytes} bytes");

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = File.Create(tempZip))
                    {
                        await contentStream.CopyToAsync(fileStream, ct);
                    }
                }

                _logger.LogInfo($"Download concluído: {new FileInfo(tempZip).Length} bytes salvos");

                // Extrai para pasta de staging
                var staging = Path.Combine(uniqueDir, "staging");
                ZipFile.ExtractToDirectory(tempZip, staging);
                File.Delete(tempZip);

                _logger.LogInfo($"Arquivos extraídos para: {staging}");

                // Lista arquivos extraídos para debug
                var extractedFiles = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
                _logger.LogInfo($"Arquivos extraídos ({extractedFiles.Length}):");
                foreach (var file in extractedFiles)
                {
                    var relativePath = file.Substring(staging.Length).TrimStart(Path.DirectorySeparatorChar);
                    var fileInfo = new FileInfo(file);
                    _logger.LogInfo($"  - {relativePath} ({fileInfo.Length} bytes)");
                }

                // Verifica se o UpdaterHost existe
                var updaterPath = Path.Combine(InstallDir, UpdaterExe);
                if (!File.Exists(updaterPath))
                {
                    throw new FileNotFoundException($"UpdaterHost não encontrado em: {updaterPath}");
                }

                _logger.LogInfo("UpdaterHost encontrado. Iniciando processo de atualização...");

                // Monta argumentos para o UpdaterHost
                var currentPid = Process.GetCurrentProcess().Id;
                var args = $"--install \"{InstallDir}\" " +
                           $"--staging \"{staging}\" " +
                           $"--exe \"{AppExeName}\" " +
                           $"--pid {currentPid} " +
                           $"--success \"{UpdateSuccessMarkerPath}\" " +
                           $"--error \"{UpdateErrorMarkerPath}\" " +
                           $"--log \"{UpdateLogPath}\" " +
                           $"--old \"{check.LocalVersion}\" " +
                           $"--new \"{check.RemoteVersion}\"";

                _logger.LogInfo($"Executando: {updaterPath}");
                _logger.LogInfo($"Argumentos: {args}");

                // Inicia o UpdaterHost SEM elevação
                var startInfo = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = args,
                    UseShellExecute = false,
                    WorkingDirectory = InstallDir,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal
                };

                var updaterProcess = Process.Start(startInfo);

                if (updaterProcess != null)
                {
                    _logger.LogInfo($"UpdaterHost iniciado com PID: {updaterProcess.Id}");

                    // Aguarda um pouco para ver se o processo iniciou corretamente
                    await Task.Delay(1000, ct);

                    if (!updaterProcess.HasExited)
                    {
                        result.Success = true;
                        result.Message = "Processo de atualização iniciado com sucesso. A aplicação será reiniciada automaticamente.";
                        _logger.LogInfo("PerformUpdateAsync concluído com sucesso.");
                    }
                    else
                    {
                        var exitCode = updaterProcess.ExitCode;
                        throw new InvalidOperationException($"UpdaterHost falhou imediatamente com código de saída: {exitCode}");
                    }
                }
                else
                {
                    throw new InvalidOperationException("Falha ao iniciar o processo UpdaterHost");
                }
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                _logger.LogError("Falha ao iniciar atualização", ex);

                // Salva erro em arquivo para debug
                try
                {
                    var errorDetails = $"Erro no SelfUpdateService ({DateTime.Now:yyyy-MM-dd HH:mm:ss}):\n" +
                                     $"Mensagem: {ex.Message}\n" +
                                     $"Tipo: {ex.GetType().Name}\n" +
                                     $"Stack Trace:\n{ex.StackTrace}\n" +
                                     $"Inner Exception: {ex.InnerException?.Message}\n";

                    File.WriteAllText(UpdateErrorMarkerPath, errorDetails);
                }
                catch { }
            }

            return result;
        }

        /// <summary>
        /// Verifica se temos permissões de escrita na pasta de instalação
        /// </summary>
        private bool CheckWritePermissions()
        {
            try
            {
                _logger.LogInfo($"Verificando permissões de escrita em: {InstallDir}");

                // Teste simples: tenta criar e deletar um arquivo temporário
                var testFile = Path.Combine(InstallDir, $"write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);

                _logger.LogInfo("Teste de escrita bem-sucedido");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Teste de escrita falhou: {ex.Message}");

                // Tenta verificar permissões via ACL como fallback
                try
                {
                    var dirInfo = new DirectoryInfo(InstallDir);
                    var security = dirInfo.GetAccessControl();
                    var identity = WindowsIdentity.GetCurrent();
                    var principal = new WindowsPrincipal(identity);

                    // Verifica se é administrador
                    if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                    {
                        _logger.LogInfo("Executando como administrador - assumindo permissões OK");
                        return true;
                    }

                    // Verifica permissões específicas
                    var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
                    foreach (FileSystemAccessRule rule in rules)
                    {
                        if (identity.Groups.Contains(rule.IdentityReference) ||
                            identity.User.Equals(rule.IdentityReference))
                        {
                            if ((rule.FileSystemRights & FileSystemRights.WriteData) == FileSystemRights.WriteData &&
                                rule.AccessControlType == AccessControlType.Allow)
                            {
                                _logger.LogInfo("Permissões de escrita confirmadas via ACL");
                                return true;
                            }
                        }
                    }
                }
                catch (Exception aclEx)
                {
                    _logger.LogError($"Erro ao verificar ACL: {aclEx.Message}");
                }

                return false;
            }
        }

        /// <summary>
        /// Limpa arquivos de status de atualizações anteriores
        /// </summary>
        private static void CleanupStatusFiles()
        {
            try
            {
                if (File.Exists(UpdateSuccessMarkerPath))
                {
                    File.Delete(UpdateSuccessMarkerPath);
                }

                if (File.Exists(UpdateErrorMarkerPath))
                {
                    File.Delete(UpdateErrorMarkerPath);
                }
            }
            catch
            {
                // Ignora erros de limpeza
            }
        }

        /// <summary>
        /// Verifica se há arquivos de status de atualização e retorna informações
        /// </summary>
        public static UpdateStatusInfo GetUpdateStatus()
        {
            var info = new UpdateStatusInfo();

            try
            {
                if (File.Exists(UpdateSuccessMarkerPath))
                {
                    var content = File.ReadAllText(UpdateSuccessMarkerPath);
                    var parts = content.Split('|');
                    info.IsSuccess = true;
                    info.OldVersion = parts.Length > 0 ? parts[0] : "";
                    info.NewVersion = parts.Length > 1 ? parts[1] : "";
                    info.Message = $"Atualização concluída com sucesso! {info.OldVersion} → {info.NewVersion}";

                    // Remove o arquivo após leitura
                    File.Delete(UpdateSuccessMarkerPath);
                }
                else if (File.Exists(UpdateErrorMarkerPath))
                {
                    info.IsSuccess = false;
                    info.Message = File.ReadAllText(UpdateErrorMarkerPath);

                    // Remove o arquivo após leitura
                    File.Delete(UpdateErrorMarkerPath);
                }
            }
            catch (Exception ex)
            {
                info.IsSuccess = false;
                info.Message = $"Erro ao verificar status: {ex.Message}";
            }

            return info;
        }

        /// <summary>
        /// Verifica se a aplicação está instalada em uma localização que permite atualização automática
        /// </summary>
        public static bool IsInstallLocationSuitable()
        {
            try
            {
                var installDir = InstallDir;

                // Locais que geralmente requerem elevação
                var restrictedPaths = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
                };

                foreach (var restrictedPath in restrictedPaths)
                {
                    if (!string.IsNullOrEmpty(restrictedPath) &&
                        installDir.StartsWith(restrictedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Version GetLocalVersion()
        {
            try
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return version ?? new Version(0, 0);
            }
            catch
            {
                return new Version(0, 0);
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("leituraWPF/1.0");
            http.Timeout = TimeSpan.FromMinutes(10); // Timeout maior para downloads
            return http;
        }

        private static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return new Version(0, 0);

            // Remove prefixos comuns como 'v' ou 'V'
            tag = tag.Trim().TrimStart('v', 'V');

            // Tenta fazer parse da versão
            return Version.TryParse(tag, out var v) ? v : new Version(0, 0);
        }
    }

    /// <summary>
    /// Informações sobre o status de uma atualização
    /// </summary>
    public class UpdateStatusInfo
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; } = "";
        public string OldVersion { get; set; } = "";
        public string NewVersion { get; set; } = "";
        public bool HasStatus => IsSuccess || !string.IsNullOrEmpty(Message);
    }
}