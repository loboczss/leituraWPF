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
using leituraWPF.Utils; // <= usar AppConfig daqui

namespace leituraWPF.Services
{
    /// <summary>
    /// Gerencia o arquivo local processados.json e sincroniza com a lista SharePoint.
    /// Versão reforçada: autodetecção de Site, checagem de colunas, logs detalhados e retry.
    /// </summary>
    public sealed class ProcessadosService
    {
        private readonly AppConfig _cfg;
        private readonly TokenService _tokenService;
        private readonly HttpClient _http;
        private readonly string _filePath;
        private readonly string _logPath;
        private string? _siteSpecifier; // "sites/{id}" OU "sites/{host}:/sites/{path}:"
        private string? _listId;
        private readonly SemaphoreSlim _mutex = new(1, 1);

        private class Entry
        {
            public string NumOS { get; set; } = string.Empty;
            public string Pasta { get; set; } = string.Empty;
            public string Usuario { get; set; } = string.Empty;
            public int Quantidade { get; set; }
            public string Versao { get; set; } = string.Empty;
            public bool Sincronizado { get; set; }
        }

        public ProcessadosService(AppConfig cfg, TokenService tokenService)
        {
            _cfg = cfg;
            _tokenService = tokenService;
            _http = new HttpClient();
            _filePath = Path.Combine(AppContext.BaseDirectory, "processados.json");
            _logPath = Path.Combine(AppContext.BaseDirectory, "processados_sync.log");
        }

        // ============ API pública ============
        public async Task AddAsync(string numos, string pasta, string usuario, IEnumerable<string> arquivos, string versao)
        {
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = await LoadAsync().ConfigureAwait(false);
                var arr = arquivos?.ToList() ?? new List<string>();
                list.Add(new Entry
                {
                    NumOS = numos ?? string.Empty,
                    Pasta = pasta ?? string.Empty,
                    Usuario = usuario ?? string.Empty,
                    Quantidade = arr.Count,
                    Versao = versao ?? string.Empty,
                    Sincronizado = false
                });
                await SaveAsync(list).ConfigureAwait(false);
            }
            finally { _mutex.Release(); }
        }

        public async Task TrySyncAsync()
        {
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = await LoadAsync().ConfigureAwait(false);
                var pendentes = list.Where(e => !e.Sincronizado).ToList();
                if (pendentes.Count == 0)
                {
                    await LogAsync("Nada a sincronizar.").ConfigureAwait(false);
                    return;
                }

                await PrepareGraphClientAsync().ConfigureAwait(false);

                var siteSpec = await ResolveSiteSpecifierAsync().ConfigureAwait(false);
                if (siteSpec is null)
                {
                    await LogAsync("Falha ao resolver Site (SiteId/URL). Abandonando sync.").ConfigureAwait(false);
                    return;
                }

                var listId = await ResolveListIdAsync(siteSpec, _cfg.ProcessLogListName).ConfigureAwait(false);
                if (string.IsNullOrEmpty(listId))
                {
                    await LogAsync($"Lista '{_cfg.ProcessLogListName}' não encontrada nem criada.").ConfigureAwait(false);
                    return;
                }
                _listId ??= listId;

                // Teste rápido: cria item mínimo (só Title). Se falhar -> permissão/rota/tokens.
                await ProbeCreateMinimalItemAsync(siteSpec, listId).ConfigureAwait(false);

                // Garante colunas
                await EnsureColumnsAsync(siteSpec, listId).ConfigureAwait(false);

                string itemsUrl = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/items";
                int ok = 0, fail = 0;

                foreach (var item in pendentes)
                {
                    var body = new
                    {
                        fields = new Dictionary<string, object?>
                        {
                            ["Title"] = string.IsNullOrWhiteSpace(item.NumOS) ? "(vazio)" : item.NumOS,
                            ["Usuario"] = item.Usuario,
                            ["Versao"] = item.Versao,
                            ["Pasta"] = item.Pasta,
                            ["Quantidade"] = item.Quantidade
                        }
                    };

                    var payload = JsonSerializer.Serialize(body);
                    var resp = await PostWithRetryAsync(itemsUrl, payload).ConfigureAwait(false);

                    if (resp.success)
                    {
                        item.Sincronizado = true;
                        ok++;
                    }
                    else
                    {
                        fail++;
                        await LogAsync($"Falha ao criar item (Title='{item.NumOS}'). " +
                                       $"HTTP {(int)resp.statusCode} {resp.statusCode}. Body={resp.body}")
                            .ConfigureAwait(false);
                    }
                }

                await SaveAsync(list).ConfigureAwait(false);
                await LogAsync($"Sync finalizado. OK={ok}, Falhas={fail}.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await LogAsync("TrySyncAsync EXCEPTION: " + ex).ConfigureAwait(false);
            }
            finally
            {
                _mutex.Release();
            }
        }

