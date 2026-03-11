using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV2.Core;
using OnionHopV2.Core.Platform;
using OnionHopV2.Core.Platform.MacOS;

namespace OnionHopV2.Core.Services;

internal sealed class VpnService : IDisposable
{
    private readonly Action<string> _log;
    private readonly object _processLock = new();
    private Process? _process;
    private int? _lastExitCode;
    private bool _disposed;
    private string? _xrayTunInterface;
    private CancellationTokenSource? _macPrivilegedMonitorCts;
    private string? _macPrivilegedSessionDir;
    private string? _macPrivilegedRunnerScriptPath;
    private string? _macPrivilegedLogPath;
    private string? _macPrivilegedSupervisorPidPath;
    private string? _macPrivilegedCorePidPath;
    private string? _macPrivilegedExitCodePath;
    private string? _macPrivilegedCoreMode;
    private string? _macPrivilegedTunInterface;
    private long _macPrivilegedLogOffset;

    public VpnService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event DataReceivedEventHandler? OutputReceived;
    public event Action<string>? OutputLineReceived;
    public event EventHandler? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_processLock)
            {
                if (_process != null)
                {
                    return !_process.HasExited;
                }
            }

            return IsMacPrivilegedTunnelRunning();
        }
    }

    public int? ExitCode { get { lock (_processLock) { return _lastExitCode ?? (_process?.HasExited == true ? _process.ExitCode : null); } } }

    public async Task StartAsync(VpnLaunchConfig config, CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VpnService));
        }

        Stop();
        _lastExitCode = null;
        token.ThrowIfCancellationRequested();

        var vpnCoreMode = NormalizeTunCoreMode(config.VpnCoreMode);
        var vpnCorePath = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? config.XrayPath
            : config.SingBoxPath;
        var vpnCoreLabel = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? "xray"
            : "sing-box";

        if (!File.Exists(vpnCorePath))
        {
            throw new FileNotFoundException($"VPN component missing: vpn/{Path.GetFileName(vpnCorePath)}", vpnCorePath);
        }

        if (PlatformHelper.NeedsWintun && (string.IsNullOrWhiteSpace(config.WintunPath) || !File.Exists(config.WintunPath)))
        {
            throw new FileNotFoundException("VPN component missing: vpn/wintun.dll", config.WintunPath);
        }

        var workDir = Path.GetDirectoryName(vpnCorePath) ?? AppContext.BaseDirectory;
        var configDir = Path.Combine(Path.GetTempPath(), "OnionHop", vpnCoreLabel);
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, $"{vpnCoreLabel}.json");

        var configJson = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? XrayConfigBuilder.BuildJson(
                config.HybridRouting,
                config.SecureDns,
                config.SocksPort,
                config.TorAppProcessNames,
                config.BypassAppProcessNames,
                config.RouteAllWebTrafficThroughTor,
                config.BlockQuicForTorApps,
                config.DohServer,
                config.DohServerPort,
                config.DohPath,
                config.TunMtu)
            : VpnConfigBuilder.BuildJson(
                config.HybridRouting,
                config.SecureDns,
                config.SocksPort,
                config.TorAppProcessNames,
                config.BypassAppProcessNames,
                config.RouteAllWebTrafficThroughTor,
                config.BlockQuicForTorApps,
                config.DohServer,
                config.DohServerPort,
                config.DohPath,
                config.TunStack,
                config.TunMtu,
                config.TunStrictRoute);
        await File.WriteAllTextAsync(configPath, configJson, token);

        _log($"Starting {vpnCoreLabel} with config: {configPath}");
        _log($"{vpnCoreLabel} config:\n{configJson}");

        // Wait longer for xray (it's slower to initialize than sing-box, especially with bridges).
        var startupDelay = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? 2000
            : 750;

        if (OperatingSystem.IsMacOS() && !PlatformHelper.IsAdministrator())
        {
            await StartMacPrivilegedTunnelAsync(
                vpnCoreMode,
                vpnCorePath,
                vpnCoreLabel,
                workDir,
                configPath,
                startupDelay,
                token).ConfigureAwait(false);
            return;
        }

        var psi = new ProcessStartInfo(vpnCorePath, $"run -c \"{configPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir
        };

        // Capture startup output for crash diagnostics.
        var startupLines = new List<string>();
        void CaptureStartupOutput(object s, DataReceivedEventArgs args)
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                lock (startupLines)
                {
                    startupLines.Add(args.Data);
                }
            }
        }

        _process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _process.Exited += HandleExited;
        _process.OutputDataReceived += HandleOutput;
        _process.OutputDataReceived += CaptureStartupOutput;
        _process.ErrorDataReceived += HandleOutput;
        _process.ErrorDataReceived += CaptureStartupOutput;

        if (!_process.Start())
        {
            throw new InvalidOperationException($"Unable to launch {vpnCoreLabel}.");
        }

        config.ProcessStarted?.Invoke(_process);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.Delay(startupDelay, token);

        // Detach startup capture.
        try
        {
            _process.OutputDataReceived -= CaptureStartupOutput;
            _process.ErrorDataReceived -= CaptureStartupOutput;
        }
        catch
        {
        }

        if (_process.HasExited)
        {
            string crashDetail;
            lock (startupLines)
            {
                crashDetail = startupLines.Count > 0
                    ? string.Join("\n", startupLines)
                    : "(no output captured)";
            }

            _log($"{vpnCoreLabel} crash output:\n{crashDetail}");
            throw new InvalidOperationException(
                $"{vpnCoreLabel} exited unexpectedly during startup (exit code {_process.ExitCode}).\n{crashDetail}");
        }

        // Xray creates the TUN interface but does NOT set up system routes (unlike sing-box auto_route).
        // We must add routes manually so traffic actually flows through the tunnel.
        if (string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal))
        {
            var tunName = OperatingSystem.IsMacOS() ? "utun99" : "OnionHop";
            SetupXrayRoutes(tunName);
        }
    }

    public void Stop()
    {
        var stoppedMacPrivilegedTunnel = StopMacPrivilegedTunnelIfNeeded();
        RemoveXrayRoutes();

        Process? proc;
        lock (_processLock)
        {
            proc = _process;
            _process = null;
        }

        if (proc == null)
        {
            if (!stoppedMacPrivilegedTunnel)
            {
                return;
            }

            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                try
                {
                    proc.CloseMainWindow();
                    proc.WaitForExit(1500);
                }
                catch
                {
                }

                proc.Kill(true);
                proc.WaitForExit(5000);
            }

            _lastExitCode = proc.HasExited ? proc.ExitCode : null;
        }
        catch (Exception ex)
        {
            _log($"Failed to stop VPN core: {ex.Message}");
        }
        finally
        {
            proc.OutputDataReceived -= HandleOutput;
            proc.ErrorDataReceived -= HandleOutput;
            proc.Exited -= HandleExited;
            proc.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void HandleExited(object? sender, EventArgs e)
    {
        Exited?.Invoke(sender, e);
    }

    private void HandleOutput(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            OutputLineReceived?.Invoke(e.Data);
        }

        OutputReceived?.Invoke(sender, e);
    }

    private void SetupXrayRoutes(string interfaceName)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                PlatformHelper.RunCommandSuccess("route", $"add -net 0.0.0.0/1 -interface {interfaceName}");
                PlatformHelper.RunCommandSuccess("route", $"add -net 128.0.0.0/1 -interface {interfaceName}");
            }
            else if (OperatingSystem.IsLinux())
            {
                PlatformHelper.RunCommandSuccess("ip", $"route add 0.0.0.0/1 dev {interfaceName}");
                PlatformHelper.RunCommandSuccess("ip", $"route add 128.0.0.0/1 dev {interfaceName}");
            }

            _xrayTunInterface = interfaceName;
            _log($"Added system routes via {interfaceName}");
        }
        catch (Exception ex)
        {
            _log($"Warning: failed to add system routes for xray TUN: {ex.Message}");
        }
    }

    private void RemoveXrayRoutes()
    {
        var iface = _xrayTunInterface;
        if (iface == null)
        {
            return;
        }

        _xrayTunInterface = null;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                PlatformHelper.RunCommandSuccess("route", $"delete -net 0.0.0.0/1 -interface {iface}");
                PlatformHelper.RunCommandSuccess("route", $"delete -net 128.0.0.0/1 -interface {iface}");
            }
            else if (OperatingSystem.IsLinux())
            {
                PlatformHelper.RunCommandSuccess("ip", $"route del 0.0.0.0/1 dev {iface}");
                PlatformHelper.RunCommandSuccess("ip", $"route del 128.0.0.0/1 dev {iface}");
            }

            _log($"Removed system routes for {iface}");
        }
        catch
        {
            // Best effort — interface may already be gone.
        }
    }

    private async Task StartMacPrivilegedTunnelAsync(
        string vpnCoreMode,
        string vpnCorePath,
        string vpnCoreLabel,
        string workDir,
        string configPath,
        int startupDelayMs,
        CancellationToken token)
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "OnionHop", "mac-vpn");
        Directory.CreateDirectory(sessionDir);

        var logPath = Path.Combine(sessionDir, $"{vpnCoreLabel}.log");
        var supervisorPidPath = Path.Combine(sessionDir, "supervisor.pid");
        var corePidPath = Path.Combine(sessionDir, "core.pid");
        var exitCodePath = Path.Combine(sessionDir, "exit.code");
        var runnerScriptPath = Path.Combine(sessionDir, $"{vpnCoreLabel}-runner.sh");
        var tunInterface = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? (OperatingSystem.IsMacOS() ? "utun99" : "OnionHop")
            : string.Empty;

        File.WriteAllText(logPath, string.Empty);
        File.WriteAllText(runnerScriptPath, BuildMacPrivilegedRunnerScript(workDir, vpnCorePath, configPath, logPath, corePidPath, exitCodePath));
        if (OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                runnerScriptPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        var result = MacAuthorization.RunScript(
            BuildMacPrivilegedStartScript(
                sessionDir,
                runnerScriptPath,
                logPath,
                supervisorPidPath,
                corePidPath,
                exitCodePath,
                tunInterface,
                startupDelayMs),
            requireAdministrator: true,
            timeoutMs: 180_000);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Administrator privileges are required to start the macOS tunnel. {result.FailureMessage}");
        }

        _macPrivilegedSessionDir = sessionDir;
        _macPrivilegedRunnerScriptPath = runnerScriptPath;
        _macPrivilegedLogPath = logPath;
        _macPrivilegedSupervisorPidPath = supervisorPidPath;
        _macPrivilegedCorePidPath = corePidPath;
        _macPrivilegedExitCodePath = exitCodePath;
        _macPrivilegedCoreMode = vpnCoreMode;
        _macPrivilegedTunInterface = tunInterface;
        _macPrivilegedLogOffset = 0;

        try
        {
            var startupWait = Stopwatch.StartNew();
            var maxStartupWaitMs = Math.Max(5000, startupDelayMs + 4000);

            while (startupWait.ElapsedMilliseconds < maxStartupWaitMs)
            {
                token.ThrowIfCancellationRequested();

                if (TryGetMacPrivilegedExitCode(out var exitCode))
                {
                    _lastExitCode = exitCode;
                    var crashDetail = ReadMacPrivilegedLogTail();
                    throw new InvalidOperationException(
                        $"{vpnCoreLabel} exited unexpectedly during startup (exit code {exitCode}).\n{crashDetail}");
                }

                if (IsMacPrivilegedTunnelRunning())
                {
                    StartMacPrivilegedMonitor();
                    return;
                }

                await Task.Delay(200, token).ConfigureAwait(false);
            }

            var timeoutDetail = ReadMacPrivilegedLogTail();
            throw new InvalidOperationException(
                $"Unable to launch {vpnCoreLabel} through macOS authorization.\n{timeoutDetail}");
        }
        catch
        {
            StopMacPrivilegedTunnelIfNeeded();
            throw;
        }
    }

    private bool StopMacPrivilegedTunnelIfNeeded()
    {
        if (_macPrivilegedRunnerScriptPath == null &&
            _macPrivilegedLogPath == null &&
            _macPrivilegedSupervisorPidPath == null &&
            _macPrivilegedCorePidPath == null &&
            _macPrivilegedExitCodePath == null)
        {
            return false;
        }

        StopMacPrivilegedMonitor();

        if (IsMacPrivilegedTunnelRunning())
        {
            var stopResult = MacAuthorization.RunScript(
                BuildMacPrivilegedStopScript(
                    _macPrivilegedSupervisorPidPath,
                    _macPrivilegedCorePidPath,
                    _macPrivilegedExitCodePath,
                    _macPrivilegedCoreMode,
                    _macPrivilegedTunInterface),
                requireAdministrator: true,
                timeoutMs: 60_000);

            if (!stopResult.Success)
            {
                _log($"Failed to stop VPN core: {stopResult.FailureMessage}");
            }
        }

        CleanupMacPrivilegedStateFiles();
        ClearMacPrivilegedState();
        return true;
    }

    private void StartMacPrivilegedMonitor()
    {
        StopMacPrivilegedMonitor();

        var logPath = _macPrivilegedLogPath;
        var exitCodePath = _macPrivilegedExitCodePath;
        if (string.IsNullOrWhiteSpace(logPath) || string.IsNullOrWhiteSpace(exitCodePath))
        {
            return;
        }

        _macPrivilegedMonitorCts = new CancellationTokenSource();
        var token = _macPrivilegedMonitorCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    DrainMacPrivilegedLogLines(logPath);

                    if (TryGetMacPrivilegedExitCode(out var exitCode))
                    {
                        _lastExitCode = exitCode;
                        Exited?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    await Task.Delay(750, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log($"Failed to monitor macOS tunnel: {ex.Message}");
                    return;
                }
            }
        }, token);
    }

    private void StopMacPrivilegedMonitor()
    {
        try
        {
            _macPrivilegedMonitorCts?.Cancel();
            _macPrivilegedMonitorCts?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _macPrivilegedMonitorCts = null;
        }
    }

    private void DrainMacPrivilegedLogLines(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _macPrivilegedLogOffset)
        {
            _macPrivilegedLogOffset = 0;
        }

        stream.Seek(_macPrivilegedLogOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        _macPrivilegedLogOffset = stream.Position;

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            OutputLineReceived?.Invoke(line);
        }
    }

    private bool IsMacPrivilegedTunnelRunning()
    {
        var corePidPath = _macPrivilegedCorePidPath;
        if (string.IsNullOrWhiteSpace(corePidPath) || !File.Exists(corePidPath))
        {
            return false;
        }

        if (TryGetMacPrivilegedExitCode(out _))
        {
            return false;
        }

        if (!TryReadIntFromFile(corePidPath, out var pid) || pid <= 0)
        {
            return false;
        }

        var psOutput = PlatformHelper.RunCommand("ps", $"-p {pid} -o pid=");
        return !string.IsNullOrWhiteSpace(psOutput);
    }

    private bool TryGetMacPrivilegedExitCode(out int exitCode)
    {
        var exitCodePath = _macPrivilegedExitCodePath;
        if (string.IsNullOrWhiteSpace(exitCodePath) || !File.Exists(exitCodePath))
        {
            exitCode = default;
            return false;
        }

        return TryReadIntFromFile(exitCodePath, out exitCode);
    }

    private static bool TryReadIntFromFile(string path, out int value)
    {
        try
        {
            var raw = File.ReadAllText(path).Trim();
            return int.TryParse(raw, out value);
        }
        catch
        {
            value = default;
            return false;
        }
    }

    private string ReadMacPrivilegedLogTail()
    {
        var logPath = _macPrivilegedLogPath;
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return "(no output captured)";
        }

        try
        {
            var lines = File.ReadAllLines(logPath);
            if (lines.Length == 0)
            {
                return "(no output captured)";
            }

            var take = Math.Min(lines.Length, 20);
            return string.Join("\n", lines[^take..]);
        }
        catch
        {
            return "(no output captured)";
        }
    }

    private void CleanupMacPrivilegedStateFiles()
    {
        foreach (var path in new[]
                 {
                     _macPrivilegedSupervisorPidPath,
                     _macPrivilegedCorePidPath,
                     _macPrivilegedExitCodePath,
                     _macPrivilegedLogPath,
                     _macPrivilegedRunnerScriptPath
                 })
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    private void ClearMacPrivilegedState()
    {
        _macPrivilegedSessionDir = null;
        _macPrivilegedRunnerScriptPath = null;
        _macPrivilegedLogPath = null;
        _macPrivilegedSupervisorPidPath = null;
        _macPrivilegedCorePidPath = null;
        _macPrivilegedExitCodePath = null;
        _macPrivilegedCoreMode = null;
        _macPrivilegedTunInterface = null;
        _macPrivilegedLogOffset = 0;
    }

    private static string BuildMacPrivilegedRunnerScript(
        string workDir,
        string vpnCorePath,
        string configPath,
        string logPath,
        string corePidPath,
        string exitCodePath)
    {
        return $$"""
            #!/bin/sh
            set -eu
            cd {{MacAuthorization.QuoteShellArgument(workDir)}} || exit 1
            {{MacAuthorization.QuoteShellArgument(vpnCorePath)}} run -c {{MacAuthorization.QuoteShellArgument(configPath)}} >> {{MacAuthorization.QuoteShellArgument(logPath)}} 2>&1 &
            CORE_PID=$!
            printf '%s\n' "$CORE_PID" > {{MacAuthorization.QuoteShellArgument(corePidPath)}}
            chmod 644 {{MacAuthorization.QuoteShellArgument(corePidPath)}} {{MacAuthorization.QuoteShellArgument(logPath)}}
            set +e
            wait "$CORE_PID"
            EXIT_CODE=$?
            set -e
            printf '%s\n' "$EXIT_CODE" > {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
            chmod 644 {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
            """;
    }

    private static string BuildMacPrivilegedStartScript(
        string sessionDir,
        string runnerScriptPath,
        string logPath,
        string supervisorPidPath,
        string corePidPath,
        string exitCodePath,
        string tunInterface,
        int startupDelayMs)
    {
        var startupSeconds = Math.Max(1, startupDelayMs / 1000);
        var xrayRouteSetup = string.IsNullOrWhiteSpace(tunInterface)
            ? string.Empty
            : $$"""
                /sbin/route add -net 0.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                /sbin/route add -net 128.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                """;

        return $$"""
            #!/bin/sh
            set -eu
            mkdir -p {{MacAuthorization.QuoteShellArgument(sessionDir)}}
            rm -f {{MacAuthorization.QuoteShellArgument(supervisorPidPath)}} {{MacAuthorization.QuoteShellArgument(corePidPath)}} {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
            : > {{MacAuthorization.QuoteShellArgument(logPath)}}
            chmod 644 {{MacAuthorization.QuoteShellArgument(logPath)}}
            nohup /bin/sh {{MacAuthorization.QuoteShellArgument(runnerScriptPath)}} >/dev/null 2>&1 &
            SUPERVISOR_PID=$!
            printf '%s\n' "$SUPERVISOR_PID" > {{MacAuthorization.QuoteShellArgument(supervisorPidPath)}}
            chmod 644 {{MacAuthorization.QuoteShellArgument(supervisorPidPath)}}
            sleep {{startupSeconds}}
            {{xrayRouteSetup}}
            printf 'vpn started pid=%s\n' "$SUPERVISOR_PID"
            """;
    }

    private static string BuildMacPrivilegedStopScript(
        string? supervisorPidPath,
        string? corePidPath,
        string? exitCodePath,
        string? coreMode,
        string? tunInterface)
    {
        var deleteRoutes = string.Equals(coreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal) &&
                           !string.IsNullOrWhiteSpace(tunInterface)
            ? $$"""
                /sbin/route delete -net 0.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                /sbin/route delete -net 128.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                """
            : string.Empty;

        return $$"""
            #!/bin/sh
            set -eu
            {{deleteRoutes}}
            if [ -f {{MacAuthorization.QuoteShellArgument(corePidPath ?? string.Empty)}} ]; then
              CORE_PID=$(cat {{MacAuthorization.QuoteShellArgument(corePidPath ?? string.Empty)}} 2>/dev/null || true)
              if [ -n "$CORE_PID" ]; then
                kill "$CORE_PID" 2>/dev/null || true
                sleep 1
                kill -9 "$CORE_PID" 2>/dev/null || true
              fi
            fi
            if [ -f {{MacAuthorization.QuoteShellArgument(supervisorPidPath ?? string.Empty)}} ]; then
              SUPERVISOR_PID=$(cat {{MacAuthorization.QuoteShellArgument(supervisorPidPath ?? string.Empty)}} 2>/dev/null || true)
              if [ -n "$SUPERVISOR_PID" ]; then
                kill "$SUPERVISOR_PID" 2>/dev/null || true
              fi
            fi
            rm -f {{MacAuthorization.QuoteShellArgument(corePidPath ?? string.Empty)}} {{MacAuthorization.QuoteShellArgument(supervisorPidPath ?? string.Empty)}} {{MacAuthorization.QuoteShellArgument(exitCodePath ?? string.Empty)}}
            """;
    }

    private static string NormalizeTunCoreMode(string? value)
    {
        return string.Equals(value, OnionHopConnectOptions.TunCoreXray, StringComparison.OrdinalIgnoreCase)
            ? OnionHopConnectOptions.TunCoreXray
            : OnionHopConnectOptions.TunCoreSingBox;
    }
}

internal sealed class VpnLaunchConfig
{
    public string SingBoxPath { get; init; } = string.Empty;
    public string XrayPath { get; init; } = string.Empty;
    public string? WintunPath { get; init; }
    public string VpnCoreMode { get; init; } = OnionHopConnectOptions.TunCoreSingBox;
    public bool HybridRouting { get; init; }
    public bool SecureDns { get; init; }
    public int SocksPort { get; init; }
    public string DohServer { get; init; } = "cloudflare-dns.com";
    public int DohServerPort { get; init; } = 443;
    public string DohPath { get; init; } = "/dns-query";
    public bool RouteAllWebTrafficThroughTor { get; init; } = true;
    public bool BlockQuicForTorApps { get; init; } = true;
    public string TunStack { get; init; } = "mixed";
    public int? TunMtu { get; init; }
    public bool TunStrictRoute { get; init; } = true;
    public IReadOnlyList<string> TorAppProcessNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BypassAppProcessNames { get; init; } = Array.Empty<string>();
    [JsonIgnore]
    public Action<Process>? ProcessStarted { get; init; }
}
