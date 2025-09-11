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
using System.Text.RegularExpressions;
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

            // Ajuda em ambientes mais chatos do Graph
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("leituraWPF/1.0");
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR");

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

                // 1) Tenta resolver pelos metadados de colunas
                var colMap = await GetColumnsMapAsync(siteSpec, listId).ConfigureAwait(false);
                string? nomeCol = ResolveInternal(colMap, "Nome", "Name", "Colaborador", "Funcionario", "Funcionário", "Title");
                string? matricCol = ResolveInternal(colMap, "Matricula", "Matrícula", "Registro", "ID Funcional", "ID", "Matric");

                // 2) Se ainda não achou, heurística pelo primeiro item
                if (nomeCol == null || matricCol == null)
                {
                    var (nomeHeur, matricHeur, keysPreview) = await HeuristicProbeFieldsAsync(siteSpec, listId, ct).ConfigureAwait(false);
                    await LogAsync($"Heuristic keys preview: {keysPreview}");
                    nomeCol ??= nomeHeur;
                    matricCol ??= matricHeur;
                }

                // 3) Fallbacks finais
                nomeCol ??= "Title"; // Nome geralmente é Title renomeado
                if (matricCol == null)
                {
                    // Tenta variantes frequentes
                    matricCol = "Matr_x00ed_cula"; // “Matrícula” internalizado
                    await LogAsync("matricCol não encontrado; usando fallback 'Matr_x00ed_cula'");
                }

                await LogAsync($"Mapeamento final: Nome='{nomeCol}' | Matricula='{matricCol}'");

                var selects = new List<string> { "Title", nomeCol, matricCol };
                var selectCsv = string.Join(",", selects.Distinct());
                await LogAsync($"SELECT -> $expand=fields($select={selectCsv})");

                // Probe 1 item (e loga fields)
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

                        // Cria um índice normalizado das chaves existentes para buscas tolerantes
                        var keyIndex = f.ToDictionary(kvp => NormalizeKey(DecodeSharePointInternalName(kvp.Key)), kvp => kvp.Key);

                        string nome = ReadBest(f, keyIndex, nomeCol, "Nome", "Title");
                        string matricula = ReadBest(f, keyIndex, matricCol, "Matricula", "Matr_x00ed_cula", "Matric", "Registro");

                        if (string.IsNullOrWhiteSpace(nome))
                        {
                            // Heurística extra: qualquer chave contendo "nome"
                            var kNome = keyIndex.Keys.FirstOrDefault(k => k.Contains("nome"));
                            if (kNome != null) nome = ObjectToString(f[keyIndex[kNome]]);
                        }

                        if (string.IsNullOrWhiteSpace(matricula))
                        {
                            // Heurística extra: qualquer chave contendo "matric"
                            var kMat = keyIndex.Keys.FirstOrDefault(k => k.Contains("matric"));
                            if (kMat != null) matricula = ObjectToString(f[keyIndex[kMat]]);
                        }

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

                // Salva JSON (somente Nome e Matrícula presentes no modelo Funcionario)
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
            // Ajuste aqui conforme tua regra de negócio
            var t = ObjectToString(m).Trim();
            t = t.TrimStart('0'); // remova se zeros à esquerda forem relevantes
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

            // Formatos aceitos:
            // - "sites/{siteId},{webId}"
            // - "sites/{id}"
            // - "sites/oneengenharia.sharepoint.com:/sites/OneEngenharia:"
            // - URL "https://oneengenharia.sharepoint.com/sites/OneEngenharia"
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

            var url = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists?$select=id,displayName,name&$top=999";
            await LogAsync("ListIndex URL: " + url);
            try
            {
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var arr = JsonNode.Parse(json)?["value"]?.AsArray() ?? new JsonArray();

                foreach (var o in arr.OfType<JsonObject>())
                {
                    var id = o["id"]?.ToString();
                    var dn = o["displayName"]?.ToString();
                    var apiName = o["name"]?.ToString();
                    await LogAsync($"  - List: display='{dn}' | apiName='{apiName}' | id={id}");
                }

                // Match por displayName OU apiName, acento-insensitive
                string target = idOrName;
                var targetN = NormalizeKey(target);
                var match = arr.OfType<JsonObject>()
                    .FirstOrDefault(o =>
                    {
                        var dn = NormalizeKey(o["displayName"]?.ToString());
                        var ap = NormalizeKey(o["name"]?.ToString());
                        return dn == targetN || ap == targetN ||
                               dn == NormalizeKey("colaboradores_automate") || ap == NormalizeKey("colaboradores_automate");
                    });

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

                    var internalDecoded = DecodeSharePointInternalName(internalName);

                    var k1 = NormalizeKey(internalDecoded);
                    if (!map.ContainsKey(k1)) map[k1] = internalName; // guardamos o original para consulta no fields

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

            // EXTRA: busca por "contém" (p.ex., qualquer coisa que contenha "matric")
            foreach (var c in candidates)
            {
                var frag = NormalizeKey(c);
                var hit = map.Keys.FirstOrDefault(k => k.Contains(frag));
                if (hit != null) return map[hit];
            }

            return null;
        }

        private static string NormalizeKey(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            // 1) Decodifica padrões _xNNNN_
            s = DecodeSharePointInternalName(s);

            // 2) Remove acentos
            var formD = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (var ch in formD)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(ch));

            // 3) Remove separadores comuns
            return sb.ToString().Replace(" ", "").Replace("_", "").Replace("-", "");
        }

        /// <summary>
        /// Decodifica sequências _xNNNN_ (hex UTF-16) dos nomes internos do SharePoint.
        /// Ex.: "Matr_x00ed_cula" -> "Matrícula"
        /// </summary>
        private static string DecodeSharePointInternalName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return Regex.Replace(s, @"_x([0-9A-Fa-f]{4})_", m =>
            {
                try
                {
                    var code = Convert.ToInt32(m.Groups[1].Value, 16);
                    return char.ConvertFromUtf32(code);
                }
                catch { return m.Value; }
            });
        }

        /// <summary>
        /// Faz um "probe" com top=1 e tenta inferir chaves de Nome/Matrícula com base nos nomes reais do objeto fields.
        /// </summary>
        private async Task<(string? nomeCol, string? matricCol, string keysPreview)> HeuristicProbeFieldsAsync(string siteSpec, string listId, CancellationToken ct)
        {
            var url = $"https://graph.microsoft.com/v1.0/{siteSpec}/lists/{listId}/items?$top=1&$expand=fields";
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return (null, null, $"HTTP {(int)resp.StatusCode}");

                var root = JsonNode.Parse(body)!.AsObject();
                var arr = root["value"]?.AsArray() ?? new JsonArray();
                if (arr.Count == 0) return (null, null, "(sem itens)");

                var f = arr[0]?["fields"] as JsonObject;
                if (f is null) return (null, null, "(sem fields)");

                var keys = f.Select(kvp => kvp.Key).ToArray();
                var preview = string.Join(", ", keys.Take(12));

                // índice normalizado
                var idx = keys.ToDictionary(k => NormalizeKey(DecodeSharePointInternalName(k)), k => k);

                string? nomeCol = null;
                string? matricCol = null;

                // Nome: preferir Title ou qualquer coisa que contenha "nome"
                if (idx.TryGetValue("title", out var titleK)) nomeCol = titleK;
                if (nomeCol == null)
                {
                    var k = idx.Keys.FirstOrDefault(x => x.Contains("nome"));
                    if (k != null) nomeCol = idx[k];
                }

                // Matrícula: qualquer coisa contendo "matric"
                {
                    var k = idx.Keys.FirstOrDefault(x => x.Contains("matric"));
                    if (k != null) matricCol = idx[k];
                }

                return (nomeCol, matricCol, preview);
            }
            catch (Exception ex)
            {
                await LogAsync("HeuristicProbeFieldsAsync EXCEPTION: " + ex);
                return (null, null, "(erro probe)");
            }
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

                var keys = f.Select(kvp => kvp.Key).Take(20).ToArray();
                await LogAsync("ProbeOne fields keys: " + string.Join(", ", keys));
            }
            catch (Exception ex)
            {
                await LogAsync("ProbeOne EXCEPTION: " + ex);
            }
        }

        private static string ReadBest(JsonObject fields, Dictionary<string, string> keyIndex, params string[] preferredOrder)
        {
            foreach (var pref in preferredOrder)
            {
                if (string.IsNullOrWhiteSpace(pref)) continue;

                var prefNorm = NormalizeKey(DecodeSharePointInternalName(pref));
                if (keyIndex.TryGetValue(prefNorm, out var realKey))
                {
                    var val = fields[realKey];
                    var s = ObjectToString(val);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }

                // tentativa direta sem normalizar (caso venha já no formato correto)
                if (fields.TryGetPropertyValue(pref, out var v2))
                {
                    var s2 = ObjectToString(v2);
                    if (!string.IsNullOrWhiteSpace(s2)) return s2;
                }
            }
            return string.Empty;
        }

        private static string ObjectToString(object? o)
        {
            if (o == null) return string.Empty;
            if (o is string s) return s;
            try
            {
                // Alguns campos vêm como número (double/int) -> converte sem notação científica
                if (o is JsonValue jv && jv.TryGetValue(out double d))
                    return d.ToString("0.###############", CultureInfo.InvariantCulture);
            }
            catch { /* ignore */ }
            return Convert.ToString(o, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }
}
