using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using leituraWPF.Services;
using leituraWPF.Views;

namespace leituraWPF
{
    public static class Program
    {
        public static AppConfig Config { get; private set; } = new();

        [STAThread]
        public static void Main()
        {
            // Carrega configuração
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
                throw new FileNotFoundException("Arquivo appsettings.json não encontrado.", path);

            var json = File.ReadAllText(path);
            Config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new AppConfig();

            // Tenta baixar o CSV de funcionários
            var csvPath = Path.Combine(AppContext.BaseDirectory, "funcionarios.csv");
            try
            {
                var tokenSvc = new TokenService(Config);
                var csvSvc = new FuncionarioCsvService(Config, tokenSvc);
                csvSvc.DownloadAsync(csvPath).GetAwaiter().GetResult();
            }
            catch
            {
                // ignora erros de download; será verificada a existência do arquivo abaixo
            }

            if (!File.Exists(csvPath))
            {
                MessageBox.Show("Não foi possível obter o arquivo de funcionários.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var app = new App();
            app.InitializeComponent();
            app.Run(new LoginWindow(csvPath));
        }
    }

    // Program.cs  (apenas a classe AppConfig mudou)
    public sealed class AppConfig
    {
        public string TenantId { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string GraphScope { get; set; } = "https://graph.microsoft.com/.default";

        public string SiteId { get; set; } = "";
        public string ListId { get; set; } = "";

        public string[] WantedPrefixes { get; set; } = Array.Empty<string>();

        // >>> Novas propriedades de performance <<<
        public int MaxParallelDownloads { get; set; } = 8;     // quantos downloads simultâneos
        public int HttpTimeoutSeconds { get; set; } = 120;   // timeout por request
        public bool SkipUnchanged { get; set; } = true;  // pular arquivos com mesma ETag
        public bool ForceDriveSearch { get; set; } = true;

        // Configurações de backup contínuo
        public string BackupSiteId { get; set; } = string.Empty;
        public string BackupDriveId { get; set; } = string.Empty; // opcional
        public string BackupListId { get; set; } = string.Empty;  // opcional
        public string BackupFolder { get; set; } = "LogsRenomeacao";
        public int BackupPollSeconds { get; set; } = 30;
    }

}