        public static async Task<bool> HasInternetConnectionAsync()
        {
            try
            {
                using var client = new HttpClient();
                using var resp = await client.GetAsync("https://graph.microsoft.com/v1.0/$metadata", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                return resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Unauthorized;
            }
            catch { return false; }
        }

        // ============ Infra/Privados ============

        private async Task PrepareGraphClientAsync()
        {
            var token = await _tokenService.GetTokenAsync().ConfigureAwait(false);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private async Task<string?> ResolveSiteSpecifierAsync()
        {
            if (!string.IsNullOrEmpty(_siteSpecifier))
                return _siteSpecifier;

            string raw = _cfg.SiteId?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(raw))
            {
                await LogAsync("AppConfig.SiteId vazio.").ConfigureAwait(false);
                return null;
            }

            if (raw.Contains(":/sites/", StringComparison.OrdinalIgnoreCase))
            {
                _siteSpecifier = $"sites/{raw.TrimStart('/')}";
                return _siteSpecifier;
            }

            if (raw.Contains(","))
            {
                _siteSpecifier = $"sites/{raw}";
                return _siteSpecifier;
            }

            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var uri = new Uri(raw);
                    var host = uri.Host;
                    var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    int idxSites = Array.FindIndex(segments, s => s.Equals("sites", StringComparison.OrdinalIgnoreCase));
                    if (idxSites >= 0 && idxSites + 1 < segments.Length)
                    {
                        var sitePath = string.Join('/', segments.Skip(idxSites + 1));
                        _siteSpecifier = $"sites/{host}:/sites/{sitePath}:";
                        return _siteSpecifier;
                    }

                    var search = await _http.GetStringAsync($"https://graph.microsoft.com/v1.0/sites?search={Uri.EscapeDataString(uri.AbsoluteUri)}")
                                            .ConfigureAwait(false);
                    var id = JsonNode.Parse(search)?["value"]?.AsArray()?.OfType<JsonObject>()
                                      ?.FirstOrDefault()?["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        _siteSpecifier = $"sites/{id}";
                        return _siteSpecifier;
                    }
                }
                catch (Exception ex)
                {
                    await LogAsync("ResolveSiteSpecifier (URL) EXCEPTION: " + ex).ConfigureAwait(false);
                    return null;
                }
            }

