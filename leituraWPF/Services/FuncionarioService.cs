using leituraWPF.Models;
using leituraWPF.Utils;
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

namespace leituraWPF.Services
{
    public sealed class FuncionarioService
    {
        private readonly AppConfig _cfg;
        private readonly TokenService _tokenService;
        private readonly HttpClient _http;
        private readonly string _logPath;

        public FuncionarioService(AppConfig cfg, TokenService tokenService)
        {
            _cfg = cfg;
            _tokenService = tokenService;

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };

            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_cfg.HttpTimeoutSeconds > 0 ? _cfg.HttpTimeoutSeconds : 120)
            };
            _http.DefaultRequestVersion = new Version(2, 0);
            _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;

            _logPath = Path.Combine(AppContext.BaseDirectory, "log.txt");
            _ = LogAsync("========= FuncionarioService inicializado =========");
        }

        // ====================== API ======================

        /// <summary>
        /// Baixa Nome e Matrícula da lista e grava em JSON. Retorna caminho do arquivo ou null.
        /// </summary>
        public async Task<string?> DownloadJsonAsync(string targetFolder, CancellationToken ct = default)
        {
            await LogAsync("=== DownloadJsonAsync: início ===");
            await LogAsync($"targetFolder='{targetFolder}'");

            try
            {
                Directory.CreateDirectory(targetFolder);

                // Auth
                string token;
                try
                {
                    token = await _tokenService.GetTokenAsync().ConfigureAwait(false);
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    _http.DefaultRequestHeaders.Accept.Clear();
                    _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    await LogAsync("Token OK (não logado).");
                }
                catch (Exception ex)
                {
                    await LogAsync("ERRO ao obter token: " + ex);
                    return null;
                }

                // Site
                var siteSpec = await ResolveSiteSpecifierAsync(_cfg.SiteId).ConfigureAwait(false);
                await LogAsync($"AppConfig.SiteId='{_cfg.SiteId}' => siteSpec='{siteSpec ?? "(null)"}'");
                if (siteSpec is null) return null;

                // Lista
                var alvoLista = string.IsNullOrWhiteSpace(_cfg.FuncionarioListId) ? "colaboradores_automate" : _cfg.FuncionarioListId!;
                var listId = await ResolveListIdAsync(siteSpec, alvoLista).ConfigureAwait(false);
                await LogAsync($"Lista alvo='{alvoLista}' => listId='{listId ?? "(null)"}'");
                if (string.IsNullOrEmpty(listId)) return null;

                // Colunas (dump + mapa)
                await DumpColumnsAsync(siteSpec, listId).ConfigureAwait(false);
                var colMap = await GetColumnsMapAsync(siteSpec, listId).ConfigureAwait(false);
                string? nomeCol = ResolveInternal(colMap, "Nome", "Name", "Colaborador", "Funcionario", "Funcionário");
                string? matricCol = ResolveInternal(colMap, "Matricula", "Matrícula", "Registro", "ID Funcional", "ID", "Matric");
                await LogAsync($"Mapeamento inferido: Nome='{nomeCol ?? "-"}' | Matricula='{matricCol ?? "-"}'");

                var selects = new List<string> { "Title" };
                if (nomeCol != null) selects.Add(nomeCol);
                if (matricCol != null) selects.Add(matricCol);
                var selectCsv = string.Join(",", selects.Distinct());
                await LogAsync($"SELECT -> $expand=fields($select={selectCsv})");

                // Probe 1 item
                await ProbeOneAsync(siteSpec, listId, selectCsv, ct).ConfigureAwait(false);

                // Paginação
                var funcionarios = new List<Funcionario>();
                string? nextUrl = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/items?$top=500&$expand=fields($select={selectCsv})";
                int page = 0, total = 0, add = 0, drop = 0;

                while (!string.IsNullOrEmpty(nextUrl))
                {
                    page++;
                    await LogAsync($"GET PAGE #{page}: {nextUrl}");
                    using var resp = await _http.GetAsync(nextUrl!, ct).ConfigureAwait(false);
                    await LogResponseAsync(resp);
                    var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                    {
                        await LogAsync($"ERRO PAGE #{page}: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body={Trunc(text, 1200)}");
                        return null;
                    }

                    var root = JsonNode.Parse(text)!.AsObject();
                    var items = root["value"]?.AsArray() ?? new JsonArray();
                    await LogAsync($"Itens na página: {items.Count}");

                    foreach (var it in items.OfType<JsonObject>())
                    {
                        total++;
                        var f = it["fields"] as JsonObject;
                        if (f is null) { drop++; continue; }

                        var nome =
                            (nomeCol != null ? f[nomeCol]?.ToString() : null) ??
                            f["Nome"]?.ToString() ??
                            f["Title"]?.ToString();

                        var matricula =
                            (matricCol != null ? f[matricCol]?.ToString() : null) ??
                            f["Matricula"]?.ToString() ??
                            f["Matr_x00ed_cula"]?.ToString() ??
                            f["Matric"]?.ToString() ??
                            f["Registro"]?.ToString();

                        if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(matricula))
                        {
                            drop++;
                            continue;
                        }

                        matricula = NormalizeMatricula(matricula);
                        funcionarios.Add(new Funcionario(matricula, nome.Trim(), "", "", "", "", ""));
                        add++;
                    }

                    nextUrl = root["@odata.nextLink"]?.ToString();
                    if (string.IsNullOrEmpty(nextUrl))
                        await LogAsync("Fim da paginação (@odata.nextLink ausente).");
                }

                await LogAsync($"Resumo: total lidos={total} | adicionados={add} | descartados={drop}");

                // Salva JSON
                var dst = Path.Combine(targetFolder, "funcionarios.json");
                var tmp = dst + ".tmp";
                try
                {
                    await using (var fs = File.Create(tmp))
                    {
                        await JsonSerializer.SerializeAsync(fs, funcionarios,
                            new JsonSerializerOptions { WriteIndented = true }, ct).ConfigureAwait(false);
                        await fs.FlushAsync(ct).ConfigureAwait(false);
                    }

                    File.Move(tmp, dst, true);
                    await LogAsync($"Arquivo gerado: {dst} (registros={funcionarios.Count})");
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
                await LogAsync("=== DownloadJsonAsync: fim (SUCESSO) ===");
                return dst;
            }
            catch (Exception ex)
            {
                await LogAsync("DownloadJsonAsync EXCEPTION: " + ex);
                await LogAsync("=== DownloadJsonAsync: fim (ERRO) ===");
                return null;
            }
        }

        /// <summary>Lê o JSON e devolve dicionário (matrícula → Funcionario). Loga tudo.</summary>
        public async Task<Dictionary<string, Funcionario>> LoadFuncionariosAsync(string jsonPath, CancellationToken ct = default)
        {
            var dict = new Dictionary<string, Funcionario>();
            await LogAsync("=== LoadFuncionariosAsync: início ===");
            await LogAsync($"jsonPath='{jsonPath}'");

            try
            {
                var exists = File.Exists(jsonPath);
                await LogAsync($"arquivo existe? {(exists ? "SIM" : "NÃO")}");
                if (!exists)
                {
                    await LogAsync("Arquivo não encontrado; retornando apenas admin.");
                    dict["258790"] = new Funcionario("258790", "Administrador", "", "", "", "", "");
                    await LogAsync("=== LoadFuncionariosAsync: fim ===");
                    return dict;
                }

                var len = new FileInfo(jsonPath).Length;
                await LogAsync($"tamanho do arquivo: {len} bytes");

                await using var fs = File.OpenRead(jsonPath);
                var list = await JsonSerializer.DeserializeAsync<List<Funcionario>>(fs, cancellationToken: ct).ConfigureAwait(false);
                if (list is null)
                {
                    await LogAsync("JSON desserializado como null.");
                }
                else
                {
                    foreach (var f in list)
                        dict[f.Matricula] = f;

                    await LogAsync($"carregados {dict.Count} funcionários do JSON.");
                    // loga os 5 primeiros
                    foreach (var kv in dict.Take(5))
                        await LogAsync($"  * {kv.Key} -> {kv.Value.Nome}");
                }
            }
            catch (Exception ex)
            {
                await LogAsync("LoadFuncionariosAsync EXCEPTION: " + ex);
            }

            // Admin fixo
            dict["258790"] = new Funcionario("258790", "Administrador", "", "", "", "", "");
            await LogAsync("Admin '258790' inserido.");
            await LogAsync("=== LoadFuncionariosAsync: fim ===");
            return dict;
        }

        // ====================== Helpers / Log ======================

        private async Task LogAsync(string msg)
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}";
            try { await File.AppendAllTextAsync(_logPath, line); } catch { }
        }

        private async Task LogResponseAsync(HttpResponseMessage resp)
        {
            try
            {
                var rid = resp.Headers.TryGetValues("request-id", out var a) ? a.FirstOrDefault() : null;
                var crid = resp.Headers.TryGetValues("client-request-id", out var b) ? b.FirstOrDefault() : null;
                await LogAsync($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} | request-id={(rid ?? "-")} | client-request-id={(crid ?? "-")}");
            }
            catch { }
        }

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...(trunc)";
        private static string NormalizeMatricula(string m)
        {
            var t = m.Trim();
            t = t.TrimStart('0'); // remove zeros à esquerda (se quiser só números, filtre aqui)
            return t;
        }
        private static bool LooksLikeGuid(string s) => Guid.TryParse(s, out _);

        private async Task<string?> ResolveSiteSpecifierAsync(string? siteIdOrUrl)
        {
            if (string.IsNullOrWhiteSpace(siteIdOrUrl))
            {
                await LogAsync("ResolveSite: SiteId vazio.");
                return null;
            }
            var raw = siteIdOrUrl.Trim();

            if (raw.StartsWith("sites/", StringComparison.OrdinalIgnoreCase))
                return raw;
            if (raw.Contains(",")) // "{siteId},{webId}"
                return $"sites/{raw}";
            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var uri = new Uri(raw);
                    var host = uri.Host;
                    var segs = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var idx = Array.FindIndex(segs, s => s.Equals("sites", StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0 && idx + 1 < segs.Length)
                    {
                        var sitePath = string.Join('/', segs.Skip(idx + 1));
                        return $"sites/{host}:/sites/{sitePath}:";
                    }
                    var searchUrl = $"https://graph.microsoft.com/v1.0/sites?search={Uri.EscapeDataString(raw)}";
                    await LogAsync($"ResolveSite fallback search: {searchUrl}");
                    var json = await _http.GetStringAsync(searchUrl).ConfigureAwait(false);
                    var id = JsonNode.Parse(json)?["value"]?.AsArray()?.OfType<JsonObject>()?.FirstOrDefault()?["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) return $"sites/{id}";
                }
                catch (Exception ex)
                {
                    await LogAsync("ResolveSite (URL) EXCEPTION: " + ex);
                }
                return null;
            }
            if (LooksLikeGuid(raw)) return $"sites/{raw}";
            if (raw.Contains(":/sites/")) return $"sites/{raw.TrimStart('/')}";
            return $"sites/{raw}";
        }

        private async Task<string?> ResolveListIdAsync(string siteSpec, string idOrName)
        {
            if (!string.IsNullOrWhiteSpace(idOrName) && LooksLikeGuid(idOrName))
                return idOrName;

            var url = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists?$select=id,displayName&$top=999";
            await LogAsync("ListIndex URL: " + url);
            try
            {
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var arr = JsonNode.Parse(json)?["value"]?.AsArray() ?? new JsonArray();

                foreach (var o in arr.OfType<JsonObject>())
                {
                    var id = o["id"]?.ToString();
                    var dn = o["displayName"]?.ToString();
                    await LogAsync($"  - List: '{dn}' (id={id})");
                }

                var match = arr.OfType<JsonObject>()
                    .FirstOrDefault(o => string.Equals(o["displayName"]?.ToString(), idOrName, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(o["displayName"]?.ToString(), "colaboradores_automate", StringComparison.OrdinalIgnoreCase));

                return match?["id"]?.ToString();
            }
            catch (Exception ex)
            {
                await LogAsync("ResolveListIdAsync EXCEPTION: " + ex);
                return null;
            }
        }

        private async Task DumpColumnsAsync(string siteSpec, string listId)
        {
            var url = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/columns?$select=name,displayName,required,text,number,choice,personOrGroup,lookup";
            await LogAsync("Columns URL: " + url);
            try
            {
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var arr = JsonNode.Parse(json)?["value"]?.AsArray() ?? new JsonArray();
                int i = 0;
                foreach (var o in arr.OfType<JsonObject>())
                {
                    i++;
                    var name = o["name"]?.ToString();
                    var disp = o["displayName"]?.ToString();
                    var req = o["required"]?.GetValue<bool?>() ?? false;
                    string kind =
                        o["text"] != null ? "text" :
                        o["number"] != null ? "number" :
                        o["choice"] != null ? "choice" :
                        o["personOrGroup"] != null ? "person" :
                        o["lookup"] != null ? "lookup" : "other";
                    await LogAsync($"  [{i}] name='{name}' | displayName='{disp}' | kind={kind} | required={req}");
                }
                if (i == 0) await LogAsync("  (sem colunas!)");
            }
            catch (Exception ex)
            {
                await LogAsync("DumpColumnsAsync EXCEPTION: " + ex);
            }
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
                    var internalName = o["name"]?.ToString();
                    var displayName = o["displayName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(internalName)) continue;

                    var k1 = NormalizeKey(internalName);
                    if (!map.ContainsKey(k1)) map[k1] = internalName;

                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        var k2 = NormalizeKey(displayName);
                        if (!map.ContainsKey(k2)) map[k2] = internalName;
                    }
                }
            }
            catch (Exception ex)
            {
                await LogAsync("GetColumnsMapAsync EXCEPTION: " + ex);
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

        private async Task ProbeOneAsync(string siteSpec, string listId, string selectCsv, CancellationToken ct)
        {
            var url = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/items?$top=1&$expand=fields($select={selectCsv})";
            await LogAsync("ProbeOne URL: " + url);
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                await LogResponseAsync(resp);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    await LogAsync($"ProbeOne FAIL: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body={Trunc(body, 1000)}");
                    return;
                }

                var root = JsonNode.Parse(body)!.AsObject();
                var arr = root["value"]?.AsArray() ?? new JsonArray();
                if (arr.Count == 0) { await LogAsync("ProbeOne: lista sem itens."); return; }

                var f = arr[0]?["fields"] as JsonObject;
                if (f is null) { await LogAsync("ProbeOne: item[0] sem fields."); return; }

                var keys = f.Select(kvp => kvp.Key).Take(10).ToArray();
                await LogAsync("ProbeOne fields keys: " + string.Join(", ", keys));
            }
            catch (Exception ex)
            {
                await LogAsync("ProbeOne EXCEPTION: " + ex);
            }
        }
    }
}
