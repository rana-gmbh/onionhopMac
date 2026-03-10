using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using OnionHopV2.App.ViewModels;

namespace OnionHopV2.App.Views;

public partial class LogsPageView : UserControl
{
    public LogsPageView()
    {
        InitializeComponent();
    }

    private AppStateViewModel? State => (DataContext as PageViewModelBase)?.State;

    private async void OnCopyCurrentTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = GetCurrentLogText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await CopyToClipboardAsync(text);
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        // On macOS, Avalonia clipboard silently fails when running as root because the
        // pasteboard service belongs to the user session. Always use pbcopy instead.
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var psi = new ProcessStartInfo("pbcopy") { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.StandardInput.WriteAsync(text);
                    proc.StandardInput.Close();
                    await proc.WaitForExitAsync();
                }
            }
            catch
            {
                // pbcopy not available — nothing we can do.
            }

            return;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                // Try wl-copy (Wayland) first, fall back to xclip (X11).
                var tool = File.Exists("/usr/bin/wl-copy") ? "wl-copy" : "xclip";
                var args = tool == "xclip" ? "-selection clipboard" : "";
                var psi = new ProcessStartInfo(tool, args) { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.StandardInput.WriteAsync(text);
                    proc.StandardInput.Close();
                    await proc.WaitForExitAsync();
                }
            }
            catch
            {
            }

            return;
        }

        // Windows / other: use Avalonia clipboard.
        // (This path is also unreachable on macOS/Linux due to early returns above.)
    }

    private async void OnExportCurrentTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = GetCurrentLogText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider == null)
        {
            return;
        }

        var name = GetSelectedTabName() ?? "logs";
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export logs",
            SuggestedFileName = $"onionhop-{name.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt"
        });

        if (file == null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(text);
    }

    private void OnClearCurrentTabClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var state = State;
        if (state == null)
        {
            return;
        }

        var tabIndex = LogsTabs?.SelectedIndex ?? 0;
        switch (tabIndex)
        {
            case 1:
                state.ClearDnsLogs();
                break;
            case 2:
                state.ClearVpnLogs();
                break;
            default:
                state.ClearAppLogs();
                break;
        }
    }

    private string GetCurrentLogText()
    {
        var state = State;
        if (state == null)
        {
            return string.Empty;
        }

        var tabIndex = LogsTabs?.SelectedIndex ?? 0;
        var lines = tabIndex switch
        {
            1 => state.DnsLogLines,
            2 => state.VpnLogLines,
            _ => state.LogLines
        };

        return string.Join(Environment.NewLine, lines);
    }

    private string? GetSelectedTabName()
    {
        if (LogsTabs?.SelectedItem is TabItem item && item.Header is string header)
        {
            return header;
        }

        return null;
    }
}
