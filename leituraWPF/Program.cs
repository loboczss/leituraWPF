using System;
using System.IO;
using System.Text.Json;
using leituraWPF.Utils;

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
}
