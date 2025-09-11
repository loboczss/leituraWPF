using leituraWPF;
using leituraWPF.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using leituraWPF.Utils;

namespace leituraWPF.Services
{
    /// <summary>
    /// Serviço responsável por baixar e ler a lista de funcionários do SharePoint.
    /// </summary>
    public sealed class FuncionarioService
    {
        private readonly AppConfig _cfg;
        private readonly TokenService _tokenService;
        private readonly HttpClient _http;

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
        }

        /// <summary>
        /// Tenta baixar a lista de funcionários do SharePoint e salvar em um arquivo JSON.
        /// </summary>
        /// <returns>Caminho local do arquivo baixado ou null se não encontrado/erro.</returns>
        public async Task<string?> DownloadJsonAsync(string targetFolder, CancellationToken ct = default)
        {
            Directory.CreateDirectory(targetFolder);

            var token = await _tokenService.GetTokenAsync().ConfigureAwait(false);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.SiteId}/lists/{_cfg.ListId}/items?expand=fields(select=Matricula,Nome)&$top=5000";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = JsonNode.Parse(json) as JsonObject;
            var arr = root?["value"] as JsonArray;
            if (arr == null) return null;

            var list = new List<Funcionario>();
            foreach (var item in arr.OfType<JsonObject>())
            {
                var fields = item["fields"] as JsonObject;
                var nome = fields?["Nome"]?.ToString();
                var matricula = fields?["Matricula"]?.ToString() ?? fields?["Matrícula"]?.ToString();
                if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(matricula))
                    continue;

                matricula = matricula.Trim().TrimStart('0');
                list.Add(new Funcionario(matricula, nome, "", "", "", "", ""));
            }

            var dst = Path.Combine(targetFolder, "funcionarios.json");
            await using var fs = File.Create(dst);
            await JsonSerializer.SerializeAsync(fs, list, cancellationToken: ct).ConfigureAwait(false);
            return dst;
        }

        /// <summary>
        /// Lê o arquivo JSON informado retornando um dicionário indexado pela matrícula.
        /// Inclui um usuário administrador fixo.
        /// </summary>
        public async Task<Dictionary<string, Funcionario>> LoadFuncionariosAsync(string jsonPath, CancellationToken ct = default)
        {
            var dict = new Dictionary<string, Funcionario>();
            if (!File.Exists(jsonPath))
            {
                dict["258790"] = new Funcionario("258790", "Administrador", "", "", "", "", "");
                return dict;
            }

            await using var fs = File.OpenRead(jsonPath);
            var list = await JsonSerializer.DeserializeAsync<List<Funcionario>>(fs, cancellationToken: ct).ConfigureAwait(false);
            if (list != null)
            {
                foreach (var f in list)
                    dict[f.Matricula] = f;
            }

            // Usuário administrador master
            dict["258790"] = new Funcionario("258790", "Administrador", "", "", "", "", "");
            return dict;
        }
    }
}
