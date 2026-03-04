using System;
using System.IO;
using OnionHopV2.Core.Platform;

namespace OnionHopV2.Core.Platform.MacOS;

internal sealed class MacDnsProxyService : IDnsProxyService
{
    private const string ResolverDirectory = "/etc/resolver";
    private const string ResolverFilePath = "/etc/resolver/onion";
    private bool _enabled;

    public bool Enable(string nameServerAddress, Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var safeNameServer = string.IsNullOrWhiteSpace(nameServerAddress)
            ? "127.0.0.1"
            : nameServerAddress.Trim();

        if (MacSwiftHelper.TryRun($"dns enable --nameserver {safeNameServer}", log))
        {
            _enabled = true;
            return true;
        }

        if (!PlatformHelper.IsAdministrator())
        {
            log(".onion DNS proxying on macOS requires running OnionHop as root (or using the Swift helper with sudo).");
            return false;
        }

        try
        {
            Directory.CreateDirectory(ResolverDirectory);
            var content = $"nameserver {safeNameServer}\nport 53\n";
            File.WriteAllText(ResolverFilePath, content);
            _enabled = true;
            log($".onion DNS proxying enabled on macOS via {ResolverFilePath} (nameserver={safeNameServer}).");
            return true;
        }
        catch (Exception ex)
        {
            log($".onion DNS proxying enable failed: {ex.Message}");
            return false;
        }
    }

    public void Disable(Action<string> log)
    {
        if (!OperatingSystem.IsMacOS() || !_enabled)
        {
            return;
        }

        if (MacSwiftHelper.TryRun("dns disable", log))
        {
            _enabled = false;
            return;
        }

        if (!PlatformHelper.IsAdministrator())
        {
            return;
        }

        try
        {
            if (File.Exists(ResolverFilePath))
            {
                File.Delete(ResolverFilePath);
            }

            _enabled = false;
            log(".onion DNS proxying disabled on macOS.");
        }
        catch (Exception ex)
        {
            log($".onion DNS proxying cleanup failed: {ex.Message}");
        }
    }
}
