import Foundation

/// One quota window (5h / Weekly / 30d / ...). OpenCodex reports *used* percent.
struct QuotaWindow: Identifiable, Hashable {
    let id: String
    let label: String
    let usedPercent: Double
    let resetAt: Date?
    /// Model-scoped windows (e.g. Claude's per-model weekly "Fable" bar) are shown,
    /// but do not gate the whole account: other models keep working when one is exhausted.
    var isScoped = false
    /// A rolling window (e.g. 5h) that has not started: the account has not been used since the
    /// last window ended, so the whole window is available and no reset time exists yet.
    var isIdle = false

    var remainingPercent: Double { max(0, min(100, 100 - usedPercent)) }

    static func idle(id: String, label: String) -> QuotaWindow {
        QuotaWindow(id: id, label: label, usedPercent: 0, resetAt: nil, isIdle: true)
    }
}

struct Account: Identifiable, Hashable {
    let id: String
    let name: String
    let plan: String?
    let isActive: Bool
    let isPaused: Bool
    let needsReauth: Bool
    let health: String?
    let windows: [QuotaWindow]
    let updatedAt: Date?

    var isUsable: Bool { !isPaused && !needsReauth }

    /// Headroom of the tightest account-wide window: an account is only as free as its most-used window.
    var remainingPercent: Double? {
        let gating = windows.filter { !$0.isScoped }
        return (gating.isEmpty ? windows : gating).map(\.remainingPercent).min()
    }

    func with(windows: [QuotaWindow]) -> Account {
        Account(id: id, name: name, plan: plan, isActive: isActive, isPaused: isPaused,
                needsReauth: needsReauth, health: health, windows: windows, updatedAt: updatedAt)
    }
}

/// All accounts of one provider in OpenCodex (Codex pool, Claude, ...). Pools are independent,
/// so "best" is computed per provider and never mixed across providers.
struct ProviderGroup: Identifiable {
    let id: String
    var accounts: [Account]
    var activeAccountID: String?
    var strategy: String?
    var pinned = false
    var error: String?
    var source: String

    var name: String { ProviderNames.name(id) }
    var shortName: String { ProviderNames.short(id) }
    var activeAccount: Account? { accounts.first { $0.isActive } }

    /// Account with the most headroom, preferring accounts that can actually be routed to.
    var best: Account? {
        let rated = accounts.filter { $0.remainingPercent != nil }
        let usable = rated.filter(\.isUsable)
        return (usable.isEmpty ? rated : usable)
            .max { ($0.remainingPercent ?? 0) < ($1.remainingPercent ?? 0) }
    }
}

enum ProviderNames {
    static let names = [
        "openai": "Codex", "anthropic": "Claude", "xai": "Grok", "kimi": "Kimi",
        "github-copilot": "Copilot", "google-antigravity": "Antigravity", "cursor": "Cursor",
        "devin": "Devin", "kiro": "Kiro", "nous": "Nous", "meta-muse": "Muse",
    ]
    static let shorts = ["openai": "CX", "anthropic": "CL"]

    static func name(_ id: String) -> String { names[id] ?? id.capitalized }
    static func short(_ id: String) -> String { shorts[id] ?? String(name(id).prefix(2)).uppercased() }
}

struct Snapshot {
    var groups: [ProviderGroup]
    var fetchedAt: Date

    /// Menu bar text. Pools are independent, so each provider gets its own best value:
    /// "CX 1% · CL 97%", or "OCX 97%" when one provider is selected / present.
    func menuTitle(provider: String) -> String {
        let rated = groups.filter { $0.best != nil }
        if let chosen = rated.first(where: { $0.id == provider }) {
            return "OCX " + Format.percent(chosen.best?.remainingPercent)
        }
        if rated.count <= 1 { return "OCX " + Format.percent(rated.first?.best?.remainingPercent) }
        return rated.map { "\($0.shortName) \(Format.percent($0.best?.remainingPercent))" }
            .joined(separator: " · ")
    }

    var textDump: String {
        var lines: [String] = []
        for g in groups {
            lines.append("[\(g.name)] source: \(g.source)  active: \(g.activeAccountID ?? "-")  strategy: \(g.strategy ?? "-")  best: \(g.best.map { "\($0.name) \(Format.percent($0.remainingPercent))" } ?? "-")")
            if let e = g.error { lines.append("  ! \(e)") }
            for a in g.accounts {
                var flags: [String] = []
                if a.isActive { flags.append("ACTIVE") }
                if a.isPaused { flags.append("paused") }
                if a.needsReauth { flags.append("reauth") }
                if let p = a.plan { flags.append(p) }
                lines.append("  - \(a.name) [\(a.id)] \(flags.joined(separator: ","))  headroom=\(Format.percent(a.remainingPercent))")
                for w in a.windows {
                    let reset = w.isIdle ? " (not started)" : w.resetAt.map { " resets in \(Format.until($0))" } ?? ""
                    let scoped = w.isScoped ? " (model-scoped)" : ""
                    lines.append("      \(w.label): used \(Format.percent(w.usedPercent)), left \(Format.percent(w.remainingPercent))\(reset)\(scoped)")
                }
                if a.windows.isEmpty { lines.append("      (no quota data yet)") }
            }
        }
        if groups.isEmpty { lines.append("(no accounts found)") }
        return lines.joined(separator: "\n")
    }
}

enum Format {
    static func percent(_ v: Double?) -> String {
        guard let v else { return "--" }
        return "\(Int(v.rounded()))%"
    }

    static func until(_ date: Date, now: Date = Date()) -> String {
        let s = Int(date.timeIntervalSince(now))
        if s <= 0 { return "now" }
        let d = s / 86_400, h = (s % 86_400) / 3_600, m = (s % 3_600) / 60
        if d > 0 { return "\(d)d \(h)h" }
        if h > 0 { return "\(h)h \(m)m" }
        return "\(max(m, 1))m"
    }

    static func ago(_ date: Date, now: Date = Date()) -> String {
        let s = Int(now.timeIntervalSince(date))
        if s < 10 { return "just now" }
        if s < 60 { return "\(s)s ago" }
        if s < 3_600 { return "\(s / 60)m ago" }
        if s < 86_400 { return "\(s / 3_600)h ago" }
        return "\(s / 86_400)d ago"
    }
}
