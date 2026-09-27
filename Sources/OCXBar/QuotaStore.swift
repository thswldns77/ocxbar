import Foundation
import SwiftUI

@MainActor
final class QuotaStore: ObservableObject {
    static let refreshInterval: UInt64 = 60

    @Published private(set) var snapshot: Snapshot?
    @Published private(set) var lastError: String?
    @Published private(set) var isLoading = false
    @Published private(set) var lastAttempt: Date?

    @Published var serverURL: String { didSet { defaults.set(serverURL, forKey: "serverURL") } }
    @Published var tokenOverride: String { didSet { defaults.set(tokenOverride, forKey: "tokenOverride") } }
    @Published var showUsed: Bool { didSet { defaults.set(showUsed, forKey: "showUsed") } }
    /// "" = every provider ("CX 1% · CL 97%"); a provider id = only that one ("OCX 97%").
    @Published var menuBarProvider: String { didSet { defaults.set(menuBarProvider, forKey: "menuBarProvider") } }

    private let defaults = UserDefaults.standard
    private var pollTask: Task<Void, Never>?

    init() {
        serverURL = defaults.string(forKey: "serverURL") ?? OCXClient.defaultURL
        tokenOverride = defaults.string(forKey: "tokenOverride") ?? ""
        showUsed = defaults.bool(forKey: "showUsed")
        menuBarProvider = defaults.string(forKey: "menuBarProvider") ?? ""
        startPolling()
    }

    /// "OCX --" whenever the last fetch failed; otherwise see Snapshot.menuTitle.
    var menuTitle: String {
        if lastError != nil { return "OCX --" }
        guard let snapshot else { return isLoading ? "OCX …" : "OCX --" }
        return snapshot.menuTitle(provider: menuBarProvider)
    }

    func startPolling() {
        pollTask?.cancel()
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refresh(force: false)
                try? await Task.sleep(nanoseconds: Self.refreshInterval * 1_000_000_000)
            }
        }
    }

    func refresh(force: Bool) async {
        guard !isLoading else { return }
        isLoading = true
        defer { isLoading = false }
        lastAttempt = Date()
        do {
            let client = try OCXClient(urlString: serverURL, token: OCXClient.resolveToken(override: tokenOverride))
            snapshot = try await client.fetchSnapshot(force: force)
            lastError = nil
        } catch {
            lastError = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }

    func refreshIfStale() {
        if let last = lastAttempt, Date().timeIntervalSince(last) < 20 { return }
        Task { await refresh(force: false) }
    }

    func apply(url: String, token: String) {
        let cleaned = url.trimmingCharacters(in: .whitespacesAndNewlines)
        serverURL = cleaned.isEmpty ? OCXClient.defaultURL : cleaned
        tokenOverride = token.trimmingCharacters(in: .whitespacesAndNewlines)
        snapshot = nil
        lastError = nil
        startPolling()
    }
}
