// UpdaterHost/Program.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace UpdaterHost
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            var cfg = Args.Parse(args);
            try
            {
                if (cfg.ParentPid > 0)
                    WaitForPidExit(cfg.ParentPid, cfg.AppExeName);

                ApplyUpdate(cfg);

                Relaunch(cfg);

                if (!string.IsNullOrEmpty(cfg.SuccessFlag))
                    File.WriteAllText(cfg.SuccessFlag, $"{cfg.OldVersion}|{cfg.NewVersion}");

                return 0;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(cfg.ErrorFlag))
                    File.WriteAllText(cfg.ErrorFlag, ex.Message);
                return 1;
            }
        }

        private static void ApplyUpdate(Args cfg)
        {
            if (!Directory.Exists(cfg.StagingDir))
                throw new InvalidOperationException("Staging não encontrado.");
            if (!Directory.Exists(cfg.InstallDir))
                Directory.CreateDirectory(cfg.InstallDir);

            foreach (var dir in Directory.GetDirectories(cfg.StagingDir, "*", SearchOption.AllDirectories))
            {
                var rel = dir.Substring(cfg.StagingDir.Length).TrimStart(Path.DirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(cfg.InstallDir, rel));
            }

            foreach (var file in Directory.GetFiles(cfg.StagingDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, "UpdaterHost.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                var rel = file.Substring(cfg.StagingDir.Length).TrimStart(Path.DirectorySeparatorChar);
                var dest = Path.Combine(cfg.InstallDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(file, dest, true);
            }

            Directory.Delete(cfg.StagingDir, true);
        }

        private static void Relaunch(Args cfg)
        {
            var exe = Path.Combine(cfg.InstallDir, cfg.AppExeName);
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = cfg.InstallDir,
                    UseShellExecute = false
                });
            }
        }

        private static void WaitForPidExit(int pid, string exeName)
        {
            var name = Path.GetFileNameWithoutExtension(exeName);
            try
            {
                while (Process.GetProcessesByName(name).Any(p => p.Id == pid))
                    System.Threading.Thread.Sleep(300);
            }
            catch { }
        }
    }

    internal class Args
    {
        public string InstallDir;
        public string StagingDir;
        public string AppExeName;
        public int ParentPid;
        public string SuccessFlag;
        public string ErrorFlag;
        public string LogPath;
        public string OldVersion;
        public string NewVersion;

        public static Args Parse(string[] a)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < a.Length - 1; i += 2)
            {
                if (a[i].StartsWith("--"))
                    d[a[i]] = a[i + 1];
            }

            int.TryParse(d.GetValueOrDefault("--pid"), out var pid);

            return new Args
            {
                InstallDir = d.GetValueOrDefault("--install"),
                StagingDir = d.GetValueOrDefault("--staging"),
                AppExeName = d.GetValueOrDefault("--exe"),
                ParentPid = pid,
                SuccessFlag = d.GetValueOrDefault("--success"),
                ErrorFlag = d.GetValueOrDefault("--error"),
                LogPath = d.GetValueOrDefault("--log"),
                OldVersion = d.GetValueOrDefault("--old"),
                NewVersion = d.GetValueOrDefault("--new")
            };
        }
    }
}

