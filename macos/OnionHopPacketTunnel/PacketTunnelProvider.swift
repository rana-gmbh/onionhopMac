import Foundation
import NetworkExtension

final class PacketTunnelProvider: NEPacketTunnelProvider {
    private var backendProcess: Process?

    override func startTunnel(options: [String : NSObject]?, completionHandler: @escaping (Error?) -> Void) {
        let config = protocolConfiguration.providerConfiguration ?? [:]

        let tunnelRemoteAddress = (config["remoteAddress"] as? String) ?? "240.0.0.2"
        let tunnelAddress = (config["tunnelAddress"] as? String) ?? "172.19.0.2"
        let tunnelSubnet = (config["tunnelSubnet"] as? String) ?? "255.255.255.252"
        let dnsServers = (config["dnsServers"] as? [String]) ?? ["1.1.1.1", "8.8.8.8"]
        let dnsMatchDomains = (config["dnsMatchDomains"] as? [String]) ?? [""]

        let settings = NEPacketTunnelNetworkSettings(tunnelRemoteAddress: tunnelRemoteAddress)
        let ipv4 = NEIPv4Settings(addresses: [tunnelAddress], subnetMasks: [tunnelSubnet])
        ipv4.includedRoutes = [NEIPv4Route.default()]
        settings.ipv4Settings = ipv4

        let dns = NEDNSSettings(servers: dnsServers)
        dns.matchDomains = dnsMatchDomains
        settings.dnsSettings = dns
        settings.mtu = (config["mtu"] as? NSNumber) ?? 1500

        setTunnelNetworkSettings(settings) { [weak self] error in
            if let error {
                completionHandler(error)
                return
            }

            self?.startOptionalBackend(config: config)
            completionHandler(nil)
        }
    }

    override func stopTunnel(with reason: NEProviderStopReason, completionHandler: @escaping () -> Void) {
        if let process = backendProcess {
            process.terminate()
            backendProcess = nil
        }

        completionHandler()
    }

    private func startOptionalBackend(config: [String: Any]) {
        guard
            let backendPath = config["backendPath"] as? String,
            !backendPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        else {
            return
        }

        let args = (config["backendArgs"] as? [String]) ?? []
        let process = Process()
        process.executableURL = URL(fileURLWithPath: backendPath)
        process.arguments = args

        do {
            try process.run()
            backendProcess = process
        } catch {
            NSLog("OnionHop PacketTunnel backend launch failed: %@", "\(error)")
        }
    }
}
