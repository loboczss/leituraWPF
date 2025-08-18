using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using leituraWPF.Models; // ClientRecord

namespace leituraWPF.Services
{
    public sealed class LocalDataService
    {
        // Nomes que você baixa pelo Graph; ajuste se usar outro padrão
        private static readonly string[] BaseNames =
        {
            "Manutencao_AC2023",
            "Manutencao_AC2024",
            "Manutencao_AC2025",
            "Manutencao_MT"
        };

        private readonly string _baseDir;

        public LocalDataService(string baseDir = null)
        {
            // pasta do executável por padrão (onde você já salva os JSONs)
            _baseDir = baseDir ?? AppContext.BaseDirectory;
        }

        /// <summary>
        /// Encontra arquivos JSON candidatos na pasta da aplicação.
        /// Aceita sufixos (ex.: Manutencao_AC2024 (1).json).
        /// </summary>
        public Task<List<string>> FindJsonFilesAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var files = new List<string>();
                foreach (var name in BaseNames)
                {
                    ct.ThrowIfCancellationRequested();
                    // qualquer arquivo que comece com o nome-base e tenha extensão .json
                    var pattern = $"{name}*.json";
                    files.AddRange(Directory.EnumerateFiles(_baseDir, pattern, SearchOption.TopDirectoryOnly));
                }
                // remove duplicados e ordena por nome
                return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).ToList();
            }, ct);
        }

        /// <summary>
        /// Lê todos os JSONs encontrados e retorna lista mesclada de ClientRecord.
        /// Respeita os arrays nomeados (manutencoes2023, manutencoes2024, etc.), quando existirem.
        /// </summary>
        public async Task<List<ClientRecord>> LoadAllRecordsAsync(CancellationToken ct = default)
        {
            var result = new List<ClientRecord>();
            var files = await FindJsonFilesAsync(ct).ConfigureAwait(false);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    var token = JToken.Parse(json);

                    // 1) Se o root for um array: já é a lista
                    if (token is JArray arr)
                    {
                        result.AddRange(ParseClientRecords(arr, Path.GetFileName(file)));
                        continue;
                    }

                    // 2) Se for um objeto, procure arrays dentro (manutencoes2023, 2024, etc.)
                    if (token is JObject obj)
                    {
                        foreach (var prop in obj.Properties())
                        {
                            if (prop.Value is JArray arr2)
                                result.AddRange(ParseClientRecords(arr2, Path.GetFileName(file)));
                        }
                    }
                }
                catch
                {
                    // Silencioso: arquivo inválido não derruba o fluxo offline.
                    // Se quiser logar, coloque aqui.
                }
            }

            // Remover duplicados por NumOS (mantém o mais recente por nome do arquivo)
            // Se não quiser deduplicar, comente o bloco abaixo.
            result = result
                .GroupBy(r => r.NumOS, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            return result;
        }

        /// <summary>
        /// Sua regra original de parser (adaptei só pra injetar o NomeArquivoBase).
        /// </summary>
        private static List<ClientRecord> ParseClientRecords(JArray array, string nomeArquivoBase)
        {
            var list = new List<ClientRecord>();
            if (array == null) return list;

            foreach (JObject obj in array.OfType<JObject>())
            {
                string numos = obj.Value<string>("NUMOS") ?? string.Empty;
                string uf = obj.Value<string>("UF") ?? (numos.Length >= 2 ? numos[..2].ToUpperInvariant() : string.Empty);

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
                    NomeArquivoBase = nomeArquivoBase ?? string.Empty
                });
            }
            return list;
        }
    }
}
