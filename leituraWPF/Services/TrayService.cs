using System;
using System.Windows.Forms;

namespace leituraWPF.Services
{
    public sealed class TrayService : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Action _showWindow;
        private readonly Action _sync;
        private readonly Action _exit;

        public TrayService(Action showWindow, Action sync, Action exit)
        {
            _showWindow = showWindow;
            _sync = sync;
            _exit = exit;

            _notifyIcon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "leituraWPF"
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Abrir o aplicativo", null, (s, e) => _showWindow());
            menu.Items.Add("Iniciar sincronização manual", null, (s, e) => _sync());
            menu.Items.Add("Fechar o aplicativo", null, (s, e) => _exit());
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (s, e) => _showWindow();
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
