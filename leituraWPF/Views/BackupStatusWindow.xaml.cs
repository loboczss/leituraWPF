using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using leituraWPF.Services;

namespace leituraWPF
{
    public partial class BackupStatusWindow : Window
    {
        private readonly BackupUploaderService _backup;
        private readonly ObservableCollection<string> _pending = new();
        private readonly ObservableCollection<string> _sent = new();
        private readonly ObservableCollection<string> _errors = new();

        public BackupStatusWindow(BackupUploaderService backup)
        {
            _backup = backup ?? throw new ArgumentNullException(nameof(backup));

            InitializeComponent();

            PendingList.ItemsSource = _pending;
            SentList.ItemsSource = _sent;
            ErrorList.ItemsSource = _errors;

            RefreshCollections();
            UpdateProgress();

            _backup.FileUploaded += OnFileUploaded;
            _backup.FileUploadFailed += OnFileUploadFailed;
            _backup.CountersChangedDetailed += OnCountersChanged;
        }

        private void OnFileUploaded(string localPath, string remotePath, long size)
        {
            var name = Path.GetFileName(localPath);
            Dispatcher.Invoke(() =>
            {
                _pending.Remove(name);
                if (!_sent.Contains(name))
                    _sent.Add(name);
                UpdateProgress();
            });
        }

        private void OnFileUploadFailed(string localPath, string name, Exception ex)
        {
            var fname = Path.GetFileName(localPath);
            Dispatcher.Invoke(() =>
            {
                _pending.Remove(fname);
                if (!_errors.Contains(fname))
                    _errors.Add(fname);
                UpdateProgress();
            });
        }

        private void OnCountersChanged(int pending, long uploaded, long error)
        {
            Dispatcher.Invoke(() =>
            {
                RefreshCollections();
                UpdateProgress();
            });
        }

        private void RefreshCollections()
        {
            _pending.Clear();
            foreach (var p in _backup.GetPendingFiles().Select(Path.GetFileName))
                _pending.Add(p);

            _sent.Clear();
            foreach (var s in _backup.GetSentFiles().Select(Path.GetFileName))
                _sent.Add(s);

            _errors.Clear();
            foreach (var e in _backup.GetErrorFiles().Select(Path.GetFileName))
                _errors.Add(e);
        }

        private void UpdateProgress()
        {
            var total = _pending.Count + _sent.Count + _errors.Count;
            double percent = total == 0 ? 0 : (_sent.Count + _errors.Count) * 100.0 / total;
            ProgressBar.Value = percent;
            ProgressText.Text = $"{percent:0}%";
        }

        protected override void OnClosed(EventArgs e)
        {
            _backup.FileUploaded -= OnFileUploaded;
            _backup.FileUploadFailed -= OnFileUploadFailed;
            _backup.CountersChangedDetailed -= OnCountersChanged;
            base.OnClosed(e);
        }
    }
}
