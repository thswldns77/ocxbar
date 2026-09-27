import AppKit
import SwiftUI

struct MenuContentView: View {
    @ObservedObject var store: QuotaStore
    @State private var showSettings = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text("OpenCodex quota").font(.headline)
                Text(store.serverURL).font(.caption).foregroundStyle(.secondary)
            }
            if let error = store.lastError {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if showSettings {
                SettingsPanel(store: store) { showSettings = false }
            } else {
                groupList
            }
            Divider()
            footer
        }
        .padding(12)
        .frame(width: 360)
        .onAppear { store.refreshIfStale() }
    }

    @ViewBuilder private var groupList: some View {
        if let snap = store.snapshot, !snap.groups.isEmpty {
            if snap.groups.reduce(0, { $0 + $1.accounts.count }) > 6 {
                ScrollView { groups(snap) }.frame(height: 480)
            } else {
                groups(snap)
            }
        } else if store.snapshot != nil {
            Text("No accounts registered in OpenCodex.").font(.callout).foregroundStyle(.secondary)
        } else if store.isLoading {
            ProgressView().controlSize(.small)
        } else {
            Text("No data yet.").font(.callout).foregroundStyle(.secondary)
        }
    }

    private func groups(_ snap: Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            ForEach(snap.groups) { group in
                ProviderSection(group: group, showUsed: store.showUsed)
            }
        }
    }

    private var footer: some View {
        HStack(spacing: 10) {
            if let fetched = store.snapshot?.fetchedAt {
                Text("Updated \(Format.ago(fetched))").font(.caption2).foregroundStyle(.secondary)
            }
            Spacer()
            Button {
                Task { await store.refresh(force: true) }
            } label: {
                if store.isLoading {
                    ProgressView().controlSize(.mini)
                } else {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
            }
            .disabled(store.isLoading)
            .help("Re-probe quota from upstream now")
            Button { showSettings.toggle() } label: { Image(systemName: "gearshape") }
                .help("Settings")
            Button { NSApplication.shared.terminate(nil) } label: { Image(systemName: "power") }
                .help("Quit OCXBar")
        }
        .buttonStyle(.borderless)
    }
}

struct ProviderSection: View {
    let group: ProviderGroup
    let showUsed: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(group.name.uppercased())
                    .font(.system(size: 11, weight: .bold))
                    .foregroundStyle(.secondary)
                if let strategy = group.strategy {
                    Text("routing: \(strategy)\(group.pinned ? " (pinned)" : "")")
                        .font(.caption2).foregroundStyle(.tertiary)
                }
                Spacer()
            }
            if let error = group.error {
                Label(error, systemImage: "exclamationmark.triangle")
                    .font(.caption2).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
            ForEach(group.accounts) { account in
                AccountRow(account: account, showUsed: showUsed,
                           isBest: group.accounts.count > 1 && account.id == group.best?.id)
            }
        }
    }
}

struct AccountRow: View {
    let account: Account
    let showUsed: Bool
    let isBest: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 6) {
                Text(account.name).font(.system(.body, weight: .semibold)).lineLimit(1).truncationMode(.middle)
                if account.isActive { Badge(text: "ACTIVE", color: .accentColor) }
                if let plan = account.plan { Badge(text: plan, color: .gray) }
                if account.isPaused { Badge(text: "paused", color: .orange) }
                if account.needsReauth { Badge(text: "reauth", color: .red) }
                Spacer()
                if isBest { Image(systemName: "star.fill").font(.caption2).foregroundStyle(.yellow).help("Most headroom") }
                Text(Format.percent(account.remainingPercent))
                    .font(.system(.title3, design: .rounded, weight: .bold))
                    .monospacedDigit()
                    .foregroundStyle(QuotaColor.forRemaining(account.remainingPercent))
                    .help("Remaining on the tightest account-wide window")
            }
            if account.windows.isEmpty {
                Text("No quota data yet").font(.caption).foregroundStyle(.secondary)
            }
            ForEach(account.windows) { WindowRow(window: $0, showUsed: showUsed) }
            if let health = account.health, health != "healthy" {
                Text("Health: \(health)").font(.caption2).foregroundStyle(.orange)
            }
        }
    }
}

struct WindowRow: View {
    let window: QuotaWindow
    let showUsed: Bool

