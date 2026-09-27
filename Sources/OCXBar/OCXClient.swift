import Foundation

enum OCXError: LocalizedError {
    case invalidURL(String)
    case unreachable(String)
    case unauthorized(String?)
    case http(Int, String?)
    case badResponse(String)

    var errorDescription: String? {
        switch self {
        case .invalidURL(let url): return "Invalid server URL: \(url)"
        case .unreachable(let why): return "OpenCodex unreachable: \(why)"
        case .unauthorized(let msg):
            return "Admin token rejected\(msg.map { " (\($0))" } ?? ""). Check ~/.opencodex/admin-api-token or set a token in Settings."
        case .http(let code, let msg): return "HTTP \(code)\(msg.map { ": \($0)" } ?? "")"
        case .badResponse(let what): return "Unexpected response from \(what) (not OpenCodex JSON?)"
        }
    }

    /// Server-wide failures: no point showing partial data.
    var isFatal: Bool {
        switch self {
        case .invalidURL, .unreachable, .unauthorized: return true
        case .http, .badResponse: return false
        }
    }
}

struct OCXClient {
    static let defaultURL = "http://127.0.0.1:10100"
    /// Handled by the dedicated Codex pool API, not the generic OAuth account API.
    static let codexProviderIDs: Set<String> = ["openai", "chatgpt", "codex"]

    let baseURL: String
    let token: String?

    init(urlString: String, token: String?) throws {
        var trimmed = urlString.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { trimmed = Self.defaultURL }
        if !trimmed.contains("://") { trimmed = "http://" + trimmed }
        while trimmed.hasSuffix("/") { trimmed.removeLast() }
        guard let url = URL(string: trimmed), url.host != nil else { throw OCXError.invalidURL(urlString) }
        self.baseURL = trimmed
        self.token = token
    }

    /// Token lookup order: explicit override -> OPENCODEX_ADMIN_AUTH_TOKEN -> $OPENCODEX_HOME/admin-api-token
    /// -> ~/.opencodex/admin-api-token (the file the OpenCodex server writes at startup).
    static func resolveToken(override: String) -> String? {
        let explicit = override.trimmingCharacters(in: .whitespacesAndNewlines)
        if !explicit.isEmpty { return explicit }
        let env = ProcessInfo.processInfo.environment
        if let t = env["OPENCODEX_ADMIN_AUTH_TOKEN"]?.trimmingCharacters(in: .whitespacesAndNewlines), !t.isEmpty { return t }
        let home = env["OPENCODEX_HOME"].flatMap { $0.isEmpty ? nil : $0 }
            ?? (NSHomeDirectory() as NSString).appendingPathComponent(".opencodex")
        let path = (home as NSString).appendingPathComponent("admin-api-token")
        guard let raw = try? String(contentsOfFile: path, encoding: .utf8) else { return nil }
        let t = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        return t.isEmpty ? nil : t
    }

    struct Response { let status: Int; let json: Any? }

