// LineraCliRunner.cs
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using LineraOrchestrator.Models;

namespace LineraOrchestrator.Services
{
    public class LineraCliRunner
    {
        private readonly LineraConfig _config;

        public LineraCliRunner(LineraConfig config)
        {
            _config = config;
          
        }

        // vẫn dùng bash để start net up ở background (nohup), giữ nguyên phương thức này
        public int StartBackgroundProcess(string args)
        {
            string logFile;
            if (EnvironmentService.IsRunningInDocker())
            {
                logFile = Path.Combine("/build/logs", "linera_output.log");
            }
            else
            {
                logFile = "/tmp/linera_output.log";
            }

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"nohup {_config.LineraCliPath} {args} > {logFile} 2>&1 & echo $!\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            process.Start();

            string pidString = process.StandardOutput.ReadToEnd().Trim();
            if (int.TryParse(pidString, out int pid))
            {
                return pid;
            }
            throw new InvalidOperationException("Failed to get PID of background process.");
        }

        // ----- CHÍNH: chạy linera CLI mà KHÔNG qua shell, từng arg riêng -----
        public async Task<string> RunAndCaptureOutputAsync(params string[] args)
        {
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _config.LineraCliPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                    foreach (var kv in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>())
        {
            var key = kv.Key.ToString() ?? "";
            if (!key.StartsWith("LINERA_"))
                psi.Environment[key] = kv.Value?.ToString() ?? "";
        }

                foreach (var a in args)
                    psi.ArgumentList.Add(a);

                if (!string.IsNullOrEmpty(_config.LineraWallet))
                    psi.Environment["LINERA_WALLET"] = _config.LineraWallet;
                if (!string.IsNullOrEmpty(_config.LineraKeystore))
                    psi.Environment["LINERA_KEYSTORE"] = _config.LineraKeystore;
                if (!string.IsNullOrEmpty(_config.LineraStorage))
                    psi.Environment["LINERA_STORAGE"] = _config.LineraStorage;

                Console.WriteLine($"[CLI-DEBUG] Running command: {psi.FileName} {string.Join(' ', psi.ArgumentList)} (Attempt {attempt}/{maxRetries})");

                var process = new Process { StartInfo = psi };
                var sb = new StringBuilder();
                var errSb = new StringBuilder();

                process.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                string stdout = sb.ToString();
                string stderr = errSb.ToString();

                if (!string.IsNullOrWhiteSpace(stdout))
                    Console.WriteLine($"[CLI-STDOUT]\n{stdout}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.WriteLine($"[CLI-STDERR]\n{stderr}");
                Console.WriteLine($"[CLI-DEBUG] ExitCode: {process.ExitCode}");
                if (process.ExitCode == 0)
                {
                    return stdout.Trim();
                }

                // xử lý RocksDB lock
                if (stderr.Contains("LOCK: Resource temporarily unavailable"))
                {
                    Console.WriteLine($"[WARN] Attempt {attempt}/{maxRetries} failed due to RocksDB lock. Waiting 3s before retry...");
                    await Task.Delay(3000);
                    continue;
                }

                // lỗi khác thì throw ngay
                Console.WriteLine($"[ERROR] linera exited with code {process.ExitCode}");
                Console.WriteLine("STDERR:");
                Console.WriteLine(stderr);
                Console.WriteLine("STDOUT:");
                Console.WriteLine(stdout);
                throw new InvalidOperationException($"linera exited with code {process.ExitCode}: {stderr}");
            }
            throw new InvalidOperationException($"linera failed after {maxRetries} retries (RocksDB lock not released).");
        }
        #region User Cli, User Service

