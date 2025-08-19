using leituraWPF;
using leituraWPF.Models;
using System;
using System.Collections.Generic;
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
    /// Serviço responsável por baixar e ler o arquivo funcionarios.csv do SharePoint.
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
        /// Tenta baixar o arquivo funcionarios.csv para a pasta indicada.
        /// </summary>
        /// <returns>Caminho local do arquivo baixado ou null se não encontrado/erro.</returns>
        public async Task<string?> DownloadCsvAsync(string targetFolder, CancellationToken ct = default)
        {
            Directory.CreateDirectory(targetFolder);
            var token = await _tokenService.GetTokenAsync().ConfigureAwait(false);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var driveId = await GetDriveIdFromListAsync(ct).ConfigureAwait(false);
            var searchUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/search(q='funcionarios.csv')?$select=name,id";
            using var resp = await _http.GetAsync(searchUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = JsonNode.Parse(json)?.AsObject();
            var arr = root?["value"] as JsonArray;
            var item = arr?.OfType<JsonObject>().FirstOrDefault(o =>
                string.Equals(o["name"]?.ToString(), "funcionarios.csv", StringComparison.OrdinalIgnoreCase));
            if (item == null) return null;
            var id = item["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) return null;

            var downloadUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{id}/content";
            var dst = Path.Combine(targetFolder, "funcionarios.csv");
            using var resp2 = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp2.IsSuccessStatusCode) return null;
            await using (var fs = File.Open(dst, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await resp2.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            return dst;
        }

        /// <summary>
        /// Lê o arquivo CSV informado retornando um dicionário indexado pela matrícula.
        /// </summary>
        public async Task<Dictionary<string, Funcionario>> LoadFuncionariosAsync(string csvPath, CancellationToken ct = default)
        {
            var dict = new Dictionary<string, Funcionario>();
            if (!File.Exists(csvPath)) return dict;

            var lines = await File.ReadAllLinesAsync(csvPath, ct).ConfigureAwait(false);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = ParseCsvLine(line);
                if (parts.Length < 7) continue;

                // Remove zeros à esquerda da matrícula
                var matricula = parts[0].TrimStart('0');

                var f = new Funcionario(matricula, parts[1], parts[2], parts[3], parts[4], parts[5], parts[6]);
                dict[f.Matricula] = f;
            }
            return dict;
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

        private static string[] ParseCsvLine(string line)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++; // skip escaped quote
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            list.Add(sb.ToString());
            return list.Select(s => s.Trim('"')).ToArray();
        }
    }
}
