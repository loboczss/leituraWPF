using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace leituraWPF.Services
{
    /// <summary>
    /// Atualizador robusto e à prova de falhas para leituraWPF
    /// </summary>
    public class AtualizadorService
    {
        private const string ApiUrl = "https://api.github.com/repos/loboczss/leituraWPF/releases/latest";
        private const string AppProductName = "leituraWPF";
        private const string AppExeName = "leituraWPF.exe";
        private const int MaxRetryAttempts = 3;
        private const int TimeoutSeconds = 60;

        private static readonly string[] PreserveDirs =
        {
            "backup-enviados",
            "backup-erros",
            "backup-pendentes",
            "downloads"
        };

        private static readonly string[] PreserveFiles =
        {
            "syncstats.json"
        };

        private static string InstallDir => AppDomain.CurrentDomain.BaseDirectory;
        public const string UpdateSuccessMarkerFile = "update_success.flag";
        public const string UpdateErrorMarkerFile = "update_error.flag";
        public const string UpdateLogFile = "update.log";
        
        public static string UpdateSuccessMarkerPath => Path.Combine(InstallDir, UpdateSuccessMarkerFile);
        public static string UpdateErrorMarkerPath => Path.Combine(InstallDir, UpdateErrorMarkerFile);
        public static string UpdateLogPath => Path.Combine(InstallDir, UpdateLogFile);

        private readonly ILogger _logger;

        public AtualizadorService(ILogger logger = null)
        {
            _logger = logger ?? new DefaultLogger();
        }

        /// <summary>
        /// Resultado da operação de atualização
        /// </summary>
        public class UpdateResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
            public Exception? Exception { get; set; }
            public Version? LocalVersion { get; set; }
            public Version? RemoteVersion { get; set; }
            public bool UpdateAvailable { get; set; }
            public bool RemoteFetchSuccessful { get; set; }
        }

        /// <summary>
        /// Verifica se há atualizações disponíveis de forma robusta
        /// </summary>
        public async Task<UpdateResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            var result = new UpdateResult();
            
            try
            {
                _logger.LogInfo("Iniciando verificação de atualizações...");

                // Limpa marcadores de erro anteriores
                CleanupMarkerFiles();

                // Obtém versões com retry
                var (local, remote, fetchOk) = await GetVersionsWithRetryAsync(cancellationToken);
                
                result.LocalVersion = local;
                result.RemoteVersion = remote;
                result.RemoteFetchSuccessful = fetchOk;

                if (!fetchOk)
                {
                    result.Message = "Não foi possível conectar ao servidor de atualizações";
                    _logger.LogWarning(result.Message);
                    return result;
                }

                result.UpdateAvailable = remote > local;
                result.Success = true;
                
                if (result.UpdateAvailable)
                {
                    result.Message = $"Atualização disponível: {local} → {remote}";
                    _logger.LogInfo(result.Message);
                }
                else
                {
                    result.Message = $"Versão atual ({local}) está atualizada";
                    _logger.LogInfo(result.Message);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                result.Message = "Operação cancelada pelo usuário";
                _logger.LogWarning(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Exception = ex;
                result.Message = $"Erro ao verificar atualizações: {ex.Message}";
                _logger.LogError(result.Message, ex);
                return result;
            }
        }

        /// <summary>
        /// Executa o processo completo de atualização
        /// </summary>
        public async Task<UpdateResult> PerformUpdateAsync(CancellationToken cancellationToken = default)
        {
            var result = new UpdateResult();
            string? downloadPath = null;
            
            try
            {
                _logger.LogInfo("Iniciando processo de atualização...");

                // Verifica se atualização está disponível
                var checkResult = await CheckForUpdatesAsync(cancellationToken);
                if (!checkResult.Success || !checkResult.UpdateAvailable)
                {
                    return checkResult;
                }

                result.LocalVersion = checkResult.LocalVersion;
                result.RemoteVersion = checkResult.RemoteVersion;

                // Valida ambiente antes da atualização
                var validation = ValidateUpdateEnvironment();
                if (!validation.Success)
                {
                    result.Message = validation.Message;
                    result.Exception = validation.Exception;
                    return result;
                }

                // Download com validação
                _logger.LogInfo("Fazendo download da atualização...");
                downloadPath = await DownloadWithValidationAsync(cancellationToken);
                
                if (string.IsNullOrEmpty(downloadPath))
                {
                    result.Message = "Falha no download da atualização";
                    return result;
                }

                // Cria e executa script de atualização
                _logger.LogInfo("Preparando script de atualização...");
                var batchPath = CreateRobustUpdateBatch(downloadPath);
                
                _logger.LogInfo("Executando atualização...");
                ExecuteUpdateBatch(batchPath);

                result.Success = true;
                result.Message = "Atualização iniciada com sucesso";
                
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Message = "Atualização cancelada pelo usuário";
                _logger.LogWarning(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Exception = ex;
                result.Message = $"Erro durante a atualização: {ex.Message}";
                _logger.LogError(result.Message, ex);
                
                // Cleanup em caso de erro
                if (!string.IsNullOrEmpty(downloadPath) && File.Exists(downloadPath))
                {
                    try { File.Delete(downloadPath); } catch { }
                }
                
                return result;
            }
        }

        /// <summary>
        /// Verifica se a atualização anterior foi bem-sucedida
        /// </summary>
        public bool WasLastUpdateSuccessful()
        {
            try
            {
                return File.Exists(UpdateSuccessMarkerPath) && !File.Exists(UpdateErrorMarkerPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Obtém logs da última atualização
        /// </summary>
        public string GetUpdateLog()
        {
            try
            {
                return File.Exists(UpdateLogPath) ? File.ReadAllText(UpdateLogPath) : "";
            }
            catch
            {
                return "";
            }
        }

        #region Métodos Privados Robustos

        private async Task<(Version Local, Version Remote, bool FetchOk)> GetVersionsWithRetryAsync(CancellationToken cancellationToken)
        {
            var local = GetLocalVersionSafely();
            
            for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
            {
                try
                {
                    using var http = CreateRobustHttpClient();
                    var json = await http.GetStringAsync(ApiUrl, cancellationToken);
                    
                    if (string.IsNullOrWhiteSpace(json))
                        throw new InvalidOperationException("Resposta vazia do servidor");

                    var obj = JObject.Parse(json);
                    var tagRaw = obj["tag_name"]?.ToString()?.Trim();
                    
                    if (string.IsNullOrEmpty(tagRaw))
                        throw new InvalidOperationException("Tag de versão não encontrada");

                    var remote = ParseVersionFromTag(tagRaw);
                    if (remote == null)
                        throw new InvalidOperationException($"Não foi possível parsear a versão: {tagRaw}");

                    _logger.LogInfo($"Versões detectadas - Local: {local}, Remota: {remote}");
                    return (local, remote, true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Tentativa {attempt}/{MaxRetryAttempts} falhou: {ex.Message}");
                    
                    if (attempt == MaxRetryAttempts)
                    {
                        _logger.LogError("Todas as tentativas de obter versão remota falharam", ex);
                        return (local, local, false);
                    }
                    
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
            }
            
            return (local, local, false);
        }

        private Version GetLocalVersionSafely()
        {
            var defaultVersion = new Version(0, 0, 0, 0);
            
            try
            {
                // Tenta obter da assembly atual
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                if (version != null)
                {
                    _logger.LogInfo($"Versão local obtida do assembly: {version}");
                    return version;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Falha ao obter versão do assembly: {ex.Message}");
            }

            // Fallback: tenta obter de arquivos
            var candidates = new[]
            {
                Path.Combine(InstallDir, $"{AppProductName}.dll"),
                Path.Combine(InstallDir, AppExeName),
                Path.Combine(InstallDir, $"{AppProductName}.exe")
            };

            foreach (var candidate in candidates.Where(File.Exists))
            {
                try
                {
                    var version = AssemblyName.GetAssemblyName(candidate).Version;
                    if (version != null)
                    {
                        _logger.LogInfo($"Versão local obtida de {candidate}: {version}");
                        return version;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Falha ao obter versão de {candidate}: {ex.Message}");
                }
            }

            _logger.LogWarning($"Usando versão padrão: {defaultVersion}");
            return defaultVersion;
        }

        private async Task<string?> DownloadWithValidationAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
            {
                try
                {
                    using var http = CreateRobustHttpClient();
                    var json = await http.GetStringAsync(ApiUrl, cancellationToken);
                    var obj = JObject.Parse(json);
                    
                    var assets = (JArray?)obj["assets"];
                    if (assets == null || assets.Count == 0)
                    {
                        throw new InvalidOperationException("Nenhum asset encontrado no release");
                    }

                    // Encontra o asset .zip
                    var asset = assets.FirstOrDefault(a =>
                    {
                        var name = a?["name"]?.ToString() ?? "";
                        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    });

                    if (asset == null)
                        throw new InvalidOperationException("Nenhum arquivo .zip encontrado nos assets");

                    var url = asset["browser_download_url"]?.ToString();
                    var fileName = asset["name"]?.ToString();
                    var sizeStr = asset["size"]?.ToString();

                    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(fileName))
                        throw new InvalidOperationException("URL ou nome do arquivo inválido");

                    // Valida tamanho esperado
                    if (long.TryParse(sizeStr, out var expectedSize) && expectedSize > 0)
                    {
                        _logger.LogInfo($"Tamanho esperado do download: {expectedSize:N0} bytes");
                    }

                    var tempPath = Path.Combine(Path.GetTempPath(), $"{AppProductName}_update_{Guid.NewGuid():N}.zip");
                    
                    // Download com progresso e validação
                    _logger.LogInfo($"Baixando {fileName} para {tempPath}...");
                    
                    var data = await http.GetByteArrayAsync(url, cancellationToken);
                    
                    // Valida tamanho do download
                    if (expectedSize > 0 && data.Length != expectedSize)
                    {
                        throw new InvalidOperationException($"Tamanho do arquivo incorreto. Esperado: {expectedSize}, Recebido: {data.Length}");
                    }

                    await File.WriteAllBytesAsync(tempPath, data, cancellationToken);
                    
                    // Valida arquivo ZIP
                    if (!IsValidZipFile(tempPath))
                    {
                        File.Delete(tempPath);
                        throw new InvalidOperationException("Arquivo ZIP corrompido");
                    }

                    _logger.LogInfo($"Download concluído e validado: {tempPath}");
                    return tempPath;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Tentativa de download {attempt}/{MaxRetryAttempts} falhou: {ex.Message}");
                    
                    if (attempt == MaxRetryAttempts)
                        throw new InvalidOperationException($"Falha no download após {MaxRetryAttempts} tentativas", ex);
                    
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
            }
            
            return null;
        }

        private UpdateResult ValidateUpdateEnvironment()
        {
            var result = new UpdateResult { Success = true };
            
            try
            {
                // Verifica se o diretório de instalação existe e é acessível
                if (!Directory.Exists(InstallDir))
                {
                    result.Success = false;
                    result.Message = "Diretório de instalação não encontrado";
                    return result;
                }

                // Verifica permissões de escrita
                var testFile = Path.Combine(InstallDir, $"write_test_{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                }
                catch
                {
                    result.Success = false;
                    result.Message = "Sem permissões de escrita no diretório de instalação";
                    return result;
                }

                // Verifica espaço em disco (pelo menos 100MB)
                var drive = new DriveInfo(Path.GetPathRoot(InstallDir) ?? "C:");
                if (drive.AvailableFreeSpace < 100 * 1024 * 1024)
                {
                    result.Success = false;
                    result.Message = "Espaço insuficiente em disco para atualização";
                    return result;
                }

                // Verifica se PowerShell está disponível
                if (!IsPowerShellAvailable())
                {
                    result.Success = false;
                    result.Message = "PowerShell não está disponível para extração";
                    return result;
                }

                _logger.LogInfo("Ambiente validado com sucesso para atualização");
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Erro na validação do ambiente: {ex.Message}";
                result.Exception = ex;
                return result;
            }
        }

        private string CreateRobustUpdateBatch(string zipPath)
        {
            string batchName = $"{AppProductName}_Update_{DateTime.Now:yyyyMMdd_HHmmss}.bat";
            string batchPath = Path.Combine(Path.GetTempPath(), batchName);
            string installDir = InstallDir.TrimEnd('\n', '\r', '\\');
            string flagPath = UpdateSuccessMarkerPath;
            string errorFlagPath = UpdateErrorMarkerPath;
            string logPath = UpdateLogPath;

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("setlocal ENABLEDELAYEDEXPANSION");
            sb.AppendLine($"set APP_EXE={AppExeName}");
            sb.AppendLine($"set ZIP=\"{zipPath}\"");
            sb.AppendLine($"set INSTALL=\"{installDir}\"");
            sb.AppendLine($"set TEMP_DIR=%TEMP%\\{AppProductName}_Update_%RANDOM%");
            sb.AppendLine($"set BACKUP_DIR=%TEMP%\\{AppProductName}_Backup_%RANDOM%");
            sb.AppendLine($"set SUCCESS_FLAG=\"{flagPath}\"");
            sb.AppendLine($"set ERROR_FLAG=\"{errorFlagPath}\"");
            sb.AppendLine($"set LOG_FILE=\"{logPath}\"");
            sb.AppendLine();
            
            // Função de logging
            sb.AppendLine(":: Função de logging");
            sb.AppendLine(":log");
            sb.AppendLine("echo %date% %time% - %~1 >> %LOG_FILE%");
            sb.AppendLine("echo %~1");
            sb.AppendLine("goto :eof");
            sb.AppendLine();

            // Função de erro com cleanup
            sb.AppendLine(":: Função de tratamento de erro");
            sb.AppendLine(":error");
            sb.AppendLine("call :log \"[ERRO] %~1\"");
            sb.AppendLine("echo erro: %~1 > %ERROR_FLAG%");
            sb.AppendLine("if exist \"%BACKUP_DIR%\" (");
            sb.AppendLine("  call :log \"[INFO] Restaurando backup...\"");
            sb.AppendLine("  robocopy \"%BACKUP_DIR%\" \"%INSTALL%\" /E /PURGE /R:3 /W:5 >nul 2>nul");
            sb.AppendLine("  if !ERRORLEVEL! LSS 8 (");
            sb.AppendLine("    call :log \"[INFO] Backup restaurado com sucesso\"");
            sb.AppendLine("  ) else (");
            sb.AppendLine("    call :log \"[FATAL] Falha na restauração do backup\"");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine("call :cleanup");
            sb.AppendLine("exit /b 1");
            sb.AppendLine();

            // Função de limpeza
            sb.AppendLine(":: Função de limpeza");
            sb.AppendLine(":cleanup");
            sb.AppendLine("if exist \"%TEMP_DIR%\" rmdir /s /q \"%TEMP_DIR%\" >nul 2>nul");
            sb.AppendLine("if exist \"%BACKUP_DIR%\" rmdir /s /q \"%BACKUP_DIR%\" >nul 2>nul");
            sb.AppendLine("if exist %ZIP% del /f /q %ZIP% >nul 2>nul");
            sb.AppendLine("goto :eof");
            sb.AppendLine();

            // Início do processo principal
            sb.AppendLine("call :log \"[INFO] Iniciando atualização robusta\"");
            sb.AppendLine();

            // Limpeza de flags anteriores
            sb.AppendLine("if exist %SUCCESS_FLAG% del /f /q %SUCCESS_FLAG% >nul 2>nul");
            sb.AppendLine("if exist %ERROR_FLAG% del /f /q %ERROR_FLAG% >nul 2>nul");
            sb.AppendLine();

            // Validações iniciais
            sb.AppendLine("call :log \"[INFO] Validando ambiente\"");
            sb.AppendLine("if not exist %ZIP% call :error \"Arquivo ZIP não encontrado\"");
            sb.AppendLine("if not exist \"%INSTALL%\" call :error \"Diretório de instalação não encontrado\"");
            sb.AppendLine();

            // Preparação de diretórios
            sb.AppendLine("call :log \"[INFO] Preparando diretórios\"");
            sb.AppendLine("if exist \"%TEMP_DIR%\" rmdir /s /q \"%TEMP_DIR%\" >nul 2>nul");
            sb.AppendLine("mkdir \"%TEMP_DIR%\" >nul 2>nul");
            sb.AppendLine("if not exist \"%TEMP_DIR%\" call :error \"Falha ao criar diretório temporário\"");
            sb.AppendLine();
            sb.AppendLine("if exist \"%BACKUP_DIR%\" rmdir /s /q \"%BACKUP_DIR%\" >nul 2>nul");
            sb.AppendLine("mkdir \"%BACKUP_DIR%\" >nul 2>nul");
            sb.AppendLine("if not exist \"%BACKUP_DIR%\" call :error \"Falha ao criar diretório de backup\"");
            sb.AppendLine();

            // Encerramento da aplicação com retry
            sb.AppendLine("call :log \"[INFO] Encerrando aplicação\"");
            sb.AppendLine("set /a retry_count=0");
            sb.AppendLine(":waitloop");
            sb.AppendLine("tasklist | find /I \"%APP_EXE%\" >nul 2>nul");
            sb.AppendLine("if %ERRORLEVEL%==0 (");
            sb.AppendLine("  set /a retry_count+=1");
            sb.AppendLine("  if !retry_count! GTR 30 call :error \"Timeout ao encerrar aplicação\"");
            sb.AppendLine("  taskkill /F /IM \"%APP_EXE%\" >nul 2>nul");
            sb.AppendLine("  timeout /t 2 /nobreak >nul");
            sb.AppendLine("  goto waitloop");
            sb.AppendLine(")");
            sb.AppendLine("call :log \"[INFO] Aplicação encerrada\"");
            sb.AppendLine();

            // Backup com robocopy
            sb.AppendLine("call :log \"[INFO] Criando backup\"");
            sb.AppendLine("robocopy \"%INSTALL%\" \"%BACKUP_DIR%\" /E /R:3 /W:5 >nul 2>nul");
            sb.AppendLine("if %ERRORLEVEL% GEQ 8 call :error \"Falha ao criar backup\"");
            sb.AppendLine();

            // Extração do ZIP com validação
            sb.AppendLine("call :log \"[INFO] Extraindo atualização\"");
            sb.AppendLine("powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"");
            sb.AppendLine("try {");
            sb.AppendLine("  Add-Type -AssemblyName System.IO.Compression.FileSystem;");
            sb.AppendLine("  [System.IO.Compression.ZipFile]::ExtractToDirectory('%ZIP%', '%TEMP_DIR%');");
            sb.AppendLine("  if (-not (Test-Path '%TEMP_DIR%\\*')) { throw 'Extração resultou em diretório vazio' }");
            sb.AppendLine("  exit 0");
            sb.AppendLine("} catch {");
            sb.AppendLine("  Write-Host $_.Exception.Message;");
            sb.AppendLine("  exit 1");
            sb.AppendLine("}\"");
            sb.AppendLine("if %ERRORLEVEL% NEQ 0 call :error \"Falha na extração do ZIP\"");
            sb.AppendLine();

            // Cópia com robocopy
            sb.AppendLine("call :log \"[INFO] Instalando atualização\"");
            var excludeDirs = string.Join(" ", PreserveDirs.Select(d => $"\\\"{d}\\\""));
            var excludeFiles = string.Join(" ", PreserveFiles.Select(f => $"\\\"{f}\\\""));
            sb.AppendLine($"robocopy \"%TEMP_DIR%\" \"%INSTALL%\" /E /R:3 /W:5 /XD {excludeDirs} /XF {excludeFiles} >nul 2>nul");
            sb.AppendLine("if %ERRORLEVEL% GEQ 8 call :error \"Falha na instalação dos arquivos\"");
            sb.AppendLine();

            // Validação pós-instalação
            sb.AppendLine("call :log \"[INFO] Validando instalação\"");
            sb.AppendLine($"if not exist \"%INSTALL%\\{AppExeName}\" call :error \"Arquivo principal não encontrado após instalação\"");
            sb.AppendLine();

            // Criação do atalho
            sb.AppendLine("call :log \"[INFO] Criando atalho\"");
            sb.AppendLine("set DESKTOP=%USERPROFILE%\\Desktop");
            sb.AppendLine($"powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%DESKTOP%\\CompillerLog.lnk');$s.TargetPath='%INSTALL%\\{AppExeName}';$s.WorkingDirectory='%INSTALL%';$s.IconLocation='%INSTALL%\\{AppExeName},0';$s.Save()\" >nul 2>nul");
            sb.AppendLine();

            // Sucesso
            sb.AppendLine("call :log \"[INFO] Atualização concluída com sucesso\"");
            sb.AppendLine("echo atualizado com sucesso em %date% %time% > %SUCCESS_FLAG%");
            sb.AppendLine();

            // Limpeza final
            sb.AppendLine("call :cleanup");
            sb.AppendLine();

            // Reinicialização
            sb.AppendLine("call :log \"[INFO] Reiniciando aplicação\"");
            sb.AppendLine($"start \"\" \"%INSTALL%\\{AppExeName}\"");
            sb.AppendLine();
            sb.AppendLine("call :log \"[INFO] Script de atualização finalizado\"");
            sb.AppendLine("endlocal");
            sb.AppendLine("exit /b 0");

            File.WriteAllText(batchPath, sb.ToString(), Encoding.UTF8);
            return batchPath;
        }

        private void ExecuteUpdateBatch(string batchPath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = batchPath,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(batchPath) ?? string.Empty
                };

                if (!CanWrite(InstallDir))
                {
                    startInfo.Verb = "runas"; // Eleva apenas se não houver permissão de escrita
                }

                var process = Process.Start(startInfo);
                if (process == null)
                    throw new InvalidOperationException("Não foi possível iniciar o processo de atualização");

                _logger.LogInfo($"Script de atualização executado: {batchPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Erro ao executar script de atualização: {ex.Message}", ex);
                throw;
            }
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                var testFile = Path.Combine(dir, Path.GetRandomFileName());
                using (File.Create(testFile, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private HttpClient CreateRobustHttpClient()
        {
            var handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
            };
            
            http.DefaultRequestHeaders.Add("User-Agent", $"{AppProductName}/1.0 (Windows NT 10.0; Win64; x64)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
            http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
            http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("deflate");
            
            return http;
        }

        private void CleanupMarkerFiles()
        {
            try
            {
                var files = new[] { UpdateSuccessMarkerPath, UpdateErrorMarkerPath };
                foreach (var file in files.Where(File.Exists))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Erro na limpeza de marcadores: {ex.Message}");
            }
        }

        private bool IsValidZipFile(string filePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                return zip.Entries.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool IsPowerShellAvailable()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-Command \"exit 0\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo);
                return process?.WaitForExit(5000) == true && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
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

        #endregion
    }

    #region Interfaces e Classes de Apoio

    public interface ILogger
    {
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message, Exception? exception = null);
    }

    /// <summary>
    /// Logger padrão que escreve no console e em arquivo
    /// </summary>
    public class DefaultLogger : ILogger
    {
        private readonly string _logFilePath;

        public DefaultLogger()
        {
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updater.log");
        }

        public void LogInfo(string message)
        {
            WriteLog("INFO", message);
        }

        public void LogWarning(string message)
        {
            WriteLog("WARN", message);
        }

        public void LogError(string message, Exception? exception = null)
        {
            var fullMessage = exception != null ? $"{message}\nException: {exception}" : message;
            WriteLog("ERROR", fullMessage);
        }

        private void WriteLog(string level, string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logEntry = $"[{timestamp}] [{level}] {message}";
            
            // Escreve no console
            Console.WriteLine(logEntry);
            
            // Escreve no arquivo (com tratamento de erro)
            try
            {
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            }
            catch
            {
                // Ignora erros de escrita no log para não quebrar o fluxo principal
            }
        }
    }

    /// <summary>
    /// Configurações avançadas para o atualizador
    /// </summary>
    public class UpdaterConfig
    {
        public int MaxRetryAttempts { get; set; } = 3;
        public int TimeoutSeconds { get; set; } = 60;
        public long MinimumFreeSpace { get; set; } = 100 * 1024 * 1024; // 100MB
        public bool RequireAdminRights { get; set; } = false;
        public bool CreateDesktopShortcut { get; set; } = true;
        public string? PreferredAssetName { get; set; }
        public bool ValidateChecksum { get; set; } = false;
        public string? ExpectedChecksum { get; set; }
    }

    /// <summary>
    /// Informações detalhadas sobre o progresso da atualização
    /// </summary>
    public class UpdateProgress
    {
        public string Stage { get; set; } = "";
        public int ProgressPercentage { get; set; }
        public string Message { get; set; } = "";
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public TimeSpan? EstimatedTimeRemaining { get; set; }
    }

    /// <summary>
    /// Extensões para o AtualizadorService com funcionalidades avançadas
    /// </summary>
    public static class AtualizadorExtensions
    {
        /// <summary>
        /// Verifica a integridade dos arquivos instalados
        /// </summary>
        public static async Task<bool> VerifyInstallationIntegrityAsync(this AtualizadorService service)
        {
            try
            {
                var installDir = AppDomain.CurrentDomain.BaseDirectory;
                var exePath = Path.Combine(installDir, "leituraWPF.exe");
                
                // Verifica se o executável principal existe
                if (!File.Exists(exePath))
                    return false;

                // Verifica se o executável não está corrompido
                try
                {
                    var version = AssemblyName.GetAssemblyName(exePath).Version;
                    return version != null;
                }
                catch
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Obtém informações detalhadas sobre o sistema para diagnóstico
        /// </summary>
        public static Dictionary<string, object> GetSystemDiagnostics()
        {
            var diagnostics = new Dictionary<string, object>();
            
            try
            {
                diagnostics["OS"] = Environment.OSVersion.ToString();
                diagnostics["Framework"] = Environment.Version.ToString();
                diagnostics["MachineName"] = Environment.MachineName;
                diagnostics["UserName"] = Environment.UserName;
                diagnostics["WorkingSet"] = Environment.WorkingSet;
                diagnostics["InstallDirectory"] = AppDomain.CurrentDomain.BaseDirectory;
                
                // Informações de disco
                var installDrive = new DriveInfo(Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory) ?? "C:");
                diagnostics["AvailableSpace"] = installDrive.AvailableFreeSpace;
                diagnostics["TotalSpace"] = installDrive.TotalSize;
                
                // Verifica conectividade
                diagnostics["InternetConnected"] = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
                
                // Verifica permissões
                var testFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"perm_test_{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                    diagnostics["WritePermissions"] = true;
                }
                catch
                {
                    diagnostics["WritePermissions"] = false;
                }
            }
            catch (Exception ex)
            {
                diagnostics["DiagnosticError"] = ex.Message;
            }
            
            return diagnostics;
        }

        /// <summary>
        /// Cria um relatório de diagnóstico completo
        /// </summary>
        public static string GenerateDiagnosticReport(this AtualizadorService service)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== RELATÓRIO DE DIAGNÓSTICO DO ATUALIZADOR ===");
            sb.AppendLine($"Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            
            // Informações do sistema
            sb.AppendLine("=== INFORMAÇÕES DO SISTEMA ===");
            var diagnostics = GetSystemDiagnostics();
            foreach (var kvp in diagnostics)
            {
                sb.AppendLine($"{kvp.Key}: {kvp.Value}");
            }
            sb.AppendLine();
            
            // Status da última atualização
            sb.AppendLine("=== STATUS DA ÚLTIMA ATUALIZAÇÃO ===");
            sb.AppendLine($"Última atualização bem-sucedida: {service.WasLastUpdateSuccessful()}");
            
            var updateLog = service.GetUpdateLog();
            if (!string.IsNullOrEmpty(updateLog))
            {
                sb.AppendLine("Log da última atualização:");
                sb.AppendLine(updateLog);
            }
            sb.AppendLine();
            
            // Integridade da instalação
            sb.AppendLine("=== INTEGRIDADE DA INSTALAÇÃO ===");
            var integrity = service.VerifyInstallationIntegrityAsync().Result;
            sb.AppendLine($"Instalação íntegra: {integrity}");
            
            return sb.ToString();
        }
    }

    #endregion
}

