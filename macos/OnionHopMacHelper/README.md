# OnionHop macOS Swift Helper

This helper provides elevated macOS operations used by OnionHop:

- System proxy apply/restore (`networksetup`)
- `.onion` resolver file management (`/etc/resolver/onion`)
- Emergency kill switch rules (`pfctl` anchor)
- NetworkExtension service control (`scutil --nc status/start/stop`)

## Build

```bash
cd OnionHop/macos/OnionHopMacHelper
./build.sh
```

## Usage

Run commands as root when required (DNS and kill switch):

```bash
./onionhop-mac-helper proxy apply --socks-port 9050 --http-port 9080
./onionhop-mac-helper proxy restore
sudo ./onionhop-mac-helper dns enable --nameserver 127.0.0.1
sudo ./onionhop-mac-helper dns disable
sudo ./onionhop-mac-helper killswitch enable
sudo ./onionhop-mac-helper killswitch disable
./onionhop-mac-helper ne status --service-name "OnionHop Tunnel"
./onionhop-mac-helper ne start --service-name "OnionHop Tunnel"
./onionhop-mac-helper ne stop --service-name "OnionHop Tunnel"
```

Set `ONIONHOP_MAC_HELPER_PATH` if the helper is installed outside the app directory.
Set `ONIONHOP_MAC_NE_SERVICE_NAME` to override the default NetworkExtension service name (`OnionHop Tunnel`).
