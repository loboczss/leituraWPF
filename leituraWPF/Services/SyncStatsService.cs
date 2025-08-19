using System.IO;
using System.Text.Json;

namespace leituraWPF.Services
{
    public class SyncStats
    {
        public int Uploaded { get; set; }
        public int Downloaded { get; set; }
    }

    public static class SyncStatsService
    {
        private static readonly string _path = Path.Combine(AppContext.BaseDirectory, "syncstats.json");

        public static SyncStats Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    return JsonSerializer.Deserialize<SyncStats>(json) ?? new SyncStats();
                }
            }
            catch { }
            return new SyncStats();
        }

        public static void Save(SyncStats stats)
        {
            try
            {
                var json = JsonSerializer.Serialize(stats);
                File.WriteAllText(_path, json);
            }
            catch { }
        }
    }
}
