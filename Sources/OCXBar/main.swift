import AppKit
import Foundation
import SwiftUI

// Headless check:  OCXBar --dump [--refresh] [--url http://127.0.0.1:10100]
// prints what the menu would show, then exits. Handy for debugging API/auth issues.
let arguments = CommandLine.arguments
if arguments.contains("--dump") {
    let urlArg = arguments.firstIndex(of: "--url").flatMap { $0 + 1 < arguments.count ? arguments[$0 + 1] : nil }
    let force = arguments.contains("--refresh")
    Task {
        do {
            let client = try OCXClient(urlString: urlArg ?? OCXClient.defaultURL,
                                       token: OCXClient.resolveToken(override: ""))
            let snapshot = try await client.fetchSnapshot(force: force)
            print("menu bar: \(snapshot.menuTitle(provider: ""))")
            print(snapshot.textDump)
            exit(0)
        } catch {
            print("menu bar: OCX --")
            FileHandle.standardError.write(Data("error: \((error as? LocalizedError)?.errorDescription ?? "\(error)")\n".utf8))
            exit(1)
        }
    }
    dispatchMain()
} else {
    OCXBarApp.main()
}

struct OCXBarApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var store = QuotaStore()

    var body: some Scene {
        MenuBarExtra {
            MenuContentView(store: store)
        } label: {
            Text(store.menuTitle).monospacedDigit()
        }
        .menuBarExtraStyle(.window)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Menu-bar only (no Dock icon), even when launched as a bare binary via swift run.
        NSApp.setActivationPolicy(.accessory)
    }
}
