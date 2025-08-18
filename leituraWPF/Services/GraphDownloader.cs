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
    public sealed class GraphDownloader
    {
        private readonly AppConfig _cfg;
        private readonly TokenService _tokenService;
        private readonly ILogSink _log;
        private readonly IProgress<double>? _progress;

        private readonly HttpClient _http;

        // Record com nomes exatos (maiúsculas importam para named args)
        private record DriveEntry(string DriveId, string ItemId, string Name, string ETag);

        public GraphDownloader(AppConfig cfg, TokenService tokenService, ILogSink log, IProgress<double>? progress = null)
        {
            _cfg = cfg;
            _tokenService = tokenService;
            _log = log;
            _progress = progress;

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = Math.Max(4, _cfg.MaxParallelDownloads > 0 ? _cfg.MaxParallelDownloads * 2 : 16)
            };

            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_cfg.HttpTimeoutSeconds > 0 ? _cfg.HttpTimeoutSeconds : 120)
            };
            _http.DefaultRequestVersion = new Version(2, 0);
            _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        }

        // ===== API PÚBLICA =====

        // Antigo (compatível)
        public Task<List<string>> DownloadMatchingJsonAsync(string targetFolder, CancellationToken ct = default)
            => DownloadMatchingJsonAsync(targetFolder, extraQueries: null, ct: ct);

        // Novo (corrige CS1739 — aceita named arg extraQueries)
        public async Task<List<string>> DownloadMatchingJsonAsync(
            string targetFolder,
            IEnumerable<string>? extraQueries,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(targetFolder))
                throw new ArgumentException("targetFolder inválido.", nameof(targetFolder));

            Directory.CreateDirectory(targetFolder);

            var token = await _tokenService.GetTokenAsync().ConfigureAwait(false);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _http.DefaultRequestHeaders.Remove("Prefer"); // não precisamos quando usamos Drive Search

            // ===== união de prefixes: appsettings + extras =====
            var cfgWanted = _cfg.WantedPrefixes?.Where(w => !string.IsNullOrWhiteSpace(w)) ?? Array.Empty<string>();
            var extra = extraQueries?.Where(w => !string.IsNullOrWhiteSpace(w)) ?? Array.Empty<string>();
            var wanted = cfgWanted.Concat(extra)
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .ToArray();

            if (wanted.Length == 0)
            {
                _log.Log("[INFO] Nenhum prefixo para buscar (WantedPrefixes vazio e sem extras).");
                return new List<string>();
            }

            List<DriveEntry>? entries = null;

            if (_cfg.ForceDriveSearch)
            {
                // 🚀 Caminho direto via Drive (evita OData em campos não indexados)
                var driveId = await GetDriveIdFromListAsync(ct);
                entries = await SearchInDriveByPrefixesAsync(driveId, wanted, ct);
                _log.Log("[INFO] ForceDriveSearch habilitado: usando busca no drive.");
            }
            else
            {
                // Caminho anterior: tenta OData na List + fallback para Drive
                _http.DefaultRequestHeaders.TryAddWithoutValidation("Prefer", "HonorNonIndexedQueriesWarningMayFailRandomly");

                entries = await TryListQueryAsync("FileLeafRef", wanted, ct)
                       ?? await TryListQueryAsync("LinkFilename", wanted, ct);

                if (entries is null)
                {
                    _log.Log("[WARN] Filtro OData na lista falhou. Usando fallback de busca no drive (search).");
                    var driveId = await GetDriveIdFromListAsync(ct);
                    entries = await SearchInDriveByPrefixesAsync(driveId, wanted, ct);
                }
            }

            entries ??= new List<DriveEntry>();
            _log.Log($"[INFO] Arquivos JSON candidatos: {entries.Count}.");
            _progress?.Report(35);

            // Índice ETag para evitar re-download
            var index = new DownloadIndexService(targetFolder);
            var toDownload = entries
                .Where(x => x.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                         && wanted.Any(p => x.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                         && index.ShouldDownload(x.ItemId, x.ETag ?? "", _cfg.SkipUnchanged))
                .ToList();

            if (_cfg.SkipUnchanged)
                _log.Log($"[INFO] Ignorando inalterados (ETag): {entries.Count - toDownload.Count}.");

            // Downloads em paralelo (sempre /drives/{driveId}/items/{id}/content)
            var degree = Math.Max(1, _cfg.MaxParallelDownloads > 0 ? _cfg.MaxParallelDownloads : 8);
            var sem = new SemaphoreSlim(degree);
            var results = new List<string>();
            int done = 0, total = Math.Max(1, toDownload.Count);

            var tasks = toDownload.Select(async x =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var dst = Path.Combine(targetFolder, x.Name);
                    var url = $"https://graph.microsoft.com/v1.0/drives/{x.DriveId}/items/{x.ItemId}/content";

                    using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    await EnsureSuccessOrThrowAsync(resp, $"GET content '{x.Name}'");

                    await using (var fs = File.Open(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await resp.Content.CopyToAsync(fs, ct);
                    }

                    index.Record(x.ItemId, x.ETag ?? "");
                    lock (results) results.Add(dst);

                    var p = 35 + (Interlocked.Increment(ref done) * 60.0 / total);
                    _progress?.Report(Math.Min(95, p));
                }
                catch (Exception ex)
                {
                    _log.Log($"[ERRO] Download '{x.Name}': {ex.Message}");
                }
                finally
                {
                    sem.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);
            index.Save();

            _progress?.Report(97);
            _log.Log($"[OK] Downloads finalizados. Novos/atualizados: {results.Count}.");

            return results;
        }

        // =========================
        // Tentativa 1: ListItem + OData (extraindo driveId)
        // =========================
        private async Task<List<DriveEntry>?> TryListQueryAsync(string fieldName, string[] prefixes, CancellationToken ct)
        {
            try
            {
                var filter = string.Join(" or ", prefixes.Select(p => $"startswith(fields/{fieldName},'{ODataEscape(p)}')"));
                var url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.SiteId}/lists/{_cfg.ListId}/items" +
                          $"?$expand=driveItem" +
                          $"&$filter=({filter})" +
                          $"&$top=2000";

                var listItems = await GetPagedAsync(url, ct);
                _log.Log($"[INFO] ListItem OK com '{fieldName}'. Itens: {listItems.Count}");

                var result = new List<DriveEntry>(listItems.Count);
                foreach (var it in listItems)
                {
                    var di = it["driveItem"] as JsonObject;
                    if (di == null) continue;

                    var name = di["name"]?.ToString() ?? "";
                    var id = di["id"]?.ToString() ?? "";
                    var eTag = di["eTag"]?.ToString() ?? "";

                    // parentReference.driveId
                    var parentRef = di["parentReference"] as JsonObject;
                    var driveId = parentRef?["driveId"]?.ToString() ?? "";

                    if (!string.IsNullOrWhiteSpace(name) &&
                        !string.IsNullOrWhiteSpace(id) &&
                        !string.IsNullOrWhiteSpace(driveId))
                    {
                        result.Add(new DriveEntry(driveId, id, name, eTag));
                    }
                }
                return result;
            }
            catch (HttpRequestException ex)
            {
                _log.Log($"[WARN] ListItem com '{fieldName}' falhou: {ex.Message}");
                return null;
            }
            catch (GraphBadRequestException ex)
            {
                _log.Log($"[WARN] 400 ListItem '{fieldName}': {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _log.Log($"[WARN] ListItem '{fieldName}' inesperado: {ex.Message}");
                return null;
            }
        }

        // =========================
        // Fallback: Search no Drive
        // =========================
        private async Task<List<DriveEntry>> SearchInDriveByPrefixesAsync(string driveId, string[] prefixes, CancellationToken ct)
        {
            var results = new List<DriveEntry>();
            int step = 0, total = Math.Max(1, prefixes.Length);

            foreach (var p in prefixes)
            {
                var url = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/search(q='{Uri.EscapeDataString(p)}')?$top=999";
                try
                {
                    var items = await GetPagedAsync(url, ct);
                    var mapped = items
                        .Where(i => i["file"] != null) // garante que é arquivo
                        .Select(i => new DriveEntry(
                            driveId,                                         // <-- posicional, evita erro de nome
                            i["id"]?.ToString() ?? "",
                            i["name"]?.ToString() ?? "",
                            i["eTag"]?.ToString() ?? ""
                        ))
                        .Where(x => !string.IsNullOrWhiteSpace(x.ItemId) && !string.IsNullOrWhiteSpace(x.Name))
                        .ToList();

                    _log.Log($"[INFO] Search '{p}': {mapped.Count} itens.");
                    results.AddRange(mapped);
                }
                catch (Exception ex)
                {
                    _log.Log($"[WARN] Search '{p}' falhou: {ex.Message}");
                }

                _progress?.Report(20 + (++step * 10.0 / total));
            }

            // remove duplicatas por chave composta (case-insensitive)
            return results
                .GroupBy(x => $"{x.DriveId}|{x.ItemId}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private async Task<string> GetDriveIdFromListAsync(CancellationToken ct)
        {
            var url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.SiteId}/lists/{_cfg.ListId}/drive";
            using var resp = await _http.GetAsync(url, ct);
            await EnsureSuccessOrThrowAsync(resp, "GET driveId (list/drive)");
            var json = await resp.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json)?.AsObject();
            var id = node?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("Não foi possível resolver o driveId para a ListId informada.");
            return id!;
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
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                await EnsureSuccessOrThrowAsync(resp, $"GET {url}");

                var json = await resp.Content.ReadAsStringAsync(ct);
                var root = JsonNode.Parse(json)?.AsObject();
                var arr = root?["value"] as JsonArray;
                if (arr != null)
                {
                    foreach (var it in arr)
                        if (it is JsonObject obj) list.Add(obj);
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
            try { body = await resp.Content.ReadAsStringAsync(); } catch { /* ignore */ }

            if (resp.StatusCode == HttpStatusCode.BadRequest)
                throw new GraphBadRequestException($"{op} retornou 400. Corpo: {Truncate(body, 500)}");

            resp.EnsureSuccessStatusCode();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max) + "...";
        }

        private sealed class GraphBadRequestException : Exception
        {
            public GraphBadRequestException(string msg) : base(msg) { }
        }
    }
}
