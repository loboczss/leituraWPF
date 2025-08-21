using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using leituraWPF.Utils;

namespace leituraWPF.Services
{
    /// <summary>
    /// Serviço responsável por acrescentar linhas ao arquivo lista.txt no SharePoint.
    /// </summary>
    public sealed class ListaService
    {
        private readonly AppConfig _cfg;
        private readonly TokenService _tokenService;
        private readonly HttpClient _http;

        public ListaService(AppConfig cfg, TokenService tokenService)
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
            _http.DefaultRequestVersion = HttpVersion.Version20;
            _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        }

        /// <summary>
        /// Acrescenta <paramref name="line"/> ao arquivo lista.txt no SharePoint.
        /// </summary>
        public async Task AppendAsync(string line, CancellationToken ct = default)
        {
            var token = await _tokenService.GetTokenAsync().ConfigureAwait(false);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var driveId = await GetDriveIdFromListAsync(ct).ConfigureAwait(false);
            var searchUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/search(q='lista.txt')?$select=name,id";
            using var resp = await _http.GetAsync(searchUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return; // falha silenciosa

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = JsonNode.Parse(json)?.AsObject();
            var arr = root?["value"] as JsonArray;
            var item = arr?.OfType<JsonObject>().FirstOrDefault(o =>
                string.Equals(o["name"]?.ToString(), "lista.txt", StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            var id = item["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) return;

            var downloadUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{id}/content";
            string existing = string.Empty;
            using (var respGet = await _http.GetAsync(downloadUrl, ct).ConfigureAwait(false))
            {
                if (respGet.IsSuccessStatusCode)
                {
                    existing = await respGet.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
            }

            if (existing.Length > 0 && !existing.EndsWith("\n")) existing += "\n";
            var newContent = existing + line + "\n";
            var content = new StringContent(newContent, Encoding.UTF8, "text/plain");
            using var respPut = await _http.PutAsync(downloadUrl, content, ct).ConfigureAwait(false);
            respPut.EnsureSuccessStatusCode();
        }

        private async Task<string> GetDriveIdFromListAsync(CancellationToken ct)
        {
            var url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.SiteId}/lists/{_cfg.ListId}/drive";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var node = JsonNode.Parse(json)?.AsObject();
            var id = node?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("Não foi possível resolver o driveId para a ListId informada.");
            return id!;
        }
    }
}
