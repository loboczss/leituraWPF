using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using leituraWPF.Services;
using System.Windows.Data;

namespace leituraWPF
{
    public partial class BackupStatusWindow : Window, INotifyPropertyChanged
    {
        #region Fields
        private readonly BackupUploaderService _backup;
        private readonly ObservableCollection<BackupItem> _pending = new();
        private readonly ObservableCollection<BackupItem> _sent = new();
        private readonly ObservableCollection<BackupItem> _errors = new();
        private readonly ObservableCollection<BackupItem> _historySent = new();
        private readonly ObservableCollection<BackupItem> _historyErrors = new();
        private ICollectionView _historySentView;
        private ICollectionView _historyErrorView;
        private string _historySearchText = string.Empty;
        private DispatcherTimer _updateTimer;
        private readonly Stopwatch _stopwatch;
        private CancellationTokenSource _cancellationTokenSource;

        private string _statusText = "Preparando backup...";
        private string _timeElapsed = "";
        private bool _isCompleted = false;
        #endregion

        #region Properties
        public string CurrentStatusText
        {
            get => _statusText;
            set
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }

        public string CurrentTimeElapsed
        {
            get => _timeElapsed;
            set
            {
                _timeElapsed = value;
                OnPropertyChanged();
            }
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                _isCompleted = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<BackupItem> HistorySent => _historySent;

        public ObservableCollection<BackupItem> HistoryErrors => _historyErrors;

        public string HistorySearchText
        {
            get => _historySearchText;
            set
            {
                _historySearchText = value;
                OnPropertyChanged();
                _historySentView?.Refresh();
                _historyErrorView?.Refresh();
            }
        }
        #endregion

        #region Constructor
        public BackupStatusWindow(BackupUploaderService backup)
        {
            _backup = backup ?? throw new ArgumentNullException(nameof(backup));
            _cancellationTokenSource = new CancellationTokenSource();
            _stopwatch = Stopwatch.StartNew();

            InitializeComponent();
            InitializeDataContext();
            InitializeTimer();
            SetupEventHandlers();

            // Inicialização assíncrona
            _ = Task.Run(InitializeAsync);
        }
        #endregion

        #region Initialization
        private void InitializeDataContext()
        {
            DataContext = this;
            PendingList.ItemsSource = _pending;
            SentList.ItemsSource = _sent;
            ErrorList.ItemsSource = _errors;

            _historySentView = CollectionViewSource.GetDefaultView(_historySent);
            _historySentView.Filter = HistoryFilter;
            _historyErrorView = CollectionViewSource.GetDefaultView(_historyErrors);
            _historyErrorView.Filter = HistoryFilter;
        }

        private void InitializeTimer()
        {
            _updateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();
        }

        private void SetupEventHandlers()
        {
            _backup.FileUploaded += OnFileUploaded;
            _backup.FileUploadFailed += OnFileUploadFailed;
            _backup.CountersChangedDetailed += OnCountersChanged;

            Loaded += BackupStatusWindow_Loaded;
            Closing += BackupStatusWindow_Closing;
        }

        private async Task InitializeAsync()
        {
            try
            {
                await Task.Delay(500, _cancellationTokenSource.Token); // Simula carregamento

                await Dispatcher.InvokeAsync(() =>
                {
                    CurrentStatusText = "Carregando arquivos...";
                    RefreshCollections();
                    LoadHistory();
                    UpdateProgress();
                    UpdateCounters();
                });
            }
            catch (OperationCanceledException)
            {
                // Operação foi cancelada
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    CurrentStatusText = $"Erro na inicialização: {ex.Message}";
                });
            }
        }
        #endregion

        #region Event Handlers
        private void BackupStatusWindow_Loaded(object sender, RoutedEventArgs e)
        {
            CurrentStatusText = "Backup iniciado";
        }

