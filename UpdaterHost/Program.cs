using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace UpdaterHost
{
    internal static class Program
    {
        private static string logPath;

        static int Main(string[] args)
        {
            var cfg = Args.Parse(args);
            logPath = cfg.LogPath ?? "updater.log";

            try
            {
                Log("=== UpdaterHost Iniciado ===");
                Log($"Args recebidos: {string.Join(" ", args)}");
                Log($"InstallDir: {cfg.InstallDir}");
                Log($"StagingDir: {cfg.StagingDir}");
                Log($"AppExeName: {cfg.AppExeName}");
                Log($"ParentPid: {cfg.ParentPid}");

                // Mostra progresso no console
                Console.WriteLine("UpdaterHost - Atualizador leituraWPF");
                Console.WriteLine("=====================================");
                Console.WriteLine("Aguardando aplicação fechar...");

                if (cfg.ParentPid > 0)
                {
                    Log($"Aguardando PID {cfg.ParentPid} fechar...");
                    WaitForPidExit(cfg.ParentPid, cfg.AppExeName);
                    Log("Processo pai fechado.");
                }

                Console.WriteLine("Aplicando atualização...");
                ApplyUpdate(cfg);

                Console.WriteLine("Reiniciando aplicação...");
                Relaunch(cfg);

                if (!string.IsNullOrEmpty(cfg.SuccessFlag))
                {
                    File.WriteAllText(cfg.SuccessFlag, $"{cfg.OldVersion}|{cfg.NewVersion}");
                    Log("Arquivo de sucesso criado.");
                }

                Console.WriteLine("Atualização concluída com sucesso!");
                Log("=== UpdaterHost Finalizado com Sucesso ===");

                // Aguarda um pouco antes de fechar para mostrar a mensagem
                Thread.Sleep(2000);
                return 0;
            }
            catch (Exception ex)
            {
                var errorMsg = $"ERRO: {ex.Message}\n\nDetalhes:\n{ex}";
                Log($"ERRO CRÍTICO: {errorMsg}");

                Console.WriteLine("\n" + new string('=', 50));
                Console.WriteLine("ERRO NA ATUALIZAÇÃO:");
                Console.WriteLine(new string('=', 50));
                Console.WriteLine(errorMsg);
                Console.WriteLine(new string('=', 50));
                Console.WriteLine("\nPressione qualquer tecla para fechar...");
                Console.ReadKey();

                if (!string.IsNullOrEmpty(cfg.ErrorFlag))
                    File.WriteAllText(cfg.ErrorFlag, ex.Message);

                return 1;
            }
        }

        private static void ApplyUpdate(Args cfg)
        {
            Log("Iniciando ApplyUpdate...");

            if (!Directory.Exists(cfg.StagingDir))
                throw new InvalidOperationException($"Diretório de staging não encontrado: {cfg.StagingDir}");

            if (!Directory.Exists(cfg.InstallDir))
            {
                Log($"Criando diretório de instalação: {cfg.InstallDir}");
                Directory.CreateDirectory(cfg.InstallDir);
            }

            // Lista arquivos no staging para debug
            var stagingFiles = Directory.GetFiles(cfg.StagingDir, "*", SearchOption.AllDirectories);
            Log($"Arquivos no staging ({stagingFiles.Length}):");
            foreach (var file in stagingFiles)
            {
                Log($"  - {file}");
            }

            // Cria diretórios
            var stagingDirs = Directory.GetDirectories(cfg.StagingDir, "*", SearchOption.AllDirectories);
            foreach (var dir in stagingDirs)
            {
                var rel = GetRelativePath(cfg.StagingDir, dir);
                var destDir = Path.Combine(cfg.InstallDir, rel);
                if (!Directory.Exists(destDir))
                {
                    Log($"Criando diretório: {destDir}");
                    Directory.CreateDirectory(destDir);
                }
            }

            // Copia arquivos
            int copiedFiles = 0;
            int totalFiles = stagingFiles.Length;

            Console.WriteLine($"Copiando {totalFiles} arquivo(s)...");

            foreach (var file in stagingFiles)
            {
                var fileName = Path.GetFileName(file);

                // Pula o próprio UpdaterHost
                if (string.Equals(fileName, "UpdaterHost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Pulando arquivo: {fileName}");
                    continue;
                }

                var rel = GetRelativePath(cfg.StagingDir, file);
                var dest = Path.Combine(cfg.InstallDir, rel);
                var destDir = Path.GetDirectoryName(dest);

                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                // Tenta copiar com retry
                bool copied = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        if (File.Exists(dest))
                        {
                            // Tenta remover atributos readonly se existir
                            try
                            {
                                File.SetAttributes(dest, FileAttributes.Normal);
                            }
                            catch { } // Ignora erros ao remover readonly
                        }

                        Log($"Copiando ({attempt}/3): {file} -> {dest}");
                        File.Copy(file, dest, true);
                        copiedFiles++;
                        copied = true;

                        // Mostra progresso no console
                        Console.Write($"\rProgresso: {copiedFiles}/{totalFiles} arquivos copiados");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Falha na tentativa {attempt}: {ex.Message}");
                        if (attempt < 3)
                        {
                            Thread.Sleep(1000);
                        }
                        else
                        {
                            throw new InvalidOperationException($"Falha ao copiar arquivo '{fileName}' após 3 tentativas: {ex.Message}", ex);
                        }
                    }
                }
            }

            Console.WriteLine(); // Nova linha após o progresso
            Log($"Total de arquivos copiados: {copiedFiles}");

            // Remove diretório de staging
            try
            {
                Log($"Removendo diretório de staging: {cfg.StagingDir}");
                Directory.Delete(cfg.StagingDir, true);
                Log("Diretório de staging removido.");
            }
            catch (Exception ex)
            {
                Log($"Aviso: Não foi possível remover staging: {ex.Message}");
                // Não é crítico, apenas avisa
            }
        }

        private static void Relaunch(Args cfg)
        {
            var exe = Path.Combine(cfg.InstallDir, cfg.AppExeName);
            Log($"Tentando reiniciar: {exe}");

            if (!File.Exists(exe))
            {
                throw new FileNotFoundException($"Executável não encontrado: {exe}");
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = cfg.InstallDir,
                    UseShellExecute = true // Mudança: usar shell execute para executáveis
                };

                var process = Process.Start(startInfo);
                Log($"Aplicação reiniciada. PID: {process?.Id}");

                // Aguarda um pouco para verificar se o processo iniciou corretamente
                Thread.Sleep(2000);

                if (process != null && process.HasExited)
                {
                    Log($"AVISO: Processo reiniciado saiu imediatamente com código: {process.ExitCode}");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Falha ao reiniciar aplicação: {ex.Message}", ex);
            }
        }

        private static void WaitForPidExit(int pid, string exeName)
        {
            var name = Path.GetFileNameWithoutExtension(exeName);
            Log($"Aguardando processo '{name}' (PID: {pid}) finalizar...");

            try
            {
                // Primeiro tenta pelo PID específico
                var targetProcess = Process.GetProcessById(pid);
                if (targetProcess != null && !targetProcess.HasExited)
                {
                    Log($"Processo encontrado pelo PID. Aguardando...");

                    // Aguarda até 60 segundos para o processo fechar naturalmente
                    if (!targetProcess.WaitForExit(60000))
                    {
                        Log("Timeout aguardando processo fechar. Tentando finalizar...");
                        try
                        {
                            targetProcess.CloseMainWindow();
                            if (!targetProcess.WaitForExit(10000))
                            {
                                Log("Processo não respondeu ao CloseMainWindow. Forçando encerramento...");
                                targetProcess.Kill();
                                targetProcess.WaitForExit(5000);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Erro ao finalizar processo: {ex.Message}");
                        }
                    }
                    Log("Processo pai finalizado.");
                }
            }
            catch (ArgumentException)
            {
                Log("Processo já não existe (pelo PID).");
            }
            catch (Exception ex)
            {
                Log($"Erro ao aguardar PID específico: {ex.Message}");
            }

            // Verificação adicional por nome do processo
            int attempts = 0;
            while (attempts < 15) // Max 15 tentativas
            {
                try
                {
                    var processes = Process.GetProcessesByName(name);
                    var stillRunning = processes.Any(p => p.Id == pid);

                    if (!stillRunning)
                    {
                        Log("Processo confirmado como finalizado.");
                        break;
                    }

                    Log($"Processo ainda ativo, aguardando... (tentativa {attempts + 1}/15)");
                    Console.Write($"\rAguardando processo fechar... {attempts + 1}/15");
                    Thread.Sleep(2000); // Aguarda 2 segundos entre tentativas
                    attempts++;
                }
                catch (Exception ex)
                {
                    Log($"Erro ao verificar processos: {ex.Message}");
                    break;
                }
            }

            Console.WriteLine(); // Nova linha após o progresso

            if (attempts >= 15)
            {
                Log("AVISO: Continuando atualização mesmo com processo ainda ativo.");
                Console.WriteLine("AVISO: Processo principal ainda ativo, mas continuando...");
            }
        }

        private static string GetRelativePath(string basePath, string fullPath)
        {
            // Normaliza os caminhos
            basePath = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar);
            fullPath = Path.GetFullPath(fullPath);

            if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("O caminho completo não está dentro do caminho base");
            }

            return fullPath.Substring(basePath.Length).TrimStart(Path.DirectorySeparatorChar);
        }

        private static void Log(string message)
        {
            var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";

            try
            {
                File.AppendAllText(logPath, logEntry + Environment.NewLine);
                // Também mostra no console para debug
                if (Debugger.IsAttached)
                {
                    Console.WriteLine($"[LOG] {message}");
                }
            }
            catch
            {
                // Se não conseguir logar em arquivo, pelo menos tenta no console
                Console.WriteLine($"[LOG] {message}");
            }
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