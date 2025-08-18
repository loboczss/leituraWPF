using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace leituraWPF.Services
{
    public sealed class DownloadIndexService
    {
        private readonly string _indexPath;
        private Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        public DownloadIndexService(string downloadsDir)
        {
            _indexPath = Path.Combine(downloadsDir, ".index.json");
            Load();
        }

        public bool ShouldDownload(string driveItemId, string eTag, bool skipUnchanged)
        {
            if (!skipUnchanged) return true;
            if (string.IsNullOrWhiteSpace(driveItemId) || string.IsNullOrWhiteSpace(eTag)) return true;

            if (_map.TryGetValue(driveItemId, out var oldTag))
                return !string.Equals(oldTag, eTag, StringComparison.Ordinal);

            return true;
        }

        public void Record(string driveItemId, string eTag)
        {
            if (string.IsNullOrWhiteSpace(driveItemId) || string.IsNullOrWhiteSpace(eTag)) return;
            _map[driveItemId] = eTag;
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_indexPath, json);
            }
            catch { /* não falhar por causa do índice */ }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_indexPath))
                {
                    var json = File.ReadAllText(_indexPath);
                    _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                           ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
