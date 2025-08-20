using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using leituraWPF.Utils;

namespace leituraWPF.Services
{
    /// <summary>
    /// Serviço responsável por fazer upload contínuo de arquivos de log/renomeação
    /// para um drive do SharePoint. Os arquivos são colocados em uma fila persistente
    /// no disco (backup-pendentes) e movidos para backup-enviados após sucesso.
    /// </summary>
    public sealed class BackupUploaderService : IDisposable
    {
        private readonly AppConfig _cfg;
        private readonly TokenService _token;
        private readonly HttpClient _http;

        private readonly string _baseDir;
        private readonly string _pendingDir;
        private readonly string _sentDir;
        private readonly string _errorDir;

        private PeriodicTimer? _timer;
        private readonly SemaphoreSlim _mutex = new(1, 1);
        private readonly HashSet<string> _ensured = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _cancellationTokenSource;

        private bool _disposed;

        // Estatísticas
        public int PendingCount { get; private set; }
        public long UploadedCountSession { get; private set; }
        public long ErrorCount { get; private set; }
        public DateTime? LastRunUtc { get; private set; }
        public DateTime? LastSuccessUtc { get; private set; }
        public bool IsRunning { get; private set; }

        // Eventos
        public event Action<string>? StatusChanged;
        public event Action<string, string, long>? FileUploaded;
        public event Action<string, string, Exception>? FileUploadFailed;
        public event Action<int, long>? CountersChanged; // pending, uploaded (mantendo compatibilidade)
        public event Action<int, long, long>? CountersChangedDetailed; // pending, uploaded, errors

        public BackupUploaderService(AppConfig cfg, TokenService token)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _token = token ?? throw new ArgumentNullException(nameof(token));

            _baseDir = AppContext.BaseDirectory;
            _pendingDir = Path.Combine(_baseDir, "backup-pendentes");
            _sentDir = Path.Combine(_baseDir, "backup-enviados");
            _errorDir = Path.Combine(_baseDir, "backup-erros");

            // Garantir que os diretórios existem
            EnsureDirectoriesExist();

