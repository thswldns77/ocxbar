import Foundation

/// Tolerant parsing of OpenCodex management responses.
/// Primary shape (verified against opencodex 2.64): GET /api/codex-auth/accounts ->
///   { accounts: [{ id, alias?, email, plan, isMain, paused, needsReauth, health: {status},
///                  quota: { shortPercent, shortWindowSeconds, shortResetAt, weeklyPercent,
///                           weeklyResetAt, monthlyPercent?, fiveHourPercent?, updatedAt } }] }
/// Other shapes (oauth account pools, upstream rate-limit windows, snake_case) are accepted too.
enum QuotaParser {
    // MARK: Accounts

    static func accounts(from json: Any?, activeID: String?, quotaMap: [String: Any]? = nil) -> [Account] {
        accountArray(json).enumerated().map { index, raw in
            account(raw, index: index, activeID: activeID, quotaMap: quotaMap)
        }
    }

    static func accountArray(_ json: Any?) -> [[String: Any]] {
        if let array = json as? [[String: Any]] { return array }
        guard let dict = json as? [String: Any] else { return [] }
        for key in ["accounts", "items", "data", "results"] {
            if let array = dict[key] as? [[String: Any]] { return array }
            if let inner = dict[key] as? [String: Any], let array = inner["accounts"] as? [[String: Any]] { return array }
        }
        return []
    }

    static func account(_ a: [String: Any], index: Int, activeID: String?, quotaMap: [String: Any]?) -> Account {
        let id = string(first(a, ["id", "accountId", "account_id", "key"])) ?? "account-\(index + 1)"
        let name = string(first(a, ["alias", "label", "name", "email", "logLabel"]))
            ?? (bool(a["isMain"]) == true ? "main" : id)
        let quota = dict(first(a, ["quota", "usage", "rateLimits", "rate_limits"])) ?? dict(quotaMap?[id]) ?? a
        var parsedWindows = windows(from: quota)
        if parsedWindows.isEmpty, let fallback = dict(quotaMap?[id]) { parsedWindows = windows(from: fallback) }

        let active = (activeID != nil && activeID == id)
            || bool(a["active"]) == true || bool(a["isActive"]) == true
        let health = string(dict(a["health"])?["status"]) ?? string(a["health"])

        return Account(
            id: id,
            name: name,
            plan: string(first(a, ["plan", "planType", "plan_type", "tier"])),
            isActive: active,
            isPaused: bool(a["paused"]) ?? false,
            needsReauth: bool(first(a, ["needsReauth", "needs_reauth"])) ?? false,
            health: health,
            windows: parsedWindows,
            updatedAt: date(first(quota, ["updatedAt", "updated_at"]))
        )
    }

    struct ActiveInfo { var id: String?; var strategy: String?; var pinned: Bool }

    static func activeInfo(_ json: Any?) -> ActiveInfo {
        guard let d = json as? [String: Any] else { return ActiveInfo(id: nil, strategy: nil, pinned: false) }
        return ActiveInfo(
            id: string(first(d, ["activeCodexAccountId", "activeAccountId", "activeId", "active_account_id"])),
            strategy: string(first(d, ["accountPoolStrategy", "strategy", "poolStrategy"])),
            pinned: bool(d["pinned"]) ?? false
        )
    }

    // MARK: Quota windows

