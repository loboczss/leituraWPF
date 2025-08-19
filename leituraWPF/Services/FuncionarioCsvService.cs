using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace leituraWPF.Services
{
    /// <summary>
    /// Responsável por baixar o arquivo funcionarios.csv do SharePoint.
    /// </summary>
    public sealed class FuncionarioCsvService
    {
        private readonly AppConfig _cfg;
        private readonly TokenService _tokenService;

        public FuncionarioCsvService(AppConfig cfg, TokenService tokenService)
        {
            _cfg = cfg;
            _tokenService = tokenService;
        }

        /// <summary>
        /// Baixa o arquivo funcionarios.csv para o caminho informado.
        /// Caso o arquivo não exista no SharePoint, nada é baixado.
        /// </summary>
        public async Task DownloadAsync(string destinationPath, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");

            var token = await _tokenService.GetTokenAsync().ConfigureAwait(false);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Resolve driveId a partir da ListId configurada
            var driveUrl = $"https://graph.microsoft.com/v1.0/sites/{_cfg.SiteId}/lists/{_cfg.ListId}/drive";
            using var driveResp = await http.GetAsync(driveUrl, ct).ConfigureAwait(false);
            driveResp.EnsureSuccessStatusCode();
            var driveJson = await driveResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var driveNode = JsonNode.Parse(driveJson)?.AsObject();
            var driveId = driveNode?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(driveId))
                throw new InvalidOperationException("Não foi possível resolver o driveId para a lista configurada.");

            // Busca pelo arquivo funcionarios.csv
            var searchUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/search(q='funcionarios.csv')?$top=1";
            using var searchResp = await http.GetAsync(searchUrl, ct).ConfigureAwait(false);
            searchResp.EnsureSuccessStatusCode();
            var searchJson = await searchResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = JsonNode.Parse(searchJson)?.AsObject();
            var array = root?["value"] as JsonArray;
            var item = array?.Select(v => v as JsonObject)
                             .FirstOrDefault(o => string.Equals(o?["name"]?.ToString(), "funcionarios.csv", StringComparison.OrdinalIgnoreCase));
            if (item == null)
                return; // Arquivo não encontrado

            var itemId = item["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(itemId))
                return;

            var downloadUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{itemId}/content";
            using var fileResp = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            fileResp.EnsureSuccessStatusCode();
            await using var fs = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await fileResp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
    }
}

