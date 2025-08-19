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
        /// Mantido por compatibilidade.
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
        /// Igual ao GetVersionsAsync, mas também indica se a consulta remota funcionou.
        /// Se RemoteFetchOk == false, significa offline/erro ao falar com o GitHub.
        /// </summary>
        public async Task<(Version LocalVersion, Version RemoteVersion, bool RemoteFetchOk)> GetVersionsWithStatusAsync()
        {
            Version localVer = new Version(0, 0, 0, 0);

            // versão local
            try
            {
                localVer = Assembly.GetExecutingAssembly().GetName().Version ?? localVer;
            }
            catch
            {
                var dllPath = Path.Combine(InstallDir, $"{AppProductName}.dll");
                var exePath = Path.Combine(InstallDir, AppExeName);
                string asmPath = File.Exists(dllPath) ? dllPath : exePath;
                if (File.Exists(asmPath))
                {
                    try
                    {
                        localVer = AssemblyName.GetAssemblyName(asmPath).Version;
                    }
                    catch (BadImageFormatException) { }
                }
            }

            // versão remota + status
            Version remoteVer = localVer;
            bool ok = false;
            try
            {
                using var http = CreateHttp();
                var json = await http.GetStringAsync(ApiUrl).ConfigureAwait(false);
                var obj = JObject.Parse(json);

                var tagRaw = (obj["tag_name"]?.ToString() ?? string.Empty).Trim();
                var parsed = ParseVersionFromTag(tagRaw);
                if (parsed != null)
                {
                    remoteVer = parsed;
                    ok = true;
                }
            }
            catch
            {
                ok = false; // offline/erro
            }

            return (localVer, remoteVer, ok);
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
        /// Gera o .bat de atualização (mesmo conteúdo que você já usa).
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
            sb.AppendLine($"set BACKUP_DIR=%TEMP%\\{AppProductName}_Backup");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Preparando atualizacao...");
            sb.AppendLine("if exist \"%TEMP_DIR%\" rmdir /s /q \"%TEMP_DIR%\" >nul 2>nul");
            sb.AppendLine("mkdir \"%TEMP_DIR%\" >nul 2>nul");
            sb.AppendLine("if exist \"%BACKUP_DIR%\" rmdir /s /q \"%BACKUP_DIR%\" >nul 2>nul");
            sb.AppendLine("mkdir \"%BACKUP_DIR%\" >nul 2>nul");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Encerrando aplicacao...");
            sb.AppendLine(":waitloop");
            sb.AppendLine("tasklist | find /I \"%APP_EXE%\" >nul 2>nul");
            sb.AppendLine("if %ERRORLEVEL%==0 (");
            sb.AppendLine("  taskkill /F /IM \"%APP_EXE%\" >nul 2>nul");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto waitloop");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Fazendo backup da instalacao atual...");
            sb.AppendLine("xcopy \"%INSTALL%\\*\" \"%BACKUP_DIR%\\\" /E /I /Y >nul 2>nul");
            sb.AppendLine("if %ERRORLEVEL% NEQ 0 (");
            sb.AppendLine("  echo [FATAL] Falha ao criar backup. Abortando.");
            sb.AppendLine("  exit /b 1");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Extraindo pacote...");
            sb.AppendLine("powershell -NoLogo -NoProfile -Command \"Expand-Archive -Path '%ZIP%' -DestinationPath '%TEMP_DIR%' -Force\" >nul 2>nul");
            sb.AppendLine("if %ERRORLEVEL% NEQ 0 (");
            sb.AppendLine("  echo [ERRO] Falha ao extrair pacote. Restaurando backup...");
            sb.AppendLine("  xcopy \"%BACKUP_DIR%\\*\" \"%INSTALL%\\\" /E /I /Y >nul 2>nul");
            sb.AppendLine("  exit /b 1");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Copiando novos arquivos...");
            sb.AppendLine("xcopy \"%TEMP_DIR%\\*\" \"%INSTALL%\\\" /E /I /Y >nul 2>nul");
            sb.AppendLine("if %ERRORLEVEL% NEQ 0 (");
            sb.AppendLine("  echo [ERRO] Falha ao copiar arquivos. Restaurando backup...");
            sb.AppendLine("  xcopy \"%BACKUP_DIR%\\*\" \"%INSTALL%\\\" /E /I /Y >nul 2>nul");
            sb.AppendLine("  exit /b 1");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Criando atalho na area de trabalho...");
            sb.AppendLine("set DESKTOP=%USERPROFILE%\\Desktop");
            sb.AppendLine($"powershell -NoLogo -NoProfile -Command \"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%DESKTOP%\\CompillerLog.lnk');$s.TargetPath='%INSTALL%\\{AppExeName}';$s.WorkingDirectory='%INSTALL%';$s.IconLocation='%INSTALL%\\{AppExeName},0';$s.Save()\" >nul 2>nul");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Limpando temporarios...");
            sb.AppendLine("rmdir /s /q \"%TEMP_DIR%\" >nul 2>nul");
            sb.AppendLine("rmdir /s /q \"%BACKUP_DIR%\" >nul 2>nul");
            sb.AppendLine();
            sb.AppendLine("echo [INFO] Iniciando aplicativo atualizado...");
            sb.AppendLine($"start \"\" \"%INSTALL%\\{AppExeName}\"");
            sb.AppendLine("endlocal");
            sb.AppendLine("exit /b 0");

            File.WriteAllText(batchPath, sb.ToString(), Encoding.UTF8);
            return batchPath;
        }

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
            http.DefaultRequestHeaders.Add("User-Agent", AppProductName);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return http;
        }

        private static Version? ParseVersionFromTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return null;

            var trimmed = new string(tagName.Trim()
                                      .TrimStart('v', 'V')
                                      .TakeWhile(c => char.IsDigit(c) || c == '.')
                                      .ToArray());

            if (Version.TryParse(trimmed, out var v)) return v;
            return null;
        }
    }
}
