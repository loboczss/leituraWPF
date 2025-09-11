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
    /// <summary>
    /// Lê Nome e Matrícula da lista "colaboradores_automate" (ou a definida em AppConfig.FuncionarioListId)
    /// via Microsoft Graph, com LOG detalhado em log.txt (na pasta do app).
    ///
    /// - Aceita AppConfig.SiteId como GUID, site-id do Graph ("{siteId},{webId}") ou URL do site
    /// - Aceita AppConfig.FuncionarioListId como GUID OU DisplayName ("colaboradores_automate")
    /// - Descobre e loga todos os nomes internos das colunas
    /// - Usa $expand=fields($select=...) corretamente
    /// - Faz paginação com @odata.nextLink
    /// - Não loga tokens; loga status, request-id e partes do payload de resposta
    /// </summary>
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
        }

        // ====================== API PRINCIPAL ======================

        /// <summary>
        /// Baixa Nome e Matrícula para um arquivo JSON local. Retorna o caminho do arquivo ou null.
        /// </summary>
        public async Task<string?> DownloadJsonAsync(string targetFolder, CancellationToken ct = default)
        {
            SafeDeleteOldLog(); // opcional: comente se quiser acumular históricos
            await LogAsync("=== DownloadJsonAsync: início ===");

            try
            {
                Directory.CreateDirectory(targetFolder);

                // 1) Auth
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

                // 2) Resolve site
                var siteSpec = await ResolveSiteSpecifierAsync(_cfg.SiteId).ConfigureAwait(false);
                await LogAsync($"SiteId fornecido: '{_cfg.SiteId}' -> siteSpec: '{siteSpec ?? "(null)"}'");
                if (siteSpec is null) return null;

                // 3) Resolve lista
                var alvoLista = string.IsNullOrWhiteSpace(_cfg.FuncionarioListId) ? "colaboradores_automate" : _cfg.FuncionarioListId!;
                var listId = await ResolveListIdAsync(siteSpec, alvoLista).ConfigureAwait(false);
                await LogAsync($"Lista alvo: '{alvoLista}' -> listId='{listId ?? "(null)"}'");
                if (string.IsNullOrEmpty(listId)) return null;

                // 4) Dump das colunas (debug pesado)
                var colsDump = await DumpColumnsAsync(siteSpec, listId).ConfigureAwait(false);

                // 5) Mapeia nomes internos prováveis
                var colMap = await GetColumnsMapAsync(siteSpec, listId).ConfigureAwait(false);
                string? nomeCol = ResolveInternal(colMap, "Nome", "Name", "Colaborador", "Funcionario", "Funcionário");
                string? matricCol = ResolveInternal(colMap, "Matricula", "Matrícula", "Registro", "ID Funcional", "ID", "Matric");
                await LogAsync($"Mapeamento inferido: Nome='{nomeCol ?? "-"}' | Matricula='{matricCol ?? "-"}'");

                // 6) Monta select
                var selects = new List<string> { "Title" };
                if (nomeCol != null) selects.Add(nomeCol);
                if (matricCol != null) selects.Add(matricCol);
                var selectCsv = string.Join(",", selects.Distinct());
                await LogAsync($"$expand=fields($select={selectCsv})");

                // 7) Teste rápido (top=1) e loga primeiros campos
                await ProbeOneAsync(siteSpec, listId, selectCsv, ct).ConfigureAwait(false);

                // 8) Paginação e coleta
                var funcionarios = new List<Funcionario>();
                string? nextUrl = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/items?$top=500&$expand=fields($select={selectCsv})";

                int page = 0, totalLidos = 0, adicionados = 0, descartados = 0;
                while (!string.IsNullOrEmpty(nextUrl))
                {
                    page++;
                    await LogAsync($"GET PAGE #{page}: {nextUrl}");
                    using var resp = await _http.GetAsync(nextUrl!, ct).ConfigureAwait(false);
                    await LogResponseAsync(resp);

                    var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        await LogAsync($"ERRO: status {(int)resp.StatusCode} {resp.ReasonPhrase}. Body={Trunc(text, 1200)}");
                        return null;
                    }

                    var root = JsonNode.Parse(text)!.AsObject();
                    var items = root["value"]?.AsArray() ?? new JsonArray();
                    await LogAsync($"Itens recebidos nesta página: {items.Count}");

                    foreach (var it in items.OfType<JsonObject>())
                    {
                        totalLidos++;
                        var f = it["fields"] as JsonObject;
                        if (f is null) { descartados++; continue; }

                        // tenta pelos mapeados; cai para nomes óbvios; tenta anti-acentuação SharePoint (x00ed)
                        var nome =
                            (nomeCol != null ? f[nomeCol]?.ToString() : null) ??
                            f["Nome"]?.ToString() ??
                            f["Title"]?.ToString();

                        var matricula =
                            (matricCol != null ? f[matricCol]?.ToString() : null) ??
                            f["Matricula"]?.ToString() ??
                            f["Matr_x00ed_cula"]?.ToString() ??   // "Matrícula"
                            f["Matric"]?.ToString() ??
                            f["Registro"]?.ToString();

                        if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(matricula))
                        {
                            descartados++;
                            continue;
                        }

                        matricula = NormalizeMatricula(matricula);
                        funcionarios.Add(new Funcionario(matricula, nome.Trim(), "", "", "", "", ""));
                        adicionados++;
                    }

                    nextUrl = root["@odata.nextLink"]?.ToString();
                    if (string.IsNullOrEmpty(nextUrl))
                        await LogAsync("Sem @odata.nextLink (fim).");
                }

                await LogAsync($"TOTAL lidos: {totalLidos} | adicionados: {adicionados} | descartados: {descartados}");

                // 9) Salva JSON
                var dst = Path.Combine(targetFolder, "funcionarios.json");
                await using (var fs = File.Create(dst))
                {
                    await JsonSerializer.SerializeAsync(fs, funcionarios,
                        new JsonSerializerOptions { WriteIndented = true }, ct).ConfigureAwait(false);
                }
                await LogAsync($"Arquivo salvo: {dst}");
                await LogAsync("=== DownloadJsonAsync: fim (SUCESSO) ===");
                return dst;
            }
            catch (Exception ex)
            {
                await LogAsync("EXCEPTION raiz: " + ex);
                await LogAsync("=== DownloadJsonAsync: fim (ERRO) ===");
                return null;
            }
        }

        /// <summary>
        /// Lê o JSON gerado e devolve dicionário por matrícula + admin fixo.
        /// </summary>
        public async Task<Dictionary<string, Funcionario>> LoadFuncionariosAsync(string jsonPath, CancellationToken ct = default)
        {
            var dict = new Dictionary<string, Funcionario>();

            try
            {
                if (File.Exists(jsonPath))
                {
                    await using var fs = File.OpenRead(jsonPath);
                    var list = await JsonSerializer.DeserializeAsync<List<Funcionario>>(fs, cancellationToken: ct).ConfigureAwait(false);
                    if (list != null)
                    {
                        foreach (var f in list) dict[f.Matricula] = f;
                    }
                    await LogAsync($"LoadFuncionarios: carregados {dict.Count} do JSON.");
                }
                else
                {
                    await LogAsync($"LoadFuncionarios: arquivo não existe: {jsonPath}");
                }
            }
            catch (Exception ex)
            {
                await LogAsync("LoadFuncionarios EXCEPTION: " + ex);
            }

            // Admin fixo
            dict["258790"] = new Funcionario("258790", "Administrador", "", "", "", "", "");
            return dict;
        }

        // ====================== HELPERS / DEBUG ======================

        private void SafeDeleteOldLog()
        {
            try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { }
        }

        private async Task LogAsync(string msg)
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}";
            try { await File.AppendAllTextAsync(_logPath, line); } catch { /* não quebra fluxo */ }
        }

        private async Task LogResponseAsync(HttpResponseMessage resp)
        {
            try
            {
                var rid = resp.Headers.TryGetValues("request-id", out var a) ? a.FirstOrDefault() : null;
                var crid = resp.Headers.TryGetValues("client-request-id", out var b) ? b.FirstOrDefault() : null;
                await LogAsync($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} | request-id={rid ?? "-"} | client-request-id={crid ?? "-"}");
            }
            catch { }
        }

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...(trunc)";

        private static string NormalizeMatricula(string m)
        {
            var t = m.Trim();
            // mantém números + letras (se existir). Se quiser só números, descomente o filtro:
            // t = new string(t.Where(char.IsDigit).ToArray());
            t = t.TrimStart('0'); // frequentemente vem com zeros à esquerda
            return t;
        }

        private static bool LooksLikeGuid(string s) => Guid.TryParse(s, out _);

        /// <summary>Converte AppConfig.SiteId (GUID ou URL) em spec aceito pelo Graph.</summary>
        private async Task<string?> ResolveSiteSpecifierAsync(string? siteIdOrUrl)
        {
            if (string.IsNullOrWhiteSpace(siteIdOrUrl))
            {
                await LogAsync("ResolveSite: SiteId vazio.");
                return null;
            }
            var raw = siteIdOrUrl.Trim();

            // Se já vier "sites/..." ou contiver vírgula (site-id do Graph), só ajusta
            if (raw.StartsWith("sites/", StringComparison.OrdinalIgnoreCase))
                return raw;
            if (raw.Contains(",")) // "{siteId},{webId}"
                return $"sites/{raw}";

            // URL completa -> "sites/{host}:/sites/{path}:"
            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var uri = new Uri(raw);
                    var host = uri.Host;
                    var segs = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var idxSites = Array.FindIndex(segs, s => s.Equals("sites", StringComparison.OrdinalIgnoreCase));
                    if (idxSites >= 0 && idxSites + 1 < segs.Length)
                    {
                        var sitePath = string.Join('/', segs.Skip(idxSites + 1));
                        return $"sites/{host}:/sites/{sitePath}:";
                    }
                    // fallback: tenta search
                    var searchUrl = $"https://graph.microsoft.com/v1.0/sites?search={Uri.EscapeDataString(raw)}";
                    await LogAsync($"ResolveSite: fallback search: {searchUrl}");
                    var json = await _http.GetStringAsync(searchUrl).ConfigureAwait(false);
                    var id = JsonNode.Parse(json)?["value"]?.AsArray()?.OfType<JsonObject>()?.FirstOrDefault()?["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                        return $"sites/{id}";
                }
                catch (Exception ex)
                {
                    await LogAsync("ResolveSite (URL) EXCEPTION: " + ex);
                }
                return null;
            }

            // GUID simples
            if (LooksLikeGuid(raw))
                return $"sites/{raw}";

            // Caso seja algo como "oneengenharia.sharepoint.com:/sites/OneEngenharia:"
            if (raw.Contains(":/sites/"))
                return $"sites/{raw.TrimStart('/')}";

            // último fallback
            return $"sites/{raw}";
        }

        /// <summary>
        /// Se idOrName for GUID retorna ele; senão procura por DisplayName e loga alternativas.
        /// </summary>
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
                    await LogAsync($"  - List found: '{dn}' (id={id})");
                }

                var match = arr.OfType<JsonObject>()
                    .FirstOrDefault(o => string.Equals(o["displayName"]?.ToString(), idOrName, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                    return match["id"]?.ToString();

                await LogAsync($"NÃO encontrei lista com displayName='{idOrName}'.");
                return null;
            }
            catch (Exception ex)
            {
                await LogAsync("ResolveListIdAsync EXCEPTION: " + ex);
                return null;
            }
        }

        /// <summary>Dump detalhado das colunas (nome interno, displayName, “tipo” básico).</summary>
        private async Task<string> DumpColumnsAsync(string siteSpec, string listId)
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
                if (i == 0) await LogAsync("  (sem colunas retornadas!)");
                return "ok";
            }
            catch (Exception ex)
            {
                await LogAsync("DumpColumnsAsync EXCEPTION: " + ex);
                return "err";
            }
        }

        /// <summary>Mapa normalizado (chave: internal ou display, sem acento/espaço) → internal.</summary>
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

        /// <summary>Busca 1 item só para validar endpoint/colunas e loga primeiros campos.</summary>
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
                if (arr.Count == 0)
                {
                    await LogAsync("ProbeOne: lista vazia (sem itens).");
                    return;
                }

                var f = arr[0]?["fields"] as JsonObject;
                if (f is null)
                {
                    await LogAsync("ProbeOne: item[0] sem 'fields'. Body=" + Trunc(body, 1000));
                    return;
                }

                // Dump de até 10 chaves encontradas
                var keys = f.Select(kvp => kvp.Key).Take(10).ToArray();
                await LogAsync("ProbeOne: primeiras chaves em fields = " + string.Join(", ", keys));
            }
            catch (Exception ex)
            {
                await LogAsync("ProbeOne EXCEPTION: " + ex);
            }
        }
    }
}
