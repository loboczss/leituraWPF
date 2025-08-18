using System;
using System.IO;
using System.Text.Json;

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

            var app = new App();
            app.InitializeComponent();
            app.Run(new MainWindow());
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