    static func windows(from q: [String: Any]) -> [QuotaWindow] {
        var out: [QuotaWindow] = []
        func add(_ id: String, _ label: String, used: Double?, reset: Any?, scoped: Bool = false) {
            guard let used, !out.contains(where: { $0.id == id }) else { return }
            let resetAt = date(reset)
            // 0% used with no reset time = the rolling window has not started yet.
            out.append(QuotaWindow(id: id, label: label, usedPercent: max(0, min(100, used)), resetAt: resetAt,
                                   isScoped: scoped, isIdle: used <= 0 && resetAt == nil && (id == "5h" || id == "weekly")))
        }
        func used(_ usedKeys: [String], remaining: [String]) -> Double? {
            if let u = number(first(q, usedKeys)) { return u }
            if let r = number(first(q, remaining)) { return 100 - r }
            return nil
        }

        add("5h", "5h",
            used: used(["fiveHourPercent", "five_hour_percent", "fiveHourUsedPercent"],
                       remaining: ["fiveHourRemainingPercent", "five_hour_remaining_percent"]),
            reset: first(q, ["fiveHourResetAt", "five_hour_reset_at"]))
        if let short = number(first(q, ["shortPercent", "short_percent"])) {
            let (id, label) = idLabel(seconds: number(first(q, ["shortWindowSeconds", "short_window_seconds"])) ?? 18_000)
            add(id, label, used: short, reset: first(q, ["shortResetAt", "short_reset_at"]))
        }
        add("weekly", "Weekly",
            used: used(["weeklyPercent", "weekly_percent", "weeklyUsedPercent"],
                       remaining: ["weeklyRemainingPercent", "weekly_remaining_percent"]),
            reset: first(q, ["weeklyResetAt", "weekly_reset_at"]))
        add("30d", "30d",
            used: used(["monthlyPercent", "monthly_percent", "thirtyDayPercent", "monthlyUsedPercent"],
                       remaining: ["monthlyRemainingPercent", "monthly_remaining_percent"]),
            reset: first(q, ["monthlyResetAt", "monthly_reset_at", "thirtyDayResetAt"]))

        // Upstream Codex rate-limit style: { primary: { used_percent, window_minutes, resets_at } }
        for key in ["primary", "secondary", "tertiary", "primary_window", "secondary_window"] {
            guard let w = dict(q[key]), let u = number(first(w, ["used_percent", "usedPercent", "percent"])) else { continue }
            let seconds = number(first(w, ["limit_window_seconds", "windowSeconds", "window_seconds"]))
                ?? number(first(w, ["window_minutes", "windowMinutes"])).map { $0 * 60 }
            let (id, label) = idLabel(seconds: seconds)
            add(id, label, used: u, reset: first(w, ["reset_at", "resetAt", "resets_at"]))
        }

        // Generic window arrays: [{ label, percent, resetAt }]. customWindows are model-scoped
        // (e.g. Claude's per-model weekly limit) and don't gate the whole account.
        for key in ["windows", "customWindows"] {
            guard let array = q[key] as? [[String: Any]] else { continue }
            for w in array {
                guard let u = number(first(w, ["usedPercent", "used_percent", "percent"])) else { continue }
                let label = string(first(w, ["label", "window", "name"])) ?? "Window"
                add(label.lowercased(), label, used: u, reset: first(w, ["resetAt", "reset_at"]),
                    scoped: key == "customWindows" || bool(w["scoped"]) == true)
            }
        }
        return out
    }

    static func idLabel(seconds: Double?) -> (String, String) {
        guard let s = seconds, s > 0 else { return ("window", "Window") }
        if abs(s - 18_000) < 120 { return ("5h", "5h") }
        if abs(s - 604_800) < 3_600 { return ("weekly", "Weekly") }
        if abs(s - 2_592_000) < 90_000 { return ("30d", "30d") }
        let label = s >= 86_400 ? "\(Int((s / 86_400).rounded()))d" : "\(Int((s / 3_600).rounded()))h"
        return (label, label)
    }

    // MARK: JSON helpers

    static func first(_ d: [String: Any], _ keys: [String]) -> Any? {
        for key in keys { if let v = d[key], !(v is NSNull) { return v } }
        return nil
    }

    static func dict(_ any: Any?) -> [String: Any]? { any as? [String: Any] }

    static func isBool(_ n: NSNumber) -> Bool { CFGetTypeID(n) == CFBooleanGetTypeID() }

    static func string(_ any: Any?) -> String? {
        if let s = any as? String {
            let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
            return t.isEmpty ? nil : t
        }
        if let n = any as? NSNumber, !isBool(n) { return n.stringValue }
        return nil
    }

    static func number(_ any: Any?) -> Double? {
        if let n = any as? NSNumber, !isBool(n) { return n.doubleValue.isFinite ? n.doubleValue : nil }
        if let s = any as? String, let d = Double(s.trimmingCharacters(in: .whitespaces)), d.isFinite { return d }
        return nil
    }

    static func bool(_ any: Any?) -> Bool? {
        if let n = any as? NSNumber, isBool(n) { return n.boolValue }
        if let s = (any as? String)?.lowercased() { return s == "true" ? true : s == "false" ? false : nil }
        return nil
    }

    /// Accepts epoch seconds, epoch milliseconds, or ISO-8601 strings.
    static func date(_ any: Any?) -> Date? {
        if let n = number(any), n > 0 { return Date(timeIntervalSince1970: n > 100_000_000_000 ? n / 1000 : n) }
        if let s = any as? String {
            let f = ISO8601DateFormatter()
            f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            return f.date(from: s) ?? ISO8601DateFormatter().date(from: s)
        }
        return nil
    }
}
