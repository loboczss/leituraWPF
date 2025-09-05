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
        private Task? _backgroundTask;

        private bool _disposed;

        // Estatísticas (thread-safe)
        private int _pendingCount;
        private long _uploadedCountSession;
        private long _errorCount;

        public int PendingCount => _pendingCount;
        public long UploadedCountSession => _uploadedCountSession;
        public long ErrorCount => _errorCount;
        public DateTime? LastRunUtc { get; private set; }
        public DateTime? LastSuccessUtc { get; private set; }
        public bool IsRunning { get; private set; }

        public IEnumerable<string> GetPendingFiles() =>
            Directory.Exists(_pendingDir)
                ? Directory.EnumerateFiles(_pendingDir, "*", SearchOption.AllDirectories)
                : Enumerable.Empty<string>();

        public IEnumerable<string> GetSentFiles() =>
            Directory.Exists(_sentDir)
                ? Directory.EnumerateFiles(_sentDir, "*", SearchOption.AllDirectories)
                : Enumerable.Empty<string>();

        public IEnumerable<string> GetErrorFiles() =>
            Directory.Exists(_errorDir)
                ? Directory.EnumerateFiles(_errorDir, "*", SearchOption.AllDirectories)
                : Enumerable.Empty<string>();

        // Eventos
        public event Action<string>? StatusChanged;
        public event Action<string, string, long>? FileUploaded;
        public event Action<string, string, Exception>? FileUploadFailed;
        public event Action<int, long>? CountersChanged;
        public event Action<int, long, long>? CountersChangedDetailed;

        public BackupUploaderService(AppConfig cfg, TokenService token)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _token = token ?? throw new ArgumentNullException(nameof(token));

            _baseDir = AppContext.BaseDirectory;
            _pendingDir = Path.Combine(_baseDir, "backup-pendentes");
            _sentDir = Path.Combine(_baseDir, "backup-enviados");
            _errorDir = Path.Combine(_baseDir, "backup-erros");

            EnsureDirectoriesExist();

            _http = new HttpClient(new HttpClientHandler
            {
                MaxConnectionsPerServer = 10
            })
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Timeout = TimeSpan.FromMinutes(15)
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
                StatusChanged?.Invoke($"[BACKUP] ERRO: Falha ao criar diretórios - {ex.Message}");
                throw; // Falha crítica
            }
        }

        public void Start()
        {
            ThrowIfDisposed();

            if (_timer != null || _backgroundTask?.IsCompleted == false) return;

            var interval = TimeSpan.FromSeconds(Math.Max(_cfg.BackupPollSeconds, 30)); // Mínimo 30s
            _timer = new PeriodicTimer(interval);
            _cancellationTokenSource = new CancellationTokenSource();

            _backgroundTask = Task.Run(BackgroundWorkerAsync, _cancellationTokenSource.Token);
        }

        private async Task BackgroundWorkerAsync()
        {
            var ct = _cancellationTokenSource!.Token;

            try
            {
                StatusChanged?.Invoke("[BACKUP] Serviço iniciado");

                // Primeira execução após 5 segundos
                await Task.Delay(5000, ct);
                await RunCycleAsync(ct);

                while (await _timer!.WaitForNextTickAsync(ct))
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        await RunCycleAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        StatusChanged?.Invoke($"[BACKUP] ERRO no ciclo: {ex.Message}");

                        // Aguardar antes de tentar novamente em caso de erro
                        try
                        {
                            await Task.Delay(TimeSpan.FromMinutes(1), ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal quando cancelado
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] ERRO crítico: {ex.Message}");
            }
            finally
            {
                StatusChanged?.Invoke("[BACKUP] Serviço finalizado");
            }
        }

        public void Stop()
        {
            StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public async Task StopAsync(TimeSpan timeout = default)
        {
            if (_timer == null && _backgroundTask?.IsCompleted != false) return;

            var timeoutToUse = timeout == default ? TimeSpan.FromSeconds(30) : timeout;

            _timer?.Dispose();
            _timer = null;

            _cancellationTokenSource?.Cancel();

            if (_backgroundTask != null)
            {
                try
                {
                    await _backgroundTask.WaitAsync(timeoutToUse);
                }
                catch (TimeoutException)
                {
                    // Ignorar timeout
                }
                catch (OperationCanceledException)
                {
                    // Normal
                }
            }

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _backgroundTask = null;
        }

        public async Task EnqueueAsync(string filePath)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            try
            {
                var name = Path.GetFileName(filePath);
                var folder = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? string.Empty;
                var dst = Path.Combine(_pendingDir, folder, name);
                var sentCandidate = Path.Combine(_sentDir, folder, name);

                if (File.Exists(sentCandidate) || File.Exists(dst))
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                // Usar async copy com buffer menor para não bloquear
                await CopyFileAsync(filePath, dst);

                Interlocked.Increment(ref _pendingCount);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] ERRO ao enfileirar {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        private static async Task CopyFileAsync(string source, string destination)
        {
            const int bufferSize = 64 * 1024; // 64KB buffer

            using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            using var destStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

            await sourceStream.CopyToAsync(destStream);
        }

        public async Task LoadPendingFromBaseDirsAsync()
        {
            ThrowIfDisposed();

            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
            var tasks = new List<Task>();

            foreach (var dir in RenamerService.EnumerarPastasBase())
            {
                if (!Directory.Exists(dir)) continue;

                tasks.Add(ProcessDirectoryAsync(dir, semaphore));
            }

            await Task.WhenAll(tasks);
            await UpdateCountersAsync();
        }

        private async Task ProcessDirectoryAsync(string dir, SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();
            try
            {
                var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    try
                    {
                        await EnqueueAsync(file);
                    }
                    catch
                    {
                        // Ignorar arquivos problemáticos individualmente
                    }
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] ERRO ao carregar {Path.GetFileName(dir)}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        }

        public void LoadPendingFromBaseDirs()
        {
            try
            {
                LoadPendingFromBaseDirsAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] ERRO ao carregar pendentes: {ex.Message}");
            }
        }

        private async Task UpdateCountersAsync()
        {
            try
            {
                var pending = await Task.Run(() => Directory.EnumerateFiles(_pendingDir, "*", SearchOption.AllDirectories).Count());
                var errors = await Task.Run(() => Directory.EnumerateFiles(_errorDir, "*", SearchOption.AllDirectories).Count());

                Interlocked.Exchange(ref _pendingCount, pending);
                Interlocked.Exchange(ref _errorCount, errors);

                CountersChanged?.Invoke(_pendingCount, _uploadedCountSession);
                CountersChangedDetailed?.Invoke(_pendingCount, _uploadedCountSession, _errorCount);
            }
            catch
            {
                // Ignorar erros de contagem
            }
        }

        public async Task<int> RetryErrorsAsync()
        {
            ThrowIfDisposed();

            var moved = 0;

            try
            {
                var files = Directory.EnumerateFiles(_errorDir, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".error", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var file in files)
                {
                    var relative = Path.GetRelativePath(_errorDir, file);
                    var destination = Path.Combine(_pendingDir, relative);

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                    if (File.Exists(destination))
                        File.Delete(destination);

                    File.Move(file, destination);

                    var errorInfo = file + ".error";
                    if (File.Exists(errorInfo))
                    {
                        try { File.Delete(errorInfo); } catch { }
                    }

                    moved++;
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] ERRO ao reenfileirar erros: {ex.Message}");
            }

            await UpdateCountersAsync();
            return moved;
        }

        public Task ForceRunOnceAsync(CancellationToken ct = default) => RunCycleAsync(ct);

        private async Task RunCycleAsync(CancellationToken ct = default)
        {
            if (!await _mutex.WaitAsync(1000, ct))
                return; // Timeout - já em execução

            IsRunning = true;

            try
            {
                await UpdateCountersAsync();

                if (_pendingCount == 0)
                    return;

                string token;
                try
                {
                    token = await _token.GetTokenAsync();
                }
                catch
                {
                    return; // Sem token, tentar no próximo ciclo
                }

                ConfigureHttpHeaders(token);

                var driveId = await ResolveDriveIdAsync(ct);
                if (string.IsNullOrEmpty(driveId))
                    return;

                await EnsureFolderAsync(driveId, _cfg.BackupFolder, ct);
                await ProcessPendingFilesAsync(driveId, ct);

                LastRunUtc = DateTime.UtcNow;
                LastSuccessUtc = DateTime.UtcNow;
                await UpdateCountersAsync();

                if (_pendingCount > 0 || _errorCount > 0)
                {
                    StatusChanged?.Invoke($"[BACKUP] Pendentes: {_pendingCount}, Enviados: {_uploadedCountSession}, Erros: {_errorCount}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                // Ignorar erros de rede - tentar no próximo ciclo
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"[BACKUP] ERRO: {ex.Message}");
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

            if (!_http.DefaultRequestHeaders.Accept.Any())
            {
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }
        }

        private async Task ProcessPendingFilesAsync(string driveId, CancellationToken ct)
        {
            var files = Directory.EnumerateFiles(_pendingDir, "*", SearchOption.AllDirectories)
                .Take(50) // Limitar a 50 arquivos por ciclo
                .ToArray();

            if (files.Length == 0) return;

            var semaphore = new SemaphoreSlim(2, 2); // Máximo 2 uploads paralelos
            var tasks = new List<Task>();

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                tasks.Add(ProcessFileWithSemaphoreAsync(semaphore, driveId, file, ct));
            }

            await Task.WhenAll(tasks);
        }

        private async Task ProcessFileWithSemaphoreAsync(SemaphoreSlim semaphore, string driveId, string file, CancellationToken ct)
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
        }

        private async Task ProcessSingleFileAsync(string driveId, string file, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(file)) return;

            var name = Path.GetFileName(file);
            var size = new FileInfo(file).Length;
            var rel = Path.GetRelativePath(_pendingDir, file);
            var sub = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? string.Empty;
            var remoteFolder = string.IsNullOrEmpty(sub) ? _cfg.BackupFolder : $"{_cfg.BackupFolder}/{sub}";

            const int maxAttempts = 2;
            Exception? lastException = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await EnsureFolderAsync(driveId, remoteFolder, ct);

                    if (size <= 4 * 1024 * 1024)
                        await UploadSmallAsync(driveId, remoteFolder, name, file, ct);
                    else
                        await UploadLargeAsync(driveId, remoteFolder, name, file, size, ct);

                    await MoveToSentAsync(file, rel);

                    Interlocked.Increment(ref _uploadedCountSession);
                    Interlocked.Decrement(ref _pendingCount);

                    FileUploaded?.Invoke(file, $"/{remoteFolder}/{name}", size);
                    return;
                }
                catch (HttpRequestException ex) when (IsRetryableError(ex) && attempt < maxAttempts)
                {
                    lastException = ex;
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    break;
                }
            }

            // Falhou definitivamente
            await MoveToErrorAsync(file, rel, lastException);

            Interlocked.Increment(ref _errorCount);
            Interlocked.Decrement(ref _pendingCount);

            FileUploadFailed?.Invoke(file, name, lastException ?? new Exception("Falha desconhecida"));
        }

        private static bool IsRetryableError(HttpRequestException ex)
        {
            return ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("network", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task MoveToSentAsync(string sourceFile, string relativePath)
        {
            var dst = Path.Combine(Path.Combine(AppContext.BaseDirectory, "backup-enviados"), relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            if (File.Exists(dst))
                File.Delete(dst);

            File.Move(sourceFile, dst);
        }

        private static async Task MoveToErrorAsync(string sourceFile, string relativePath, Exception? exception)
        {
            var dst = Path.Combine(Path.Combine(AppContext.BaseDirectory, "backup-erros"), relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            var errorFile = dst + ".error";
            var errorInfo = new
            {
                Timestamp = DateTime.UtcNow,
                Exception = exception?.GetType().Name ?? "Unknown",
                Message = exception?.Message ?? "Erro desconhecido"
            };

            try
            {
                await File.WriteAllTextAsync(errorFile, JsonSerializer.Serialize(errorInfo));
            }
            catch { }

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
                string url = !string.IsNullOrEmpty(_cfg.BackupListId)
                    ? $"https://graph.microsoft.com/v1.0/sites/{_cfg.BackupSiteId}/lists/{_cfg.BackupListId}/drive"
                    : $"https://graph.microsoft.com/v1.0/sites/{_cfg.BackupSiteId}/drive";

                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                    return null;

                var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                return json?["id"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private async Task EnsureFolderAsync(string driveId, string folder, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(folder) || _ensured.Contains(folder))
                return;

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

                        if (!resp2.IsSuccessStatusCode && resp2.StatusCode != HttpStatusCode.Conflict)
                            return; // Falha silenciosa - não travar o processo
                    }

                    _ensured.Add(current);
                }
                catch
                {
                    return; // Falha silenciosa
                }
            }
        }

        private async Task UploadSmallAsync(string driveId, string folder, string name, string path, CancellationToken ct)
        {
            var url = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(folder)}/{Uri.EscapeDataString(name)}:/content?@microsoft.graph.conflictBehavior=replace";

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            using var content = new StreamContent(fs);
            using var resp = await _http.PutAsync(url, content, ct);

            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Upload falhou: {resp.StatusCode}");
            }
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

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Sessão falhou: {resp.StatusCode}");

            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var uploadUrl = json?["uploadUrl"]?.ToString();
            if (string.IsNullOrEmpty(uploadUrl))
                throw new InvalidOperationException("uploadUrl vazio");

            const int chunkSize = 5 * 1024 * 1024; // 5MB chunks para melhor performance
            long sent = 0;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, useAsync: true);
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
                    throw new HttpRequestException($"Chunk falhou: {resp2.StatusCode}");
                }

                sent += read;
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
            _disposed = true;

            try
            {
                StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            catch { }

            _timer?.Dispose();
            _http?.Dispose();
            _mutex?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }
}