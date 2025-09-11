using System;
using System.Collections.Generic;
using System.Globalization;
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
using leituraWPF.Utils; // AppConfig daqui

namespace leituraWPF.Services
{
    /// <summary>
    /// Sincroniza processados.json com uma lista do SharePoint via Microsoft Graph.
    /// Cenário: todas as colunas (exceto Title) são TEXTO (Single line of text).
    /// - NÃO cria colunas (evita 403); apenas descobre nomes internos existentes
    /// - Envia TUDO como string (Quantidade convertida p/ string)
    /// - Trunca "Arquivos" para ~255 chars (se single line) com sufixo "(+N)"
    /// - Probe (cria e apaga item mínimo) para validar permissão/rota
    /// - Retry para 429/5xx, logs detalhados
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
            public string NumOS { get; set; } = string.Empty;         // vai em Title
            public string Pasta { get; set; } = string.Empty;          // opcional: se existir coluna "Pasta"
            public string Usuario { get; set; } = string.Empty;        // texto
            public string Pc { get; set; } = string.Empty;             // usuário do Windows
            public List<string> Arquivos { get; set; } = new();        // texto (join em ",")
            public int Quantidade { get; set; }                         // será enviado como string
            public string Versao { get; set; } = string.Empty;         // texto
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

        // ================== API pública ==================