        // User Cli main function: each process for each user
        public async Task<string> RunWithOptionsAsync(string wallet, string keystore, string storage, params string[]? args)
        {
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _config.LineraCliPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Xây dựng command line với global options
                var allArgs = new List<string>();

                if (!string.IsNullOrEmpty(wallet))
                {
                    allArgs.Add("--wallet");
                    allArgs.Add(wallet);
                }
                if (!string.IsNullOrEmpty(keystore))
                {
                    allArgs.Add("--keystore");
                    allArgs.Add(keystore);
                }
                if (!string.IsNullOrEmpty(storage))
                {
                    allArgs.Add("--storage");
                    allArgs.Add(storage);
                }

                if (args != null && args.Length > 0)
                {
                    allArgs.AddRange(args);
                }

                foreach (var arg in allArgs)
                    psi.ArgumentList.Add(arg);
                Console.WriteLine($"[CLI-DEBUG] Running command: {psi.FileName} {string.Join(' ', allArgs)} (Attempt {attempt}/{maxRetries})");

                // QUAN TRỌNG: KHÔNG set environment variables LINERA_* toàn cục
                // Chỉ sử dụng command-line flags để tránh race conditions
                foreach (var kv in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>())
                {
                    var key = kv.Key.ToString() ?? "";
                    // KHÔNG truyền LINERA_* environment variables - dùng flags thay thế
                    if (!key.StartsWith("LINERA_"))
                    {
                        psi.Environment[key] = kv.Value?.ToString() ?? "";
                    }
                }

                var process = new Process { StartInfo = psi };
                var sb = new StringBuilder();
                var errSb = new StringBuilder();

                process.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Timeout → kill process → retry → nếu vẫn fail → CreateIndependentUserChainAsync gọi CleanupUserDirectory
                var processTask = process.WaitForExitAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));

                var completedTask = await Task.WhenAny(processTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    // Timeout
                    process.Kill();
                    Console.WriteLine($"[WARN] Process timed out after 30 seconds. Attempt {attempt}/{maxRetries}");

                    if (attempt < maxRetries)
                    {
                        await Task.Delay(3000);
                        continue;
                    }
                    else
                    {
                        throw new TimeoutException("linera command timed out after 30 seconds");
                    }
                }

                string stdout = sb.ToString();  
                string stderr = errSb.ToString();

