// Services/GraphDownloader.cs
using leituraWPF.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace leituraWPF.Services
{
    public sealed class GraphDownloader : IDisposable
    {
        private readonly AppConfig _cfg;
        private readonly TokenService _tokenService;
        private readonly ILogSink _log;
        private readonly IProgress<double>? _progress;
        private readonly HttpClient _http;

        private bool _disposed;
        private int _downloadedCount;

        // Controle de erros para evitar spam no log
        private readonly HashSet<string> _loggedErrors = new();
        private int _consecutiveFailures;
        private const int MaxConsecutiveFailures = 10;

        // Record com nomes exatos (maiúsculas importam para named args)
        private sealed record DriveEntry(string DriveId, string ItemId, string Name, string ETag);

        public GraphDownloader(AppConfig cfg, TokenService tokenService, ILogSink log, IProgress<double>? progress = null)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _progress = progress;

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = CalculateMaxConnections(),
                UseCookies = false // Melhora performance para APIs
            };

            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(30, _cfg.HttpTimeoutSeconds))
            };

            SetupHttpClientDefaults();
        }

        // ===== API PÚBLICA =====

        /// <summary>
        /// Baixa arquivos JSON que correspondem aos prefixos configurados
        /// </summary>
        /// <param name="targetFolder">Pasta de destino</param>
        /// <param name="ct">Token de cancelamento</param>
        /// <returns>Lista de caminhos dos arquivos baixados</returns>
        public Task<List<string>> DownloadMatchingJsonAsync(string targetFolder, CancellationToken ct = default)
            => DownloadMatchingJsonAsync(targetFolder, extraQueries: null, ct: ct);

        /// <summary>
        /// Baixa arquivos JSON que correspondem aos prefixos configurados e consultas extras
        /// </summary>
        /// <param name="targetFolder">Pasta de destino</param>
        /// <param name="extraQueries">Consultas adicionais aos prefixos configurados</param>
        /// <param name="ct">Token de cancelamento</param>
        /// <returns>Lista de caminhos dos arquivos baixados</returns>
        public async Task<List<string>> DownloadMatchingJsonAsync(
            string targetFolder,
            IEnumerable<string>? extraQueries,
            CancellationToken ct = default)
        {
            try
            {
                ThrowIfDisposed();
                ValidateTargetFolder(targetFolder);

                Directory.CreateDirectory(targetFolder);

                await SetupAuthenticationAsync().ConfigureAwait(false);

                var wanted = GetWantedPrefixes(extraQueries);
                if (wanted.Length == 0)
                {
                    _log.Log("[INFO] Nenhum prefixo configurado para busca.");
                    return new List<string>();
                }

                var entries = await GetDriveEntriesAsync(wanted, ct).ConfigureAwait(false);
                _log.Log($"[INFO] Encontrados {entries.Count} arquivos candidatos.");
                _progress?.Report(35);

                // Reset contador para novos downloads
                _downloadedCount = 0;

                var toDownload = FilterEntriesForDownload(entries, wanted, targetFolder);
                if (_cfg.SkipUnchanged && entries.Count > toDownload.Count)
                    _log.Log($"[INFO] {entries.Count - toDownload.Count} arquivos já estão atualizados.");

                var results = await DownloadFilesInParallelAsync(toDownload, targetFolder, ct).ConfigureAwait(false);

                _progress?.Report(100);
                _log.Log($"[OK] Download concluído: {results.Count} arquivos processados.");

                return results;
            }
            catch (OperationCanceledException)
            {
                _log.Log("[INFO] Operação cancelada pelo usuário.");
                throw;
            }
            catch (Exception ex) when (IsCriticalException(ex))
            {
                LogUniqueError($"CRITICAL", ex.Message);
                throw; // Re-throw exceções críticas
            }
            catch (Exception ex)
            {
                LogUniqueError($"ERROR", ex.Message);
                return new List<string>(); // Retorna lista vazia em caso de erro não-crítico
            }
        }

        // =========================
        // MÉTODOS PRIVADOS
        // =========================

        private void SetupHttpClientDefaults()
        {
            _http.DefaultRequestVersion = new Version(2, 0);
            _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _http.DefaultRequestHeaders.Add("User-Agent", "leituraWPF/1.0");
        }

        private int CalculateMaxConnections()
        {
            var maxParallel = _cfg.MaxParallelDownloads > 0 ? _cfg.MaxParallelDownloads : 8;
            return Math.Max(4, maxParallel * 2);
        }

        private static void ValidateTargetFolder(string targetFolder)
        {
            if (string.IsNullOrWhiteSpace(targetFolder))
                throw new ArgumentException("targetFolder não pode ser nulo ou vazio.", nameof(targetFolder));
        }

        private async Task SetupAuthenticationAsync()
        {
            try
            {
                var token = await _tokenService.GetTokenAsync().ConfigureAwait(false);
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Falha na autenticação", ex);
            }
        }

        private string[] GetWantedPrefixes(IEnumerable<string>? extraQueries)
        {
            var cfgWanted = _cfg.WantedPrefixes?.Where(w => !string.IsNullOrWhiteSpace(w)) ?? Array.Empty<string>();
            var extra = extraQueries?.Where(w => !string.IsNullOrWhiteSpace(w)) ?? Array.Empty<string>();

            return cfgWanted.Concat(extra)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToArray();
        }

        private async Task<List<DriveEntry>> GetDriveEntriesAsync(string[] wanted, CancellationToken ct)
        {
            try
            {
                if (_cfg.ForceDriveSearch)
                {
                    var driveId = await GetDriveIdFromListAsync(ct).ConfigureAwait(false);
                    return await SearchInDriveByPrefixesAsync(driveId, wanted, ct).ConfigureAwait(false);
                }

                // Tenta OData na List + fallback para Drive
                _http.DefaultRequestHeaders.TryAddWithoutValidation("Prefer", "HonorNonIndexedQueriesWarningMayFailRandomly");

                var entries = await TryListQueryAsync("FileLeafRef", wanted, ct).ConfigureAwait(false)
                           ?? await TryListQueryAsync("LinkFilename", wanted, ct).ConfigureAwait(false);

                if (entries is null)
                {
                    var driveId = await GetDriveIdFromListAsync(ct).ConfigureAwait(false);
                    entries = await SearchInDriveByPrefixesAsync(driveId, wanted, ct).ConfigureAwait(false);
                }

                return entries ?? new List<DriveEntry>();
            }
            catch (Exception ex)
            {
                LogUniqueError("SEARCH", $"Falha na busca de arquivos: {ex.Message}");
                return new List<DriveEntry>(); // Retorna lista vazia em caso de falha
            }
        }

        private List<DriveEntry> FilterEntriesForDownload(List<DriveEntry> entries, string[] wanted, string targetFolder)
        {
            var index = new DownloadIndexService(targetFolder);

            return entries
                .Where(IsJsonFileMatchingPrefixes)
                .Where(entry => index.ShouldDownload(entry.ItemId, entry.ETag ?? "", _cfg.SkipUnchanged))
                .ToList();

            bool IsJsonFileMatchingPrefixes(DriveEntry entry) =>
                entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                wanted.Any(prefix => entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<List<string>> DownloadFilesInParallelAsync(
            List<DriveEntry> toDownload,
            string targetFolder,
            CancellationToken ct)
        {
            if (toDownload.Count == 0)
                return new List<string>();

            var degree = Math.Max(1, _cfg.MaxParallelDownloads > 0 ? _cfg.MaxParallelDownloads : 8);
            using var sem = new SemaphoreSlim(degree);

            var results = new List<string>();
            var index = new DownloadIndexService(targetFolder);
            int done = 0;
            var total = Math.Max(1, toDownload.Count);

            var tasks = toDownload.Select(entry => DownloadSingleFileAsync(entry, targetFolder, sem, index, results, ct, total));

            await Task.WhenAll(tasks).ConfigureAwait(false);
            index.Save();

            return results;
        }

        private async Task DownloadSingleFileAsync(
            DriveEntry entry,
            string targetFolder,
            SemaphoreSlim semaphore,
            DownloadIndexService index,
            List<string> results,
            CancellationToken ct,
            int total)
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var destinationPath = Path.Combine(targetFolder, entry.Name);
                var url = $"https://graph.microsoft.com/v1.0/drives/{entry.DriveId}/items/{entry.ItemId}/content";

                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    HandleHttpError(response, entry.Name);
                    return;
                }

                await WriteFileAsync(response, destinationPath, ct).ConfigureAwait(false);

                index.Record(entry.ItemId, entry.ETag ?? "");

                lock (results)
                {
                    results.Add(destinationPath);
                }

                // Reset contador de falhas consecutivas em caso de sucesso
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                ReportProgress(total);
            }
            catch (OperationCanceledException)
            {
                // Silencioso para cancelamentos
                throw;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("timeout"))
            {
                LogUniqueError("TIMEOUT", $"Timeout no download de '{entry.Name}'");
            }
            catch (Exception ex) when (!IsCriticalException(ex))
            {
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                if (failures <= 3) // Log apenas as primeiras 3 falhas
                {
                    LogUniqueError($"DOWNLOAD_{entry.Name}", ex.Message);
                }

                if (failures >= MaxConsecutiveFailures)
                {
                    _log.Log("[WARN] Muitas falhas consecutivas - possível problema de conectividade.");
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        private static async Task WriteFileAsync(HttpResponseMessage response, string destinationPath, CancellationToken ct)
        {
            const int bufferSize = 81920; // 80KB buffer

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

            await responseStream.CopyToAsync(fileStream, bufferSize, ct).ConfigureAwait(false);
        }

        private void ReportProgress(int total)
        {
            var currentDone = Interlocked.Increment(ref _downloadedCount);
            var progress = 35 + (currentDone * 60.0 / total);
            _progress?.Report(Math.Min(95, progress));
        }

        // =========================
        // Tentativa 1: ListItem + OData (extraindo driveId)
        // =========================
        private async Task<List<DriveEntry>?> TryListQueryAsync(string fieldName, string[] prefixes, CancellationToken ct)
        {
            try
            {
                var filter = BuildODataFilter(fieldName, prefixes);
                var url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.SiteId}/lists/{_cfg.ListId}/items" +
                          $"?$expand=driveItem" +
                          $"&$filter=({filter})" +
                          $"&$top=2000";

                var listItems = await GetPagedAsync(url, ct).ConfigureAwait(false);
                return ExtractDriveEntriesFromListItems(listItems);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!IsCriticalException(ex))
            {
                // Log silencioso - método é usado como tentativa
                return null;
            }
        }

        private static string BuildODataFilter(string fieldName, string[] prefixes)
        {
            return string.Join(" or ", prefixes.Select(p => $"startswith(fields/{fieldName},'{ODataEscape(p)}')"));
        }

        private static List<DriveEntry> ExtractDriveEntriesFromListItems(List<JsonObject> listItems)
        {
            var result = new List<DriveEntry>(listItems.Count);

            foreach (var item in listItems)
            {
                if (item["driveItem"] is not JsonObject driveItem) continue;

                var name = driveItem["name"]?.ToString() ?? "";
                var id = driveItem["id"]?.ToString() ?? "";
                var eTag = driveItem["eTag"]?.ToString() ?? "";
                var driveId = (driveItem["parentReference"] as JsonObject)?["driveId"]?.ToString() ?? "";

                if (IsValidDriveEntry(name, id, driveId))
                {
                    result.Add(new DriveEntry(driveId, id, name, eTag));
                }
            }

            return result;

            static bool IsValidDriveEntry(string name, string id, string driveId) =>
                !string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(id) &&
                !string.IsNullOrWhiteSpace(driveId);
        }

        // =========================
        // Fallback: Search no Drive
        // =========================
        private async Task<List<DriveEntry>> SearchInDriveByPrefixesAsync(string driveId, string[] prefixes, CancellationToken ct)
        {
            var results = new List<DriveEntry>();
            int step = 0;
            var total = Math.Max(1, prefixes.Length);

            foreach (var prefix in prefixes)
            {
                await SearchSinglePrefixAsync(driveId, prefix, results, ct).ConfigureAwait(false);
                ReportSearchProgress(++step, total);
            }

            return DeduplicateResults(results);
        }

        private async Task SearchSinglePrefixAsync(string driveId, string prefix, List<DriveEntry> results, CancellationToken ct)
        {
            var url = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/search(q='{Uri.EscapeDataString(prefix)}')?$top=999";
            try
            {
                var items = await GetPagedAsync(url, ct).ConfigureAwait(false);
                var mapped = items
                    .Where(i => i["file"] != null) // garante que é arquivo
                    .Select(i => new DriveEntry(
                        driveId,
                        i["id"]?.ToString() ?? "",
                        i["name"]?.ToString() ?? "",
                        i["eTag"]?.ToString() ?? ""
                    ))
                    .Where(x => !string.IsNullOrWhiteSpace(x.ItemId) && !string.IsNullOrWhiteSpace(x.Name))
                    .ToList();

                results.AddRange(mapped);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!IsCriticalException(ex))
            {
                LogUniqueError($"SEARCH_{prefix}", $"Busca falhou para '{prefix}': {ex.Message}");
            }
        }

        private void ReportSearchProgress(int step, int total)
        {
            _progress?.Report(20 + (step * 10.0 / total));
        }

        private static List<DriveEntry> DeduplicateResults(List<DriveEntry> results)
        {
            return results
                .GroupBy(x => $"{x.DriveId}|{x.ItemId}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private async Task<string> GetDriveIdFromListAsync(CancellationToken ct)
        {
            var url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.SiteId}/lists/{_cfg.ListId}/drive";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            await EnsureSuccessOrThrowAsync(resp, "GET driveId (list/drive)").ConfigureAwait(false);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var node = JsonNode.Parse(json)?.AsObject();
            var id = node?["id"]?.ToString();

            return !string.IsNullOrWhiteSpace(id)
                ? id!
                : throw new InvalidOperationException("Não foi possível resolver o driveId para a ListId informada.");
        }

        // =========================
        // Helpers HTTP/OData
        // =========================
        private async Task<List<JsonObject>> GetPagedAsync(string initialUrl, CancellationToken ct)
        {
            var list = new List<JsonObject>();
            var url = initialUrl;

            while (!string.IsNullOrEmpty(url))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                await EnsureSuccessOrThrowAsync(resp, $"GET {url}").ConfigureAwait(false);

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var root = JsonNode.Parse(json)?.AsObject();

                if (root?["value"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item is JsonObject obj)
                            list.Add(obj);
                    }
                }

                url = root?["@odata.nextLink"]?.ToString();
            }

            return list;
        }

        private static string ODataEscape(string s) => s.Replace("'", "''");

        private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage resp, string op)
        {
            if (resp.IsSuccessStatusCode) return;

            string body = string.Empty;
            try
            {
                body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore  
            }

            throw resp.StatusCode switch
            {
                HttpStatusCode.BadRequest => new GraphBadRequestException($"{op}: {Truncate(body, 200)}"),
                HttpStatusCode.Unauthorized => new UnauthorizedAccessException($"Token inválido ou expirado: {op}"),
                HttpStatusCode.Forbidden => new UnauthorizedAccessException($"Acesso negado: {op}"),
                HttpStatusCode.NotFound => new FileNotFoundException($"Recurso não encontrado: {op}"),
                HttpStatusCode.TooManyRequests => new InvalidOperationException($"Rate limit atingido: {op}"),
                _ => new HttpRequestException($"{op} falhou com status {resp.StatusCode}")
            };
        }

        private void HandleHttpError(HttpResponseMessage response, string fileName)
        {
            var errorKey = $"HTTP_{response.StatusCode}";

            var message = response.StatusCode switch
            {
                HttpStatusCode.NotFound => $"Arquivo não encontrado: {fileName}",
                HttpStatusCode.Unauthorized => "Token de autenticação inválido",
                HttpStatusCode.Forbidden => $"Acesso negado ao arquivo: {fileName}",
                HttpStatusCode.TooManyRequests => "Rate limit atingido - muitas requisições",
                _ => $"Erro HTTP {response.StatusCode} ao baixar {fileName}"
            };

            LogUniqueError(errorKey, message);
        }

        private void LogUniqueError(string errorType, string message)
        {
            var key = $"{errorType}:{message.GetHashCode()}";

            lock (_loggedErrors)
            {
                if (_loggedErrors.Add(key))
                {
                    _log.Log($"[{errorType}] {message}");
                }
            }
        }

        private static bool IsCriticalException(Exception ex) =>
            ex is OutOfMemoryException or
                  StackOverflowException or
                  AccessViolationException or
                  AppDomainUnloadedException or
                  BadImageFormatException or
                  InvalidProgramException;

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s[..max] + "...";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GraphDownloader));
        }

        // =========================
        // IDisposable
        // =========================
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _http?.Dispose();
            }
            catch
            {
                // Silencioso no dispose
            }
            finally
            {
                _disposed = true;
            }
        }

        private sealed class GraphBadRequestException : Exception
        {
            public GraphBadRequestException(string msg) : base(msg) { }
        }
    }
}