        // Nova assinatura (com NumOS)
        public async Task AddAsync(string numos, string pasta, string usuario, string pc, IEnumerable<string> arquivos, string versao)
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
                    Pc = pc ?? string.Empty,
                    Arquivos = arr,
                    Quantidade = arr.Count,
                    Versao = versao ?? string.Empty,
                    Sincronizado = false
                });
                await SaveAsync(list).ConfigureAwait(false);
            }
            finally { _mutex.Release(); }
        }

        // Compat anterior (sem NumOS)
        public Task AddAsync(string pasta, string usuario, string pc, IEnumerable<string> arquivos, string versao)
            => AddAsync(numos: string.Empty, pasta: pasta, usuario: usuario, pc: pc, arquivos: arquivos, versao: versao);

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
                    await LogAsync("Falha ao resolver Site (SiteId/URL).").ConfigureAwait(false);
                    return;
                }

                var listId = await ResolveListIdAsync(siteSpec, _cfg.ProcessLogListName).ConfigureAwait(false);
                if (string.IsNullOrEmpty(listId))
                {
                    await LogAsync($"Lista '{_cfg.ProcessLogListName}' não encontrada nem criada.").ConfigureAwait(false);
                    return;
                }
                _listId ??= listId;

                // Valida que consigo criar item mínimo
                await ProbeCreateMinimalItemAsync(siteSpec, listId).ConfigureAwait(false);

                // Descobre nomes internos das colunas (sem criar nada)
                var colMap = await GetColumnsMapAsync(siteSpec, listId).ConfigureAwait(false);
                string? colUsuario = ResolveInternal(colMap, "Usuario", "Usuário", "User", "Responsavel", "Responsável");
                string? colPc = ResolveInternal(colMap, "PC", "Pc");
                string? colVersao = ResolveInternal(colMap, "Versao", "Versão", "Version");
                string? colArquivos = ResolveInternal(colMap, "Arquivos", "Files", "Documentos");
                string? colPasta = ResolveInternal(colMap, "Pasta", "Folder", "Diretorio", "Diretório", "Path");
                string? colQuantidade = ResolveInternal(colMap, "Quantidade", "Qtd", "Quantity", "Qtde");

                await LogAsync($"Mapeamento: Usuario={colUsuario ?? "-"} | PC={colPc ?? "-"} | Versao={colVersao ?? "-"} | Arquivos={colArquivos ?? "-"} | Pasta={colPasta ?? "-"} | Quantidade={colQuantidade ?? "-"}")
                    .ConfigureAwait(false);

                // Limpa probes antigos (se houver)
                await CleanupOldProbeItemsAsync(siteSpec, listId).ConfigureAwait(false);

                string itemsUrl = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/items";
                int ok = 0, fail = 0;

                // helper local – garante string
                static string S(object? v) => v?.ToString() ?? string.Empty;

                foreach (var item in pendentes)
                {
                    // Title = NumOS (fallback legível)
                    var title = !string.IsNullOrWhiteSpace(item.NumOS)
                        ? item.NumOS.Trim()
                        : (string.IsNullOrWhiteSpace(item.Pasta) ? "Sem-NumOS" : $"OS-{item.Pasta}");

                    var fields = new Dictionary<string, object?> { ["Title"] = S(title) };

                    // Usuario (texto)
                    if (colUsuario != null)
                        fields[colUsuario] = S(item.Usuario);

                    // PC (texto)
                    if (colPc != null)
                        fields[colPc] = S(item.Pc);

                    // Versao (texto)
                    if (colVersao != null)
                        fields[colVersao] = S(item.Versao);

                    // Arquivos (texto, truncado para ~255)
                    if (colArquivos != null)
                    {
                        var joined = string.Join(",", item.Arquivos ?? new List<string>());
                        const int max = 255; // ajuste se sua coluna aceitar mais
                        if (joined.Length > max)
                            joined = joined.Substring(0, max - 10) + $" (+{joined.Length - (max - 10)})";
                        fields[colArquivos] = joined;
                    }

                    // Pasta (texto) – só se existir
                    if (colPasta != null)
                        fields[colPasta] = S(item.Pasta);

                    // Quantidade (coluna é TEXTO → manda string)
                    if (colQuantidade != null)
                        fields[colQuantidade] = item.Quantidade.ToString(CultureInfo.InvariantCulture);

                    var body = new { fields };
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
                        await LogAsync($"Falha ao criar item (Title='{title}'). HTTP {(int)resp.statusCode} {resp.statusCode}. Body={resp.body}")
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

        // ================== Infra privada ==================

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

            if (raw.Contains(",")) // site-id do Graph
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

                    // fallback: busca pelo site
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

                // Tenta criar lista se não existir (pode falhar por permissão)
                var createBody = new { displayName = listDisplayName, list = new { template = "genericList" } };
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

        // ---- Descoberta de nomes internos (sem tipos) ----
        private static string NormalizeKey(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var formD = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (var ch in formD)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString().Replace(" ", "").Replace("_", "").Replace("-", "");
        }

        private async Task<Dictionary<string, string>> GetColumnsMapAsync(string siteSpec, string listId)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var url = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/columns?$select=name,displayName";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var arr = JsonNode.Parse(json)?["value"]?.AsArray() ?? new JsonArray();
                foreach (var o in arr.OfType<JsonObject>())
                {
                    var name = o?["name"]?.ToString();         // internal
                    var disp = o?["displayName"]?.ToString();  // label
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var k1 = NormalizeKey(name);
                    if (!map.ContainsKey(k1)) map[k1] = name;

                    if (!string.IsNullOrWhiteSpace(disp))
                    {
                        var k2 = NormalizeKey(disp);
                        if (!map.ContainsKey(k2)) map[k2] = name;
                    }
                }
            }
            catch (Exception ex)
            {
                await LogAsync("GetColumnsMapAsync EXCEPTION: " + ex).ConfigureAwait(false);
            }
            return map;
        }

        private static string? ResolveInternal(Dictionary<string, string> map, params string[] candidates)
        {
            foreach (var c in candidates)
            {
                var key = NormalizeKey(c);
                if (map.TryGetValue(key, out var internalName))
                    return internalName;
            }
            return null;
        }

        private async Task CleanupOldProbeItemsAsync(string siteSpec, string listId, int maxToDelete = 200)
        {
            try
            {
                var url = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/items?$top={maxToDelete}&expand=fields($select=Title)";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var items = JsonNode.Parse(json)?["value"]?.AsArray() ?? new JsonArray();

                foreach (var it in items.OfType<JsonObject>())
                {
                    var id = it?["id"]?.ToString();
                    var title = it?["fields"]?["Title"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(id) && title.StartsWith("probe-", StringComparison.OrdinalIgnoreCase))
                    {
                        await _http.DeleteAsync($"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/items/{id}").ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                await LogAsync("CleanupOldProbeItemsAsync EXCEPTION: " + ex).ConfigureAwait(false);
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
                    throw new InvalidOperationException("Falha no PROBE. Verifique permissões e SiteId/URL.");
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
            catch (Exception ex)
            {
                await LogAsync("LoadAsync EXCEPTION: " + ex).ConfigureAwait(false);
            }
            return new List<Entry>();
        }

        private async Task SaveAsync(List<Entry> entries)
        {
            try
            {
                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await LogAsync("SaveAsync EXCEPTION: " + ex).ConfigureAwait(false);
            }
        }

        private async Task LogAsync(string message)
        {
            try
            {
                var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(_logPath, line).ConfigureAwait(false);
            }
            catch { /* não trava o fluxo */ }
        }
    }
}
