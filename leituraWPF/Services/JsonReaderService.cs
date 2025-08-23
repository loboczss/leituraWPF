using leituraWPF.Models;
using leituraWPF.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace leituraWPF.Services
{
    public sealed class JsonReaderService
    {
        private readonly ILogSink _log;

        public JsonReaderService(ILogSink log) => _log = log;

        /// <summary>
        /// Lê um arquivo JSON tolerante:
        ///  - JArray puro
        ///  - JObject com "value" ou "data" como array
        ///  - JObject cujo PRIMEIRO campo com valor JArray (ex.: "manutencoes2023") será usado
        /// </summary>
        public JArray LoadJArrayFlexible(string filePath)
        {
            using var sr = new StreamReader(filePath);
            using var reader = new JsonTextReader(sr);

            var token = JToken.ReadFrom(reader);

            if (token is JArray arrDirect)
                return arrDirect;

            if (token is JObject obj)
            {
                // Caminhos comuns
                if (obj.TryGetValue("value", StringComparison.OrdinalIgnoreCase, out var v) && v is JArray va)
                    return va;
                if (obj.TryGetValue("data", StringComparison.OrdinalIgnoreCase, out var d) && d is JArray da)
                    return da;

                // Formato do usuário: { "manutencoes2023": [ ... ] } (ou 2024, 2025, etc)
                // Estratégia: pegar o primeiro property cujo valor seja JArray
                var firstArrayProp = obj.Properties()
                                        .FirstOrDefault(p => p.Value is JArray);
                if (firstArrayProp?.Value is JArray anyArray)
                    return anyArray;
            }

            throw new InvalidDataException(
                "O JSON não é um array nem contém um array conhecido ('value'/'data' ou 'manutencoesXXXX').");
        }

        /// <summary>
        /// Mesma lógica de parse enviada, com fallback da UF pelo início do NUMOS.
        /// </summary>
        public static List<ClientRecord> ParseClientRecords(JArray array)
        {
            var list = new List<ClientRecord>();
            if (array == null) return list;

            foreach (JObject obj in array.OfType<JObject>())
            {
                string numosRaw = obj.Value<string>("NUMOS") ?? string.Empty;
                string uf = obj.Value<string>("UF") ?? (numosRaw.Length >= 2 ? numosRaw[..2].ToUpperInvariant() : string.Empty);

                // Normaliza o NUMOS garantindo o prefixo da UF
                string numos = new string(numosRaw.Where(char.IsLetterOrDigit).ToArray());
                if (!string.IsNullOrEmpty(uf) && !numos.StartsWith(uf, StringComparison.OrdinalIgnoreCase))
                    numos = uf + numos;

                list.Add(new ClientRecord
                {
                    Rota = obj.Value<string>("ROTA") ?? string.Empty,
                    Tipo = (obj.Value<string>("TIPO") ?? string.Empty).ToUpperInvariant(),
                    NumOS = numos,
                    NumOcorrencia = obj.Value<string>("NUMOCORRENCIA") ?? string.Empty,
                    Obra = obj.Value<string>("OBRA") ?? string.Empty,
                    IdSigfi = obj.Value<string>("IDSIGFI") ?? string.Empty,
                    UC = obj.Value<string>("UC") ?? string.Empty,
                    NomeCliente = obj.Value<string>("NOMECLIENTE") ?? string.Empty,
                    Empresa = (obj.Value<string>("EMPRESA") ?? string.Empty).ToUpperInvariant(),
                    TipoDesigfi = (obj.Value<string>("TIPODESIGFI") ?? string.Empty).ToUpperInvariant(),
                    UF = uf,
                    NomeArquivoBase = string.Empty
                });
            }
            return list;
        }
    }
}
