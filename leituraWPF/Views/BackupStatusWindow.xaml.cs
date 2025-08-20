using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace leituraWPF
{
    public partial class BackupStatusWindow : Window
    {
        public BackupStatusWindow(IEnumerable<string> pending, IEnumerable<string> sent, IEnumerable<string> errors)
        {
            InitializeComponent();

            PendingList.ItemsSource = pending.Select(Path.GetFileName);
            SentList.ItemsSource = sent.Select(Path.GetFileName);
            ErrorList.ItemsSource = errors.Select(Path.GetFileName);
        }
    }
}
