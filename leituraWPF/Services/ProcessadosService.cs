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
using leituraWPF.Utils;

namespace leituraWPF.Services
{
    /// <summary>
    /// Gerencia o arquivo local processados.json e sincroniza com a lista SharePoint.
    /// </summary>
    public sealed class ProcessadosService
    {
        private readonly AppConfig _cfg;
        private readonly TokenService _tokenService;
        private readonly HttpClient _http;
        private readonly string _filePath;
        private string? _listId;
        private readonly SemaphoreSlim _mutex = new(1, 1);

        private class Entry
        {
            public string Pasta { get; set; } = string.Empty;
            public string Usuario { get; set; } = string.Empty;
            public List<string> Arquivos { get; set; } = new();
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
        }

        public async Task AddAsync(string pasta, string usuario, IEnumerable<string> arquivos, string versao)
        {
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = await LoadAsync().ConfigureAwait(false);
                var arr = arquivos.ToList();
                list.Add(new Entry
                {
                    Pasta = pasta,
                    Usuario = usuario,
                    Arquivos = arr,
                    Quantidade = arr.Count,
                    Versao = versao,
                    Sincronizado = false
                });
                await SaveAsync(list).ConfigureAwait(false);
            }
            finally
            {
                _mutex.Release();
            }
        }

        public async Task TrySyncAsync()
        {
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = await LoadAsync().ConfigureAwait(false);
                var pendentes = list.Where(e => !e.Sincronizado).ToList();
                if (pendentes.Count == 0) return;

                try
                {
                    var listId = await ResolveListIdAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(listId)) return;

                    var token = await _tokenService.GetTokenAsync().ConfigureAwait(false);
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    _http.DefaultRequestHeaders.Accept.Clear();
                    _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.SiteId}/lists/{listId}/items";

                    foreach (var item in pendentes)
                    {
                        var body = new
                        {
                            fields = new
                            {
                                Title = item.Pasta,
                                Usuario = item.Usuario,
                                Arquivos = string.Join(",", item.Arquivos),
                                Quantidade = item.Quantidade,
                                Versao = item.Versao
                            }
                        };
                        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                        using var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            item.Sincronizado = true;
                        }
                        else
                        {
                            break; // interrompe em caso de falha
                        }
                    }

                    await SaveAsync(list).ConfigureAwait(false);
                }
                catch
                {
                    // mantém arquivo para próxima tentativa
                }
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
            catch
            {
                return false;
            }
        }

        private async Task<string?> ResolveListIdAsync()
        {
            if (!string.IsNullOrEmpty(_listId)) return _listId;

            try
            {
                var token = await _tokenService.GetTokenAsync().ConfigureAwait(false);
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var url = $"https://graph.microsoft.com/v1.0/sites/{_cfg.SiteId}/lists?$filter=displayName eq '{_cfg.ProcessLogListName}'&$select=id";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var obj = JsonNode.Parse(json)?.AsObject();
                var arr = obj?["value"] as JsonArray;
                var id = arr?.OfType<JsonObject>().FirstOrDefault()?["id"]?.ToString();
                _listId = id;
                return id;
            }
            catch
            {
                return null;
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
            catch { }
            return new List<Entry>();
        }

        private async Task SaveAsync(List<Entry> list)
        {
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        }
    }
}

