// Services/SelfUpdateService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace leituraWPF.Services
{
    /// <summary>
    /// Serviço simples de autoatualização. Verifica o último release do GitHub,
    /// baixa o pacote e delega a troca de arquivos ao UpdaterHost.
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
                using var http = CreateHttpClient();
                var json = await http.GetStringAsync(ApiUrl, ct).ConfigureAwait(false);
                var obj = JObject.Parse(json);
                var tag = obj["tag_name"]?.ToString();
                result.RemoteVersion = ParseVersion(tag);
                result.RemoteFetchSuccessful = true;
                result.UpdateAvailable = result.RemoteVersion > result.LocalVersion;
                result.Success = true;
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
                var check = await CheckForUpdatesAsync(ct).ConfigureAwait(false);
                result.RemoteFetchSuccessful = check.RemoteFetchSuccessful;

                if (!check.Success || !check.UpdateAvailable)
                {
                    result.Success = false;
                    result.Message = check.Message ?? "Nenhuma atualização disponível.";
                    return result;
                }

                // Baixa o ZIP
                using var http = CreateHttpClient();
                var json = await http.GetStringAsync(ApiUrl, ct).ConfigureAwait(false);
                var assets = (JArray)JObject.Parse(json)["assets"];
                var url = (assets?.First as JObject)?["browser_download_url"]?.ToString();
                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException("Pacote de atualização não encontrado.");

                var tempZip = Path.Combine(Path.GetTempPath(), $"upd_{Guid.NewGuid():N}.zip");
                var bytes = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                File.WriteAllBytes(tempZip, bytes);

                var staging = Path.Combine(Path.GetTempPath(), $"upd_{Guid.NewGuid():N}");
                ZipFile.ExtractToDirectory(tempZip, staging);
                File.Delete(tempZip);

                // Inicia UpdaterHost
                var updaterPath = Path.Combine(InstallDir, UpdaterExe);
                if (!File.Exists(updaterPath))
                    throw new FileNotFoundException("UpdaterHost não encontrado.", updaterPath);

                var args = $"--install \"{InstallDir}\" --staging \"{staging}\" --exe \"{AppExeName}\" " +
                           $"--pid {Process.GetCurrentProcess().Id} --success \"{UpdateSuccessMarkerPath}\" " +
                           $"--error \"{UpdateErrorMarkerPath}\" --log \"{UpdateLogPath}\" " +
                           $"--old \"{check.LocalVersion}\" --new \"{check.RemoteVersion}\"";

                Process.Start(new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = args,
                    UseShellExecute = false,
                    WorkingDirectory = InstallDir
                });

                result.Success = true;
                result.Message = "Atualização iniciada.";
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                _logger.LogError("Falha ao iniciar atualização", ex);
            }

            return result;
        }

        private static Version GetLocalVersion()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            }
            catch
            {
                return new Version(0, 0);
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("leituraWPF");
            return http;
        }

        private static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return new Version(0, 0);
            tag = tag.Trim().TrimStart('v', 'V');
            return Version.TryParse(tag, out var v) ? v : new Version(0, 0);
        }
    }
}