            // Configurar HttpClient com timeouts apropriados
            _http = new HttpClient
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                Timeout = TimeSpan.FromMinutes(30) // Timeout maior para uploads grandes
            };
        }

        private void EnsureDirectoriesExist()
        {
            try
            {
                Directory.CreateDirectory(_pendingDir);
                Directory.CreateDirectory(_sentDir);
                Directory.CreateDirectory(_errorDir);
            }
            catch (Exception ex)
            {
                // Não fazer throw aqui para não quebrar a inicialização
                StatusChanged?.Invoke($"[BACKUP] Aviso: Erro ao criar diretórios - {ex.Message}");
            }
        }

        public void Start()
        {
            ThrowIfDisposed();

            if (_timer != null) return;

            var interval = TimeSpan.FromSeconds(Math.Max(_cfg.BackupPollSeconds, 10)); // Mínimo 10 segundos
            _timer = new PeriodicTimer(interval);
            _cancellationTokenSource = new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                try
                {
                    StatusChanged?.Invoke($"[BACKUP] Serviço iniciado com intervalo de {interval.TotalSeconds}s");

                    // Primeira execução imediata
                    await RunCycleAsync(_cancellationTokenSource.Token);

                    while (await _timer.WaitForNextTickAsync(_cancellationTokenSource.Token))
                    {
                        try
                        {
                            await RunCycleAsync(_cancellationTokenSource.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            StatusChanged?.Invoke($"[BACKUP] falha no ciclo: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    StatusChanged?.Invoke("[BACKUP] Serviço cancelado");
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"[BACKUP] Erro crítico: {ex.Message}");
                }
            }, _cancellationTokenSource.Token);
        }

        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        public async Task StopAsync(TimeSpan timeout = default)
        {
            if (_timer == null) return;

            var timeoutToUse = timeout == default ? TimeSpan.FromSeconds(30) : timeout;

            _timer.Dispose();
            _timer = null;

            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();

                // Aguardar que o ciclo atual termine
                var waitTask = Task.Run(async () =>
                {
                    while (IsRunning)
                    {
                        await Task.Delay(100);
                    }
                });

                try
                {
                    await waitTask.WaitAsync(timeoutToUse);
                }
                catch
                {
                    // Ignore erros de timeout
                }

                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }

            StatusChanged?.Invoke("[BACKUP] Serviço parado");
        }

        private async Task<bool> EnqueueFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                var name = Path.GetFileName(filePath);
                var folder = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? string.Empty;
                var dst = Path.Combine(_pendingDir, folder, name);
                var sentCandidate = Path.Combine(_sentDir, folder, name);

                // Verificar se já foi processado
                if (File.Exists(sentCandidate) || File.Exists(dst))
                    return false;

                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                // Usar async para não bloquear
                using var source = File.OpenRead(filePath);
                using var dest = File.OpenWrite(dst);
                await source.CopyToAsync(dest);

                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] Erro ao enfileirar {filePath}: {ex.Message}");
                return false;
            }
        }

        public async Task EnqueueAsync(string filePath)
        {
            ThrowIfDisposed();

            await EnqueueFileAsync(filePath);
            await UpdateCountersAsync();
        }

        public async Task LoadPendingFromBaseDirsAsync()
        {
            ThrowIfDisposed();

            var tasks = new List<Task>();

            foreach (var dir in RenamerService.EnumerarPastasBase())
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            await EnqueueFileAsync(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        StatusChanged?.Invoke($"[BACKUP] Erro ao carregar diretório {dir}: {ex.Message}");
                    }
                }));
            }

            await Task.WhenAll(tasks);
            await UpdateCountersAsync();
        }

        // Método síncrono para compatibilidade com código existente
        public void LoadPendingFromBaseDirs()
        {
            LoadPendingFromBaseDirsAsync().GetAwaiter().GetResult();
        }

        private async Task UpdateCountersAsync()
        {
            try
            {
                PendingCount = Directory.EnumerateFiles(_pendingDir, "*", SearchOption.AllDirectories).Count();
                ErrorCount = Directory.EnumerateFiles(_errorDir, "*", SearchOption.AllDirectories).Count();
                CountersChanged?.Invoke(PendingCount, UploadedCountSession);
                CountersChangedDetailed?.Invoke(PendingCount, UploadedCountSession, ErrorCount);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] Erro ao atualizar contadores: {ex.Message}");
            }
        }

        public Task ForceRunOnceAsync(CancellationToken ct = default) => RunCycleAsync(ct);

        private async Task RunCycleAsync(CancellationToken ct = default)
        {
            if (!await _mutex.WaitAsync(100, ct)) // Timeout curto para evitar bloqueio
            {
                return; // Já em execução
            }

            IsRunning = true;

            try
            {
                await UpdateCountersAsync();

                StatusChanged?.Invoke($"[BACKUP] ciclo iniciado ({PendingCount} pendentes)");

                if (PendingCount == 0)
                {
                    StatusChanged?.Invoke("[BACKUP] nenhum arquivo pendente");
                    return;
                }

                string token;
                try
                {
                    token = await _token.GetTokenAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke("[BACKUP] offline/sem token");
                    return;
                }

                ConfigureHttpHeaders(token);

                var driveId = await ResolveDriveIdAsync(ct);
                if (string.IsNullOrEmpty(driveId))
                {
                    StatusChanged?.Invoke("[BACKUP] driveId não resolvido");
                    return;
                }

                try
                {
                    await EnsureFolderAsync(driveId, _cfg.BackupFolder, ct);
                }
                catch (HttpRequestException ex)
                {
                    StatusChanged?.Invoke($"[BACKUP] rede indisponível: {ex.Message}");
                    return;
                }

                await ProcessPendingFilesAsync(driveId, ct);

                LastRunUtc = DateTime.UtcNow;
                LastSuccessUtc = DateTime.UtcNow;
                await UpdateCountersAsync();

                StatusChanged?.Invoke($"[BACKUP] ciclo concluído: pendentes={PendingCount}, enviados(sessão)={UploadedCountSession}, erros={ErrorCount}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] ciclo falhou: {ex.Message}");
            }
            finally
            {
                IsRunning = false;
                _mutex.Release();
            }
        }

        private void ConfigureHttpHeaders(string token)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private async Task ProcessPendingFilesAsync(string driveId, CancellationToken ct)
        {
            var files = Directory.EnumerateFiles(_pendingDir, "*", SearchOption.AllDirectories).ToList();
            var semaphore = new SemaphoreSlim(3, 3); // Máximo 3 uploads paralelos

            var tasks = files.Select(async file =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    await ProcessSingleFileAsync(driveId, file, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        private async Task ProcessSingleFileAsync(string driveId, string file, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(file);
            var size = new FileInfo(file).Length;
            var rel = Path.GetRelativePath(_pendingDir, file);
            var sub = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? string.Empty;
            var remoteFolder = string.IsNullOrEmpty(sub) ? _cfg.BackupFolder : $"{_cfg.BackupFolder}/{sub}";

            try
            {
                await EnsureFolderAsync(driveId, remoteFolder, ct);
                var remote = $"/{remoteFolder}/{name}";

                const int maxAttempts = 3;
                Exception? lastException = null;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        if (size <= 4 * 1024 * 1024)
                            await UploadSmallAsync(driveId, remoteFolder, name, file, ct);
                        else
                            await UploadLargeAsync(driveId, remoteFolder, name, file, size, ct);

                        // Sucesso - mover para enviados
                        await MoveToSentAsync(file, rel);

                        UploadedCountSession++;
                        PendingCount--;

                        StatusChanged?.Invoke($"[BACKUP] Arquivo enviado: {name} ({size} bytes)");
                        FileUploaded?.Invoke(file, remote, size);
                        CountersChanged?.Invoke(PendingCount, UploadedCountSession);
                        CountersChangedDetailed?.Invoke(PendingCount, UploadedCountSession, ErrorCount);
                        return;
                    }
                    catch (HttpRequestException ex) when (IsNetworkError(ex))
                    {
                        lastException = ex;
                        StatusChanged?.Invoke($"[BACKUP] rede indisponível em {name}, tentativa {attempt}");

                        if (attempt < maxAttempts)
                            await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct); // Backoff exponencial
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("409"))
                    {
                        lastException = ex;
                        StatusChanged?.Invoke($"[BACKUP] conflito em {name}, tentativa {attempt}");

                        if (attempt < maxAttempts)
                            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        break; // Erro não recuperável
                    }
                }

                // Se chegou até aqui, todas as tentativas falharam
                await MoveToErrorAsync(file, rel, lastException);

                ErrorCount++;
                PendingCount--;

                StatusChanged?.Invoke($"[BACKUP] Falha definitiva em {name} após {maxAttempts} tentativas");
                FileUploadFailed?.Invoke(file, name, lastException ?? new Exception("Erro desconhecido"));
                CountersChanged?.Invoke(PendingCount, UploadedCountSession);
                CountersChangedDetailed?.Invoke(PendingCount, UploadedCountSession, ErrorCount);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] Erro inesperado em {name}: {ex.Message}");
                await MoveToErrorAsync(file, rel, ex);
                ErrorCount++;
                PendingCount--;
                CountersChanged?.Invoke(PendingCount, UploadedCountSession);
                CountersChangedDetailed?.Invoke(PendingCount, UploadedCountSession, ErrorCount);
            }
        }

        private static bool IsNetworkError(HttpRequestException ex)
        {
            return ex.Message.Contains("timeout") ||
                   ex.Message.Contains("connection") ||
                   ex.Message.Contains("network");
        }

        private async Task MoveToSentAsync(string sourceFile, string relativePath)
        {
            var dst = Path.Combine(_sentDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            if (File.Exists(dst))
                File.Delete(dst);

            File.Move(sourceFile, dst);
        }

        private async Task MoveToErrorAsync(string sourceFile, string relativePath, Exception? exception)
        {
            var dst = Path.Combine(_errorDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            // Criar arquivo de erro com detalhes
            var errorFile = dst + ".error";
            var errorInfo = new
            {
                Timestamp = DateTime.UtcNow,
                OriginalFile = sourceFile,
                Exception = exception?.ToString() ?? "Erro desconhecido"
            };

            try
            {
                await File.WriteAllTextAsync(errorFile, JsonSerializer.Serialize(errorInfo, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* ignore */ }

            if (File.Exists(dst))
                File.Delete(dst);

            File.Move(sourceFile, dst);
        }

        private async Task<string?> ResolveDriveIdAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_cfg.BackupDriveId))
                return _cfg.BackupDriveId;

            try
            {
                string url;
                if (!string.IsNullOrEmpty(_cfg.BackupListId))
                {
                    url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.BackupSiteId}/lists/{_cfg.BackupListId}/drive";
                }
                else
                {
                    url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.BackupSiteId}/drive";
                }

                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                return json?["id"]?.ToString();
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] Erro ao resolver DriveId: {ex.Message}");
                return null;
            }
        }

        private async Task EnsureFolderAsync(string driveId, string folder, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(folder) || _ensured.Contains(folder)) return;

            var segments = folder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string current = string.Empty;

            foreach (var seg in segments)
            {
                current = string.IsNullOrEmpty(current) ? seg : $"{current}/{seg}";
                if (_ensured.Contains(current)) continue;

                try
                {
                    var getUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(current)}";
                    using var resp = await _http.GetAsync(getUrl, ct);

                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        var parent = Path.GetDirectoryName(current)?.Replace('\\', '/') ?? string.Empty;
                        var createUrl = string.IsNullOrEmpty(parent)
                            ? $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/children"
                            : $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(parent)}:/children";

                        var body = new JsonObject
                        {
                            ["name"] = seg,
                            ["folder"] = new JsonObject(),
                            ["@microsoft.graph.conflictBehavior"] = "replace"
                        };

                        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                        using var resp2 = await _http.PostAsync(createUrl, content, ct);

                        // 409 = já existe, podemos ignorar
                        if (!resp2.IsSuccessStatusCode && resp2.StatusCode != HttpStatusCode.Conflict)
                        {
                            var errorBody = await resp2.Content.ReadAsStringAsync(ct);
                            throw new InvalidOperationException($"Falha ao criar pasta {current}: {resp2.StatusCode} {errorBody}");
                        }
                    }

                    _ensured.Add(current);
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"[BACKUP] Erro ao garantir pasta {current}: {ex.Message}");
                    throw;
                }
            }
        }

        private async Task UploadSmallAsync(string driveId, string folder, string name, string path, CancellationToken ct)
        {
            var url = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(folder)}/{Uri.EscapeDataString(name)}:/content?@microsoft.graph.conflictBehavior=replace";

            using var fs = File.OpenRead(path);
            using var content = new StreamContent(fs);
            using var resp = await _http.PutAsync(url, content, ct);

            await EnsureSuccessAsync(resp, $"PUT {name}");
        }

        private async Task UploadLargeAsync(string driveId, string folder, string name, string path, long size, CancellationToken ct)
        {
            var createUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(folder)}/{Uri.EscapeDataString(name)}:/createUploadSession";
            var body = new JsonObject
            {
                ["item"] = new JsonObject
                {
                    ["@microsoft.graph.conflictBehavior"] = "replace"
                }
            };

            using var createContent = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(createUrl, createContent, ct);
            await EnsureSuccessAsync(resp, "createUploadSession");

            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var uploadUrl = json?["uploadUrl"]?.ToString();
            if (string.IsNullOrEmpty(uploadUrl))
                throw new InvalidOperationException("uploadUrl vazio");

            const int chunkSize = 10 * 1024 * 1024; // 10MB chunks
            long sent = 0;

            using var fs = File.OpenRead(path);
            var buffer = new byte[chunkSize];

            while (sent < size)
            {
                ct.ThrowIfCancellationRequested();

                int read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(chunkSize, size - sent)), ct);
                if (read <= 0) break;

                using var part = new ByteArrayContent(buffer, 0, read);
                part.Headers.ContentRange = new ContentRangeHeaderValue(sent, sent + read - 1, size);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                using var resp2 = await _http.PutAsync(uploadUrl, part, ct);
                if (!resp2.IsSuccessStatusCode && resp2.StatusCode != HttpStatusCode.Accepted)
                {
                    var errorBody = await resp2.Content.ReadAsStringAsync(ct);
                    throw new InvalidOperationException($"Upload chunk falhou: {resp2.StatusCode} {errorBody}");
                }

                sent += read;

                // Log de progresso para arquivos grandes
                if (size > 50 * 1024 * 1024) // > 50MB
                {
                    var progress = (double)sent / size * 100;
                    StatusChanged?.Invoke($"[BACKUP] Upload progress {name}: {progress:F1}%");
                }
            }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string op)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"{op} falhou: {(int)resp.StatusCode} {resp.ReasonPhrase} {body}");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BackupUploaderService));
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] Erro ao parar serviço: {ex.Message}");
            }

            _timer?.Dispose();
            _http?.Dispose();
            _mutex?.Dispose();
            _cancellationTokenSource?.Dispose();

            _disposed = true;
        }
    }
}