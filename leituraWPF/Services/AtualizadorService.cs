// Services/AtualizadorService.cs
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace leituraWPF.Services
{
    /// <summary>
    /// Atualizador simples para leituraWPF:
    /// - Consulta https://api.github.com/repos/loboczss/leituraWPF/releases/latest
    /// - Compara versão local x remota (usa tag_name, ex.: v1.2.3)
    /// - Baixa o primeiro asset .zip do release
    /// - Gera um .bat que fecha o app, expande o zip e relança o exe
    /// </summary>
    public class AtualizadorService
    {
        private const string ApiUrl = "https://api.github.com/repos/loboczss/leituraWPF/releases/latest";

        // Nomes padrão do seu app:
        private const string AppProductName = "leituraWPF";
        private const string AppExeName = "leituraWPF.exe";

        private static string InstallDir => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>
        /// Retorna (versão local, versão remota).
        /// </summary>
        public async Task<(Version LocalVersion, Version RemoteVersion)> GetVersionsAsync()
        {
            Version localVer = new Version(0, 0, 0, 0);

            // --- Descobre versão local ---
            try
            {
                localVer = Assembly.GetExecutingAssembly().GetName().Version ?? localVer;
            }
            catch
            {
                // fallback por arquivo (caso não haja metadados)
                var dllPath = Path.Combine(InstallDir, $"{AppProductName}.dll");
                var exePath = Path.Combine(InstallDir, AppExeName);
                string asmPath = File.Exists(dllPath) ? dllPath : exePath;
                if (File.Exists(asmPath))
                {
                    try
                    {
                        localVer = AssemblyName.GetAssemblyName(asmPath).Version;
                    }
                    catch (BadImageFormatException)
                    {
                        // sem metadados -> fica 0.0.0.0
                    }
                }
            }

            // --- Descobre versão remota (GitHub tag_name) ---
            Version remoteVer = localVer;
            try
            {
                using var http = CreateHttp();
                var json = await http.GetStringAsync(ApiUrl).ConfigureAwait(false);
                var obj = JObject.Parse(json);

                var tagRaw = (obj["tag_name"]?.ToString() ?? string.Empty).Trim();
                var parsed = ParseVersionFromTag(tagRaw);
                if (parsed != null)
                    remoteVer = parsed;
            }
            catch
            {
                // sem internet/erro → mantém remote = local
            }

            return (localVer, remoteVer);
        }

        /// <summary>
        /// Faz download do primeiro asset .zip do release mais recente.
        /// Retorna o caminho temporário do zip salvo; ou null se não houver.
        /// </summary>
        public async Task<string?> DownloadLatestReleaseAsync(string? preferNameContains = null)
        {
            using var http = CreateHttp();
            var json = await http.GetStringAsync(ApiUrl).ConfigureAwait(false);
            var obj = JObject.Parse(json);

            var assets = (JArray?)obj["assets"];
            if (assets == null || assets.Count == 0)
                return null;

            // Escolha do asset:
            // 1) Se preferNameContains foi informado, tenta casar com .zip
            // 2) Senão, pega o primeiro .zip
            JToken? asset = null;

            if (!string.IsNullOrWhiteSpace(preferNameContains))
            {
                asset = assets.FirstOrDefault(a =>
                {
                    var name = a?["name"]?.ToString() ?? "";
                    return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                           name.IndexOf(preferNameContains, StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }

            asset ??= assets.FirstOrDefault(a =>
            {
                var name = a?["name"]?.ToString() ?? "";
                return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            });

            if (asset == null)
                return null;

            var url = asset["browser_download_url"]?.ToString();
            if (string.IsNullOrWhiteSpace(url))
                return null;

            var fileName = asset["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = Path.GetFileName(url);

            var dest = Path.Combine(Path.GetTempPath(), fileName!);

            var data = await http.GetByteArrayAsync(url!).ConfigureAwait(false);
            await File.WriteAllBytesAsync(dest, data).ConfigureAwait(false);

            return dest;
        }

        /// <summary>
        /// Gera um .bat que:
        /// - aguarda o fechamento do leituraWPF.exe
        /// - extrai o ZIP para uma pasta temporária
        /// - copia os arquivos para o diretório de instalação
        /// - recria atalho na área de trabalho
        /// - remove temporários
        /// - relança o leituraWPF.exe
        /// Retorna o caminho do .bat gerado.
        /// </summary>
        public string CreateUpdateBatch(string zipPath)
        {
            string batchName = $"{AppProductName}_Update.bat";
            string batchPath = Path.Combine(Path.GetTempPath(), batchName);
            string installDir = InstallDir.TrimEnd('\n', '\r', '\\');

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal ENABLEDELAYEDEXPANSION");
            sb.AppendLine($"set APP_EXE={AppExeName}");
            sb.AppendLine($"set ZIP=\"{zipPath}\"");
            sb.AppendLine($"set INSTALL=\"{installDir}\"");
            sb.AppendLine($"set TEMP_DIR=%TEMP%\\{AppProductName}_Update");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Preparando atualizacao... >nul");
            sb.AppendLine("if exist \"%TEMP_DIR%\" rmdir /s /q \"%TEMP_DIR%\" >nul 2>nul");
            sb.AppendLine("mkdir \"%TEMP_DIR%\" >nul 2>nul");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Encerrando aplicacao se ainda estiver aberta...");
            sb.AppendLine(":waitloop");
            sb.AppendLine("tasklist | find /I \"%APP_EXE%\" >nul 2>nul");
            sb.AppendLine("if %ERRORLEVEL%==0 (");
            sb.AppendLine("  taskkill /F /IM \"%APP_EXE%\" >nul 2>nul");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto waitloop");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Extraindo pacote...");
            sb.AppendLine("powershell -NoLogo -NoProfile -Command \"Expand-Archive -Path '%ZIP%' -DestinationPath '%TEMP_DIR%' -Force\" >nul 2>nul");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Copiando arquivos...");
            sb.AppendLine("xcopy \"%TEMP_DIR%\\*\" \"%INSTALL%\\\" /E /I /Y >nul 2>nul");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Criando atalho na area de trabalho...");
            sb.AppendLine("set DESKTOP=%USERPROFILE%\\Desktop");
            sb.AppendLine($"powershell -NoLogo -NoProfile -Command \"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%DESKTOP%\\{AppProductName}.lnk');$s.TargetPath='%INSTALL%\\{AppExeName}';$s.WorkingDirectory='%INSTALL%';$s.IconLocation='%INSTALL%\\{AppExeName},0';$s.Save()\" >nul 2>nul");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Limpando temporarios...");
            sb.AppendLine("rmdir /s /q \"%TEMP_DIR%\" >nul 2>nul");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Iniciando aplicativo atualizado...");
            sb.AppendLine($"start \"\" \"%INSTALL%\\{AppExeName}\"");
            sb.AppendLine("endlocal");
            sb.AppendLine("exit");

            File.WriteAllText(batchPath, sb.ToString(), Encoding.UTF8);
            return batchPath;
        }

        /// <summary>
        /// Helper: cria HttpClient com User-Agent e timeout decente.
        /// </summary>
        private static HttpClient CreateHttp()
        {
            var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
            // GitHub API exige User-Agent
            http.DefaultRequestHeaders.Add("User-Agent", AppProductName);
            // Opcional: aceitar JSON
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return http;
        }

        /// <summary>
        /// Converte "v1.2.3" ou "1.2.3" em Version. Ignora sufixos (ex.: -beta).
        /// </summary>
        private static Version? ParseVersionFromTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return null;

            // remove 'v' inicial e corta até caracteres não numéricos/ponto
            var trimmed = new string(tagName.Trim()
                                      .TrimStart('v', 'V')
                                      .TakeWhile(c => char.IsDigit(c) || c == '.')
                                      .ToArray());

            if (Version.TryParse(trimmed, out var v)) return v;
            return null;
        }
    }
}
