using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace leituraWPF.Services
{
    /// <summary>
    /// Serviço de atualização que delega todo o processo ao aplicativo externo AtualizaAPP.exe
    /// localizado na subpasta "AtualizaAPP".
    /// Apenas verifica se há nova versão disponível e, quando requisitado, inicia o executável externo.
    /// </summary>
    public class ExternalUpdateService : IUpdateService
    {
        private const string ApiUrl = "https://api.github.com/repos/loboczss/leituraWPF/releases/latest";
        private const string UpdaterExe = "AtualizaAPP.exe";
        private const string UpdaterDir = "AtualizaAPP";
        private static string InstallDir => AppDomain.CurrentDomain.BaseDirectory;

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
        {
            var result = new UpdateCheckResult
            {
                LocalVersion = GetLocalVersion()
            };

            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("leituraWPF/1.0");
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
            }

            return result;
        }

        public Task<UpdatePerformResult> PerformUpdateAsync(CancellationToken ct = default)
        {
            var result = new UpdatePerformResult { RemoteFetchSuccessful = true };
            try
            {
                var exePath = Path.Combine(InstallDir, UpdaterDir, UpdaterExe);
                if (!File.Exists(exePath))
                {
                    result.Success = false;
                    result.Message = $"{UpdaterExe} não encontrado.";
                    return Task.FromResult(result);
                }

                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)
                };
                Process.Start(psi);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
            }

            return Task.FromResult(result);
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

        private static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return new Version(0, 0);
            tag = tag.Trim().TrimStart('v', 'V');
            return Version.TryParse(tag, out var v) ? v : new Version(0, 0);
        }
    }
}
