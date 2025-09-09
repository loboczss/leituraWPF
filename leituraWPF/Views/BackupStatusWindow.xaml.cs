using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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

        private readonly ConcurrentDictionary<string, BackupItem> _pendingCache = new();
        private readonly ConcurrentDictionary<string, BackupItem> _sentCache = new();
        private readonly ConcurrentDictionary<string, BackupItem> _errorCache = new();

        private ICollectionView _historySentView;
        private ICollectionView _historyErrorView;
        private string _historySearchText = string.Empty;
        private DispatcherTimer _updateTimer;
        private readonly Stopwatch _stopwatch;
        private CancellationTokenSource _cancellationTokenSource;

        private string _statusText = "Preparando backup...";
        private string _timeElapsed = "";
        private bool _isCompleted = false;
        private bool _isInitialized = false;

        // Throttling para atualizações de UI
        private readonly SemaphoreSlim _uiUpdateSemaphore = new(1, 1);
        private DateTime _lastProgressUpdate = DateTime.MinValue;
        private readonly TimeSpan _progressUpdateThrottle = TimeSpan.FromMilliseconds(100);
        #endregion

        #region Properties
        public string CurrentStatusText
        {
            get => _statusText;
            private set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CurrentTimeElapsed
        {
            get => _timeElapsed;
            private set
            {
                if (_timeElapsed != value)
                {
                    _timeElapsed = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            private set
            {
                if (_isCompleted != value)
                {
                    _isCompleted = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<BackupItem> HistorySent => _historySent;
        public ObservableCollection<BackupItem> HistoryErrors => _historyErrors;

        public string HistorySearchText
        {
            get => _historySearchText;
            set
            {
                if (_historySearchText != value)
                {
                    _historySearchText = value;
                    OnPropertyChanged();
                    ApplyHistoryFilter();
                }
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

            // Inicialização assíncrona sem bloquear UI
            InitializeAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
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
        }

        private async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(100, cancellationToken); // Reduzido de 500ms

                await UpdateStatusAsync("Carregando arquivos...", cancellationToken);

                var tasks = new[]
                {
                    RefreshCollectionsAsync(cancellationToken),
                    LoadHistoryAsync(cancellationToken)
                };

                await Task.WhenAll(tasks);

                await UpdateProgressAndCountersAsync(cancellationToken);
                _isInitialized = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Operação foi cancelada
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"Erro na inicialização: {ex.Message}", cancellationToken);
                Debug.WriteLine($"Erro na inicialização: {ex}");
            }
        }
        #endregion

        #region Event Handlers
        private void BackupStatusWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialized)
                CurrentStatusText = "Backup iniciado";
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            if (_stopwatch.IsRunning)
            {
                var elapsed = _stopwatch.Elapsed;
                CurrentTimeElapsed = $"Tempo: {elapsed:hh\\:mm\\:ss}";
                UpdateStatusText();
            }
        }

        private async void OnFileUploaded(string localPath, string remotePath, long size)
        {
            var fileName = GetDisplayName(localPath);
            if (string.IsNullOrEmpty(fileName)) return;

            try
            {
                // Remove do cache de pendentes
                _pendingCache.TryRemove(fileName, out _);

                // Adiciona ao cache de enviados se não existir
                var sentItem = new BackupItem
                {
                    FileName = fileName,
                    Size = FormatFileSize(size),
                    CompletedAt = DateTime.Now,
                    RemotePath = remotePath
                };

                if (_sentCache.TryAdd(fileName, sentItem))
                {
                    await UpdateCollectionAsync(_pending, _sent, fileName, sentItem);
                }

                await ThrottledProgressUpdateAsync();
                LoadHistoryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao processar arquivo enviado {fileName}: {ex.Message}");
            }
        }

        private async void OnFileUploadFailed(string localPath, string errorMessage, Exception ex)
        {
            var fileName = GetDisplayName(localPath);
            if (string.IsNullOrEmpty(fileName)) return;

            try
            {
                // Remove do cache de pendentes
                _pendingCache.TryRemove(fileName, out _);

                var errorItem = new BackupItem
                {
                    FileName = fileName,
                    ErrorMessage = ex?.Message ?? errorMessage ?? "Erro desconhecido",
                    CompletedAt = DateTime.Now
                };

                if (_errorCache.TryAdd(fileName, errorItem))
                {
                    await UpdateCollectionAsync(_pending, _errors, fileName, errorItem);
                }

                await ThrottledProgressUpdateAsync();
                LoadHistoryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception updateEx)
            {
                Debug.WriteLine($"Erro ao processar falha de upload {fileName}: {updateEx.Message}");
            }
        }

        private async void OnCountersChanged(int pending, long uploaded, long error)
        {
            try
            {
                var token = _cancellationTokenSource.Token;
                var refreshTask = RefreshCollectionsAsync(token);
                var historyTask = LoadHistoryAsync(token);

                await Task.WhenAll(refreshTask, historyTask);
                await UpdateProgressAndCountersAsync(token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao atualizar contadores: {ex.Message}");
            }
        }

        private async void RetryErrors_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _backup.RetryErrorsAsync();
                await RefreshCollectionsAsync(_cancellationTokenSource.Token);

                await Dispatcher.InvokeAsync(() => _errors.Clear(), DispatcherPriority.Background);
                _errorCache.Clear();

                await _backup.ForceRunOnceAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao reenviar arquivos: {ex.Message}");
            }
        }

        private async void RetryHistory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string path)
                return;

            try
            {
                var token = _cancellationTokenSource.Token;
                await _backup.RetryErrorAsync(path);

                var tasks = new[]
                {
                    RefreshCollectionsAsync(token),
                    LoadHistoryAsync(token)
                };

                await Task.WhenAll(tasks);
                await _backup.ForceRunOnceAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao reenviar arquivo do histórico: {ex.Message}");
            }
        }
        #endregion

        #region Private Methods
        private async Task RefreshCollectionsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var pendingItems = await Task.Run(() =>
                {
                    var items = new List<BackupItem>();
                    foreach (var filePath in _backup.GetPendingFiles())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var fileName = Path.GetFileName(filePath);
                        if (string.IsNullOrEmpty(fileName)) continue;

                        if (!File.Exists(filePath)) continue;

                        var fileInfo = new FileInfo(filePath);
                        var item = new BackupItem
                        {
                            FileName = fileName,
                            Size = FormatFileSize(fileInfo.Length),
                            FilePath = filePath
                        };

                        items.Add(item);
                        _pendingCache.TryAdd(fileName, item);
                    }
                    return items;
                }, cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    _pending.Clear();
                    foreach (var item in pendingItems)
                        _pending.Add(item);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Operação cancelada
            }
            catch (Exception ex)
            {
                await UpdateStatusAsync($"Erro ao atualizar listas: {ex.Message}", cancellationToken);
                Debug.WriteLine($"Erro em RefreshCollections: {ex}");
            }
        }

        private async Task LoadHistoryAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var (sentItems, errorItems) = await Task.Run(() =>
                {
                    var sent = _backup.GetSentFiles()
                        .AsParallel()
                        .Where(f => File.Exists(f))
                        .Select(filePath =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var info = new FileInfo(filePath);
                            return new BackupItem
                            {
                                FileName = GetDisplayName(filePath),
                                CompletedAt = info.LastWriteTime,
                                FilePath = filePath
                            };
                        })
                        .OrderByDescending(f => f.CompletedAt)
                        .ToList();

                    var errors = _backup.GetErrorFiles()
                        .AsParallel()
                        .Where(f => !f.EndsWith(".error", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
                        .Select(filePath =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var info = new FileInfo(filePath);
                            return new BackupItem
                            {
                                FileName = GetDisplayName(filePath),
                                CompletedAt = info.LastWriteTime,
                                FilePath = filePath
                            };
                        })
                        .OrderByDescending(f => f.CompletedAt)
                        .ToList();

                    return (sent, errors);
                }, cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateCollection(_historySent, sentItems);
                    UpdateCollection(_historyErrors, errorItems);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Operação cancelada
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao carregar histórico: {ex.Message}");
            }
        }

        private async Task UpdateCollectionAsync(ObservableCollection<BackupItem> fromCollection,
            ObservableCollection<BackupItem> toCollection, string fileName, BackupItem newItem)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                var existingItem = fromCollection.FirstOrDefault(x =>
                    x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

                if (existingItem != null)
                    fromCollection.Remove(existingItem);

                if (!toCollection.Any(x => x.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                    toCollection.Add(newItem);

            }, DispatcherPriority.Background);
        }

        private static void UpdateCollection<T>(ObservableCollection<T> collection, IEnumerable<T> newItems)
        {
            collection.Clear();
            foreach (var item in newItems)
                collection.Add(item);
        }

        private async Task ThrottledProgressUpdateAsync()
        {
            var now = DateTime.Now;
            if (now - _lastProgressUpdate < _progressUpdateThrottle) return;

            _lastProgressUpdate = now;
            await UpdateProgressAndCountersAsync(_cancellationTokenSource.Token);
        }

        private async Task UpdateProgressAndCountersAsync(CancellationToken cancellationToken = default)
        {
            if (!await _uiUpdateSemaphore.WaitAsync(50, cancellationToken)) return;

            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    var pending = _pending.Count;
                    var sent = _sent.Count;
                    var errors = _errors.Count;

                    UpdateProgress(pending, sent, errors);
                    UpdateCounters(pending, sent, errors);
                }, DispatcherPriority.Background);
            }
            finally
            {
                _uiUpdateSemaphore.Release();
            }
        }

        private void UpdateProgress(long pending, long uploaded, long error)
        {
            try
            {
                var total = pending + uploaded + error;
                var completed = uploaded + error;

                if (total == 0)
                {
                    SetProgressValues(0, "0%");
                    return;
                }

                var percent = Math.Min((completed * 100.0) / total, 100);
                var percentText = $"{percent:F1}%";

                SetProgressValues(percent, percentText);

                if (completed == total && total > 0 && !IsCompleted)
                {
                    IsCompleted = true;
                    _stopwatch.Stop();
                    CurrentStatusText = error > 0
                        ? $"Backup concluído com {error} erro(s)"
                        : "Backup concluído com sucesso!";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro em UpdateProgress: {ex.Message}");
            }
        }

        private void SetProgressValues(double value, string text)
        {
            ProgressBar.Value = value;
            ProgressText.Text = text;
            MainProgress.Text = text;
        }

        private void UpdateCounters(long pending, long uploaded, long error)
        {
            try
            {
                PendingCount.Text = $"{pending} Pendente{(pending != 1 ? "s" : "")}";
                SentCount.Text = $"{uploaded} Enviado{(uploaded != 1 ? "s" : "")}";
                ErrorCount.Text = $"{error} Erro{(error != 1 ? "s" : "")}";
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
                var pendingCount = _pending.Count;
                var totalProcessed = _sent.Count + _errors.Count;
                var total = pendingCount + totalProcessed;

                CurrentStatusText = pendingCount > 0
                    ? $"Processando arquivos... ({totalProcessed} de {total})"
                    : totalProcessed > 0
                        ? "Finalizando backup..."
                        : "Aguardando arquivos...";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro em UpdateStatusText: {ex.Message}");
            }
        }

        private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn ||
                btn.Tag is not string path ||
                !File.Exists(path)) return;

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

        private static string FormatFileSize(long bytes)
        {
            if (bytes == 0) return "0 B";

            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            var counter = 0;
            var number = (decimal)bytes;

            while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
            {
                number /= 1024;
                counter++;
            }

            return $"{number:n1} {suffixes[counter]}";
        }

        private bool HistoryFilter(object obj)
        {
            return obj is BackupItem item &&
                   (string.IsNullOrWhiteSpace(HistorySearchText) ||
                    item.FileName?.Contains(HistorySearchText, StringComparison.OrdinalIgnoreCase) == true);
        }

        private void ApplyHistoryFilter()
        {
            _historySentView?.Refresh();
            _historyErrorView?.Refresh();
        }

        private static string GetDisplayName(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return filePath;

            try
            {
                var directory = Path.GetDirectoryName(filePath);
                var folderName = string.IsNullOrEmpty(directory) ? string.Empty :
                    new DirectoryInfo(directory).Name;
                var prefix = folderName.Split('_').FirstOrDefault() ?? folderName;

                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var suffix = fileName.Split('_').LastOrDefault() ?? fileName;

                return string.IsNullOrEmpty(prefix) ? suffix : $"{prefix}_{suffix}";
            }
            catch
            {
                return Path.GetFileName(filePath) ?? filePath;
            }
        }

        private async Task UpdateStatusAsync(string status, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return;

            try
            {
                await Dispatcher.InvokeAsync(() => CurrentStatusText = status,
                    DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao atualizar status: {ex.Message}");
            }
        }

        private async Task CleanupAsync()
        {
            try
            {
                _updateTimer?.Stop();
                _stopwatch?.Stop();

                await _cancellationTokenSource.CancelAsync();

                if (_backup != null)
                {
                    _backup.FileUploaded -= OnFileUploaded;
                    _backup.FileUploadFailed -= OnFileUploadFailed;
                    _backup.CountersChangedDetailed -= OnCountersChanged;
                }

                // Pequena pausa para operações assíncronas terminarem
                await Task.Delay(50);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro durante cleanup: {ex.Message}");
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _uiUpdateSemaphore?.Dispose();
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
            set
            {
                if (_fileName != value)
                {
                    _fileName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string Size
        {
            get => _size;
            set
            {
                if (_size != value)
                {
                    _size = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (_errorMessage != value)
                {
                    _errorMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime? CompletedAt
        {
            get => _completedAt;
            set
            {
                if (_completedAt != value)
                {
                    _completedAt = value;
                    OnPropertyChanged();
                }
            }
        }

        public string FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string RemotePath
        {
            get => _remotePath;
            set
            {
                if (_remotePath != value)
                {
                    _remotePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DisplayText =>
            !string.IsNullOrEmpty(Size) ? $"{FileName} ({Size})" : FileName ?? string.Empty;

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    #endregion
}