    var body: some View {
        HStack(spacing: 8) {
            Text(window.label).font(.caption).lineLimit(1).frame(width: 46, alignment: .leading)
                .foregroundStyle(window.isScoped ? .secondary : .primary)
                .help(window.isScoped ? "Model-scoped limit (does not block other models)" : "")
            QuotaBar(percent: showUsed ? window.usedPercent : window.remainingPercent,
                     color: QuotaColor.forRemaining(window.remainingPercent))
            Text(showUsed ? "\(Format.percent(window.usedPercent)) used" : "\(Format.percent(window.remainingPercent)) left")
                .font(.caption).monospacedDigit()
                .frame(width: 64, alignment: .trailing)
            Text(window.isIdle ? "not started" : window.resetAt.map { "↻ " + Format.until($0) } ?? "")
                .font(.caption2).foregroundStyle(.secondary).monospacedDigit()
                .lineLimit(1).minimumScaleFactor(0.8)
                .frame(width: 56, alignment: .trailing)
                .help(window.isIdle
                      ? "Window starts on the next request; the full limit is available"
                      : window.resetAt.map { "Resets \($0.formatted(date: .abbreviated, time: .shortened))" } ?? "")
        }
    }
}

struct Badge: View {
    let text: String
    let color: Color

    var body: some View {
        Text(text)
            .font(.system(size: 9, weight: .semibold))
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(color.opacity(0.18), in: Capsule())
            .foregroundStyle(color)
    }
}

struct QuotaBar: View {
    let percent: Double
    let color: Color

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.secondary.opacity(0.2))
                Capsule().fill(color).frame(width: geo.size.width * max(0, min(100, percent)) / 100)
            }
        }
        .frame(height: 6)
    }
}

enum QuotaColor {
    static func forRemaining(_ remaining: Double?) -> Color {
        guard let r = remaining else { return .secondary }
        if r < 10 { return .red }
        if r < 30 { return .orange }
        return .green
    }
}

struct SettingsPanel: View {
    @ObservedObject var store: QuotaStore
    let onClose: () -> Void

    @State private var url: String
    @State private var token: String
    @State private var launchAtLogin = LoginItem.isEnabled
    @State private var loginError: String?

    init(store: QuotaStore, onClose: @escaping () -> Void) {
        self.store = store
        self.onClose = onClose
        _url = State(initialValue: store.serverURL)
        _token = State(initialValue: store.tokenOverride)
    }

    private var providerChoices: [String] {
        var ids = store.snapshot?.groups.map(\.id) ?? []
        if !store.menuBarProvider.isEmpty && !ids.contains(store.menuBarProvider) { ids.append(store.menuBarProvider) }
        return ids
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Server URL").font(.caption).foregroundStyle(.secondary)
            TextField(OCXClient.defaultURL, text: $url).textFieldStyle(.roundedBorder)
            Text("Admin token (optional)").font(.caption).foregroundStyle(.secondary)
            SecureField("auto: ~/.opencodex/admin-api-token", text: $token).textFieldStyle(.roundedBorder)
            Picker("Menu bar", selection: $store.menuBarProvider) {
                Text("All providers").tag("")
                ForEach(providerChoices, id: \.self) { Text(ProviderNames.name($0)).tag($0) }
            }
            Picker("List shows", selection: $store.showUsed) {
                Text("Remaining").tag(false)
                Text("Used").tag(true)
            }
            .pickerStyle(.segmented)
            Toggle("Launch at login", isOn: $launchAtLogin)
                .disabled(!LoginItem.isAvailable)
                .onChange(of: launchAtLogin) { newValue in
                    guard newValue != LoginItem.isEnabled else { return }
                    do {
                        try LoginItem.set(newValue)
                        loginError = nil
                    } catch {
                        loginError = error.localizedDescription
                        launchAtLogin = LoginItem.isEnabled
                    }
                }
            if !LoginItem.isAvailable {
                Text("Launch at login needs the .app bundle (scripts/make-app.sh).").font(.caption2).foregroundStyle(.secondary)
            }
            if let loginError { Text(loginError).font(.caption2).foregroundStyle(.red) }
            HStack {
                Button("Default URL") { url = OCXClient.defaultURL }
                Spacer()
                Button("Cancel", action: onClose)
                Button("Save") {
                    store.apply(url: url, token: token)
                    onClose()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
    }
}
