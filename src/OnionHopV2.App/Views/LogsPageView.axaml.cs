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

    private async Task CopyToClipboardAsync(string text)
    {
        if (OperatingSystem.IsMacOS())
        {
            // On macOS, Avalonia clipboard and pbcopy don't work reliably when running as root.
            // Write text to a temp file, then use osascript "do shell script" to read it into the clipboard.
            // osascript accesses the pasteboard through Apple Events, which works across privilege levels.
            try
            {
                var tempFile = Path.Combine(Path.GetTempPath(), $"onionhop-clip-{Environment.ProcessId}.txt");
                await File.WriteAllTextAsync(tempFile, text);

                var psi = new ProcessStartInfo("/usr/bin/osascript")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add($"set the clipboard to (do shell script \"cat '{tempFile}'\")");
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                }

                try { File.Delete(tempFile); } catch { }
                return;
            }
            catch
            {
            }

            return;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
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

        // Windows: use Avalonia clipboard.
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
            }
        }
        catch
        {
        }
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
