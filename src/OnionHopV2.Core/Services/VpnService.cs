using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    private bool _macPrivilegedManagesOnionResolver;
    private string? _macPrivilegedStopMarkerPath;
    private string? _macPrivilegedStartedMarkerPath;
    private string? _macPrivilegedConfigKey;
    private long _macPrivilegedLogOffset;
    private Process? _macPrivilegedOsascriptProcess;

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

        token.ThrowIfCancellationRequested();
        var launch = await PrepareLaunchArtifactsAsync(config, token).ConfigureAwait(false);

        if (OperatingSystem.IsMacOS() && !PlatformHelper.IsAdministrator())
        {
            _lastExitCode = null;
            if (!CanReuseMacPrivilegedTunnel(launch.ConfigKey))
            {
                Stop();
                await LaunchMacPrivilegedTunnelAsync(launch, config, token).ConfigureAwait(false);
            }

            await WaitForMacPrivilegedTunnelReadyAsync(launch.VpnCoreLabel, launch.StartupDelayMs, token).ConfigureAwait(false);
            return;
        }

        Stop();
        _lastExitCode = null;

        var psi = new ProcessStartInfo(launch.VpnCorePath, $"run -c \"{launch.ConfigPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = launch.WorkDir
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
            throw new InvalidOperationException($"Unable to launch {launch.VpnCoreLabel}.");
        }

        config.ProcessStarted?.Invoke(_process);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.Delay(launch.StartupDelayMs, token);

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

            _log($"{launch.VpnCoreLabel} crash output:\n{crashDetail}");
            throw new InvalidOperationException(
                $"{launch.VpnCoreLabel} exited unexpectedly during startup (exit code {_process.ExitCode}).\n{crashDetail}");
        }

        // Xray creates the TUN interface but does NOT set up system routes (unlike sing-box auto_route).
        // We must add routes manually so traffic actually flows through the tunnel.
        if (string.Equals(launch.VpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal))
        {
            var tunName = OperatingSystem.IsMacOS() ? "utun99" : "OnionHop";
            SetupXrayRoutes(tunName);
        }
    }

    public async Task PrepareMacPrivilegedTunnelAsync(VpnLaunchConfig config, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS() || PlatformHelper.IsAdministrator())
        {
            return;
        }

        token.ThrowIfCancellationRequested();
        var launch = await PrepareLaunchArtifactsAsync(config, token).ConfigureAwait(false);
        if (CanReuseMacPrivilegedTunnel(launch.ConfigKey))
        {
            _log("Reusing prepared macOS administrator tunnel session.");
            return;
        }

        Stop();
        _lastExitCode = null;
        await LaunchMacPrivilegedTunnelAsync(launch, config, token).ConfigureAwait(false);
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

    private async Task<VpnLaunchArtifacts> PrepareLaunchArtifactsAsync(VpnLaunchConfig config, CancellationToken token)
    {
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
        await File.WriteAllTextAsync(configPath, configJson, token).ConfigureAwait(false);

        _log($"Starting {vpnCoreLabel} with config: {configPath}");
        _log($"{vpnCoreLabel} config:\n{configJson}");

        var startupDelayMs = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? 2000
            : 750;
        var configKey = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{vpnCoreMode}\n{config.ManageOnionResolver}\n{config.OnionDnsNameServer ?? string.Empty}\n{configJson}")));

        return new VpnLaunchArtifacts(
            VpnCoreMode: vpnCoreMode,
            VpnCorePath: vpnCorePath,
            VpnCoreLabel: vpnCoreLabel,
            WorkDir: workDir,
            ConfigPath: configPath,
            StartupDelayMs: startupDelayMs,
            ConfigKey: configKey);
    }

    private async Task LaunchMacPrivilegedTunnelAsync(VpnLaunchArtifacts launch, VpnLaunchConfig config, CancellationToken token)
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "OnionHop", "mac-vpn");
        Directory.CreateDirectory(sessionDir);

        var logPath = Path.Combine(sessionDir, $"{launch.VpnCoreLabel}.log");
        var supervisorPidPath = Path.Combine(sessionDir, "supervisor.pid");
        var corePidPath = Path.Combine(sessionDir, "core.pid");
        var exitCodePath = Path.Combine(sessionDir, "exit.code");
        var stopMarkerPath = Path.Combine(sessionDir, "stop.marker");
        var startedMarkerPath = Path.Combine(sessionDir, "started.marker");
        var runnerScriptPath = Path.Combine(sessionDir, $"{launch.VpnCoreLabel}-runner.sh");
        var tunInterface = string.Equals(launch.VpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? (OperatingSystem.IsMacOS() ? "utun99" : "OnionHop")
            : string.Empty;

        // Clean up old state files.
        foreach (var f in new[] { logPath, supervisorPidPath, corePidPath, exitCodePath, stopMarkerPath, startedMarkerPath })
        {
            try { File.Delete(f); } catch { }
        }

        File.WriteAllText(logPath, string.Empty);

        // Build the combined runner script (includes setup + SOCKS wait + VPN start + monitor).
        // This script runs SYNCHRONOUSLY inside the osascript admin context,
        // keeping the osascript process alive for the lifetime of the VPN session.
        File.WriteAllText(
            runnerScriptPath,
            BuildMacPrivilegedRunnerScript(
                sessionDir,
                launch.WorkDir,
                launch.VpnCorePath,
                launch.ConfigPath,
                logPath,
                supervisorPidPath,
                corePidPath,
                exitCodePath,
                stopMarkerPath,
                startedMarkerPath,
                config.SocksPort,
                tunInterface,
                launch.StartupDelayMs,
                config.ManageOnionResolver,
                config.OnionDnsNameServer));
        if (OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                runnerScriptPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        token.ThrowIfCancellationRequested();

        // Launch osascript as a non-blocking Process. The runner script runs directly
        // inside the admin context (no backgrounding with nohup), so the osascript process
        // stays alive for the entire VPN session. This avoids the issue where backgrounded
        // processes get killed when the Authorization Services context exits.
        var appleScript = $"do shell script \"/bin/sh \" & quoted form of \"{MacAuthorization.EscapeAppleScriptString(runnerScriptPath)}\" with administrator privileges";
        var psi = new ProcessStartInfo("osascript")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(appleScript);

        var osascriptProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!osascriptProcess.Start())
        {
            throw new InvalidOperationException("Failed to launch macOS authorization prompt.");
        }

        // Read output asynchronously to prevent buffer deadlocks.
        osascriptProcess.BeginOutputReadLine();
        osascriptProcess.BeginErrorReadLine();

        // Wait for the "started" marker (confirms admin auth succeeded and script is running)
        // or for the osascript process to exit (user canceled the password dialog).
        var authWait = Stopwatch.StartNew();
        while (authWait.ElapsedMilliseconds < 180_000)
        {
            token.ThrowIfCancellationRequested();

            if (File.Exists(startedMarkerPath))
            {
                break;
            }

            if (osascriptProcess.HasExited)
            {
                osascriptProcess.Dispose();
                throw new OperationCanceledException("Administrator privileges were not granted.");
            }

            await Task.Delay(200, token).ConfigureAwait(false);
        }

        if (!File.Exists(startedMarkerPath))
        {
            try { osascriptProcess.Kill(entireProcessTree: true); } catch { }
            osascriptProcess.Dispose();
            throw new OperationCanceledException("Timed out waiting for administrator authorization.");
        }

        _macPrivilegedOsascriptProcess = osascriptProcess;
        _macPrivilegedSessionDir = sessionDir;
        _macPrivilegedRunnerScriptPath = runnerScriptPath;
        _macPrivilegedLogPath = logPath;
        _macPrivilegedSupervisorPidPath = supervisorPidPath;
        _macPrivilegedCorePidPath = corePidPath;
        _macPrivilegedExitCodePath = exitCodePath;
        _macPrivilegedCoreMode = launch.VpnCoreMode;
        _macPrivilegedTunInterface = tunInterface;
        _macPrivilegedManagesOnionResolver = config.ManageOnionResolver;
        _macPrivilegedStopMarkerPath = stopMarkerPath;
        _macPrivilegedStartedMarkerPath = startedMarkerPath;
        _macPrivilegedConfigKey = launch.ConfigKey;
        _macPrivilegedLogOffset = 0;
        StartMacPrivilegedMonitor();
        await Task.Delay(100, token).ConfigureAwait(false);
    }

    private async Task WaitForMacPrivilegedTunnelReadyAsync(string vpnCoreLabel, int startupDelayMs, CancellationToken token)
    {
        try
        {
            var startupWait = Stopwatch.StartNew();
            var maxStartupWaitMs = Math.Max(15_000, startupDelayMs + 10_000);

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
            _macPrivilegedExitCodePath == null &&
            _macPrivilegedOsascriptProcess == null)
        {
            return false;
        }

        StopMacPrivilegedMonitor();

        // Signal the runner script to stop gracefully.
        SignalMacPrivilegedStop();

        // Wait for the runner script to detect the stop marker and exit cleanly.
        var stopWait = Stopwatch.StartNew();
        while (stopWait.ElapsedMilliseconds < 5000)
        {
            if (TryGetMacPrivilegedExitCode(out _))
            {
                break;
            }

            var osascriptDead = _macPrivilegedOsascriptProcess == null ||
                                _macPrivilegedOsascriptProcess.HasExited;
            if (osascriptDead && !IsMacPrivilegedTunnelRunning())
            {
                break;
            }

            Thread.Sleep(200);
        }

        // If still running, kill the osascript process tree (which includes the runner and VPN core).
        KillMacPrivilegedOsascriptProcess();

        // If the VPN core is somehow still alive (e.g., it detached), use admin privileges to kill it.
        if (IsMacPrivilegedTunnelRunning())
        {
            var stopResult = MacAuthorization.RunScript(
                BuildMacPrivilegedStopScript(
                    _macPrivilegedSupervisorPidPath,
                    _macPrivilegedCorePidPath,
                    _macPrivilegedExitCodePath,
                    _macPrivilegedCoreMode,
                    _macPrivilegedManagesOnionResolver,
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

    private void KillMacPrivilegedOsascriptProcess()
    {
        var proc = _macPrivilegedOsascriptProcess;
        if (proc == null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch
        {
        }
        finally
        {
            try { proc.Dispose(); } catch { }
            _macPrivilegedOsascriptProcess = null;
        }
    }

    private bool CanReuseMacPrivilegedTunnel(string configKey)
    {
        if (string.IsNullOrWhiteSpace(configKey) ||
            !string.Equals(_macPrivilegedConfigKey, configKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (TryGetMacPrivilegedExitCode(out _))
        {
            return false;
        }

        // Check that the osascript process is still alive (runner is still running).
        if (_macPrivilegedOsascriptProcess == null || _macPrivilegedOsascriptProcess.HasExited)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(_macPrivilegedRunnerScriptPath) &&
               !string.IsNullOrWhiteSpace(_macPrivilegedSupervisorPidPath);
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

                    // Also detect if the osascript process died unexpectedly.
                    if (_macPrivilegedOsascriptProcess is { HasExited: true } && !TryGetMacPrivilegedExitCode(out _))
                    {
                        _lastExitCode = -1;
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

    private bool HasMacPrivilegedSupervisorProcess()
    {
        // Check the osascript process first (it hosts the runner script).
        if (_macPrivilegedOsascriptProcess is { HasExited: false })
        {
            return true;
        }

        var supervisorPidPath = _macPrivilegedSupervisorPidPath;
        if (string.IsNullOrWhiteSpace(supervisorPidPath) || !File.Exists(supervisorPidPath))
        {
            return false;
        }

        if (!TryReadIntFromFile(supervisorPidPath, out var pid) || pid <= 0)
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
                     _macPrivilegedRunnerScriptPath,
                     _macPrivilegedStopMarkerPath,
                     _macPrivilegedStartedMarkerPath
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
        KillMacPrivilegedOsascriptProcess();
        _macPrivilegedSessionDir = null;
        _macPrivilegedRunnerScriptPath = null;
        _macPrivilegedLogPath = null;
        _macPrivilegedSupervisorPidPath = null;
        _macPrivilegedCorePidPath = null;
        _macPrivilegedExitCodePath = null;
        _macPrivilegedCoreMode = null;
        _macPrivilegedTunInterface = null;
        _macPrivilegedManagesOnionResolver = false;
        _macPrivilegedStopMarkerPath = null;
        _macPrivilegedStartedMarkerPath = null;
        _macPrivilegedConfigKey = null;
        _macPrivilegedLogOffset = 0;
    }

    private void SignalMacPrivilegedStop()
    {
        var stopMarkerPath = _macPrivilegedStopMarkerPath;
        if (string.IsNullOrWhiteSpace(stopMarkerPath))
        {
            return;
        }

        try
        {
            File.WriteAllText(stopMarkerPath, "stop");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Builds a self-contained runner script that includes setup tasks, SOCKS port wait,
    /// VPN core launch, and monitoring. This script runs SYNCHRONOUSLY inside the osascript
    /// admin context so the process stays alive for the full VPN session.
    /// </summary>
    private static string BuildMacPrivilegedRunnerScript(
        string sessionDir,
        string workDir,
        string vpnCorePath,
        string configPath,
        string logPath,
        string supervisorPidPath,
        string corePidPath,
        string exitCodePath,
        string stopMarkerPath,
        string startedMarkerPath,
        int socksPort,
        string tunInterface,
        int startupDelayMs,
        bool manageOnionResolver,
        string? onionDnsNameServer)
    {
        var startupSeconds = Math.Max(1, startupDelayMs / 1000);
        const int readyTimeoutSeconds = 420;
        var xrayRouteSetup = string.IsNullOrWhiteSpace(tunInterface)
            ? string.Empty
            : $$"""
                /sbin/route add -net 0.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                /sbin/route add -net 128.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                """;
        var xrayRouteCleanup = string.IsNullOrWhiteSpace(tunInterface)
            ? string.Empty
            : $$"""
                /sbin/route delete -net 0.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                /sbin/route delete -net 128.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                """;
        var onionResolverSetup = manageOnionResolver
            ? $$"""
                mkdir -p /etc/resolver
                printf 'nameserver %s\nport 53\n' {{MacAuthorization.QuoteShellArgument(string.IsNullOrWhiteSpace(onionDnsNameServer) ? "127.0.0.1" : onionDnsNameServer.Trim())}} > /etc/resolver/onion
                chmod 644 /etc/resolver/onion
                """
            : string.Empty;
        var onionResolverCleanup = manageOnionResolver
            ? "rm -f /etc/resolver/onion"
            : string.Empty;

        return $$"""
            #!/bin/sh
            set -eu

            # Signal that authorization was granted and script is running.
            printf '' > {{MacAuthorization.QuoteShellArgument(startedMarkerPath)}}
            chmod 644 {{MacAuthorization.QuoteShellArgument(startedMarkerPath)}}

            # Setup: prepare session directory and log file.
            mkdir -p {{MacAuthorization.QuoteShellArgument(sessionDir)}}
            rm -f {{MacAuthorization.QuoteShellArgument(supervisorPidPath)}} {{MacAuthorization.QuoteShellArgument(corePidPath)}} {{MacAuthorization.QuoteShellArgument(exitCodePath)}} {{MacAuthorization.QuoteShellArgument(stopMarkerPath)}}
            : > {{MacAuthorization.QuoteShellArgument(logPath)}}
            chmod 644 {{MacAuthorization.QuoteShellArgument(logPath)}}

            # Record our PID as the supervisor.
            printf '%s\n' "$$" > {{MacAuthorization.QuoteShellArgument(supervisorPidPath)}}
            chmod 644 {{MacAuthorization.QuoteShellArgument(supervisorPidPath)}}

            # Setup: configure .onion DNS resolver if needed.
            {{onionResolverSetup}}

            # Cleanup handler for graceful exit.
            cleanup() {
              {{xrayRouteCleanup}}
              {{onionResolverCleanup}}
            }
            trap cleanup EXIT

            cd {{MacAuthorization.QuoteShellArgument(workDir)}} || exit 1

            # Wait for Tor SOCKS port to become available.
            DEADLINE=$(($(date +%s) + {{readyTimeoutSeconds}}))
            while :; do
              if [ -f {{MacAuthorization.QuoteShellArgument(stopMarkerPath)}} ]; then
                printf '%s\n' 130 > {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
                chmod 644 {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
                exit 0
              fi
              if /usr/bin/nc -z 127.0.0.1 {{socksPort}} >/dev/null 2>&1; then
                break
              fi
              if [ "$(date +%s)" -ge "$DEADLINE" ]; then
                printf 'Timed out waiting for Tor SOCKS port %s\n' {{socksPort}} >> {{MacAuthorization.QuoteShellArgument(logPath)}}
                printf '%s\n' 124 > {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
                chmod 644 {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
                exit 0
              fi
              sleep 1
            done

            # Start VPN core.
            {{MacAuthorization.QuoteShellArgument(vpnCorePath)}} run -c {{MacAuthorization.QuoteShellArgument(configPath)}} >> {{MacAuthorization.QuoteShellArgument(logPath)}} 2>&1 &
            CORE_PID=$!
            printf '%s\n' "$CORE_PID" > {{MacAuthorization.QuoteShellArgument(corePidPath)}}
            chmod 644 {{MacAuthorization.QuoteShellArgument(corePidPath)}} {{MacAuthorization.QuoteShellArgument(logPath)}}
            sleep {{startupSeconds}}
            {{xrayRouteSetup}}

            # Monitor VPN core, checking for stop signal.
            set +e
            while kill -0 "$CORE_PID" 2>/dev/null; do
              if [ -f {{MacAuthorization.QuoteShellArgument(stopMarkerPath)}} ]; then
                kill "$CORE_PID" 2>/dev/null || true
                sleep 1
                kill -9 "$CORE_PID" 2>/dev/null || true
              fi
              sleep 1
            done
            wait "$CORE_PID"
            EXIT_CODE=$?
            set -e
            printf '%s\n' "$EXIT_CODE" > {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
            chmod 644 {{MacAuthorization.QuoteShellArgument(exitCodePath)}}
            """;
    }

    private static string BuildMacPrivilegedStopScript(
        string? supervisorPidPath,
        string? corePidPath,
        string? exitCodePath,
        string? coreMode,
        bool manageOnionResolver,
        string? tunInterface)
    {
        var deleteRoutes = string.Equals(coreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal) &&
                           !string.IsNullOrWhiteSpace(tunInterface)
            ? $$"""
                /sbin/route delete -net 0.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                /sbin/route delete -net 128.0.0.0/1 -interface {{MacAuthorization.QuoteShellArgument(tunInterface)}} >/dev/null 2>&1 || true
                """
            : string.Empty;
        var removeOnionResolver = manageOnionResolver
            ? "rm -f /etc/resolver/onion\n"
            : string.Empty;

        return $$"""
            #!/bin/sh
            set -eu
            {{deleteRoutes}}
            {{removeOnionResolver}}
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

    private sealed record VpnLaunchArtifacts(
        string VpnCoreMode,
        string VpnCorePath,
        string VpnCoreLabel,
        string WorkDir,
        string ConfigPath,
        int StartupDelayMs,
        string ConfigKey);

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
    public bool ManageOnionResolver { get; init; }
    public string? OnionDnsNameServer { get; init; }
    [JsonIgnore]
    public Action<Process>? ProcessStarted { get; init; }
}
