using Microsoft.Win32;
using System.Diagnostics;

namespace leituraWPF.Services
{
    public static class StartupService
    {
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "leituraWPF";

        public static void ConfigureStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, writable: true);
                var exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                key?.SetValue(APP_NAME, $"\"{exe}\"");
            }
            catch
            {
                // ignorado: sem permissão de registro
            }
        }
    }
}