            _siteSpecifier = $"sites/{raw}";
            return _siteSpecifier;
        }

        private async Task<string?> ResolveListIdAsync(string siteSpec, string listDisplayName)
        {
            try
            {
                var url = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists?$select=id,displayName";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var arr = JsonNode.Parse(json)?["value"]?.AsArray();

                var match = arr?.OfType<JsonObject>()
                    .FirstOrDefault(o => string.Equals(o?["displayName"]?.ToString(), listDisplayName, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                    return match["id"]?.ToString();

                var createBody = new
                {
                    displayName = listDisplayName,
                    list = new { template = "genericList" }
                };
                var payload = JsonSerializer.Serialize(createBody);
                var resp = await _http.PostAsync($"https://graph.microsoft.com/v1.0/{siteSpec}/lists",
                                                 new StringContent(payload, Encoding.UTF8, "application/json"))
                                      .ConfigureAwait(false);

                var bodyText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    await LogAsync($"Falha ao criar lista '{listDisplayName}'. HTTP {(int)resp.StatusCode} {resp.StatusCode}. Body={bodyText}")
                        .ConfigureAwait(false);
                    return null;
                }

                var created = JsonNode.Parse(bodyText)?.AsObject();
                return created?["id"]?.ToString();
            }
            catch (Exception ex)
            {
                await LogAsync("ResolveListIdAsync EXCEPTION: " + ex).ConfigureAwait(false);
                return null;
            }
        }

        private async Task EnsureColumnsAsync(string siteSpec, string listId)
        {
            try
            {
                var url = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/columns?$select=id,name,displayName";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var existing = (JsonNode.Parse(json)?["value"]?.AsArray() ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Select(o => new
                    {
                        Name = o?["name"]?.ToString() ?? "",
                        DisplayName = o?["displayName"]?.ToString() ?? ""
                    })
                    .ToList();

                bool Has(string internalOrDisplay) =>
                    existing.Any(e =>
                        internalOrDisplay.Equals(e.Name, StringComparison.OrdinalIgnoreCase) ||
                        internalOrDisplay.Equals(e.DisplayName, StringComparison.OrdinalIgnoreCase));

                async Task CreateTextAsync(string internalName, string displayName)
                {
                    var body = new { name = internalName, displayName = displayName, text = new { } };
                    var payload = JsonSerializer.Serialize(body);
                    var resp = await _http.PostAsync(
                        $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/columns",
                        new StringContent(payload, Encoding.UTF8, "application/json")).ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                    {
                        var t = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        await LogAsync($"Falha ao criar coluna '{displayName}' (internal '{internalName}'). " +
                                       $"HTTP {(int)resp.StatusCode} {resp.StatusCode}. Body={t}").ConfigureAwait(false);
                    }
                }

                async Task CreateNumberAsync(string internalName, string displayName)
                {
                    var body = new { name = internalName, displayName = displayName, number = new { } };
                    var payload = JsonSerializer.Serialize(body);
                    var resp = await _http.PostAsync(
                        $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/columns",
                        new StringContent(payload, Encoding.UTF8, "application/json")).ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                    {
                        var t = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        await LogAsync($"Falha ao criar coluna '{displayName}' (internal '{internalName}'). " +
                                       $"HTTP {(int)resp.StatusCode} {resp.StatusCode}. Body={t}").ConfigureAwait(false);
                    }
                }

                if (!Has("Usuario")) await CreateTextAsync("Usuario", "Usuário").ConfigureAwait(false);
                if (!Has("Pasta")) await CreateTextAsync("Pasta", "Pasta").ConfigureAwait(false);
                if (!Has("Quantidade")) await CreateNumberAsync("Quantidade", "Quantidade").ConfigureAwait(false);
                if (!Has("Versao")) await CreateTextAsync("Versao", "Versão").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await LogAsync("EnsureColumnsAsync EXCEPTION: " + ex).ConfigureAwait(false);
            }
        }

        private async Task ProbeCreateMinimalItemAsync(string siteSpec, string listId)
        {
            try
            {
                var url = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/items";
                var title = $"probe-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                var body = new { fields = new { Title = title } };
                var payload = JsonSerializer.Serialize(body);
                var resp = await PostWithRetryAsync(url, payload).ConfigureAwait(false);

                if (!resp.success)
                {
                    await LogAsync($"PROBE falhou. HTTP {(int)resp.statusCode} {resp.statusCode}. Body={resp.body}")
                        .ConfigureAwait(false);
                    throw new InvalidOperationException("Falha no PROBE de criação de item. Verifique permissões e SiteId/URL.");
                }
                else
                {
                    try
                    {
                        var id = JsonNode.Parse(resp.body)?["id"]?.ToString();
                        if (!string.IsNullOrEmpty(id))
                            await _http.DeleteAsync($"{url}/{id}").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await LogAsync("Falha ao remover item de probe: " + ex).ConfigureAwait(false);
                    }
                    await LogAsync("PROBE OK (item mínimo criado e removido).").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await LogAsync("ProbeCreateMinimalItemAsync EXCEPTION: " + ex).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<(bool success, HttpStatusCode statusCode, string body)> PostWithRetryAsync(string url, string payload, int maxAttempts = 3)
        {
            int attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                        return (true, resp.StatusCode, text);

                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    {
                        var delayMs = Math.Min(30000, (int)Math.Pow(2, attempt) * 500);
                        await LogAsync($"POST falhou (tentativa {attempt}/{maxAttempts}) HTTP {(int)resp.StatusCode}. Esperando {delayMs} ms. Body={text}")
                            .ConfigureAwait(false);
                        if (attempt < maxAttempts) { await Task.Delay(delayMs).ConfigureAwait(false); continue; }
                    }

                    return (false, resp.StatusCode, text);
                }
                catch (Exception ex)
                {
                    await LogAsync($"POST EXCEPTION (tentativa {attempt}/{maxAttempts}): {ex}").ConfigureAwait(false);
                    if (attempt >= maxAttempts) return (false, 0, ex.ToString());
                    await Task.Delay(800 * attempt).ConfigureAwait(false);
                }
            }
        }

        private async Task<List<Entry>> LoadAsync()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                    var list = JsonSerializer.Deserialize<List<Entry>>(json);
                    if (list != null) return list;
                }
            }
            catch (Exception ex) { await LogAsync("LoadAsync EXCEPTION: " + ex).ConfigureAwait(false); }
            return new List<Entry>();
        }

        private async Task SaveAsync(List<Entry> list)
        {
            try
            {
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
            }
            catch (Exception ex) { await LogAsync("SaveAsync EXCEPTION: " + ex).ConfigureAwait(false); }
        }

        private async Task LogAsync(string message)
        {
            try
            {
                var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(_logPath, line).ConfigureAwait(false);
            }
            catch { /* não deixa o log travar a execução */ }
        }
    }
}
