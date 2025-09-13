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

        // IDs do seu ambiente (do seu log). Pode deixar no appsettings se preferir.
        private string SiteSpec => BuildSiteSpec(_cfg.SiteId) ?? "sites/03b55b3a-5e43-430f-90db-687ed2c5b32f";
        private string ListId => !string.IsNullOrWhiteSpace(_cfg.FuncionarioListId)
                                  ? _cfg.FuncionarioListId!
                                  : "69ecf1ce-386c-4e49-8607-7391aa3f734b";

        public FuncionarioService(AppConfig cfg, TokenService tokenService)
        {
            _cfg = cfg;
            _tokenService = tokenService;

            var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_cfg.HttpTimeoutSeconds > 0 ? _cfg.HttpTimeoutSeconds : 120),
                DefaultRequestVersion = new Version(2, 0)
            };
            _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("leituraWPF/1.0");
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR");
        }

        /// <summary>Garante que o arquivo funcionarios.json exista. Se não existir, baixa. Retorna o caminho do JSON ou null.</summary>
        public async Task<string?> EnsureJsonAsync(CancellationToken ct = default)
        {
            var dst = Path.Combine(AppContext.BaseDirectory, "funcionarios.json");
            if (!File.Exists(dst) || new FileInfo(dst).Length < 5)
            {
                var ok = await DownloadJsonAsync(Path.GetDirectoryName(dst)!, ct);
                if (ok == null) return null;
            }
            return dst;
        }

        /// <summary>Baixa Nome/Matrícula e grava em funcionarios.json no targetFolder. Retorna caminho ou null.</summary>
        public async Task<string?> DownloadJsonAsync(string targetFolder, CancellationToken ct = default)
        {
            Directory.CreateDirectory(targetFolder);

            var token = await _tokenService.GetTokenAsync().ConfigureAwait(false);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Campos internos mapeados no seu ambiente
            var selectCsv = "Title,nome,matricula";
            var url = $"https://graph.microsoft.com/v1.0/{SiteSpec}/lists/{ListId}/items?$top=500&$expand=fields($select={selectCsv})";

            var lista = new List<Funcionario>();

            while (!string.IsNullOrEmpty(url))
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!.AsObject();
                var items = root["value"]?.AsArray() ?? new JsonArray();

                foreach (var it in items.OfType<JsonObject>())
                {
                    var f = it["fields"] as JsonObject; if (f is null) continue;
                    var idx = f.ToDictionary(k => NormalizeKey(DecodeSP(k.Key)), k => k.Key);

                    string nome = ReadField(f, idx, "nome", "title");
                    string matricula = ReadField(f, idx, "matricula", "Matricula", "Matr_x00ed_cula");

                    if (string.IsNullOrWhiteSpace(nome))
                    { var k = idx.Keys.FirstOrDefault(x => x.Contains("nome")); if (k != null) nome = ToStr(f[idx[k]]); }
                    if (string.IsNullOrWhiteSpace(matricula))
                    { var k = idx.Keys.FirstOrDefault(x => x.Contains("matric")); if (k != null) matricula = ToStr(f[idx[k]]); }

                    if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(matricula)) continue;

                    lista.Add(new Funcionario(NormalizeMatricula(matricula), nome.Trim(), "", "", "", "", ""));
                }
                url = root["@odata.nextLink"]?.ToString();
            }

            var dst = Path.Combine(targetFolder, "funcionarios.json");
            var tmp = dst + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, lista, new JsonSerializerOptions { WriteIndented = true }, ct).ConfigureAwait(false);
            File.Move(tmp, dst, true);
            return dst;
        }

        /// <summary>Lê o JSON e devolve dicionário (matrícula → Funcionario). Adiciona o admin fixo.</summary>
        public async Task<Dictionary<string, Funcionario>> LoadFuncionariosAsync(string jsonPath, CancellationToken ct = default)
        {
            var dict = new Dictionary<string, Funcionario>();
            if (File.Exists(jsonPath))
            {
                await using var fs = File.OpenRead(jsonPath);
                var list = await JsonSerializer.DeserializeAsync<List<Funcionario>>(fs, cancellationToken: ct).ConfigureAwait(false);
                if (list != null) foreach (var f in list) dict[f.Matricula] = f;
            }
            dict["258790"] = new Funcionario("258790", "Administrador", "", "", "", "", "");
            return dict;
        }

        // ===== Helpers bem mínimos =====
        private static string? BuildSiteSpec(string? siteIdOrUrl)
        {
            if (string.IsNullOrWhiteSpace(siteIdOrUrl)) return null;
            var raw = siteIdOrUrl.Trim();
            if (raw.StartsWith("sites/", StringComparison.OrdinalIgnoreCase)) return raw;
            if (raw.Contains(",")) return $"sites/{raw}";
            if (Guid.TryParse(raw, out _)) return $"sites/{raw}";
            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(raw);
                var segs = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                var i = Array.FindIndex(segs, s => s.Equals("sites", StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i + 1 < segs.Length) return $"sites/{uri.Host}:/sites/{string.Join('/', segs.Skip(i + 1))}:";
            }
            return $"sites/{raw}";
        }

        private static string ReadField(JsonObject fields, Dictionary<string, string> idx, params string[] candidates)
        {
            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                var norm = NormalizeKey(DecodeSP(c));
                if (idx.TryGetValue(norm, out var real)) return ToStr(fields[real]);
                if (fields.TryGetPropertyValue(c, out var v)) return ToStr(v);
            }
            return string.Empty;
        }

        private static string ToStr(object? v)
        {
            if (v is null) return string.Empty;
            if (v is string s) return s;
            if (v is JsonValue jv && jv.TryGetValue(out double d)) return d.ToString("0.###############", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string NormalizeMatricula(string m) => ToStr(m).Trim().TrimStart('0');

        private static string NormalizeKey(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = DecodeSP(s);
            var t = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(t.Length);
            foreach (var ch in t)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString().Replace(" ", "").Replace("_", "").Replace("-", "");
        }

        private static string DecodeSP(string s) =>
            string.IsNullOrEmpty(s) ? s :
            Regex.Replace(s, @"_x([0-9A-Fa-f]{4})_", m =>
            {
                try { return char.ConvertFromUtf32(Convert.ToInt32(m.Groups[1].Value, 16)); }
                catch { return m.Value; }
            });
    }
}
