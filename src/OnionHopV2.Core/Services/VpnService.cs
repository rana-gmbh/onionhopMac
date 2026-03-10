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

namespace OnionHopV2.Core.Services;

internal sealed class VpnService : IDisposable
{
    private readonly Action<string> _log;
    private readonly object _processLock = new();
    private Process? _process;
    private int? _lastExitCode;
    private bool _disposed;

    public VpnService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event DataReceivedEventHandler? OutputReceived;
    public event EventHandler? Exited;

    public bool IsRunning { get { lock (_processLock) { return _process != null && !_process.HasExited; } } }
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

        // Wait longer for xray (it's slower to initialize than sing-box, especially with bridges).
        var startupDelay = string.Equals(vpnCoreMode, OnionHopConnectOptions.TunCoreXray, StringComparison.Ordinal)
            ? 2000
            : 750;
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
    }

    public void Stop()
    {
        Process? proc;
        lock (_processLock)
        {
            proc = _process;
            if (proc == null)
            {
                return;
            }

            _process = null;
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
        OutputReceived?.Invoke(sender, e);
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
