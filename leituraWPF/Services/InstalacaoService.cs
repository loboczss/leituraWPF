using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

namespace leituraWPF.Services
{
    /// <summary>
    /// Serviço enxuto para trabalhar com os arquivos locais de instalação
    /// (ex.: <c>Instalacao_AC.json</c>). O download é feito externamente via
    /// <c>GraphDownloader</c>.
    /// </summary>
    public sealed class InstalacaoService
    {
        private static string BuildPath(string uf) =>
            Path.Combine(AppContext.BaseDirectory, "downloads", $"Instalacao_{uf}.json");

        /// <summary>
        /// Busca no arquivo <c>Instalacao_{uf}.json</c> pelo
        /// <paramref name="idSigfi"/>. Retorna Nome do Cliente e Rota, se
        /// encontrado; caso contrário retorna <c>null</c>.
        /// </summary>
        public (string NomeCliente, string Rota)? BuscarPorIdSigfi(string uf, string idSigfi)
        {
            if (string.IsNullOrWhiteSpace(idSigfi) || string.IsNullOrWhiteSpace(uf))
                return null;

            string path = BuildPath(uf);
            if (!File.Exists(path))
                return null;

            try
            {
                var json = File.ReadAllText(path);
                var root = JObject.Parse(json);
                var arr = root["instalacoes"] as JArray;
                if (arr == null) return null;

                foreach (var item in arr.OfType<JObject>())
                {
                    var val = item.Value<string>("IDSERVICOSCONJ");
                    if (string.Equals(val, idSigfi, StringComparison.OrdinalIgnoreCase))
                    {
                        string cliente = item.Value<string>("NOMEDOCLIENTE") ?? string.Empty;
                        string rota = item.Value<string>("ROTA") ?? string.Empty;
                        return (cliente, rota);
                    }
                }
            }
            catch
            {
                // Se quiser, logue erros aqui.
            }
            return null;
        }
    }
}
