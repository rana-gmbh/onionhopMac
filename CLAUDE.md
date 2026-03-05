# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# dotnet 9 path on macOS (Homebrew)
export DOTNET=/opt/homebrew/Cellar/dotnet@9/9.0.114/bin/dotnet

# Build
$DOTNET build src/OnionHopV2.Core/          # core library only
$DOTNET build OnionHopV2.sln -c Release     # full solution

# Run GUI
xattr -cr src/OnionHopV2.App/bin            # REQUIRED on macOS before every run
$DOTNET run --project src/OnionHopV2.App

# Run CLI
$DOTNET run --project src/OnionHopV2.Cli

# Run tests
$DOTNET test src/OnionHopV2.Tests/
$DOTNET test src/OnionHopV2.Tests/ --filter "FullyQualifiedName~MyTestClass.MyTest"
```

## Architecture

**OnionHop V2** is a cross-platform Tor routing app (Windows/Linux/macOS) with two connection modes: **Proxy** (system/SOCKS proxy) and **TUN/VPN** (sing-box or xray).

### Projects

| Project | Type | Purpose |
|---------|------|---------|
| `OnionHopV2.Core` | Library | All networking, Tor management, platform services |
| `OnionHopV2.App` | WinExe | Avalonia + SukiUI GUI (MVVM via CommunityToolkit.Mvvm) |
| `OnionHopV2.Cli` | Console | Interactive CLI interface |
| `OnionHopV2.Tests` | xUnit | Tests for Core |

Package versions are centralized in `Directory.Packages.props`.

### Core Library (`OnionHopV2.Core`)

**`OnionHopClient`** is the central facade — all connectivity flows through it. It exposes events (`Log`, `StatusUpdated`, `DependencyUpdated`) and manages the full connection lifecycle.

Key services in `Services/`:
- **`TorService`** — launches/monitors the Tor daemon, handles control port and auth cookie
- **`VpnService`** — TUN mode via sing-box or xray
- **`TorBridgeManager`** — pluggable transports (obfs4, snowflake, webtunnel, conjure), BridgeDB integration
- **`AdminHelperServer/Client`** — privilege escalation IPC for root operations on Unix/macOS
- **`SmartConnectAdvisor`** — intelligent node/bridge selection

**Connection config** uses the immutable record `OnionHopConnectOptions` (~96 properties covering modes, bridges, DNS, kill switch, etc.).

### Platform Abstraction

Platform-specific services follow a common pattern with interfaces (`IProxyService`, `IDnsProxyService`, `IKillSwitchService`, `IAutoStartService`) and per-OS implementations:

- **Windows**: Registry proxy, netsh firewall, Wintun TUN driver
- **macOS**: `networksetup` proxy, `pf` kill switch, NetworkExtension VPN, Swift interop (`MacSwiftHelper`)
- **Linux**: gsettings/kwriteconfig proxy, iptables kill switch, systemd-resolved DNS

`PlatformHelper` provides factory methods and utilities. Note: `PlatformHelper.IsAdministrator()` is a **method**, not a property.

### App Layer (`OnionHopV2.App`)

MVVM with Avalonia + SukiUI:
- **`AppStateViewModel`** — main state hub (~150 settings properties, connection state, UI commands)
- **Views**: `HomePageView`, `SettingsPageView`, `LogsPageView`, `AboutPageView`
- **`App.axaml.cs`** — tray icon, single-instance IPC, window chrome management

### Entry Point (`Program.cs`)

Handles `--basedir` argument (stored in `Program.OverrideBaseDirectory` for macOS root relaunch), AdminHelperServer detection, and single-instance mutex.

## macOS Specifics

- **Gatekeeper**: After every build, run `xattr -cr src/OnionHopV2.App/bin` before launching, otherwise macOS shows "app is damaged"
- **Root relaunch**: App re-launches itself as root for TUN mode, passing `--basedir` to preserve the original data directory
- Early logging via `StartupLogger.Write()` (`OnionHopV2.Core.Services` namespace)

## Commit Guidelines

- Never add "Co-Authored-By" or any Claude/AI attribution to commits
