// Utils/AppConfig.cs
using System.Collections.Generic;

namespace leituraWPF.Utils
{
    public sealed class AppConfig
    {
        // ---- Graph / Auth ----
        public string TenantId { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string GraphScope { get; set; } = "https://graph.microsoft.com/.default";

        // ---- Fonte (download de JSON) ----
        public string SiteId { get; set; } = "";
        public string ListId { get; set; } = "";
        public List<string>? WantedPrefixes { get; set; }

        public int MaxParallelDownloads { get; set; } = 8;
        public int HttpTimeoutSeconds { get; set; } = 120;
        public bool SkipUnchanged { get; set; } = true;
        public bool ForceDriveSearch { get; set; } = true;

        // ---- Backup (NOVO) ----
        public string BackupSiteId { get; set; } = "";          // site do SharePoint para backup
        public string BackupDriveId { get; set; } = "";         // opcional; se vazio, resolvemos via /sites/{id}/drive
        public string BackupFolder { get; set; } = "LogsRenomeacao"; // subpasta destino (criada se não existir)
        public int BackupPollSeconds { get; set; } = 30;        // frequência de varredura (segundos)
    }
}
