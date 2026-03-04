# OnionHop Packet Tunnel (macOS NetworkExtension)

This folder contains the starter files for OnionHop's macOS `NEPacketTunnelProvider` path.

## Why this exists

Running the desktop app itself as root is not ideal. The Packet Tunnel architecture allows:

- normal (non-root) GUI/CLI app process
- VPN lifecycle controlled through macOS NetworkExtension profiles
- a signed, isolated extension process for privileged tunnel behavior

## Current status

This is a bootstrap target and configuration template. You still need to:

1. Create an Xcode app + Packet Tunnel extension target.
2. Use these files (`PacketTunnelProvider.swift`, `Info.plist.template`) in that target.
3. Sign with the required capabilities/entitlements:
   - `com.apple.developer.networking.networkextension`
4. Install a VPN profile named `OnionHop Tunnel` (or set `ONIONHOP_MAC_NE_SERVICE_NAME`).

After profile installation, OnionHop can start/stop the tunnel without launching the app as root, via:

- `onionhop-mac-helper ne status`
- `onionhop-mac-helper ne start`
- `onionhop-mac-helper ne stop`

## Important

The tunnel provider logic here is intentionally minimal. Production routing (Tor over TUN with split rules, DNS policy, and full traffic forwarding) still requires implementing packet handling/backend integration inside the extension.
