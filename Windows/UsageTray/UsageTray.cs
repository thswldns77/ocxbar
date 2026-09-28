using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UsageTray
{
    internal sealed class Quota
    {
        public string Name;
        public double Remaining;
        public DateTimeOffset? Reset;
    }

    internal sealed class Usage
    {
        public string Name;
        public string Error;
        public string Source;
        public DateTime CheckedAt;
        public bool Stale;
        public readonly List<Quota> Quotas = new List<Quota>();
        public double? Headline
        {
            get
            {
                if (Quotas.Count == 0) return null;
                if (String.Equals(Name, "Claude Code", StringComparison.Ordinal))
                {
                    var weekly = Quotas.FirstOrDefault(q => q.Name == "주간");
                    if (weekly != null) return weekly.Remaining;
                }
                var common = Quotas.Where(q => !q.Name.EndsWith(" 주간", StringComparison.Ordinal)).ToArray();
                IEnumerable<Quota> selected = common.Length == 0 ? (IEnumerable<Quota>)Quotas : common;
                return selected.Min(q => q.Remaining);
            }
        }
    }

    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        public static object Parse(string value) { return Serializer.DeserializeObject(value); }
        public static string Stringify(object value) { return Serializer.Serialize(value); }
        public static IDictionary<string, object> Map(object value) { return value as IDictionary<string, object>; }
        public static object Get(object value, string key)
        {
            var map = Map(value);
            object result;
            return map != null && map.TryGetValue(key, out result) ? result : null;
        }
        public static string Text(object value) { return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture); }
        public static double? Number(object value)
        {
            double n;
            return value != null && double.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !double.IsNaN(n) && !double.IsInfinity(n) ? n : (double?)null;
        }
        public static DateTimeOffset? Reset(object value)
        {
            var number = Number(value);
            try
            {
                if (number.HasValue) return DateTimeOffset.FromUnixTimeSeconds((long)number.Value);
                DateTimeOffset result;
                if (DateTimeOffset.TryParse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result)) return result;
            }
            catch (ArgumentOutOfRangeException) { }
            return null;
        }
    }

    internal static class Providers
    {
        private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageTray");
        private static readonly string ClaudeCache = Path.Combine(DataDir, "claude-status.json");
        private static readonly string CodexUsageCache = Path.Combine(DataDir, "codex-usage.json");
        private static readonly string ClaudeUsageCache = Path.Combine(DataDir, "claude-usage.json");

        private static Usage SaveUsage(Usage usage, string path)
        {
            if (usage.Quotas.Count == 0) return usage;
            try
            {
                Directory.CreateDirectory(DataDir);
                var quotas = usage.Quotas.Select(q => new Dictionary<string, object> {
                    { "name", q.Name }, { "remaining", q.Remaining },
                    { "reset", q.Reset.HasValue ? (object)q.Reset.Value.ToUnixTimeSeconds() : null }
                }).ToArray();
                var data = new Dictionary<string, object> {
                    { "checkedAt", new DateTimeOffset(usage.CheckedAt).ToUnixTimeSeconds() },
                    { "source", usage.Source }, { "quotas", quotas }
                };
                File.WriteAllText(path, Json.Stringify(data), Encoding.UTF8);
            }
            catch { }
            return usage;
        }

        private static Usage ReadUsageCache(string path, string name)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromMinutes(30)) return null;
                var data = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                var usage = new Usage { Name = name, Source = Json.Text(Json.Get(data, "source")) ?? "이전 조회", Stale = true };
                var checkedAt = Json.Reset(Json.Get(data, "checkedAt"));
                usage.CheckedAt = checkedAt.HasValue ? checkedAt.Value.LocalDateTime : file.LastWriteTime;
                var rows = Json.Get(data, "quotas") as object[];
                if (rows == null) return null;
                foreach (var row in rows)
                {
                    var remaining = Json.Number(Json.Get(row, "remaining"));
                    var label = Json.Text(Json.Get(row, "name"));
                    if (remaining.HasValue && remaining.Value >= 0 && remaining.Value <= 100 && !String.IsNullOrWhiteSpace(label))
                        usage.Quotas.Add(new Quota { Name = label, Remaining = remaining.Value, Reset = Json.Reset(Json.Get(row, "reset")) });
                }
                return usage.Quotas.Count > 0 ? usage : null;
            }
            catch { return null; }
        }

        private static Usage CachedOrFailure(string path, string name, string error)
        {
            return ReadUsageCache(path, name) ?? Failure(name, error);
        }

        private static void Add(Usage usage, string name, object percentUsed, object reset)
        {
            var used = Json.Number(percentUsed);
            if (!used.HasValue || used.Value < 0 || used.Value > 100) return;
            usage.Quotas.Add(new Quota { Name = name, Remaining = Math.Max(0, 100 - used.Value), Reset = Json.Reset(reset) });
        }

        public static Usage ParseCodex(object response)
        {
            var usage = new Usage { Name = "Codex", Source = "Codex app-server", CheckedAt = DateTime.Now };
            var result = Json.Get(response, "result") ?? response;
            var byId = Json.Get(result, "rateLimitsByLimitId");
            var snapshot = Json.Get(byId, "codex") ?? Json.Get(result, "rateLimits");
            if (snapshot == null && Json.Map(byId) != null) snapshot = Json.Map(byId).Values.FirstOrDefault();
            if (snapshot == null) { usage.Error = "사용량 데이터가 없습니다."; return usage; }
            AddCodexWindow(usage, Json.Get(snapshot, "primary"));
            AddCodexWindow(usage, Json.Get(snapshot, "secondary"));
            if (usage.Quotas.Count == 0) usage.Error = "표시할 한도 정보가 없습니다.";
            return usage;
        }

        private static void AddCodexWindow(Usage usage, object window)
        {
            if (window == null) return;
            var mins = Json.Number(Json.Get(window, "windowDurationMins"));
            string name = !mins.HasValue ? "한도" : mins.Value <= 360 ? "5시간" : mins.Value >= 10000 ? "주간" : Math.Round(mins.Value / 60) + "시간";
            Add(usage, name, Json.Get(window, "usedPercent"), Json.Get(window, "resetsAt"));
        }

        public static Usage ParseClaude(object response, bool statusLine)
        {
            var usage = new Usage { Name = "Claude Code", Source = statusLine ? "Claude 상태줄" : "Claude OAuth", CheckedAt = DateTime.Now };
            if (statusLine)
            {
                var limits = Json.Get(response, "rate_limits");
                Add(usage, "5시간", Json.Get(Json.Get(limits, "five_hour"), "used_percentage"), Json.Get(Json.Get(limits, "five_hour"), "resets_at"));
                Add(usage, "주간", Json.Get(Json.Get(limits, "seven_day"), "used_percentage"), Json.Get(Json.Get(limits, "seven_day"), "resets_at"));
            }
            else
            {
                AddClaudeWindow(usage, "5시간", Json.Get(response, "five_hour"));
                AddClaudeWindow(usage, "주간", Json.Get(response, "seven_day"));
                AddClaudeWindow(usage, "Opus 주간", Json.Get(response, "seven_day_opus"));
                AddClaudeWindow(usage, "Sonnet 주간", Json.Get(response, "seven_day_sonnet"));
                var limits = Json.Get(response, "limits") as object[];
                if (limits != null)
                {
                    foreach (var limit in limits)
                    {
                        var kind = Json.Text(Json.Get(limit, "kind"));
                        string name = null;
                        if (kind == "session") name = "5시간";
                        else if (kind == "weekly_all") name = "주간";
                        else if (kind == "weekly_scoped")
                        {
                            var scope = Json.Get(limit, "scope");
                            var model = Json.Get(scope, "model");
                            name = (Json.Text(Json.Get(model, "display_name")) ?? Json.Text(Json.Get(scope, "display_name")) ?? "모델") + " 주간";
                        }
                        if (name == null || usage.Quotas.Any(q => q.Name == name)) continue;
                        Add(usage, name, Json.Get(limit, "percent") ?? Json.Get(limit, "percent_used"), Json.Get(limit, "resets_at"));
                    }
                }
                var extra = Json.Get(response, "extra_usage");
                if (usage.Quotas.Count == 0) Add(usage, "월간 지출 한도", Json.Get(extra, "utilization"), null);
            }
            if (usage.Quotas.Count == 0) usage.Error = "표시할 한도 정보가 없습니다.";
            return usage;
        }

        private static void AddClaudeWindow(Usage usage, string name, object window)
        {
            Add(usage, name, Json.Get(window, "utilization"), Json.Get(window, "resets_at"));
        }

        public static Usage FetchCodex()
        {
            var exe = FindCodex();
            if (exe == null) return Failure("Codex", "Codex CLI를 찾지 못했습니다. 설치 후 로그인하세요.");
            Process process = null;
            try
            {
                var info = new ProcessStartInfo(exe, "app-server --stdio")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                process = Process.Start(info);
                process.StandardInput.WriteLine(Json.Stringify(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { clientInfo = new { name = "usage-tray", title = "Usage Tray", version = "1.0" }, capabilities = new { } } }));
                process.StandardInput.Flush();
                var initialized = ReadResponse(process, 1, 25000);
                if (Json.Get(initialized, "error") != null) throw new Exception("Codex 초기화 오류");
                process.StandardInput.WriteLine(Json.Stringify(new { jsonrpc = "2.0", method = "initialized", @params = new { } }));
                process.StandardInput.WriteLine(Json.Stringify(new { jsonrpc = "2.0", id = 2, method = "account/rateLimits/read", @params = new { } }));
                process.StandardInput.Flush();
                var answer = ReadResponse(process, 2, 25000);
                var error = Json.Get(answer, "error");
                if (error != null) return Failure("Codex", SafeError(Json.Text(Json.Get(error, "message"))));
                var parsed = ParseCodex(answer);
                return parsed.Quotas.Count > 0 ? SaveUsage(parsed, CodexUsageCache) : CachedOrFailure(CodexUsageCache, "Codex", parsed.Error);
            }
            catch (Exception e) { return CachedOrFailure(CodexUsageCache, "Codex", SafeError(e.Message)); }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    process.Dispose();
                }
            }
        }

        private static object ReadResponse(Process process, int id, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                var read = Task.Run(() => process.StandardOutput.ReadLine());
                if (!read.Wait(remaining)) throw new TimeoutException("Codex 응답 시간 초과");
                var line = read.Result;
                if (line == null) throw new Exception("Codex 서버가 종료되었습니다.");
                object value;
                try { value = Json.Parse(line); } catch { continue; }
                var responseId = Json.Number(Json.Get(value, "id"));
                if (responseId.HasValue && responseId.Value == id) return value;
            }
            throw new TimeoutException("Codex 응답 시간 초과");
        }

        private static string FindCodex()
        {
            var overridePath = Environment.GetEnvironmentVariable("CODEX_EXE");
            if (!String.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try { var candidate = Path.Combine(dir.Trim('"'), "codex.exe"); if (File.Exists(candidate)) return candidate; } catch { }
            }
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            try { return Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault(); } catch { return null; }
        }

        public static Usage FetchClaude()
        {
            var token = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
            if (String.IsNullOrWhiteSpace(token))
            {
                var configRoot = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (String.IsNullOrWhiteSpace(configRoot)) configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                try
                {
                    var credentials = Json.Parse(File.ReadAllText(Path.Combine(configRoot, ".credentials.json"), Encoding.UTF8));
                    token = Json.Text(Json.Get(Json.Get(credentials, "claudeAiOauth"), "accessToken"));
                }
                catch { }
            }
            if (!String.IsNullOrWhiteSpace(token))
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
                    request.Method = "GET";
                    request.Timeout = 15000;
                    request.ReadWriteTimeout = 15000;
                    request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                    request.Headers["anthropic-beta"] = "oauth-2025-04-20";
                    request.UserAgent = "UsageTray/1.0";
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        var parsed = ParseClaude(Json.Parse(reader.ReadToEnd()), false);
                        if (parsed.Quotas.Count > 0) return SaveUsage(parsed, ClaudeUsageCache);
                    }
                }
                catch (WebException e)
                {
                    var http = e.Response as HttpWebResponse;
                    if (http != null && http.StatusCode == HttpStatusCode.Unauthorized)
                        return Failure("Claude Code", "Claude Code 로그인 토큰이 만료되었습니다. Claude Code에서 다시 로그인하세요.");
                    if (http != null && http.StatusCode == (HttpStatusCode)429)
                        return CacheOrFailure("Claude 사용량 조회가 잠시 제한되었습니다.");
                }
                catch { }
            }
            if (String.IsNullOrWhiteSpace(token)) return Failure("Claude Code", FindClaude() == null ? "Claude Code를 찾지 못했습니다." : "Claude 앱의 로그인과 사용량 조회 인증은 별개입니다. 로그인 버튼을 눌러 주세요.");
            return CacheOrFailure("Claude 사용량을 조회하지 못했습니다.");
        }

        private static Usage CacheOrFailure(string error)
        {
            try
            {
                var file = new FileInfo(ClaudeCache);
                if (file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMinutes(15))
                {
                    var cached = ParseClaude(Json.Parse(File.ReadAllText(ClaudeCache, Encoding.UTF8)), true);
                    if (cached.Quotas.Count > 0) { cached.CheckedAt = file.LastWriteTime; return cached; }
                }
            }
            catch { }
            return CachedOrFailure(ClaudeUsageCache, "Claude Code", error);
        }

        public static void CaptureClaudeStatus()
        {
            try
            {
                var input = Console.In.ReadToEnd();
                var parsed = Json.Parse(input);
                var usage = ParseClaude(parsed, true);
                if (usage.Quotas.Count > 0)
                {
                    Directory.CreateDirectory(DataDir);
                    var retained = new Dictionary<string, object> { { "rate_limits", Json.Get(parsed, "rate_limits") } };
                    File.WriteAllText(ClaudeCache, Json.Stringify(retained), Encoding.UTF8);
                }
                var model = Json.Text(Json.Get(Json.Get(parsed, "model"), "display_name")) ?? "Claude";
                Console.Write(model);
                foreach (var quota in usage.Quotas) Console.Write(" | " + quota.Name + " " + Math.Round(quota.Remaining) + "% 남음");
                Console.WriteLine();
            }
            catch { Console.WriteLine("Claude"); }
        }

        private static Usage Failure(string name, string message) { return new Usage { Name = name, Error = message, CheckedAt = DateTime.Now }; }
        private static string SafeError(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "사용량을 조회하지 못했습니다.";
            if (value.IndexOf("authentication required", StringComparison.OrdinalIgnoreCase) >= 0) return "로그인이 필요합니다. Codex CLI에서 로그인하세요.";
            return value.Length > 140 ? value.Substring(0, 140) : value;
        }

        public static string FindClaude()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                foreach (var file in new[] { "claude.exe", "claude.cmd" })
                {
                    try { var candidate = Path.Combine(dir.Trim('"'), file); if (File.Exists(candidate)) return candidate; } catch { }
                }
            }
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            if (File.Exists(local)) return local;
            var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "claude.exe");
            if (File.Exists(winget)) return winget;
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            try
            {
                foreach (var package in Directory.GetDirectories(packages, "Claude_*"))
                {
                    var bundled = Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude-code");
                    if (!Directory.Exists(bundled)) continue;
                    var found = Directory.GetFiles(bundled, "claude.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        public static void StartLogin(bool isCodex)
        {
            var exe = isCodex ? FindCodex() : FindClaude();
            if (exe == null)
            {
                var url = isCodex ? "https://developers.openai.com/codex/quickstart" : "https://code.claude.com/docs/en/setup";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                MessageBox.Show((isCodex ? "Codex" : "Claude Code") + " CLI 설치 안내를 브라우저에서 열었습니다. 설치 후 로그인 버튼을 다시 눌러 주세요.", "Usage Tray", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe, isCodex ? "login" : "auth login") { UseShellExecute = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
                MessageBox.Show("열린 로그인 창에서 브라우저 인증을 마친 뒤 '새로고침'을 누르세요.", "Usage Tray", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e) { MessageBox.Show("로그인 창을 열지 못했습니다: " + e.Message, "Usage Tray", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon codexIcon;
        private readonly NotifyIcon claudeIcon;
        private readonly System.Windows.Forms.Timer timer;
        private readonly Dashboard dashboard;
        private readonly SummaryStrip summaryStrip;
        private Usage codex = new Usage { Name = "Codex", Error = "조회 중" };
        private Usage claude = new Usage { Name = "Claude Code", Error = "조회 중" };
        private bool refreshing;
        private bool showSummaryStrip;
        private bool summaryInsideTaskbar;
        private AppearanceSettings appearance;
        private readonly Mutex mutex;

        public TrayApp(Mutex singleInstance)
        {
            mutex = singleInstance;
            dashboard = new Dashboard(RefreshNow);
            codexIcon = MakeIcon("C", Color.FromArgb(30, 75, 69));
            claudeIcon = MakeIcon("A", Color.FromArgb(175, 95, 65));
            showSummaryStrip = ReadShowSummaryStrip();
            summaryInsideTaskbar = ReadSummaryInsideTaskbar();
            appearance = AppearanceSettings.Load();
            summaryStrip = new SummaryStrip(ShowDashboard, appearance) { ContextMenuStrip = codexIcon.ContextMenuStrip };
            summaryStrip.InsideTaskbar = summaryInsideTaskbar;
            dashboard.VisibleChanged += (s, e) => UpdateSummaryVisibility();
            summaryStrip.UpdateUsage(codex, claude);
            UpdateSummaryVisibility();
            timer = new System.Windows.Forms.Timer { Interval = 300000 };
            timer.Tick += (s, e) => RefreshNow();
            timer.Start();
            RefreshNow();
        }

        private NotifyIcon MakeIcon(string letter, Color color)
        {
            var icon = new NotifyIcon { Visible = true, Text = letter == "C" ? "Codex 조회 중" : "Claude Code 조회 중", Icon = IconPainter.Draw(letter, null, color) };
            var menu = new ContextMenuStrip();
            menu.Items.Add("사용량 보기", null, (s, e) => ShowDashboard());
            menu.Items.Add("지금 새로고침", null, (s, e) => RefreshNow());
            menu.Items.Add("Codex 로그인 / 계정 변경", null, (s, e) => Providers.StartLogin(true));
            menu.Items.Add("Claude Code 로그인 / 계정 변경", null, (s, e) => Providers.StartLogin(false));
            var stripOption = new ToolStripMenuItem("가로 사용량 표시");
            stripOption.Click += (s, e) =>
            {
                showSummaryStrip = !showSummaryStrip;
                SaveShowSummaryStrip(showSummaryStrip);
                UpdateSummaryVisibility();
            };
            menu.Opening += (s, e) => stripOption.Checked = showSummaryStrip;
            menu.Items.Add(stripOption);
            var dockOption = new ToolStripMenuItem("작업표시줄 안쪽에 표시");
            dockOption.Click += (s, e) =>
            {
                summaryInsideTaskbar = !summaryInsideTaskbar;
                SaveSummaryInsideTaskbar(summaryInsideTaskbar);
                summaryStrip.InsideTaskbar = summaryInsideTaskbar;
                summaryStrip.PlaceNearTaskbar();
            };
            menu.Opening += (s, e) => dockOption.Checked = summaryInsideTaskbar;
            menu.Items.Add(dockOption);
            menu.Items.Add("표시 설정...", null, (s, e) => ShowAppearanceSettings());
            var startup = new ToolStripMenuItem("Windows 시작 시 실행") { Checked = IsStartup() };
            startup.Click += (s, e) => { SetStartup(!IsStartup()); startup.Checked = IsStartup(); };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, (s, e) => ExitThread());
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDashboard(); };
            return icon;
        }

        private void ShowDashboard()
        {
            dashboard.UpdateUsage(codex, claude);
            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            dashboard.Location = new Point(work.Right - dashboard.Width - 12, work.Bottom - dashboard.Height - 12);
            dashboard.Show();
            dashboard.WindowState = FormWindowState.Normal;
            dashboard.Activate();
        }

        private void ShowAppearanceSettings()
        {
            using (var dialog = new AppearanceDialog(appearance))
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                appearance = dialog.Settings;
                appearance.Save();
                summaryStrip.ApplyAppearance(appearance);
            }
        }

        private void UpdateSummaryVisibility()
        {
            if (summaryStrip == null) return;
            if (showSummaryStrip && !dashboard.Visible)
            {
                summaryStrip.PlaceNearTaskbar();
                summaryStrip.Show();
                summaryStrip.PlaceNearTaskbar();
            }
            else summaryStrip.Hide();
        }

        private async void RefreshNow()
        {
            if (refreshing) return;
            refreshing = true;
            dashboard.SetRefreshing(true);
            try
            {
                var first = Task.Run(() => Providers.FetchCodex());
                var second = Task.Run(() => Providers.FetchClaude());
                await Task.WhenAll(first, second);
                codex = PreserveTransient(first.Result, codex);
                claude = PreserveTransient(second.Result, claude);
                UpdateIcon(codexIcon, codex, "C", Color.FromArgb(30, 75, 69));
                UpdateIcon(claudeIcon, claude, "A", Color.FromArgb(175, 95, 65));
                summaryStrip.UpdateUsage(codex, claude);
                dashboard.UpdateUsage(codex, claude);
            }
            finally { refreshing = false; dashboard.SetRefreshing(false); }
        }

        private static Usage PreserveTransient(Usage current, Usage previous)
        {
            if (current.Quotas.Count > 0 || previous.Quotas.Count == 0) return current;
            var error = current.Error ?? "";
            if (error.Contains("시간 초과") || error.Contains("서버가 종료") || error.Contains("조회하지 못했습니다") || error.Contains("잠시 제한"))
            {
                previous.Stale = true;
                return previous;
            }
            return current;
        }

        private static void UpdateIcon(NotifyIcon icon, Usage usage, string letter, Color color)
        {
            var old = icon.Icon;
            icon.Icon = IconPainter.Draw(letter, usage.Headline, color);
            if (old != null) old.Dispose();
            var quotas = usage.Quotas.AsEnumerable();
            if (letter == "A") quotas = quotas.OrderBy(q => q.Name == "주간" ? 0 : 1);
            var summary = usage.Headline.HasValue ? String.Join(", ", quotas.Select(q => q.Name + " " + Math.Round(q.Remaining) + "% 남음")) : usage.Error;
            var tooltip = usage.Name + (usage.Stale ? " (이전 조회): " : ": ") + summary;
            icon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
        }

        private static bool IsStartup()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key != null && key.GetValue("UsageTray") != null;
        }
        private static bool ReadShowSummaryStrip()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\UsageTray"))
                    return key == null || Convert.ToInt32(key.GetValue("ShowSummaryStrip", 1)) != 0;
            }
            catch { return true; }
        }
        private static void SaveShowSummaryStrip(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\UsageTray"))
                    key.SetValue("ShowSummaryStrip", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }
        private static bool ReadSummaryInsideTaskbar()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\UsageTray"))
                    return key == null || Convert.ToInt32(key.GetValue("SummaryInsideTaskbar", 1)) != 0;
            }
            catch { return true; }
        }
        private static void SaveSummaryInsideTaskbar(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\UsageTray"))
                    key.SetValue("SummaryInsideTaskbar", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }
        private static void SetStartup(bool enabled)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null) return;
                if (enabled) key.SetValue("UsageTray", "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue("UsageTray", false);
            }
        }
        protected override void ExitThreadCore()
        {
            timer.Stop();
            codexIcon.Visible = false;
            claudeIcon.Visible = false;
            codexIcon.Dispose();
            claudeIcon.Dispose();
            summaryStrip.Dispose();
            dashboard.Dispose();
            mutex.ReleaseMutex();
            mutex.Dispose();
            base.ExitThreadCore();
        }
    }

    internal sealed class AppearanceSettings
    {
        public float FontSize = 11f;
        public bool Bold = true;
        public bool AutoTextColor = true;
        public Color TextColor = Color.FromArgb(18, 30, 24);

        public AppearanceSettings Copy()
        {
            return new AppearanceSettings { FontSize = FontSize, Bold = Bold, AutoTextColor = AutoTextColor, TextColor = TextColor };
        }

        public static AppearanceSettings Load()
        {
            var value = new AppearanceSettings();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\UsageTray"))
                {
                    if (key == null) return value;
                    value.FontSize = Math.Max(8f, Math.Min(12f, Convert.ToInt32(key.GetValue("SummaryFontTenths", 110)) / 10f));
                    value.Bold = Convert.ToInt32(key.GetValue("SummaryFontBold", 1)) != 0;
                    value.AutoTextColor = Convert.ToInt32(key.GetValue("SummaryAutoTextColor", 1)) != 0;
                    value.TextColor = Color.FromArgb(Convert.ToInt32(key.GetValue("SummaryTextColor", value.TextColor.ToArgb())));
                }
            }
            catch { return new AppearanceSettings(); }
            return value;
        }

        public void Save()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\UsageTray"))
                {
                    key.SetValue("SummaryFontTenths", (int)Math.Round(FontSize * 10), RegistryValueKind.DWord);
                    key.SetValue("SummaryFontBold", Bold ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("SummaryAutoTextColor", AutoTextColor ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("SummaryTextColor", TextColor.ToArgb(), RegistryValueKind.DWord);
                }
            }
            catch { }
        }
    }

    internal sealed class AppearanceDialog : Form
    {
        private readonly NumericUpDown fontSize;
        private readonly CheckBox bold;
        private readonly CheckBox autoColor;
        private readonly Button chooseColor;
        private readonly Panel preview;
        private Color textColor;
        public AppearanceSettings Settings { get; private set; }

        public AppearanceDialog(AppearanceSettings current)
        {
            Settings = current.Copy();
            textColor = Settings.TextColor;
            Text = "가로 사용량 표시 설정";
            Font = new Font("Malgun Gothic", 9f);
            ClientSize = new Size(390, 276);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;

            Controls.Add(new Label { Text = "글자 크기", Location = new Point(20, 22), Size = new Size(92, 25), TextAlign = ContentAlignment.MiddleLeft });
            fontSize = new NumericUpDown { Location = new Point(124, 22), Size = new Size(72, 25), Minimum = 8, Maximum = 12,
                DecimalPlaces = 1, Increment = 0.5m, Value = (decimal)Settings.FontSize };
            Controls.Add(fontSize);
            Controls.Add(new Label { Text = "pt  (8–12)", Location = new Point(204, 22), Size = new Size(120, 25), TextAlign = ContentAlignment.MiddleLeft });

            bold = new CheckBox { Text = "굵은 글씨", Location = new Point(20, 57), Size = new Size(180, 27), Checked = Settings.Bold };
            Controls.Add(bold);
            autoColor = new CheckBox { Text = "작업표시줄에 맞춰 글자색 자동 선택", Location = new Point(20, 91), Size = new Size(342, 27), Checked = Settings.AutoTextColor };
            Controls.Add(autoColor);
            chooseColor = new Button { Text = "글자색 선택...", Location = new Point(20, 124), Size = new Size(118, 29) };
            chooseColor.Click += (s, e) =>
            {
                using (var colorDialog = new ColorDialog { Color = textColor, FullOpen = true })
                    if (colorDialog.ShowDialog(this) == DialogResult.OK) { textColor = colorDialog.Color; preview.Invalidate(); }
            };
            Controls.Add(chooseColor);

            Controls.Add(new Label { Text = "미리보기", Location = new Point(20, 164), Size = new Size(120, 22) });
            preview = new Panel { Location = new Point(20, 187), Size = new Size(350, 44), BackColor = Color.FromArgb(236, 242, 224) };
            preview.Paint += PaintPreview;
            Controls.Add(preview);

            var ok = new Button { Text = "저장", Location = new Point(214, 241), Size = new Size(75, 27), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "취소", Location = new Point(295, 241), Size = new Size(75, 27), DialogResult = DialogResult.Cancel };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            fontSize.ValueChanged += (s, e) => preview.Invalidate();
            bold.CheckedChanged += (s, e) => preview.Invalidate();
            autoColor.CheckedChanged += (s, e) => { chooseColor.Enabled = !autoColor.Checked; preview.Invalidate(); };
            chooseColor.Enabled = !autoColor.Checked;
            ok.Click += (s, e) =>
            {
                Settings.FontSize = (float)fontSize.Value;
                Settings.Bold = bold.Checked;
                Settings.AutoTextColor = autoColor.Checked;
                Settings.TextColor = textColor;
            };
        }

        private void PaintPreview(object sender, PaintEventArgs e)
        {
            var color = autoColor.Checked ? Color.FromArgb(18, 30, 24) : textColor;
            var style = bold.Checked ? FontStyle.Bold : FontStyle.Regular;
            using (var font = new Font("Malgun Gothic", (float)fontSize.Value, style))
            {
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                TextRenderer.DrawText(e.Graphics, "Codex 5h 96% / 주간 95%", font, new Rectangle(7, 0, preview.Width - 14, 22), color, flags);
                TextRenderer.DrawText(e.Graphics, "Claude 5h 64% / 주간 71% / Fable 95%", font, new Rectangle(7, 22, preview.Width - 14, 22), color, flags);
            }
        }
    }

    internal sealed class SummaryStrip : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        private readonly Action openDashboard;
        private readonly System.Windows.Forms.Timer positionTimer;
        private readonly ToolTip tooltip = new ToolTip();
        private Font compactLabelFont;
        private AppearanceSettings appearance;
        private Bitmap taskbarBackground;
        private bool lightTaskbar = true;
        private string codexLine = "Codex --";
        private string claudeLine = "Claude --";
        private double? codexRemaining;
        private double? claudeRemaining;
        private bool insideTaskbar;
        private bool mouseDown;
        private bool dragged;
        private int dragStartMouseX;
        private int dragStartLeft;
        private int taskbarGap;
        public bool InsideTaskbar
        {
            get { return insideTaskbar; }
            set
            {
                insideTaskbar = value;
                ResizeForContent();
                Cursor = value ? Cursors.SizeWE : Cursors.Hand;
                UpdateTooltip();
                Invalidate();
            }
        }

        public SummaryStrip(Action open, AppearanceSettings initialAppearance)
        {
            openDashboard = open;
            appearance = initialAppearance.Copy();
            compactLabelFont = new Font("Malgun Gothic", appearance.FontSize, appearance.Bold ? FontStyle.Bold : FontStyle.Regular);
            ClientSize = new Size(352, 44);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(251, 252, 250);
            DoubleBuffered = true;
            taskbarGap = ReadTaskbarGap();
            MouseDown += OnStripMouseDown;
            MouseMove += OnStripMouseMove;
            MouseUp += OnStripMouseUp;
            MouseCaptureChanged += (s, e) => { if (!Capture) mouseDown = false; };
            positionTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            positionTimer.Tick += (s, e) => { if (Visible) PlaceNearTaskbar(); };
            positionTimer.Start();
        }

        public void ApplyAppearance(AppearanceSettings settings)
        {
            appearance = settings.Copy();
            var previous = compactLabelFont;
            compactLabelFont = new Font("Malgun Gothic", appearance.FontSize, appearance.Bold ? FontStyle.Bold : FontStyle.Regular);
            previous.Dispose();
            ResizeForContent();
            if (Visible) PlaceNearTaskbar();
            Invalidate();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var value = base.CreateParams;
                value.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return value;
            }
        }

        public void UpdateUsage(Usage codex, Usage claude)
        {
            codexRemaining = codex.Headline;
            claudeRemaining = claude.Headline;
            codexLine = "Codex " + FormatTaskbarQuotas(codex, false);
            claudeLine = "Claude " + FormatTaskbarQuotas(claude, true);
            ResizeForContent();
            UpdateTooltip();
            if (Visible) PlaceNearTaskbar();
            Invalidate();
        }

        internal static string FormatTaskbarQuotas(Usage usage, bool includeFable)
        {
            var parts = new List<string>();
            var fiveHour = usage.Quotas.FirstOrDefault(q => q.Name == "5시간");
            var weekly = usage.Quotas.FirstOrDefault(q => q.Name == "주간");
            var fable = includeFable ? usage.Quotas.FirstOrDefault(q => q.Name.StartsWith("Fable", StringComparison.OrdinalIgnoreCase)) : null;
            if (fiveHour != null) parts.Add("5h " + Math.Round(fiveHour.Remaining) + "%");
            if (weekly != null) parts.Add("주간 " + Math.Round(weekly.Remaining) + "%");
            if (fable != null) parts.Add("Fable " + Math.Round(fable.Remaining) + "%");
            if (parts.Count == 0 && usage.Headline.HasValue) parts.Add(Math.Round(usage.Headline.Value) + "%");
            return parts.Count == 0 ? "--" : String.Join(" / ", parts);
        }

        private void ResizeForContent()
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            var textWidth = Math.Max(TextRenderer.MeasureText(codexLine, compactLabelFont, Size.Empty, flags).Width,
                                     TextRenderer.MeasureText(claudeLine, compactLabelFont, Size.Empty, flags).Width);
            ClientSize = new Size(InsideTaskbar ? Math.Max(208, textWidth + 16) : Math.Max(352, textWidth + 16), 44);
        }

        private void UpdateTooltip()
        {
            tooltip.SetToolTip(this, codexLine + "\n" + claudeLine +
                (InsideTaskbar ? "\n좌우로 드래그하여 옮기거나 클릭해 자세히 봅니다." : "\n클릭하면 자세한 사용량을 봅니다."));
        }

        private static int ReadTaskbarGap()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\UsageTray"))
                    return key == null ? 8 : Math.Max(8, Convert.ToInt32(key.GetValue("TaskbarGap", 8)));
            }
            catch { return 8; }
        }

        private void SaveTaskbarGap()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\UsageTray"))
                    key.SetValue("TaskbarGap", taskbarGap, RegistryValueKind.DWord);
            }
            catch { }
        }

        private bool TryGetTaskbarBand(out NativeRect bounds, out int rightEdge)
        {
            bounds = new NativeRect();
            rightEdge = 0;
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out bounds) ||
                bounds.Right - bounds.Left <= Width + 50 || bounds.Bottom - bounds.Top < Height) return false;
            var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            NativeRect trayBounds;
            rightEdge = tray != IntPtr.Zero && GetWindowRect(tray, out trayBounds) &&
                        trayBounds.Left > bounds.Left + Width + 16 && trayBounds.Left < bounds.Right
                ? trayBounds.Left : bounds.Right - 372;
            return rightEdge - Width - 8 >= bounds.Left + 8;
        }

        private void OnStripMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !InsideTaskbar) return;
            mouseDown = true;
            dragged = false;
            dragStartMouseX = System.Windows.Forms.Cursor.Position.X;
            dragStartLeft = Left;
            Capture = true;
        }

        private void OnStripMouseMove(object sender, MouseEventArgs e)
        {
            if (!mouseDown || !InsideTaskbar) return;
            var delta = System.Windows.Forms.Cursor.Position.X - dragStartMouseX;
            if (!dragged && Math.Abs(delta) < 4) return;
            dragged = true;
            NativeRect bounds;
            int rightEdge;
            if (!TryGetTaskbarBand(out bounds, out rightEdge)) return;
            var left = Math.Max(bounds.Left + 8, Math.Min(rightEdge - Width - 8, dragStartLeft + delta));
            if (Left != left) SetWindowPos(Handle, new IntPtr(-1), left, Top, Width, Height, 0x0010);
        }

        private void OnStripMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var wasDragged = dragged;
            mouseDown = false;
            dragged = false;
            Capture = false;
            if (wasDragged)
            {
                NativeRect bounds;
                int rightEdge;
                if (TryGetTaskbarBand(out bounds, out rightEdge))
                {
                    taskbarGap = Math.Max(8, rightEdge - (Left + Width));
                    SaveTaskbarGap();
                }
                PlaceNearTaskbar();
            }
            else if (ClientRectangle.Contains(e.Location)) openDashboard();
        }

        public void PlaceNearTaskbar()
        {
            if (mouseDown) return;
            var screen = Screen.PrimaryScreen;
            var work = screen.WorkingArea;
            var point = new Point(work.Right - Width - 12, work.Top > screen.Bounds.Top ? work.Top + 8 : work.Bottom - Height - 8);
            var docked = false;
            NativeRect taskbarBounds = new NativeRect();
            if (InsideTaskbar)
            {
                int rightEdge;
                if (TryGetTaskbarBand(out taskbarBounds, out rightEdge))
                {
                    docked = true;
                    point = new Point(Math.Max(taskbarBounds.Left + 8, Math.Min(rightEdge - Width - 8, rightEdge - Width - taskbarGap)),
                        taskbarBounds.Top + (taskbarBounds.Bottom - taskbarBounds.Top - Height) / 2);
                }
            }
            if (Location != point) Location = point;
            if (docked && Visible)
            {
                SetWindowPos(Handle, new IntPtr(-1), point.X, point.Y, Width, Height, 0x0010);
                SampleTaskbarBackground(taskbarBounds);
            }
        }

        private void SampleTaskbarBackground(NativeRect taskbarBounds)
        {
            if (taskbarBounds.Bottom - taskbarBounds.Top < Height + 2) return;
            try
            {
                using (var topLine = new Bitmap(Width, 1))
                using (var bottomLine = new Bitmap(Width, 1))
                using (var topGraphics = Graphics.FromImage(topLine))
                using (var bottomGraphics = Graphics.FromImage(bottomLine))
                {
                    topGraphics.CopyFromScreen(Left, taskbarBounds.Top + 1, 0, 0, new Size(Width, 1));
                    bottomGraphics.CopyFromScreen(Left, taskbarBounds.Bottom - 1, 0, 0, new Size(Width, 1));
                    var background = new Bitmap(Width, Height);
                    for (int x = 0; x < Width; x++)
                    {
                        var top = topLine.GetPixel(x, 0);
                        var bottom = bottomLine.GetPixel(x, 0);
                        for (int y = 0; y < Height; y++)
                        {
                            var fraction = (double)(y + 1) / (Height + 1);
                            background.SetPixel(x, y, Color.FromArgb(
                                (int)Math.Round(top.R + (bottom.R - top.R) * fraction),
                                (int)Math.Round(top.G + (bottom.G - top.G) * fraction),
                                (int)Math.Round(top.B + (bottom.B - top.B) * fraction)));
                        }
                    }
                    var middle = background.GetPixel(Width / 2, Height / 2);
                    lightTaskbar = middle.R * 0.2126 + middle.G * 0.7152 + middle.B * 0.0722 >= 145;
                    var previous = taskbarBackground;
                    taskbarBackground = background;
                    if (previous != null) previous.Dispose();
                    Invalidate();
                }
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            if (InsideTaskbar)
            {
                if (taskbarBackground != null && taskbarBackground.Size == ClientSize) g.DrawImageUnscaled(taskbarBackground, 0, 0);
                else using (var background = new SolidBrush(BackColor)) g.FillRectangle(background, ClientRectangle);
            }
            else
            {
                using (var background = new SolidBrush(BackColor)) g.FillRectangle(background, ClientRectangle);
                using (var border = new Pen(Color.FromArgb(185, 194, 188))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            var taskbarText = appearance.AutoTextColor
                ? (lightTaskbar ? Color.FromArgb(18, 30, 24) : Color.FromArgb(250, 253, 250))
                : appearance.TextColor;
            TextRenderer.DrawText(g, codexLine, compactLabelFont, new Rectangle(8, 0, Width - 16, 22),
                InsideTaskbar ? taskbarText : (appearance.AutoTextColor ? UsageColor(codexRemaining) : taskbarText), flags);
            TextRenderer.DrawText(g, claudeLine, compactLabelFont, new Rectangle(8, 22, Width - 16, 22),
                InsideTaskbar ? taskbarText : (appearance.AutoTextColor ? UsageColor(claudeRemaining) : taskbarText), flags);
        }

        private static Color UsageColor(double? remaining)
        {
            if (!remaining.HasValue) return Color.Gray;
            if (remaining.Value < 10) return Color.FromArgb(195, 56, 56);
            if (remaining.Value < 30) return Color.FromArgb(194, 119, 31);
            return Color.FromArgb(37, 126, 82);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                positionTimer.Stop();
                positionTimer.Dispose();
                tooltip.Dispose();
                compactLabelFont.Dispose();
                if (taskbarBackground != null) taskbarBackground.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class IconPainter
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool DestroyIcon(IntPtr handle);
        public static Icon Draw(string letter, double? remaining, Color baseColor)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                var color = !remaining.HasValue ? Color.FromArgb(100, 106, 113) : remaining.Value < 10 ? Color.FromArgb(196, 54, 54) : remaining.Value < 25 ? Color.FromArgb(210, 139, 43) : baseColor;
                using (var brush = new SolidBrush(color)) g.FillRoundedRectangle(brush, new Rectangle(0, 0, 32, 32), 6);
                using (var font = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.White)) g.DrawString(letter, font, brush, 2, 0);
                var number = !remaining.HasValue ? "?" : Math.Round(remaining.Value).ToString(CultureInfo.InvariantCulture);
                using (var font = new Font("Segoe UI", number.Length == 3 ? 14 : 18, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.White))
                {
                    var size = g.MeasureString(number, font);
                    g.DrawString(number, font, brush, (32 - size.Width) / 2, 12);
                }
                var handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        private static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle r, int radius)
        {
            using (var path = new GraphicsPath())
            {
                path.AddArc(r.Left, r.Top, radius * 2, radius * 2, 180, 90);
                path.AddArc(r.Right - radius * 2, r.Top, radius * 2, radius * 2, 270, 90);
                path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
                path.AddArc(r.Left, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }
    }

    internal sealed class Dashboard : Form
    {
        private readonly Panel list = new Panel();
        private readonly Button refresh = new Button();
        private readonly Label updated = new Label();
        public Dashboard(Action refreshAction)
        {
            Text = "Usage Tray";
            ClientSize = new Size(452, 535);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(247, 248, 249);
            Font = new Font("Malgun Gothic", 9);
            var title = new Label { Text = "AI 사용량", Font = new Font("Malgun Gothic", 17, FontStyle.Bold), Location = new Point(16, 12), AutoSize = true };
            Controls.Add(title);
            var subtitle = new Label { Text = "Codex와 Claude Code · 공통 한도 중 가장 적게 남은 값", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(18, 44) };
            Controls.Add(subtitle);
            list.Location = new Point(14, 72);
            list.Size = new Size(424, 405);
            list.AutoScroll = true;
            Controls.Add(list);
            refresh.Text = "새로고침";
            refresh.Location = new Point(330, 490);
            refresh.Size = new Size(106, 32);
            refresh.Click += (s, e) => refreshAction();
            Controls.Add(refresh);
            updated.ForeColor = Color.DimGray;
            updated.Location = new Point(18, 496);
            updated.Size = new Size(300, 24);
            updated.Text = "계정 상태 확인 중";
            Controls.Add(updated);
            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        }
        public void SetRefreshing(bool active) { refresh.Enabled = !active; refresh.Text = active ? "조회 중..." : "새로고침"; }
        public void UpdateUsage(Usage codex, Usage claude)
        {
            list.SuspendLayout();
            list.AutoScroll = false;
            list.AutoScrollMinSize = Size.Empty;
            foreach (Control old in list.Controls.Cast<Control>().ToArray()) old.Dispose();
            list.Controls.Clear();
            int y = 0;
            var first = MakeCard(codex, Color.FromArgb(32, 87, 76), true);
            first.Location = new Point(0, y);
            list.Controls.Add(first);
            y += first.Height + 10;
            var second = MakeCard(claude, Color.FromArgb(183, 99, 67), false);
            second.Location = new Point(0, y);
            list.Controls.Add(second);
            y += second.Height;
            var workingHeight = Screen.FromPoint(Cursor.Position).WorkingArea.Height;
            var windowFrameHeight = Height - ClientSize.Height;
            var maxListHeight = Math.Max(260, workingHeight - list.Top - 14 - refresh.Height - 13 - windowFrameHeight - 24);
            var contentHeight = y + 8;
            list.Height = Math.Min(maxListHeight, contentHeight);
            if (contentHeight > maxListHeight)
            {
                list.AutoScrollMinSize = new Size(0, contentHeight);
                list.AutoScroll = true;
            }
            list.AutoScrollPosition = Point.Empty;
            list.ResumeLayout();
            refresh.Top = list.Bottom + 14;
            updated.Top = refresh.Top + 6;
            ClientSize = new Size(452, refresh.Bottom + 13);
            updated.Text = "최근 확인  " + DateTime.Now.ToString("HH:mm:ss") + "  ·  5분마다 갱신";
        }

        private static Panel MakeCard(Usage usage, Color accent, bool isCodex)
        {
            var count = Math.Min(usage.Quotas.Count, 5);
            var height = Math.Max(145, 121 + count * 48);
            var card = new Panel { Size = new Size(405, height), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            var heading = new Label { Text = usage.Name, Font = new Font("Malgun Gothic", 11, FontStyle.Bold), ForeColor = accent, Location = new Point(14, 12), AutoSize = true };
            card.Controls.Add(heading);
            var percent = usage.Headline.HasValue ? Math.Round(usage.Headline.Value) + "%" : "--";
            card.Controls.Add(new Label { Text = percent, Font = new Font("Segoe UI", 18, FontStyle.Bold), ForeColor = RemainingColor(usage.Headline), TextAlign = ContentAlignment.TopRight, Location = new Point(303, 5), Size = new Size(84, 42) });
            var status = usage.Quotas.Count == 0 ? usage.Error ?? "한도 정보 없음" : (usage.Stale ? "마지막 정상 조회" : "연결됨") + " · " + usage.Source + " · " + usage.CheckedAt.ToString("HH:mm") + " 확인";
            card.Controls.Add(new Label { Text = status, ForeColor = usage.Quotas.Count == 0 ? Color.FromArgb(157, 82, 56) : Color.DimGray, Location = new Point(15, 45), Size = new Size(372, usage.Quotas.Count == 0 ? 37 : 26), AutoEllipsis = true });
            int y = 88;
            foreach (var quota in usage.Quotas.Take(5))
            {
                card.Controls.Add(new Label { Text = quota.Name, Location = new Point(15, y - 3), Size = new Size(82, 24), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true });
                var track = new Panel { Location = new Point(98, y + 5), Size = new Size(194, 9), BackColor = Color.FromArgb(228, 232, 234) };
                var fill = new Panel { Location = new Point(0, 0), Size = new Size((int)Math.Round(194 * quota.Remaining / 100), 9), BackColor = RemainingColor(quota.Remaining) };
                track.Controls.Add(fill);
                card.Controls.Add(track);
                card.Controls.Add(new Label { Text = Math.Round(quota.Remaining) + "% 남음", TextAlign = ContentAlignment.MiddleRight, Location = new Point(295, y - 2), Size = new Size(92, 23), ForeColor = RemainingColor(quota.Remaining) });
                var reset = ResetText(quota.Reset);
                if (reset != null) card.Controls.Add(new Label { Text = reset, ForeColor = Color.Gray, Font = new Font("Malgun Gothic", 8), Location = new Point(99, y + 19), Size = new Size(288, 19) });
                y += 48;
            }
            var login = new Button { Text = usage.Quotas.Count == 0 ? "로그인" : "계정 변경", Location = new Point(280, height - 35), Size = new Size(106, 27) };
            login.Click += (s, e) => Providers.StartLogin(isCodex);
            card.Controls.Add(login);
            if (usage.Quotas.Count == 0 && !isCodex && Providers.FindClaude() == null)
            {
                login.Text = "설치 안내";
                card.Controls.Add(new Label { Text = "Claude Code 설치 후 여기서 로그인", Location = new Point(15, height - 31), Size = new Size(250, 20), ForeColor = Color.DimGray });
            }
            return card;
        }
        private static Color RemainingColor(double? remaining)
        {
            if (!remaining.HasValue) return Color.Gray;
            if (remaining.Value < 10) return Color.FromArgb(195, 56, 56);
            if (remaining.Value < 30) return Color.FromArgb(194, 119, 31);
            return Color.FromArgb(43, 129, 86);
        }
        private static string ResetText(DateTimeOffset? reset)
        {
            if (!reset.HasValue) return null;
            var remaining = reset.Value - DateTimeOffset.Now;
            if (remaining.TotalSeconds <= 0) return "곧 초기화";
            string duration = remaining.TotalDays >= 1 ? (int)remaining.TotalDays + "일 " + remaining.Hours + "시간" : remaining.TotalHours >= 1 ? (int)remaining.TotalHours + "시간 " + remaining.Minutes + "분" : Math.Max(1, remaining.Minutes) + "분";
            return duration + " 후 초기화 · " + reset.Value.ToLocalTime().ToString("M/d HH:mm");
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (args.Contains("--claude-status")) { Providers.CaptureClaudeStatus(); return 0; }
            if (args.Contains("--once"))
            {
                foreach (var usage in new[] { Providers.FetchCodex(), Providers.FetchClaude() })
                {
                    Console.WriteLine(usage.Name + ": " + (usage.Error ?? String.Join(", ", usage.Quotas.Select(q => q.Name + " " + Math.Round(q.Remaining) + "%"))));
                }
                return 0;
            }
            if (args.Contains("--self-test"))
            {
                var codex = Providers.ParseCodex(Json.Parse("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":70,\"windowDurationMins\":10080}}}}"));
                var claude = Providers.ParseClaude(Json.Parse("{\"five_hour\":{\"utilization\":12},\"seven_day\":{\"utilization\":42}}"), false);
                var claudeFiveHourLow = Providers.ParseClaude(Json.Parse("{\"five_hour\":{\"utilization\":80},\"seven_day\":{\"utilization\":42}}"), false);
                var claudeWeeklyFable = Providers.ParseClaude(Json.Parse("{\"seven_day\":{\"utilization\":42},\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":5,\"scope\":{\"model\":{\"display_name\":\"Fable\"}}}]}"), false);
                var scoped = Providers.ParseClaude(Json.Parse("{\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":91,\"scope\":{\"model\":{\"display_name\":\"Opus\"}}}]}"), false);
                var status = Providers.ParseClaude(Json.Parse("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":40}}}"), true);
                bool ok = codex.Quotas.Count == 2 && codex.Headline == 30 && claude.Quotas.Count == 2 && claude.Headline == 58 && claudeFiveHourLow.Headline == 58 && scoped.Headline == 9 && status.Headline == 60 &&
                    SummaryStrip.FormatTaskbarQuotas(codex, false) == "5h 75% / 주간 30%" &&
                    SummaryStrip.FormatTaskbarQuotas(claude, true) == "5h 88% / 주간 58%" &&
                    SummaryStrip.FormatTaskbarQuotas(claudeWeeklyFable, true) == "주간 58% / Fable 95%";
                Console.WriteLine(ok ? "PASS" : "FAIL");
                return ok ? 0 : 1;
            }
            bool created;
            var mutex = new Mutex(true, @"Local\UsageTraySingleInstance", out created);
            if (!created) { mutex.Dispose(); return 0; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(mutex));
            return 0;
        }
    }
}