        private async void BackupStatusWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!IsCompleted && _pending.Any())
            {
                var result = System.Windows.MessageBox.Show(
                    "O backup ainda está em andamento. Deseja realmente fechar?",
                    "Backup em Progresso",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            await CleanupAsync();
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            if (_stopwatch.IsRunning)
            {
                var elapsed = _stopwatch.Elapsed;
                CurrentTimeElapsed = $"Tempo: {elapsed:hh\\:mm\\:ss}";

                // Atualiza status baseado no progresso
                UpdateStatusText();
            }
        }

        private async void OnFileUploaded(string localPath, string remotePath, long size)
        {
            var fileName = GetDisplayName(localPath);

            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    // Remove da lista de pendentes
                    var pendingItem = _pending.FirstOrDefault(x => x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                    if (pendingItem != null)
                    {
                        _pending.Remove(pendingItem);
                    }

                    // Adiciona à lista de enviados se não existir
                    if (!_sent.Any(x => x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _sent.Add(new BackupItem
                        {
                            FileName = fileName,
                            Size = FormatFileSize(size),
                            CompletedAt = DateTime.Now,
                            RemotePath = remotePath
                        });
                    }

                    UpdateProgress();
                    UpdateCounters();
                    LoadHistory();
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                // Log do erro sem afetar a UI
                Debug.WriteLine($"Erro ao atualizar UI para arquivo enviado {fileName}: {ex.Message}");
            }
        }

        private async void OnFileUploadFailed(string localPath, string errorMessage, Exception ex)
        {
            var fileName = GetDisplayName(localPath);

            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    // Remove da lista de pendentes
                    var pendingItem = _pending.FirstOrDefault(x => x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                    if (pendingItem != null)
                    {
                        _pending.Remove(pendingItem);
                    }

                    // Adiciona à lista de erros se não existir
                    if (!_errors.Any(x => x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _errors.Add(new BackupItem
                        {
                            FileName = fileName,
                            ErrorMessage = ex?.Message ?? errorMessage ?? "Erro desconhecido",
                            CompletedAt = DateTime.Now
                        });
                    }

                    UpdateProgress();
                    UpdateCounters();
                    LoadHistory();
                }, DispatcherPriority.Background);
            }
            catch (Exception updateEx)
            {
                Debug.WriteLine($"Erro ao atualizar UI para falha de upload {fileName}: {updateEx.Message}");
            }
        }

        private async void OnCountersChanged(int pending, long uploaded, long error)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    RefreshCollections();
                    LoadHistory();
                    UpdateProgress();
                    UpdateCounters();
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao atualizar contadores: {ex.Message}");
            }
        }
        #endregion

        #region Private Methods
        private void RefreshCollections()
        {
            try
            {
                // Atualiza pendentes
                _pending.Clear();
                var pendingFiles = _backup.GetPendingFiles();
                foreach (var filePath in pendingFiles)
                {
                    var fileName = GetDisplayName(filePath);
                    var fileInfo = new FileInfo(filePath);

                    _pending.Add(new BackupItem
                    {
                        FileName = fileName,
                        Size = FormatFileSize(fileInfo.Length),
                        FilePath = filePath
                    });
                }
            }
            catch (Exception ex)
            {
                CurrentStatusText = $"Erro ao atualizar listas: {ex.Message}";
                Debug.WriteLine($"Erro em RefreshCollections: {ex}");
            }
        }

        private void LoadHistory()
        {
            try
            {
                _historySent.Clear();
                var sentFiles = _backup.GetSentFiles()
                    .OrderByDescending(f => File.GetLastWriteTime(f));
                foreach (var filePath in sentFiles)
                {
                    var info = new FileInfo(filePath);
                    _historySent.Add(new BackupItem
                    {
                        FileName = GetDisplayName(filePath),
                        CompletedAt = info.LastWriteTime,
                        FilePath = filePath
                    });
                }

                _historyErrors.Clear();
                var errorFiles = _backup.GetErrorFiles()
                    .Where(f => !f.EndsWith(".error", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => File.GetLastWriteTime(f));
                foreach (var filePath in errorFiles)
                {
                    var info = new FileInfo(filePath);
                    _historyErrors.Add(new BackupItem
                    {
                        FileName = GetDisplayName(filePath),
                        CompletedAt = info.LastWriteTime,
                        FilePath = filePath
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao carregar histórico: {ex.Message}");
            }
        }

        private void UpdateProgress()
        {
            try
            {
                var total = _pending.Count + _sent.Count + _errors.Count;
                var completed = _sent.Count + _errors.Count;

                if (total == 0)
                {
                    ProgressBar.Value = 0;
                    ProgressText.Text = "0%";
                    return;
                }

                double percent = (completed * 100.0) / total;
                ProgressBar.Value = Math.Min(percent, 100);
                ProgressText.Text = $"{percent:F1}%";

                // Verifica se completou
                if (completed == total && total > 0)
                {
                    IsCompleted = true;
                    _stopwatch.Stop();
                    CurrentStatusText = _errors.Any() ?
                        $"Backup concluído com {_errors.Count} erro(s)" :
                        "Backup concluído com sucesso!";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro em UpdateProgress: {ex.Message}");
            }
        }

        private void UpdateCounters()
        {
            try
            {
                PendingCount.Text = $"{_pending.Count} Pendente{(_pending.Count != 1 ? "s" : "")}";
                SentCount.Text = $"{_sent.Count} Enviado{(_sent.Count != 1 ? "s" : "")}";
                ErrorCount.Text = $"{_errors.Count} Erro{(_errors.Count != 1 ? "s" : "")}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro em UpdateCounters: {ex.Message}");
            }
        }

        private void UpdateStatusText()
        {
            if (IsCompleted) return;

            try
            {
                if (_pending.Any())
                {
                    CurrentStatusText = $"Processando arquivos... ({_sent.Count + _errors.Count} de {_pending.Count + _sent.Count + _errors.Count})";
                }
                else if (_sent.Any() || _errors.Any())
                {
                    CurrentStatusText = "Finalizando backup...";
                }
                else
                {
                    CurrentStatusText = "Aguardando arquivos...";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro em UpdateStatusText: {ex.Message}");
            }
        }

        private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string path && File.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Erro ao abrir local do arquivo: {ex.Message}");
                }
            }
        }

        private static string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;

            while (Math.Round(number / 1024) >= 1)
            {
                number = number / 1024;
                counter++;
            }

            return $"{number:n1} {suffixes[counter]}";
        }

        private bool HistoryFilter(object obj)
        {
            if (obj is not BackupItem item) return false;
            if (string.IsNullOrWhiteSpace(HistorySearchText)) return true;
            return item.FileName?.Contains(HistorySearchText, StringComparison.OrdinalIgnoreCase) == true;
        }

        private static string GetDisplayName(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return filePath;

            var directory = Path.GetDirectoryName(filePath);
            var folderName = string.IsNullOrEmpty(directory) ? string.Empty : new DirectoryInfo(directory).Name;
            var prefix = folderName.Split('_').FirstOrDefault() ?? folderName;

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var suffix = fileName.Split('_').LastOrDefault() ?? fileName;

            return string.IsNullOrEmpty(prefix) ? suffix : $"{prefix}_{suffix}";
        }

        private async Task CleanupAsync()
        {
            try
            {
                _updateTimer?.Stop();
                _stopwatch?.Stop();
                _cancellationTokenSource?.Cancel();

                // Desregistra eventos
                if (_backup != null)
                {
                    _backup.FileUploaded -= OnFileUploaded;
                    _backup.FileUploadFailed -= OnFileUploadFailed;
                    _backup.CountersChangedDetailed -= OnCountersChanged;
                }

                // Aguarda um pouco para operações assíncronas terminarem
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro durante cleanup: {ex.Message}");
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _updateTimer?.Stop();
            }
        }
        #endregion

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion

        #region Cleanup
        protected override async void OnClosed(EventArgs e)
        {
            await CleanupAsync();
            base.OnClosed(e);
        }
        #endregion
    }

    #region Helper Classes
    public class BackupItem : INotifyPropertyChanged
    {
        private string _fileName;
        private string _size;
        private string _errorMessage;
        private DateTime? _completedAt;
        private string _filePath;
        private string _remotePath;

        public string FileName
        {
            get => _fileName;
            set { _fileName = value; OnPropertyChanged(); }
        }

        public string Size
        {
            get => _size;
            set { _size = value; OnPropertyChanged(); }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); }
        }

        public DateTime? CompletedAt
        {
            get => _completedAt;
            set { _completedAt = value; OnPropertyChanged(); }
        }

        public string FilePath
        {
            get => _filePath;
            set { _filePath = value; OnPropertyChanged(); }
        }

        public string RemotePath
        {
            get => _remotePath;
            set { _remotePath = value; OnPropertyChanged(); }
        }

        public string DisplayText =>
            !string.IsNullOrEmpty(Size) ? $"{FileName} ({Size})" : FileName;

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    #endregion
}