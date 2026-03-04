# OnionHop V2 (Avalonia + SukiUI)

This is the **V2 UI** for OnionHop, rebuilt with **Avalonia** + **SukiUI** for a cleaner, modern, cross-platform UI.

## Status

- UI + Networking/routing: **Windows, Linux, macOS**

## Build

```bash
dotnet build "OnionHop/OnionHopV2.sln" -c Release
```

## Run

```bash
dotnet run --project "OnionHop/src/OnionHopV2.App" -c Release
```

On first connect, the app will ensure Tor + sing-box dependencies in its output directory (Wintun is also downloaded on Windows).

## Run CLI

```bash
dotnet run --project "OnionHop/src/OnionHopV2.Cli" -c Release
```

CLI quick start:

```
connect --smart on
countries
connect --smart off --exit us --entry nl
status
disconnect
```

## Platform Notes

- **Windows:** Proxy uses Windows registry. TUN uses sing-box + Wintun. Kill switch uses Windows Firewall (netsh). Auto-start uses registry Run key.
- **Linux:** Proxy uses gsettings (GNOME) or kwriteconfig5/6 (KDE). TUN uses sing-box + kernel TUN. Kill switch uses iptables. Auto-start uses XDG autostart (.desktop file). Requires `libevent-2.1-7` for Tor.
- **macOS:** Proxy uses `networksetup`. TUN can run via root (`sing-box` + `utun`) or via a configured NetworkExtension profile (`OnionHop Tunnel`) without launching the app as root. Kill switch uses `pf` (packet filter). Auto-start uses LaunchAgents plist.

## Build Installers (Windows)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-installer-cli.ps1
```

## Build Portable Packages (Windows)

GUI portable ZIP:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-portable-v2.ps1
```

CLI portable ZIP:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-portable-cli.ps1
```