                Console.WriteLine($"[USER-CLI-DEBUG] ExitCode: {process.ExitCode}");
                if (!string.IsNullOrWhiteSpace(stdout))
                    Console.WriteLine($"[USER-CLI-DEBUG] STDOUT:\n{stdout}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.WriteLine($"[USER-CLI-DEBUG] STDERR:\n{stderr}");

                if (process.ExitCode == 0)
                    return stdout.Trim();

                // Retry logic cho RocksDB lock
                if (stderr.Contains("LOCK: Resource temporarily unavailable"))
                {
                    Console.WriteLine($"[WARN] Attempt {attempt}/{maxRetries} failed due to RocksDB lock. Waiting 3s before retry...");
                    await Task.Delay(3000);
                    continue;
                }

                Console.WriteLine($"[ERROR] linera exited with code {process.ExitCode}");
                Console.WriteLine("STDERR:");
                Console.WriteLine(stderr);
                Console.WriteLine("STDOUT:");
                Console.WriteLine(stdout);
                throw new InvalidOperationException($"linera exited with code {process.ExitCode}: {stderr}");
            }
            throw new InvalidOperationException($"linera failed after {maxRetries} retries");
        }

        /// <summary>
        /// Start user background service trên port riêng
        /// </summary>
        public async Task<bool> StartUserBackgroundService(string wallet, string keystore, string storage, int port)
        {
            // XÁC ĐỊNH LOG FILE PATH
            string logFile;
            if (EnvironmentService.IsRunningInDocker())
            {
                // Docker: dùng /build/logs với tên dựa trên port
                logFile = Path.Combine("/build/logs", $"user_service_{port}.log");
            }
            else
            {
                // Local: dùng /tmp
                logFile = $"/tmp/linera_user_{port}.log";
            }

            // Đảm bảo thư mục log tồn tại
            var logDir = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            Console.WriteLine($"[USER-SERVICE] Starting service on port {port}");
            Console.WriteLine($"[USER-SERVICE] Log file: {logFile}");

            try
            {
                // Kill process đang listen port này
                Process.Start("fuser", $"-k {port}/tcp")?.WaitForExit(1000);
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[USER-SERVICE] Warning while killing port {port}: {ex.Message}");
            }

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"nohup {_config.LineraCliPath} --wallet '{wallet}' --keystore '{keystore}' --storage '{storage}' service --port {port} > {logFile} 2>&1 & echo $!\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Console.WriteLine($"[USER-SERVICE-CLI] Wallet: {wallet}");
            Console.WriteLine($"[USER-SERVICE-CLI] Keystore: {keystore}");
            Console.WriteLine($"[USER-SERVICE-CLI] Storage: {storage}");

            var process = new Process { StartInfo = psi };
            process.Start();

            bool isRunning = false;
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(2000);
                isRunning = IsUserServiceRunning(port);

                if (isRunning)
                {
                    Console.WriteLine($"[USER-SERVICE] Service is running on port {port} (attempt {i + 1}/5)");
                    break;
                }
                else
                {
                    Console.WriteLine($"[USER-SERVICE] Service not ready yet, waiting... (attempt {i + 1}/5)");
                }
            }

            if (!isRunning)
            {
                Console.WriteLine($"[USER-SERVICE] Failed to start service on port {port} after 5 attempts");
            }

            return isRunning;
        }
        public bool IsUserServiceRunning(int port)
        {
            try
            {
                using var client = new TcpClient();

                var task = client.ConnectAsync("localhost", port);
                if (task.Wait(TimeSpan.FromSeconds(2)))
                {
                    return client.Connected;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
        public bool IsPortInUse(int port)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect("localhost", port);
                return true; // port đang bận
            }
            catch
            {
                return false; // port rảnh
            }
        }
        #endregion

        #region CLI: Linera net up & linera service --port 8080

        // Keep existing background-start helper which reads /tmp/linera_output.log
        public async Task<LineraConfig> StartLineraNetUpInBackgroundAsync()
        {
            Console.WriteLine("[LOCAL] Starting linera net up in background...");

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"nohup {_config.LineraCliPath} net up > /tmp/linera_output.log 2>&1 & echo $!\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            process.Start();

            string pidStr = process.StandardOutput.ReadToEnd().Trim();
            if (!int.TryParse(pidStr, out int pid))
                throw new InvalidOperationException("Không lấy được PID của linera net up");

            _config.LineraNetPid = pid;

            string? wallet = null;
            string? keystore = null;
            string? storage = null;
            int retries = 30; // thử lại 30s
            while ((wallet == null || storage == null) && retries-- > 0)
            {
                if (File.Exists("/tmp/linera_output.log"))
                {
                    var lines = File.ReadAllLines("/tmp/linera_output.log");
                    foreach (var line in lines)
                    {
                        if (wallet == null && line.Contains("export LINERA_WALLET"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"export LINERA_WALLET=""([^""]+)""");
                            if (match.Success) wallet = match.Groups[1].Value;
                        }
                        if (keystore == null && line.Contains("export LINERA_KEYSTORE"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"export LINERA_KEYSTORE=""([^""]+)""");
                            if (match.Success) keystore = match.Groups[1].Value;
                        }
                        if (storage == null && line.Contains("export LINERA_STORAGE"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"export LINERA_STORAGE=""([^""]+)""");
                            if (match.Success) storage = match.Groups[1].Value;
                        }
                    }
                }
                await Task.Delay(500);
            }
            // save to Linera env
            _config.LineraWallet = wallet;
            _config.LineraKeystore = keystore;
            _config.LineraStorage = storage;

            Environment.SetEnvironmentVariable("LINERA_WALLET", wallet);
            Environment.SetEnvironmentVariable("LINERA_KEYSTORE", keystore);
            Environment.SetEnvironmentVariable("LINERA_STORAGE", storage);

            Console.WriteLine($"[ENV SET] LINERA_WALLET={wallet}");
            Console.WriteLine($"[ENV SET] LINERA_STORAGE={storage}");
            return _config;
        }

        // Automatic Orchestrator Linera Services
        public async Task<int> StartLineraServiceInBackgroundAsync(int port = 8080)
        {
            string logFile;
            if (EnvironmentService.IsRunningInDocker())
            {
                logFile = Path.Combine("/build/logs", "linera_publisher_service.log");
            }
            else
            {
                logFile = "/tmp/linera_service.log";
            }

            // ensure log file exist
            var logDir = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            if (File.Exists(logFile)) File.Delete(logFile);

            Console.WriteLine($"[PUBLISHER-SERVICE] Starting service on port {port}");
            Console.WriteLine($"[PUBLISHER-SERVICE] Log file: {logFile}");

            var psi = new ProcessStartInfo
            {
                FileName = _config.LineraCliPath, // trực tiếp linera executable
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // args: service --port {port}
            psi.ArgumentList.Add("service");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());

            if (!string.IsNullOrEmpty(_config.LineraWallet))
                psi.Environment["LINERA_WALLET"] = _config.LineraWallet;
            if (!string.IsNullOrEmpty(_config.LineraStorage))
                psi.Environment["LINERA_STORAGE"] = _config.LineraStorage;
            if (!string.IsNullOrEmpty(_config.LineraKeystore))
                psi.Environment["LINERA_KEYSTORE"] = _config.LineraKeystore;

            var process = new Process { StartInfo = psi };

            // redirect output to log file manually
            var stdoutSb = new StringBuilder();
            var stderrSb = new StringBuilder();
            process.OutputDataReceived += (s, e) => { if (e.Data != null) { stdoutSb.AppendLine(e.Data); File.AppendAllText(logFile, e.Data + Environment.NewLine); } };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) { stderrSb.AppendLine(e.Data); File.AppendAllText(logFile, e.Data + Environment.NewLine); } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // give a short moment for service to initialize and create GraphiQL lines
            await Task.Delay(500);

            // validate started
            if (process.HasExited)
            {
                var err = stderrSb.ToString();
                var outp = stdoutSb.ToString();
                Console.WriteLine("Linera service failed to start.");
                Console.WriteLine("STDERR: " + err + "\nSTDOUT: " + outp);
                throw new InvalidOperationException($"Linera Service exited immediately: {err}");
            }

            // return PID
            return process.Id;
        }

        #endregion

        #region Docker publisher ,user

        // Phương thức DÀNH RIÊNG CHO DOCKER - Đơn giản hóa tối đa
        public async Task<LineraConfig> StartLineraNetUpInBackgroundForDockerAsync()
        {
            Console.WriteLine("[DOCKER] Starting linera net up for Docker...");

            // XÁC ĐỊNH LOG FILE PATH
            var logFile = Path.Combine("/build/logs", "linera_net_up.log");
            var logDir = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            // CHẠY BACKGROUND VỚI nohup
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"nohup linera net up > {logFile} 2>&1 & echo $!\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            process.Start();

            string pidStr = await process.StandardOutput.ReadToEndAsync();
            if (!int.TryParse(pidStr.Trim(), out int pid))
                throw new InvalidOperationException("Failed to get PID");

            Console.WriteLine($"[DOCKER] Linera net up started with PID: {pid}");

            // ĐỢI VÀ ĐỌC OUTPUT ĐỂ LẤY ENV VARS
            await Task.Delay(5000); // Chờ 5s

            if (File.Exists(logFile))
            {
                var logContent = File.ReadAllText(logFile);
                Console.WriteLine($"[DOCKER] Log content: {logContent}");

                // PARSE ENV VARS TỪ LOG
                var lines = File.ReadAllLines(logFile);
                foreach (var line in lines)
                {
                    if (line.Contains("export LINERA_WALLET"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"export LINERA_WALLET=""([^""]+)""");
                        if (match.Success) _config.LineraWallet = match.Groups[1].Value;
                    }
                    if (line.Contains("export LINERA_KEYSTORE"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"export LINERA_KEYSTORE=""([^""]+)""");
                        if (match.Success) _config.LineraKeystore = match.Groups[1].Value;
                    }
                    if (line.Contains("export LINERA_STORAGE"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"export LINERA_STORAGE=""([^""]+)""");
                        if (match.Success) _config.LineraStorage = match.Groups[1].Value;
                    }
                }
            }

            Console.WriteLine($"[DOCKER SUCCESS] Linera net up running in background");
            return _config;
        }
        #endregion
    }
}
