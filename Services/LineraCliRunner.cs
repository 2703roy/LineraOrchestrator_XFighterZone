// LineraCliRunner.cs
using System.Diagnostics;
using System.Text;
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
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"nohup {_config.LineraCliPath} {args} > /tmp/linera_output.log 2>&1 & echo $!\"",
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
        // Sử dụng params string[] args: hãy gọi _cli.RunAndCaptureOutputAsync("publish-module", path1, path2, "--json-argument", json)
        // Thiết lập env cho tiến trình con trực tiếp: RunAndCaptureOutputAsync
        // Đảm bảo tiến trình linera spawn từ dotnet có đúng env,
        // vì export trong shell khác không tự lan sang tiến trình dotnet.
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
                    psi.Environment[kv.Key.ToString() ?? ""] = kv.Value?.ToString() ?? "";
                }

                foreach (var a in args)
                    psi.ArgumentList.Add(a);

                if (!string.IsNullOrEmpty(_config.LineraWallet))
                    psi.Environment["LINERA_WALLET"] = _config.LineraWallet;
                if (!string.IsNullOrEmpty(_config.LineraKeystore))
                    psi.Environment["LINERA_KEYSTORE"] = _config.LineraKeystore;
                if (!string.IsNullOrEmpty(_config.LineraStorage))
                    psi.Environment["LINERA_STORAGE"] = _config.LineraStorage;

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
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    return stdout.Trim();
                }

                // xử lý RocksDB lock
                if (stderr.Contains("LOCK: Resource temporarily unavailable"))
                {
                    Console.WriteLine($"[WARN] Attempt {attempt}/{maxRetries} failed due to RocksDB lock. Waiting 3s before retry...");
                    await Task.Delay(3000);
                    continue; // thử lại vòng lặp
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

        // Keep existing background-start helper which reads /tmp/linera_output.log
        public async Task<LineraConfig> StartLineraNetInBackgroundAsync()
        {
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
            return _config;
        }
        // Automatic Orchestrator Linera Services
        public async Task<int> StartLineraServiceInBackgroundAsync(int port = 8080)
        {
            string logFile = "/tmp/linera_service.log";
            if (File.Exists(logFile)) File.Delete(logFile);

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
                Console.WriteLine("STDERR:", err, "STDOUT:", outp);
                throw new InvalidOperationException($"Linera Service exited immediately: {err}");
            }

            // return PID
            return process.Id;
        }
    }
}