    func get(_ path: String, timeout: TimeInterval = 10) async throws -> Response {
        guard let url = URL(string: baseURL + path) else { throw OCXError.invalidURL(baseURL + path) }
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let token { request.setValue(token, forHTTPHeaderField: "X-OpenCodex-API-Key") }
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            return Response(status: status, json: try? JSONSerialization.jsonObject(with: data))
        } catch {
            throw OCXError.unreachable(error.localizedDescription)
        }
    }

    private func check(_ r: Response, _ source: String) throws {
        let message = QuotaParser.string((r.json as? [String: Any])?["error"])
        switch r.status {
        case 200..<300:
            guard r.json is [String: Any] || r.json is [Any] else { throw OCXError.badResponse(source) }
        case 401, 403: throw OCXError.unauthorized(message)
        default: throw OCXError.http(r.status, message)
        }
    }

    private static func describe(_ error: Error) -> String {
        (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
    }

    /// force = ask OpenCodex to re-probe upstream quota instead of returning its cached values.
    func fetchSnapshot(force: Bool) async throws -> Snapshot {
        async let codexCall = fetchCodexGroup(force: force)
        async let othersCall = fetchOAuthGroups(force: force)

        var groups: [ProviderGroup] = []
        do {
            let codex = try await codexCall
            if !codex.accounts.isEmpty { groups.append(codex) }
        } catch let error as OCXError where error.isFatal {
            _ = await othersCall
            throw error
        } catch {
            groups.append(ProviderGroup(id: "openai", accounts: [], error: Self.describe(error),
                                        source: "/api/codex-auth/accounts"))
        }
        groups += await othersCall
        return Snapshot(groups: groups, fetchedAt: Date())
    }

    // MARK: Codex pool (/api/codex-auth/*)

    func fetchCodexGroup(force: Bool) async throws -> ProviderGroup {
        let timeout: TimeInterval = force ? 45 : 10
        async let accountsCall = get("/api/codex-auth/accounts" + (force ? "?refresh=1" : ""), timeout: timeout)
        async let activeCall = try? get("/api/codex-auth/active")

        let accountsResponse = try await accountsCall
        let activeResponse = await activeCall

        var source = "/api/codex-auth/accounts"
        var accountsJSON = accountsResponse.json
        if accountsResponse.status == 404 {
            // Older/alternate layout: generic OAuth account pool for the openai provider.
            source = "/api/oauth/accounts?provider=openai"
            let fallback = try await get("/api/oauth/accounts?provider=openai&quota=1" + (force ? "&refresh=1" : ""), timeout: timeout)
            try check(fallback, source)
            accountsJSON = fallback.json
        } else {
            try check(accountsResponse, source)
        }

        var info = QuotaParser.activeInfo(activeResponse.flatMap { $0.status == 200 ? $0.json : nil })
        if info.id == nil { info.id = QuotaParser.activeInfo(accountsJSON).id }
        if info.strategy == nil { info.strategy = QuotaParser.activeInfo(accountsJSON).strategy }

        var accounts = QuotaParser.accounts(from: accountsJSON, activeID: info.id)
        if accounts.contains(where: { $0.windows.isEmpty }),
           let quota = try? await get("/api/codex-auth/quota"), quota.status == 200,
           let map = (quota.json as? [String: Any])?["quotas"] as? [String: Any] {
            accounts = QuotaParser.accounts(from: accountsJSON, activeID: info.id, quotaMap: map)
        }

        return ProviderGroup(id: "openai", accounts: accounts, activeAccountID: info.id,
                             strategy: info.strategy, pinned: info.pinned, source: source)
    }

    // MARK: Other OAuth providers (/api/oauth/*): Claude, Kimi, Grok, ...

    /// Discovers providers from /api/oauth/providers and returns only those with logged-in accounts.
    func fetchOAuthGroups(force: Bool) async -> [ProviderGroup] {
        var providers = ["anthropic"]
        if let r = try? await get("/api/oauth/providers"), r.status == 200 {
            let raw = ((r.json as? [String: Any])?["providers"] as? [Any]) ?? (r.json as? [Any]) ?? []
            let names = raw.compactMap { item in
                QuotaParser.string(item)
                    ?? QuotaParser.string(QuotaParser.dict(item).flatMap { QuotaParser.first($0, ["id", "name", "provider"]) })
            }
            if !names.isEmpty { providers = names }
        }
        providers = providers.map { $0.lowercased() }.filter { !Self.codexProviderIDs.contains($0) }

        return await withTaskGroup(of: (Int, ProviderGroup?).self) { taskGroup in
            for (index, provider) in providers.enumerated() {
                taskGroup.addTask { (index, await fetchOAuthGroup(provider, force: force)) }
            }
            var found: [(Int, ProviderGroup)] = []
            for await (index, group) in taskGroup { if let group { found.append((index, group)) } }
            return found.sorted { $0.0 < $1.0 }.map(\.1)
        }
    }

    func fetchOAuthGroup(_ provider: String, force: Bool) async -> ProviderGroup? {
        let encoded = provider.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? provider
        // Cheap local read first: skip providers with no logged-in accounts.
        guard let list = try? await get("/api/oauth/accounts?provider=\(encoded)"), list.status == 200,
              !QuotaParser.accountArray(list.json).isEmpty else { return nil }

        // quota=1 is TTL-cached server side; refresh=1 bypasses the cache (manual Refresh only).
        let source = "/api/oauth/accounts?provider=\(encoded)&quota=1"
        var json = list.json
        var quotaError: String?
        do {
            let quota = try await get(source + (force ? "&refresh=1" : ""), timeout: force ? 45 : 20)
            try check(quota, source)
            json = quota.json
        } catch {
            quotaError = "Quota unavailable: " + Self.describe(error)
        }
        let info = QuotaParser.activeInfo(json)
        return ProviderGroup(id: provider, accounts: Self.fillIdleFiveHour(QuotaParser.accounts(from: json, activeID: info.id)),
                             activeAccountID: info.id, strategy: info.strategy, pinned: info.pinned,
                             error: quotaError, source: source)
    }

    /// Claude omits the 5h bucket entirely when an account has no active 5h window (the last one
    /// ended and it has not been used since). If a sibling account reports a 5h window, show the
    /// missing one as idle (100% left, not started) instead of silently dropping the row.
    static func fillIdleFiveHour(_ accounts: [Account]) -> [Account] {
        guard accounts.contains(where: { $0.windows.contains { $0.id == "5h" } }) else { return accounts }
        return accounts.map { account in
            guard !account.windows.isEmpty, !account.windows.contains(where: { $0.id == "5h" }) else { return account }
            return account.with(windows: [.idle(id: "5h", label: "5h")] + account.windows)
        }
    }
}
