// UpdaterHost/Program.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace UpdaterHost
{
    internal static class Program
    {
        // Janela de progresso exibida em thread separada
        private static ProgressWindow? _window;

        [STAThread]
        private static int Main(string[] args)
        {
            var cfg = Args.Parse(args);
            var log = new FileLogger(cfg.LogPath);

            // Inicia UI em outra thread para não bloquear o processo de atualização
            var uiThread = new Thread(() =>
            {
                _window = new ProgressWindow();
                ProgressReporter.ProgressChanged += msg =>
                    _window.Dispatcher.Invoke(() => _window.SetStatus(msg));
                var app = new Application();
                app.Run(_window);
            });
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();

            // Aguarda janela ser criada
            while (_window == null || !_window.IsLoaded)
                Thread.Sleep(50);

            int result = RunUpdate(args, cfg, log);

            // Encerra UI
            _window.Dispatcher.Invoke(() => _window.Close());
            uiThread.Join();

            return result;
        }

        private static int RunUpdate(string[] args, Args cfg, FileLogger log)
        {
            try
            {
                log.Info("=== UpdaterHost iniciado (gerenciado, sem shell) ===");
                log.Info("Config: " + cfg);

                // Elevação se necessário
                if (!CanWrite(cfg.InstallDir))
                {
                    log.Warn("Sem permissão de escrita. Tentando elevar...");
                    if (!IsAdministrator())
                    {
                        RelaunchAsAdmin(args);
                        return 0;
                    }
                }

                // Espera o app principal fechar (por PID ou nome)
                if (cfg.ParentPid > 0)
                {
                    ProgressReporter.Report($"Aguardando processo PID {cfg.ParentPid} encerrar...");
                    log.Info($"Aguardando processo PID {cfg.ParentPid} encerrar...");
                    WaitForPidExit(cfg.ParentPid, cfg.AppExeName, log, 120);
                }
                else
                {
                    ProgressReporter.Report($"Aguardando '{cfg.AppExeName}' encerrar...");
                    log.Info($"Aguardando '{cfg.AppExeName}' encerrar...");
                    WaitForProcessNameExit(cfg.AppExeName, log, 120);
                }

                // Valida staging/instalação
                if (!Directory.Exists(cfg.StagingDir))
                    throw new InvalidOperationException("Staging inexistente.");
                if (!Directory.Exists(cfg.InstallDir))
                    throw new InvalidOperationException("Diretório de instalação inexistente.");

                // Prepara backup
                ProgressReporter.Report("Criando backup...");
                var backupDir = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}");
                log.Info($"Criando backup: {backupDir}");
                Directory.CreateDirectory(backupDir);
                CopyDirectory(cfg.InstallDir, backupDir, log, excludeNames: new[] { "update_success.flag", "update_error.flag", "update.log" });

                try
                {
                    // Aplica a atualização
                    ProgressReporter.Report("Aplicando atualização...");
                    log.Info("Aplicando atualização (cópia recursiva com overwrite)...");
                    ApplyUpdate(cfg, log);

                    // Validação pós-instalação
                    var exePath = Path.Combine(cfg.InstallDir, cfg.AppExeName);
                    if (!File.Exists(exePath))
                        throw new InvalidOperationException($"Executável principal não encontrado após atualização: {exePath}");

                    // Cria atalho se indicado
                    if (cfg.CreateShortcut)
                    {
                        try
                        {
                            ProgressReporter.Report("Criando atalho...");
                            CreateShortcutOnDesktop(cfg, exePath, log);
                        }
                        catch (Exception ex)
                        {
                            log.Warn("Falha ao criar atalho: " + ex.Message);
                        }
                    }

                    // Marca sucesso
                    var flagContent = (!string.IsNullOrWhiteSpace(cfg.OldVersion) && !string.IsNullOrWhiteSpace(cfg.NewVersion))
                        ? $"{cfg.OldVersion}|{cfg.NewVersion}"
                        : "ok";
                    WriteText(cfg.SuccessFlagPath, flagContent);
                    TryDeleteFile(cfg.ErrorFlagPath, log);

                    log.Info("Atualização concluída com sucesso.");
                    TryDeleteDirectory(backupDir, log);
                }
                catch (Exception exApply)
                {
                    log.Error("Erro ao aplicar atualização: " + exApply);

                    // Marca erro
                    WriteText(cfg.ErrorFlagPath, exApply.Message);

                    // Rollback
                    try
                    {
                        log.Warn("Iniciando ROLLBACK...");
                        RollbackFromBackup(cfg.InstallDir, backupDir, log);
                        log.Warn("Rollback concluído.");
                    }
                    catch (Exception rbEx)
                    {
                        log.Error("Rollback falhou: " + rbEx);
                    }
                }
                finally
                {
                    // Limpeza do staging
                    TryDeleteDirectory(cfg.StagingDir, log);
                }

                // Relança app
                ProgressReporter.Report("Relançando aplicação...");
                var relaunch = Path.Combine(cfg.InstallDir, cfg.AppExeName);
                if (File.Exists(relaunch))
                {
                    log.Info("Relançando aplicação...");
                    var psi = new ProcessStartInfo
                    {
                        FileName = relaunch,
                        UseShellExecute = false,
                        WorkingDirectory = cfg.InstallDir
                    };
                    Process.Start(psi);
                }
                else
                {
                    log.Warn("Executável não encontrado para relançar.");
                }

                ProgressReporter.Report("Finalizado.");
                log.Info("UpdaterHost finalizado.");
                return 0;
            }
            catch (Exception ex)
            {
                try { WriteText(cfg.ErrorFlagPath, ex.Message); } catch { }
                try
                {
                    var logFallback = new FileLogger(cfg.LogPath);
                    logFallback.Error("Falha fatal no UpdaterHost: " + ex);
                }
                catch { }
                return 1;
            }
        }

        // ====== Aplicação da atualização ======
        private static void ApplyUpdate(Args cfg, FileLogger log)
        {
            var installFiles = ListRelativeFiles(cfg.InstallDir);
            var stagingFiles = ListRelativeFiles(cfg.StagingDir);

            // Remove lixo antigo (exceto UpdaterHost.exe)
            var toDelete = installFiles.Except(stagingFiles, StringComparer.OrdinalIgnoreCase)
                .Where(rel => !string.Equals(Path.GetFileName(rel), "UpdaterHost.exe", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var rel in toDelete)
            {
                var path = Path.Combine(cfg.InstallDir, rel);
                TryDeleteFile(path, log);
            }

            // Copia/atualiza
            foreach (var rel in stagingFiles)
            {
                if (string.Equals(Path.GetFileName(rel), "UpdaterHost.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                var src = Path.Combine(cfg.StagingDir, rel);
                var dst = Path.Combine(cfg.InstallDir, rel);

                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                CopyFile(src, dst, overwrite: true, log);
            }
        }

        private static List<string> ListRelativeFiles(string root)
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(p => p.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length)
                               .TrimStart(Path.DirectorySeparatorChar))
                .ToList();
        }

        private static void CopyDirectory(string sourceDir, string destDir, FileLogger log, IEnumerable<string> excludeNames = null)
        {
            excludeNames = excludeNames ?? Array.Empty<string>();
            Directory.CreateDirectory(destDir);

            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = dir.Substring(sourceDir.TrimEnd(Path.DirectorySeparatorChar).Length)
                             .TrimStart(Path.DirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destDir, rel));
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (excludeNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                var rel = file.Substring(sourceDir.TrimEnd(Path.DirectorySeparatorChar).Length)
                              .TrimStart(Path.DirectorySeparatorChar);
                var dst = Path.Combine(destDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                CopyFile(file, dst, overwrite: true, log);
            }
        }

        private static void RollbackFromBackup(string installDir, string backupDir, FileLogger log)
        {
            if (!Directory.Exists(backupDir))
                throw new InvalidOperationException("Backup ausente para rollback.");

            foreach (var file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, "UpdaterHost.exe", StringComparison.OrdinalIgnoreCase)) continue;
                TryDeleteFile(file, log);
            }
            foreach (var dir in Directory.GetDirectories(installDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                TryDeleteDirectory(dir, log);
            }

            CopyDirectory(backupDir, installDir, log);
        }

        // ====== IO helpers ======
        private static void CopyFile(string src, string dst, bool overwrite, FileLogger log)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (File.Exists(dst))
                    {
                        var attr = File.GetAttributes(dst);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(dst, attr & ~FileAttributes.ReadOnly);
                    }
                    File.Copy(src, dst, overwrite);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
            }
            using (var fsIn = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                using (var fsOut = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fsIn.CopyTo(fsOut, 1024 * 1024);
                }
            }
        }

        private static void TryDeleteFile(string path, FileLogger log = null)
        {
            try
            {
                if (File.Exists(path))
                {
                    var a = File.GetAttributes(path);
                    if ((a & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
                    File.Delete(path);
                }
            }
            catch (Exception ex) { log?.Warn($"Não foi possível excluir '{path}': {ex.Message}"); }
        }

        private static void TryDeleteDirectory(string path, FileLogger log = null)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception ex) { log?.Warn($"Não foi possível remover '{path}': {ex.Message}"); }
        }

        private static void WriteText(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        // ====== Esperas/Permissões ======
        private static void WaitForPidExit(int pid, string exeName, FileLogger log, int timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            var exeOnly = Path.GetFileName(exeName);
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    try
                    {
                        var runningName = Path.GetFileName(p.MainModule.FileName);
                        if (!string.Equals(runningName, exeOnly, StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                    catch { /* acesso negado em alguns casos; só espera */ }
                }
                catch
                {
                    // processo já terminou
                    break;
                }

                Thread.Sleep(300);

                if (!Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeOnly)).Any())
                    break;
            }
        }

        private static void WaitForProcessNameExit(string exeName, FileLogger log, int timeoutSeconds)
        {
            var name = Path.GetFileNameWithoutExtension(exeName);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                if (!Process.GetProcessesByName(name).Any())
                    return;
                Thread.Sleep(300);
            }
            log.Warn("Timeout aguardando processo por nome encerrar.");
        }

        private static bool CanWrite(string dir)
        {
            try { var p = Path.Combine(dir, $"w_{Guid.NewGuid():N}.tmp"); File.WriteAllText(p, "x"); File.Delete(p); return true; }
            catch { return false; }
        }

        private static bool IsAdministrator()
        {
            try
            {
                var wi = System.Security.Principal.WindowsIdentity.GetCurrent();
                var wp = new System.Security.Principal.WindowsPrincipal(wi);
                return wp.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static void RelaunchAsAdmin(string[] args)
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(" ", args.Select(QuoteIfNeeded)),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            Process.Start(psi);
        }

        private static string QuoteIfNeeded(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return s.IndexOf(' ') >= 0 ? ("\"" + s.Replace("\"", "\\\"") + "\"") : s;
        }

        // ====== Criar atalho (COM interop forte; sem dynamic) ======
        private static void CreateShortcutOnDesktop(Args cfg, string exePath, FileLogger log)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnk = Path.Combine(desktop, cfg.ShortcutName);

                var shellLink = (IShellLinkW)new ShellLink();
                shellLink.SetPath(exePath);
                shellLink.SetWorkingDirectory(cfg.InstallDir);
                shellLink.SetIconLocation(exePath, 0);

                var pf = (IPersistFile)shellLink;
                pf.Save(lnk, true);

                log.Info($"Atalho criado: {lnk}");
            }
            catch (Exception ex)
            {
                log.Warn("Falha ao criar atalho (IShellLinkW): " + ex.Message);
            }
        }
    }

    internal static class ProgressReporter
    {
        public static event Action<string> ProgressChanged;
        public static void Report(string message)
            => ProgressChanged?.Invoke(message);
    }

    // ====== argumentos/DTOs ======
    internal sealed class Args
    {
        public string InstallDir { get; private set; }
        public string StagingDir { get; private set; }
        public string AppExeName { get; private set; }
        public int ParentPid { get; private set; }
        public string SuccessFlagPath { get; private set; }
        public string ErrorFlagPath { get; private set; }
        public string LogPath { get; private set; }
        public bool CreateShortcut { get; private set; }
        public string ShortcutName { get; private set; }
        public string OldVersion { get; private set; }
        public string NewVersion { get; private set; }

        public static Args Parse(string[] a)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string last = null;
            foreach (var s in a)
            {
                if (s.StartsWith("--")) { last = s; d[last] = "true"; }
                else if (last != null) { d[last] = s; last = null; }
            }

            return new Args
            {
                InstallDir = d.TryGet("--install"),
                StagingDir = d.TryGet("--staging"),
                AppExeName = d.TryGet("--exe"),
                ParentPid = d.TryGetInt("--pid"),
                SuccessFlagPath = d.TryGet("--success"),
                ErrorFlagPath = d.TryGet("--error"),
                LogPath = d.TryGet("--log"),
                CreateShortcut = d.TryGetBool("--shortcut"),
                ShortcutName = d.TryGet("--shortcutName", "CompillerLog.lnk"),
                OldVersion = d.TryGet("--oldVersion"),
                NewVersion = d.TryGet("--newVersion")
            };
        }

        public override string ToString()
        {
            return $"Install='{InstallDir}', Staging='{StagingDir}', Exe='{AppExeName}', Pid={ParentPid}, " +
                   $"Success='{SuccessFlagPath}', Error='{ErrorFlagPath}', Log='{LogPath}', Shortcut={CreateShortcut}, ShortcutName='{ShortcutName}', " +
                   $"OldVersion='{OldVersion}', NewVersion='{NewVersion}'";
        }
    }

    internal static class ArgExt
    {
        public static string TryGet(this Dictionary<string, string> d, string k, string def = "")
            => d.ContainsKey(k) ? d[k] : def;

        public static int TryGetInt(this Dictionary<string, string> d, string k)
        {
            int x; return int.TryParse(d.TryGet(k), out x) ? x : 0;
        }
        public static bool TryGetBool(this Dictionary<string, string> d, string k)
        {
            bool b; return bool.TryParse(d.TryGet(k), out b) ? b : false;
        }
    }

    internal sealed class FileLogger
    {
        private readonly string _path;
        public FileLogger(string path)
        {
            _path = string.IsNullOrWhiteSpace(path)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update.log")
                : path;
            try { Directory.CreateDirectory(Path.GetDirectoryName(_path)); } catch { }
        }

        public void Info(string m) => Write("INFO", m);
        public void Warn(string m) => Write("WARN", m);
        public void Error(string m) => Write("ERROR", m);

        private void Write(string level, string msg)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}";
            try { File.AppendAllText(_path, line + Environment.NewLine); } catch { }
        }
    }

    // ====== COM interop para atalho ======
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        int GetIDList(out IntPtr ppidl);
        int SetIDList(IntPtr pidl);
        int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        int GetHotkey(out short pwHotkey);
        int SetHotkey(short wHotkey);
        int GetShowCmd(out int piShowCmd);
        int SetShowCmd(int iShowCmd);
        int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        int Resolve(IntPtr hwnd, int fFlags);
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0000010B-0000-0000-C000-000000000046")]
    internal interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}

