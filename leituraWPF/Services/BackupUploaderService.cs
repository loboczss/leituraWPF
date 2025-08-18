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

namespace leituraWPF.Services
{
    /// <summary>
    /// Serviço responsável por fazer upload contínuo de arquivos de log/renomeação
    /// para um drive do SharePoint. Os arquivos são colocados em uma fila persistente
    /// no disco (backup-pendentes) e movidos para backup-enviados após sucesso.
    /// </summary>
    public sealed class BackupUploaderService
    {
        private readonly AppConfig _cfg;
        private readonly TokenService _token;
        private readonly HttpClient _http;

        private readonly string _baseDir;
        private readonly string _pendingDir;
        private readonly string _sentDir;

        private PeriodicTimer? _timer;
        private readonly SemaphoreSlim _mutex = new(1, 1);

        public int PendingCount { get; private set; }
        public long UploadedCountSession { get; private set; }
        public DateTime? LastRunUtc { get; private set; }

        public event Action<string>? StatusChanged;
        public event Action<string, string, long>? FileUploaded;
        public event Action<int, long>? CountersChanged;

        public BackupUploaderService(AppConfig cfg, TokenService token)
        {
            _cfg = cfg;
            _token = token;

            _baseDir = AppContext.BaseDirectory;
            _pendingDir = Path.Combine(_baseDir, "backup-pendentes");
            _sentDir = Path.Combine(_baseDir, "backup-enviados");
            Directory.CreateDirectory(_pendingDir);
            Directory.CreateDirectory(_sentDir);

            _http = new HttpClient();
            _http.DefaultRequestVersion = HttpVersion.Version20;
            _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        }

        public void Start()
        {
            if (_timer != null) return;
            var interval = TimeSpan.FromSeconds(_cfg.BackupPollSeconds > 0 ? _cfg.BackupPollSeconds : 30);
            _timer = new PeriodicTimer(interval);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await _timer.WaitForNextTickAsync())
                    {
                        await RunCycleAsync();
                    }
                }
                catch { /* timer disposed */ }
            });
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        public async Task EnqueueAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

            var name = Path.GetFileName(filePath);
            var dst = Path.Combine(_pendingDir, name);
            if (File.Exists(Path.Combine(_sentDir, name))) return; // já enviado

            try
            {
                File.Copy(filePath, dst, true);
            }
            catch { /* ignore */ }

            PendingCount = Directory.EnumerateFiles(_pendingDir).Count();
            CountersChanged?.Invoke(PendingCount, UploadedCountSession);
        }

        public Task ForceRunOnceAsync(CancellationToken ct = default) => RunCycleAsync(ct);

        private async Task RunCycleAsync(CancellationToken ct = default)
        {
            if (!await _mutex.WaitAsync(0, ct)) return; // já em execução
            try
            {
                PendingCount = Directory.EnumerateFiles(_pendingDir).Count();
                StatusChanged?.Invoke($"ciclo iniciado ({PendingCount} pendentes)");

                string token;
                try
                {
                    token = await _token.GetTokenAsync().ConfigureAwait(false);
                }
                catch
                {
                    StatusChanged?.Invoke("offline/sem token");
                    return;
                }

                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                _http.DefaultRequestHeaders.Accept.Clear();
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var driveId = await ResolveDriveIdAsync(ct);
                if (string.IsNullOrEmpty(driveId))
                {
                    StatusChanged?.Invoke("driveId não resolvido");
                    return;
                }

                await EnsureFolderAsync(driveId, ct);
                var files = Directory.EnumerateFiles(_pendingDir).ToList();
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(file);
                    var size = new FileInfo(file).Length;
                    var remote = $"/{_cfg.BackupFolder}/{name}";
                    try
                    {
                        if (size <= 4 * 1024 * 1024)
                            await UploadSmallAsync(driveId, name, file, ct);
                        else
                            await UploadLargeAsync(driveId, name, file, size, ct);

                        var dst = Path.Combine(_sentDir, name);
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(file, dst);

                        UploadedCountSession++;
                        PendingCount--;
                        FileUploaded?.Invoke(file, remote, size);
                        CountersChanged?.Invoke(PendingCount, UploadedCountSession);
                    }
                    catch (Exception ex)
                    {
                        StatusChanged?.Invoke($"falha ao enviar {name}: {ex.Message}");
                    }
                }

                LastRunUtc = DateTime.UtcNow;
                CountersChanged?.Invoke(PendingCount, UploadedCountSession);
                StatusChanged?.Invoke($"ciclo concluído: pendentes={PendingCount}, enviados(sessão)={UploadedCountSession}");
            }
            finally
            {
                _mutex.Release();
            }
        }

        private async Task<string?> ResolveDriveIdAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_cfg.BackupDriveId))
                return _cfg.BackupDriveId;

            if (!string.IsNullOrEmpty(_cfg.BackupListId))
            {
                var url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.BackupSiteId}/lists/{_cfg.BackupListId}/drive";
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return null;
                var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                return json?["id"]?.ToString();
            }
            else
            {
                var url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.BackupSiteId}/drive";
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return null;
                var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                return json?["id"]?.ToString();
            }
        }

        private async Task EnsureFolderAsync(string driveId, CancellationToken ct)
        {
            var folder = _cfg.BackupFolder ?? "LogsRenomeacao";
            var getUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(folder)}";
            using var resp = await _http.GetAsync(getUrl, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                var createUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/children";
                var body = new { name = folder, folder = new { }, @"@microsoft.graph.conflictBehavior" = "replace" };
                using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var resp2 = await _http.PostAsync(createUrl, content, ct);
                // 409 = já existe → ignora
            }
        }

        private async Task UploadSmallAsync(string driveId, string name, string path, CancellationToken ct)
        {
            var url = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(_cfg.BackupFolder)}/{Uri.EscapeDataString(name)}:/content?@microsoft.graph.conflictBehavior=replace";
            using var fs = File.OpenRead(path);
            using var content = new StreamContent(fs);
            using var resp = await _http.PutAsync(url, content, ct);
            await EnsureSuccessAsync(resp, $"PUT {name}");
        }

        private async Task UploadLargeAsync(string driveId, string name, string path, long size, CancellationToken ct)
        {
            var createUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{Uri.EscapeDataString(_cfg.BackupFolder)}/{Uri.EscapeDataString(name)}:/createUploadSession";
            var body = new { item = new { @"@microsoft.graph.conflictBehavior" = "replace" } };
            using var createContent = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(createUrl, createContent, ct);
            await EnsureSuccessAsync(resp, "createUploadSession");
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var uploadUrl = json?["uploadUrl"]?.ToString();
            if (string.IsNullOrEmpty(uploadUrl)) throw new InvalidOperationException("uploadUrl vazio");

            const int chunk = 6 * 1024 * 1024;
            long sent = 0;
            using var fs = File.OpenRead(path);
            var buffer = new byte[chunk];
            while (sent < size)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(chunk, size - sent)), ct);
                if (read <= 0) break;
                using var part = new ByteArrayContent(buffer, 0, read);
                part.Headers.ContentRange = new ContentRangeHeaderValue(sent, sent + read - 1, size);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var resp2 = await _http.PutAsync(uploadUrl, part, ct);
                if (!resp2.IsSuccessStatusCode && resp2.StatusCode != HttpStatusCode.Accepted)
                    throw new InvalidOperationException($"Upload chunk falhou: {resp2.StatusCode}");
                sent += read;
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
    }
}
