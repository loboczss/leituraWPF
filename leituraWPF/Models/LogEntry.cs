using System;

namespace leituraWPF.Models
{
    public sealed class LogEntry
    {
        public DateTime Hora { get; set; }
        public string Tipo { get; set; } = "INFO";
        public string Emoji { get; set; } = "ℹ️";
        public string Mensagem { get; set; } = "";
    }
}
