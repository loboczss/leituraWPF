// Utils/AppConfig.cs
using System.Collections.Generic;

namespace leituraWPF.Utils
{
    public sealed class AppConfig
    {
        // ==== Auth / Graph ====
        public string TenantId { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string GraphScope { get; set; } = "https://graph.microsoft.com/.default";

        // ==== SharePoint origem (manutenção/instalação) ====
        public string SiteId { get; set; } = "";
        public string ListId { get; set; } = "";
        public string FuncionarioListId { get; set; } = "";
        public string ProcessLogListName { get; set; } = "ListCompillerLog";

        public List<string>? WantedPrefixes { get; set; }

        public int MaxParallelDownloads { get; set; } = 8;
        public int HttpTimeoutSeconds { get; set; } = 120;
        public bool SkipUnchanged { get; set; } = true;
        public bool ForceDriveSearch { get; set; } = true;

        // ==== Backup / Upload para SharePoint ====
        // Estratégias suportadas (em ordem de prioridade):
        // 1) BackupDriveId (usa diretamente esse drive)
        public string? BackupDriveId { get; set; }    // opcional

        // 2) BackupSiteId + BackupListId (resolve o drive da Document Library)
        public string? BackupSiteId { get; set; }     // opcional
        public string? BackupListId { get; set; }     // opcional

        // 3) BackupWebUrl (ex.: https://oneengenharia.sharepoint.com/sites/OneEngenharia)
        //    Se informado, o serviço resolve o site + drive padrão via WebUrl.
        public string? BackupWebUrl { get; set; }     // opcional

        // Pasta raiz dentro do drive de backup
        public string BackupFolder { get; set; } = "LogsRenomeacao";

        // Intervalo do loop contínuo (segundos)
        public int BackupPollSeconds { get; set; } = 30;
    }
}
