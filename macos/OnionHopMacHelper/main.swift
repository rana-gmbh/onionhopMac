import Foundation

struct ProxyConfig: Codable {
    let enabled: Bool
    let server: String
    let port: Int
}

struct ServiceState: Codable {
    let service: String
    let socks: ProxyConfig
    let web: ProxyConfig
    let secureWeb: ProxyConfig
}

private let statePath = "/tmp/onionhop-mac-helper-proxy-state.json"
private let resolverPath = "/etc/resolver/onion"
private let killSwitchAnchor = "com.onionhop.killswitch"

@discardableResult
func run(_ command: String, _ args: [String], input: String? = nil) -> (status: Int32, stdout: String, stderr: String) {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    process.arguments = [command] + args

    let outPipe = Pipe()
    let errPipe = Pipe()
    process.standardOutput = outPipe
    process.standardError = errPipe

    if let input {
        let inPipe = Pipe()
        process.standardInput = inPipe
        do {
            try process.run()
            inPipe.fileHandleForWriting.write(Data(input.utf8))
            inPipe.fileHandleForWriting.closeFile()
        } catch {
            return (1, "", "\(error)")
        }
    } else {
        do {
            try process.run()
        } catch {
            return (1, "", "\(error)")
        }
    }

    process.waitUntilExit()
    let out = String(data: outPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
    let err = String(data: errPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
    return (process.terminationStatus, out.trimmingCharacters(in: .whitespacesAndNewlines), err.trimmingCharacters(in: .whitespacesAndNewlines))
}

func fail(_ message: String) -> Never {
    fputs("error: \(message)\n", stderr)
    exit(1)
}

func requireRoot() {
    if getuid() != 0 {
        fail("this command must run as root (sudo)")
    }
}

func parseProxy(_ output: String) -> ProxyConfig {
    var enabled = false
    var server = "127.0.0.1"
    var port = 0

    for line in output.split(separator: "\n").map({ String($0).trimmingCharacters(in: .whitespaces) }) {
        if line.lowercased().hasPrefix("enabled:") {
            enabled = line.lowercased().contains("yes")
        } else if line.lowercased().hasPrefix("server:") {
            server = line.split(separator: ":", maxSplits: 1).last.map(String.init)?.trimmingCharacters(in: .whitespaces) ?? server
        } else if line.lowercased().hasPrefix("port:") {
            let p = line.split(separator: ":", maxSplits: 1).last.map(String.init)?.trimmingCharacters(in: .whitespaces) ?? "0"
            port = Int(p) ?? 0
        }
    }

    return ProxyConfig(enabled: enabled, server: server, port: port)
}

func listNetworkServices() -> [String] {
    let result = run("networksetup", ["-listallnetworkservices"])
    if result.status != 0 {
        fail("failed to list network services: \(result.stderr)")
    }

    return result.stdout
        .split(separator: "\n")
        .map { String($0).trimmingCharacters(in: .whitespacesAndNewlines) }
        .filter { !$0.isEmpty && !$0.hasPrefix("*") && !$0.lowercased().hasPrefix("an asterisk") }
}

func readServiceState(_ service: String) -> ServiceState {
    let socks = parseProxy(run("networksetup", ["-getsocksfirewallproxy", service]).stdout)
    let web = parseProxy(run("networksetup", ["-getwebproxy", service]).stdout)
    let secure = parseProxy(run("networksetup", ["-getsecurewebproxy", service]).stdout)
    return ServiceState(service: service, socks: socks, web: web, secureWeb: secure)
}

func saveState(_ state: [ServiceState]) {
    let encoder = JSONEncoder()
    do {
        let data = try encoder.encode(state)
        try data.write(to: URL(fileURLWithPath: statePath))
    } catch {
        fail("failed to save proxy state: \(error)")
    }
}

func loadState() -> [ServiceState] {
    let path = URL(fileURLWithPath: statePath)
    guard let data = try? Data(contentsOf: path) else {
        return []
    }
    return (try? JSONDecoder().decode([ServiceState].self, from: data)) ?? []
}

func applyProxy(socksPort: Int, httpPort: Int?) {
    let services = listNetworkServices()
    let snapshot = services.map(readServiceState)
    saveState(snapshot)

    for service in services {
        _ = run("networksetup", ["-setsocksfirewallproxy", service, "127.0.0.1", "\(socksPort)"])
        _ = run("networksetup", ["-setsocksfirewallproxystate", service, "on"])

        if let httpPort {
            _ = run("networksetup", ["-setwebproxy", service, "127.0.0.1", "\(httpPort)"])
            _ = run("networksetup", ["-setsecurewebproxy", service, "127.0.0.1", "\(httpPort)"])
            _ = run("networksetup", ["-setwebproxystate", service, "on"])
            _ = run("networksetup", ["-setsecurewebproxystate", service, "on"])
        } else {
            _ = run("networksetup", ["-setwebproxystate", service, "off"])
            _ = run("networksetup", ["-setsecurewebproxystate", service, "off"])
        }
    }

    print("proxy applied for \(services.count) services")
}

func restoreProxy() {
    let state = loadState()
    guard !state.isEmpty else {
        print("no saved proxy state")
        return
    }

    for serviceState in state {
        let service = serviceState.service
        if serviceState.socks.enabled && serviceState.socks.port > 0 {
            _ = run("networksetup", ["-setsocksfirewallproxy", service, serviceState.socks.server, "\(serviceState.socks.port)"])
            _ = run("networksetup", ["-setsocksfirewallproxystate", service, "on"])
        } else {
            _ = run("networksetup", ["-setsocksfirewallproxystate", service, "off"])
        }

        if serviceState.web.enabled && serviceState.web.port > 0 {
            _ = run("networksetup", ["-setwebproxy", service, serviceState.web.server, "\(serviceState.web.port)"])
            _ = run("networksetup", ["-setwebproxystate", service, "on"])
        } else {
            _ = run("networksetup", ["-setwebproxystate", service, "off"])
        }

        if serviceState.secureWeb.enabled && serviceState.secureWeb.port > 0 {
            _ = run("networksetup", ["-setsecurewebproxy", service, serviceState.secureWeb.server, "\(serviceState.secureWeb.port)"])
            _ = run("networksetup", ["-setsecurewebproxystate", service, "on"])
        } else {
            _ = run("networksetup", ["-setsecurewebproxystate", service, "off"])
        }
    }

    print("proxy restored")
}

func enableOnionDns(nameServer: String) {
    requireRoot()
    let resolverDir = URL(fileURLWithPath: "/etc/resolver", isDirectory: true)
    do {
        try FileManager.default.createDirectory(at: resolverDir, withIntermediateDirectories: true)
        let content = "nameserver \(nameServer)\nport 53\n"
        try content.write(to: URL(fileURLWithPath: resolverPath), atomically: true, encoding: .utf8)
        print("onion dns enabled")
    } catch {
        fail("failed to write resolver file: \(error)")
    }
}

func disableOnionDns() {
    requireRoot()
    if FileManager.default.fileExists(atPath: resolverPath) {
        do {
            try FileManager.default.removeItem(atPath: resolverPath)
            print("onion dns disabled")
        } catch {
            fail("failed to delete resolver file: \(error)")
        }
    }
}

func enableKillSwitch() {
    requireRoot()
    _ = run("pfctl", ["-E"])
    let result = run("pfctl", ["-a", killSwitchAnchor, "-f", "-"], input: "block drop out quick all\npass out quick on lo0 all\n")
    if result.status != 0 {
        fail("failed to enable kill switch: \(result.stderr)")
    }
    print("kill switch enabled")
}

func disableKillSwitch() {
    requireRoot()
    let result = run("pfctl", ["-a", killSwitchAnchor, "-F", "all"])
    if result.status != 0 {
        fail("failed to disable kill switch: \(result.stderr)")
    }
    print("kill switch disabled")
}

func parseStringFlag(_ args: [String], _ name: String) -> String? {
    guard let index = args.firstIndex(of: name), index + 1 < args.count else {
        return nil
    }

    let value = args[index + 1].trimmingCharacters(in: .whitespacesAndNewlines)
    return value.isEmpty ? nil : value
}

func resolveNetworkExtensionServiceName(_ args: [String]) -> String {
    if let explicit = parseStringFlag(args, "--service-name") {
        return explicit
    }

    if let envName = ProcessInfo.processInfo.environment["ONIONHOP_MAC_NE_SERVICE_NAME"],
       !envName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
        return envName.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    return "OnionHop Tunnel"
}

func ensureNetworkExtensionServiceExists(_ serviceName: String) {
    let list = run("scutil", ["--nc", "list"])
    if list.status != 0 {
        let detail = list.stderr.isEmpty ? list.stdout : list.stderr
        fail("failed to query network extension services: \(detail)")
    }

    if !list.stdout.localizedCaseInsensitiveContains(serviceName) {
        fail("network extension service '\(serviceName)' not found. Install and configure the Packet Tunnel profile first.")
    }
}

func showNetworkExtensionStatus(serviceName: String) {
    ensureNetworkExtensionServiceExists(serviceName)
    let status = run("scutil", ["--nc", "status", serviceName])
    if status.status != 0 {
        let detail = status.stderr.isEmpty ? status.stdout : status.stderr
        fail("failed to read status for '\(serviceName)': \(detail)")
    }

    if status.stdout.isEmpty && status.stderr.isEmpty {
        print("status unknown")
        return
    }

    if status.stdout.isEmpty {
        print(status.stderr)
    } else if status.stderr.isEmpty {
        print(status.stdout)
    } else {
        print("\(status.stdout)\n\(status.stderr)")
    }
}

func startNetworkExtension(serviceName: String) {
    ensureNetworkExtensionServiceExists(serviceName)
    let start = run("scutil", ["--nc", "start", serviceName])
    if start.status != 0 {
        let detail = start.stderr.isEmpty ? start.stdout : start.stderr
        fail("failed to start network extension '\(serviceName)': \(detail)")
    }

    print("network extension start requested for \(serviceName)")
}

func stopNetworkExtension(serviceName: String) {
    ensureNetworkExtensionServiceExists(serviceName)
    let stop = run("scutil", ["--nc", "stop", serviceName])
    if stop.status != 0 {
        let detail = stop.stderr.isEmpty ? stop.stdout : stop.stderr
        fail("failed to stop network extension '\(serviceName)': \(detail)")
    }

    print("network extension stop requested for \(serviceName)")
}

func parseIntFlag(_ args: [String], _ name: String) -> Int? {
    guard let index = args.firstIndex(of: name), index + 1 < args.count else {
        return nil
    }
    return Int(args[index + 1])
}

let args = Array(CommandLine.arguments.dropFirst())
guard args.count >= 2 else {
    fail("usage: onionhop-mac-helper <proxy|dns|killswitch|ne> <apply|restore|enable|disable|status|start|stop> [options]")
}

switch (args[0], args[1]) {
case ("proxy", "apply"):
    guard let socksPort = parseIntFlag(args, "--socks-port"), socksPort > 0 else {
        fail("missing --socks-port")
    }
    let httpPort = parseIntFlag(args, "--http-port")
    applyProxy(socksPort: socksPort, httpPort: httpPort)
case ("proxy", "restore"):
    restoreProxy()
case ("dns", "enable"):
    let nameserverIndex = args.firstIndex(of: "--nameserver")
    let nameserver = (nameserverIndex != nil && nameserverIndex! + 1 < args.count) ? args[nameserverIndex! + 1] : "127.0.0.1"
    enableOnionDns(nameServer: nameserver)
case ("dns", "disable"):
    disableOnionDns()
case ("killswitch", "enable"):
    enableKillSwitch()
case ("killswitch", "disable"):
    disableKillSwitch()
case ("ne", "status"):
    showNetworkExtensionStatus(serviceName: resolveNetworkExtensionServiceName(args))
case ("ne", "start"):
    startNetworkExtension(serviceName: resolveNetworkExtensionServiceName(args))
case ("ne", "stop"):
    stopNetworkExtension(serviceName: resolveNetworkExtensionServiceName(args))
default:
    fail("unknown command")
}
