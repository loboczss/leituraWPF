using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

namespace leituraWPF.Services
{
    /// <summary>
    /// Serviço enxuto para trabalhar com o arquivo local Instalacao_AC.json.
    /// O download é feito externamente via GraphDownloader.
    /// </summary>
    public sealed class InstalacaoService
    {
        public static string OfflinePath =>
            Path.Combine(AppContext.BaseDirectory, "downloads", "Instalacao_AC.json");

        private void GarantirArquivoLocal()
        {
            if (!File.Exists(OfflinePath))
            {
                throw new FileNotFoundException(
                    "Instalacao_AC.json não encontrado. Sincronize primeiro (Sincronizar Tudo) ou tente novamente o fluxo de instalação.");
            }
        }

        /// <summary>
        /// Busca no Instalacao_AC.json por IDSERVICOSCONJ == idSigfi.
        /// Retorna NomeCliente e Rota, se encontrado.
        /// </summary>
        public (string NomeCliente, string Rota)? BuscarPorIdSigfi(string idSigfi)
        {
            if (string.IsNullOrWhiteSpace(idSigfi)) return null;

            GarantirArquivoLocal();

            try
            {
                var json = File.ReadAllText(OfflinePath);
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
