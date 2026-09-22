using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Agent.Common;
using Microsoft.Win32;

namespace LicenseKeygen;

public partial class MainWindow : Window
{
    private const string DefaultKeyPath = @"E:\RemoteControl\license\private.pem";
    private const string DefaultServer = "202.60.232.209:8080";

    /// <summary>启动时读到令牌的那句状态（默认的"正在读取服务器状态…"会把它冲掉，所以留一份）</summary>
    private string _tokenStatusAtStartup = "";

    public MainWindow()
    {
        InitializeComponent();
        if (!File.Exists(DefaultKeyPath)) KeyBox.Text = "";
        // 默认参数：生效=今天、到期=一年后（可在界面上改），服务器/客户/主控端数记住上次填写
        NbfPicker.SelectedDate = LicenseTime.Today;
        ExpPicker.SelectedDate = LicenseTime.Today.AddDays(365);
        LoadSettings();
        // 令牌：本机设置里存的那条 → 本机令牌文件；都没有就留空（下面的提示会说明去哪儿填）
        LoadTokenAtStartup();
        _tokenStatusAtStartup = StatusText.Text;
        LoadHelp();
        EnsureHistoryLoaded(force: true);
        StatusText.Text = _tokenStatusAtStartup.Length > 0
            ? _tokenStatusAtStartup
            : File.Exists(DefaultKeyPath) ? "正在读取服务器状态…" : "未找到私钥文件";
        UpdateInstallLink();    // 令牌/服务器都可能来自本机设置，先把安装入口链接填好
        SignBtn.Click += (_, _) => Sign();
        CopyBtn.Click += (_, _) =>
        {
            if (CopyToClipboard(CodeBox.Text)) StatusText.Text = "已复制到剪贴板";
            else StatusText.Text = "复制失败：剪贴板被别的程序占用了，请再点一次";
        };
        SaveBtn.Click += (_, _) => Save();
        VerifyBtn.Click += (_, _) => Verify();
        PickKeyBtn.Click += (_, _) => { PickKey(); EnsureHistoryLoaded(); };
        GenKeyBtn.Click += (_, _) => { GenerateKey(); EnsureHistoryLoaded(); };
        LoadServerBtn.Click += async (_, _) => { await LoadServerStatus(); await RefreshServerStatusAsync(quiet: true); };
        InstallBtn.Click += async (_, _) => await InstallToServerAsync();

        // ---- 激活码管理
        RefreshStatusBtn.Click += async (_, _) => await RefreshServerStatusAsync(quiet: false);
        SyncRevokedBtn.Click += async (_, _) => await SyncRevocationsAsync(quiet: false);
        ReadTokenBtn.Click += (_, _) => ReadToken();
        ClearTokenBtn.Click += (_, _) => ClearToken();
        CopyInstallLinkBtn.Click += (_, _) => CopyInstallLink();
        ServerBox.TextChanged += (_, _) => UpdateInstallLink();
        // 明文框与打码框内容互相同步；改完立刻更新安装入口链接与令牌来路提示
        _syncingToken = true;
        TokenBox.TextChanged += (_, _) => { SyncTokenFields(fromText: true); UpdateInstallLink(); };
        TokenPw.PasswordChanged += (_, _) => { SyncTokenFields(fromText: false); UpdateInstallLink(); };
        _syncingToken = false;
        CopyCodeBtn.Click += (_, _) => CopySelectedCode();
        DeleteBtn.Click += (_, _) => DeleteSelected();
        RestoreBtn.Click += (_, _) => RestoreSelected();
        ExportCsvBtn.Click += (_, _) => ExportCsv();
        ShowRevokedChk.Checked += (_, _) => RefreshGrid();
        ShowRevokedChk.Unchecked += (_, _) => RefreshGrid();
        // 令牌框默认明文（看得见才好核对）；勾「隐藏」才打码。
        // Click 管鼠标点（Checked/Unchecked 先于 Click 触发，所以两边都带上幂等判断）；
        // Checked/Unchecked 管"状态被程序改"的情况（比如 UIA 自动化里 Toggle）。
        HideTokenChk.Click += (_, _) => ApplyTokenMask();
        HideTokenChk.Checked += (_, _) => ApplyTokenMask();
        HideTokenChk.Unchecked += (_, _) => ApplyTokenMask();
        MainTabs.SelectionChanged += async (_, e) =>
        {
            // 只认页签自身的切换：SelectionChanged 是冒泡事件，表格里选中一行也会冒到 TabControl，
            // 不加这层判断的话，用户每点一行都会触发"重新载入台账 → 重建列表"，把刚选中的行又清掉。
            if (!ReferenceEquals(e.Source, MainTabs)) return;
            if (MainTabs.SelectedIndex != 1) return;
            EnsureHistoryLoaded(force: true);
            RefreshGrid();
            await RefreshServerStatusAsync(quiet: true);
        };

        Loaded += async (_, _) => { await AutoLoadServerAsync(); await RefreshServerStatusAsync(quiet: true); };
    }

    private string KeyPath => KeyBox.Text.Trim();

    // ---------------------------------------------------------------- 默认参数记忆

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "keygen-settings.json");

    private sealed class Settings
    {
        public string Server { get; set; } = DefaultServer;
        public string Sub { get; set; } = "";
        public int MaxViewers { get; set; } = 2;
        public int ValidDays { get; set; } = 365;
        public string LastLic { get; set; } = "";

        /// <summary>中继令牌（撤销/恢复/同步要用来调管理接口）。存在本机设置里，明文。</summary>
        public string Token { get; set; } = "";
    }

    private Settings _settings = new();

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                // 本次实际读到了什么，留个底：令牌框被清空后要按它兜回来
                _settingsTokenOnDisk = _settings.Token?.Trim() ?? "";
            }
        }
        catch { _settings = new Settings(); }

        ServerBox.Text = string.IsNullOrWhiteSpace(_settings.Server) ? DefaultServer : _settings.Server;
        SubBox.Text = _settings.Sub;
        MaxViewBox.Text = _settings.MaxViewers.ToString();
        SetToken(_settings.Token ?? "");
        ExpPicker.SelectedDate = LicenseTime.Today.AddDays(_settings.ValidDays <= 0 ? 365 : _settings.ValidDays);
        LicBox.Text = SuggestNextLic(_settings.LastLic);
    }

    /// <summary>启动时从设置文件里读到的令牌（不为空说明本机确实存过一条）。</summary>
    private string _settingsTokenOnDisk = "";

    /// <summary>明文框写入时置位：避免"明文→打码→明文"来回触发对方的变更事件。</summary>
    private bool _syncingToken;

    /// <summary>当前令牌：明文框是主，打码框只是它的替身。</summary>
    private string Token() => TokenBox.Text.Trim();

    /// <summary>同时写两个框（打码框只是在「隐藏」时露脸，内容必须一致）。</summary>
    private void SetToken(string value)
    {
        _syncingToken = true;
        try
        {
            if (TokenBox.Text != value) TokenBox.Text = value;
            if (TokenPw.Password != value) TokenPw.Password = value;
        }
        finally { _syncingToken = false; }
        SyncTokenFields(fromText: true);   // 万一有一边没吃到值，这里兜一次
    }

    private void SyncTokenFields(bool fromText)
    {
        if (_syncingToken) return;
        _syncingToken = true;
        try
        {
            if (fromText) TokenPw.Password = TokenBox.Text;
            else TokenBox.Text = TokenPw.Password;
        }
        finally { _syncingToken = false; }
    }

    /// <summary>按「隐藏」的勾选状态切换明文/打码框，并把两边内容对齐。</summary>
    private void ApplyTokenMask()
    {
        bool mask = HideTokenChk.IsChecked == true;
        SetToken(Token());                     // 切之前先把内容同步好（打码框没吃到的编辑在这里补上）
        bool masked = TokenPw.Visibility == Visibility.Visible;
        if (mask == masked) return;            // 已经切过了：直接返回，避免来回触发
        TokenPw.Visibility = mask ? Visibility.Visible : Visibility.Collapsed;
        TokenBox.Visibility = mask ? Visibility.Collapsed : Visibility.Visible;
        UpdateTokenHint();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Server = ServerBox.Text.Trim();
            _settings.Sub = SubBox.Text.Trim();
            _settings.MaxViewers = int.TryParse(MaxViewBox.Text.Trim(), out var mv) ? mv : 0;
            if (NbfPicker.SelectedDate.HasValue && ExpPicker.SelectedDate.HasValue)
                _settings.ValidDays = (int)Math.Round((ExpPicker.SelectedDate.Value - NbfPicker.SelectedDate.Value).TotalDays);
            _settings.LastLic = LicBox.Text.Trim();
            _settings.Token = TokenBox.Text.Trim();
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>下一个授权编号：HK-REL-0001 → HK-REL-0002（按上次/服务器上现有编号 +1）</summary>
    private static string SuggestNextLic(string previous)
    {
        if (string.IsNullOrWhiteSpace(previous)) return "HK-REL-0001";
        int i = previous.Length;
        while (i > 0 && char.IsDigit(previous[i - 1])) i--;
        if (i == previous.Length) return previous + "-0002";
        var prefix = previous[..i];
        var digits = previous[i..];
        if (!int.TryParse(digits, out var n)) return previous + "-0002";
        return prefix + (n + 1).ToString(new string('0', digits.Length));
    }

    /// <summary>启动时自动读服务器：顺便把部署ID与"下一个授权编号"填好</summary>
    private async Task AutoLoadServerAsync()
    {
        try
        {
            var text = await FetchLicenseJsonAsync();
            if (text == null) return;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var dep = root.TryGetProperty("deployment", out var d) ? d.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(dep)) SrvBox.Text = dep;
            if (root.TryGetProperty("lic", out var licEl) && licEl.GetString() is { Length: > 0 } cur)
            {
                if (string.IsNullOrWhiteSpace(LicBox.Text) || LicBox.Text == "HK-REL-0001")
                    LicBox.Text = SuggestNextLic(cur);
            }
            StatusText.Text = "已读取服务器参数";
        }
        catch { }
    }

    // ---------------------------------------------------------------- 台账（激活码管理）

    private LicenseHistory _history = new();
    private string _historyPath = "";
    private string _serverHash = "";        // 服务器当前安装的激活码指纹
    private string _serverState = "";       // 服务器当前授权状态
    private string _serverReason = "";

    /// <summary>台账跟着私钥走：换份 exe、换台机器打开都是同一本账。</summary>
    private void EnsureHistoryLoaded(bool force = false)
    {
        var path = LicenseHistory.PathForKey(KeyPath);
        if (!force && path == _historyPath) return;
        _historyPath = path;
        _history = LicenseHistory.Load(path);
        RefreshGrid();
    }

    private void SaveHistory() => _history.Save(_historyPath);

    private void RefreshGrid()
    {
        // 重建列表会丢选中项（复制/删除都依赖选中），所以按激活码指纹记住并还原
        var selectedHash = (MgmtGrid.SelectedItem as LicenseRow)?.Record.Hash() ?? "";
        var rows = _history.Newest()
            .Where(r => ShowRevokedChk.IsChecked == true || !r.Revoked)
            .Select(r => new LicenseRow(r, _serverHash))
            .ToList();
        MgmtGrid.ItemsSource = rows;
        if (selectedHash.Length > 0)
        {
            var again = rows.FirstOrDefault(r => r.Record.Hash() == selectedHash);
            if (again != null) MgmtGrid.SelectedItem = again;
        }

        var total = _history.Records.Count;
        var revoked = _history.Records.Count(r => r.Revoked);
        var unsynced = _history.Records.Count(r => r.Revoked && r.SyncedAt == 0);
        var summary = $"台账共 {total} 条：有效 {total - revoked}，已删除 {revoked}";
        if (unsynced > 0) summary += $"（其中 {unsynced} 条未同步到服务器！）";
        if (_serverState.Length > 0) summary += $"　服务器：{StateText(_serverState)}";
        MgmtSummary.Text = summary;
    }

    private static string StateText(string state) => state switch
    {
        "ok" => "已授权（正常）",
        "missing" => "未安装激活码",
        "invalid" => "无效",
        "expired" => "已过期",
        "not_yet" => "尚未生效",
        "clock_anomaly" => "时钟异常",
        "revoked" => "已撤销",
        _ => state,
    };

    /// <summary>
    /// 当前该用的令牌 = 令牌框里现在的值。
    /// 只在启动时读过一次设置/令牌文件（见 LoadTokenAtStartup）：之后框里的内容就是唯一依据，
    /// 清空就是清空 —— 不能在这里拿"历史值"往回填，否则会把用户刚删掉的令牌又塞回框里。
    /// </summary>
    private string ResolveToken() => Token();

    /// <summary>
    /// 启动时把令牌备好：令牌框（设置文件里存的那条）→ 本机令牌文件。
    /// 只在启动时做这一次自动读取：之后用户清空就是清空，不再偷偷兜回来。
    /// </summary>
    private void LoadTokenAtStartup()
    {
        if (ResolveToken().Length > 0) return;
        var found = ReadTokenFromFiles();
        if (found is not { } hit) return;      // 没有就算了：令牌框留空，下面的提示会说明去哪儿填
        SetToken(hit.Token);
        SaveSettings();
        StatusText.Text = "已自动读取中继令牌：" + hit.Path;
    }

    /// <summary>
    /// 手动点「读取令牌」：令牌框为空时去本机文件里找一条填上。
    /// 与启动时的自动读取分开：这里是用户主动点的，找不到要把"去哪儿找"讲清楚。
    /// </summary>
    private void ReadToken()
    {
        if (Token().Length > 0)
        {
            StatusText.Text = "令牌框里已有内容：先点「清空」再读，或直接改成新令牌";
            UpdateInstallLink();
            return;
        }
        var found = ReadTokenFromFiles();
        if (found is not { } hit)
        {
            StatusText.Text = "本机没找到令牌文件：把服务器上 systemctl cat rcserver 里的 RC_TOKEN " +
                              "存成 " + PreferredTokenPath() + "，或直接粘贴到「令牌」框";
            UpdateInstallLink();
            return;
        }
        SetToken(hit.Token);
        SaveSettings();
        UpdateInstallLink();
        StatusText.Text = $"已从 {hit.Path} 读取令牌（{hit.Token.Length} 位，内容见上方「令牌」框）";
    }

    /// <summary>清空令牌：同时清掉本机设置里的那份，避免下次启动又被自动填回来。</summary>
    private void ClearToken()
    {
        SetToken("");
        _settings.Token = "";
        _settingsTokenOnDisk = "";
        SaveSettings();
        UpdateInstallLink();
        StatusText.Text = "已清空令牌（本机设置里的那份也一并清掉）";
    }

    /// <summary>令牌文件里读到的结果：令牌本身 + 读它的文件（提示里要告诉用户是哪一份）。</summary>
    private readonly record struct TokenHit(string Token, string Path);

    /// <summary>按候选顺序找令牌文件。文件长度必须够且不含空白，否则视为脏数据跳过。</summary>
    private TokenHit? ReadTokenFromFiles()
    {
        foreach (var path in TokenFileCandidates())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path).Trim();
                // 令牌是一条不含空白的短串；文件里有别的内容就跳过，别把乱七八糟的东西填进去
                if (text.Length < 16 || text.Contains(' ') || text.Contains('\n') || text.Contains('\r')) continue;
                return new TokenHit(text, path);
            }
            catch { }
        }
        return null;
    }

    /// <summary>跟用户说"把令牌存成哪个文件"时用的路径（候选里的第一个）。</summary>
    private string PreferredTokenPath() => TokenFileCandidates().First();

    private IEnumerable<string> TokenFileCandidates()
    {
        var keyDir = "";
        try
        {
            var full = Path.GetFullPath(KeyPath);
            keyDir = Path.GetDirectoryName(full) ?? "";
        }
        catch { }
        if (keyDir.Length > 0)
        {
            yield return Path.Combine(keyDir, "admin-token.txt");   // 管理令牌（RC_ADMIN_TOKEN）—— /admin 只认它
            yield return Path.Combine(keyDir, "relay-token.txt");
            yield return Path.Combine(keyDir, "hk-token.txt");
        }
        yield return @"E:\tools\hk-admin-token.txt";      // 管理令牌（2026-09 起 /admin 不再收中继令牌）
        yield return @"E:\tools\hk-token.txt";            // 旧约定：中继令牌（未配置管理令牌的旧服务器兜底）
        yield return Path.Combine(AppContext.BaseDirectory, "relay-token.txt");
    }

    // ---------------------------------------------------------------- 安装入口（/admin 链接）

    /// <summary>
    /// 把「带令牌的 /admin 链接」填进签发页那个只读框：手动安装激活码时直接复制粘贴到浏览器。
    /// 链接在 UI 上是明文（窗口本身就是厂商侧工具，令牌也握在厂商手里）；
    /// 复制出去的用途就是"打开管理页"，不含令牌的链接反而不顶用。
    /// </summary>
    private void UpdateInstallLink()
    {
        var baseUrl = ServerBase();
        var token = ResolveToken();
        _adminUrl = baseUrl.Length == 0 || token.Length == 0
            ? ""
            : baseUrl + "/admin?token=" + Uri.EscapeDataString(token);
        InstallLinkBox.Text = _adminUrl.Length > 0
            ? _adminUrl
            : baseUrl.Length == 0 ? "（先填「中继服务器」）" : "（先填「令牌」，或点上面的「读取令牌」）";
        UpdateTokenHint();
    }

    /// <summary>
    /// 令牌那一行下面的一句话，回答"我这令牌是哪来的/为什么是空的"。
    /// 令牌框默认明文可见，所以这句话主要用来交代"来路"和"当前有没有"。
    /// </summary>
    private void UpdateTokenHint()
    {
        if (TokenHint == null) return;
        TokenHint.Text = TokenSourceText();
    }

    /// <summary>给 SetTokenMasked 用的一句话（切可见性时只刷这行，不重算链接）。</summary>
    private string UpdateTokenHintText() => TokenSourceText();

    private string TokenSourceText()
    {
        var token = Token();
        if (token.Length == 0)
            return "当前没有令牌：直接粘到上面「令牌」框，或存成 " + PreferredTokenPath() +
                   " 后点「读取令牌」（服务器上执行 systemctl cat rcserver 可看到 RC_TOKEN）。";

        var source =
            _settingsTokenOnDisk.Length > 0 && _settingsTokenOnDisk == token
                ? "，来自本机设置 " + SettingsPath
                : ReadTokenFromFiles() is { } hit && hit.Token == token
                    ? "，来自 " + hit.Path
                    : "，手动填入";
        return $"已填令牌：{token.Length} 位{source}。";
    }

    private void CopyInstallLink()
    {
        if (_adminUrl.Length == 0) { StatusText.Text = "安装入口链接还不完整：" + InstallLinkBox.Text; return; }
        StatusText.Text = CopyToClipboard(_adminUrl)
            ? "已复制安装入口链接（含令牌，可直接粘到浏览器打开）"
            : "复制失败：剪贴板被别的程序占用了，请再点一次";
    }

    /// <summary>
    /// 写剪贴板。别的程序占着剪贴板时 WPF 会抛（不是致命错误），等一小会儿重试几次；
    /// 全都失败就返回 false，由调用方给一句能看懂的提示 —— "复制"是高频操作，值得多试两下。
    /// </summary>
    private static bool CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);   // 参数校验失败（null/空串）会抛 ArgumentNullException，不重试
                return true;
            }
            catch (ArgumentNullException) { return false; }
            catch { Thread.Sleep(60); }    // ExternalException：剪贴板被占用，稍等再试
        }
        return false;
    }

    private string _adminUrl = "";

    /// <summary>安装/撤销需要令牌；没有就给出可执行的指引（去哪儿拿、填到哪）。</summary>
    private bool EnsureToken()
    {
        if (ResolveToken().Length > 0) return true;
        StatusText.Text = "未填中继令牌：把服务器上 RC_TOKEN 存成 " + PreferredTokenPath() +
                          "，或直接粘到上面「令牌」框（服务器上执行 systemctl cat rcserver 可看到）";
        return false;
    }

    /// <summary>服务器地址（接受 IP:端口 或完整 URL），带 http:// 前缀。</summary>
    private string ServerBase()
    {
        var input = ServerBox.Text.Trim();
        if (input.Length == 0) return "";
        var url = input.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? input : "http://" + input;
        return url.TrimEnd('/');
    }

    /// <summary>读服务器状态：更新授权状态、把"哪一条正在被使用"标进台账。</summary>
    private async Task RefreshServerStatusAsync(bool quiet)
    {
        try
        {
            var text = await FetchLicenseJsonAsync();
            if (text == null)
            {
                if (!quiet) StatusText.Text = "读取失败：请先填「中继服务器」";
                return;
            }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            _serverState = root.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            _serverReason = root.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
            _serverHash = root.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";
            var lic = root.TryGetProperty("lic", out var l) ? l.GetString() ?? "" : "";
            var expires = root.TryGetProperty("expires", out var e) ? e.GetString() ?? "" : "";
            var revokedCount = root.TryGetProperty("revokedCount", out var rc) && rc.TryGetInt32(out var n) ? n : 0;

            // 观察到"这条正在被使用"就记进台账（本地留痕：哪些用过了）
            if (_serverHash.Length > 0)
            {
                var rec = _history.Records.FirstOrDefault(r => r.Hash() == _serverHash);
                if (rec != null && rec.InstalledAt == 0)
                {
                    rec.InstalledAt = DateTimeOffset.TryParse(
                        root.TryGetProperty("installedAt", out var ia) ? ia.GetString() : null,
                        out var when) ? when.ToUnixTimeSeconds() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    SaveHistory();
                }
            }

            var detail = $"服务器 {ServerBase()}：{StateText(_serverState)}";
            if (lic.Length > 0) detail += $"，编号 {lic}";
            if (expires.Length > 0) detail += $"，到期 {expires[..Math.Min(10, expires.Length)]}";
            detail += $"，撤销名单 {revokedCount} 条";
            if (_serverReason.Length > 0 && _serverState != "ok") detail += " — " + _serverReason;
            StatusText.Text = detail;
            UpdateInstallLink();   // 服务器地址/令牌可能刚被改动过
            RefreshGrid();
        }
        catch (Exception ex)
        {
            if (!quiet) StatusText.Text = "读取服务器状态失败：" + ex.Message;
        }
    }

    /// <summary>把本地台账里"已删除"（及已恢复的）记录同步到中继的撤销名单。</summary>
    private async Task<bool> SyncRevocationsAsync(bool quiet)
    {
        var baseUrl = ServerBase();
        if (baseUrl.Length == 0)
        {
            StatusText.Text = "同步撤销名单需要先填「中继服务器」";
            return false;
        }
        if (!EnsureToken()) return false;
        var token = Token();

        var add = _history.Records.Where(r => r.Revoked && r.SyncedAt == 0).ToList();
        var remove = _history.PendingRemove.ToList();
        if (add.Count == 0 && remove.Count == 0)
        {
            if (!quiet) StatusText.Text = "没有待同步的撤销记录";
            return true;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                add = add.Select(r => new
                {
                    hash = r.Hash(),
                    lic = r.Lic,
                    sub = r.Sub,
                    exp = r.Exp,
                    at = r.RevokedAt,
                    note = string.IsNullOrWhiteSpace(r.Note) ? "上位机删除" : r.Note,
                }).ToArray(),
                remove = remove.ToArray(),
            });

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/admin/revoked/sync");
            req.Headers.Add("X-RC-Token", token);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                StatusText.Text = $"同步失败（HTTP {(int)resp.StatusCode}）：{Shorten(body)}";
                return false;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var r in add) r.SyncedAt = now;
            _history.PendingRemove.Clear();
            SaveHistory();
            SaveSettings();
            await RefreshServerStatusAsync(quiet: true);
            StatusText.Text = $"撤销名单已同步：新增 {add.Count} 条，移除 {remove.Count} 条";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "同步失败（撤销未生效）：" + ex.Message;
            return false;
        }
    }

    private static string Shorten(string s)
    {
        s = s.Trim();
        return s.Length <= 160 ? s : s[..160] + "…";
    }

    private void CopySelectedCode()
    {
        if (MgmtGrid.SelectedItem is not LicenseRow row) { StatusText.Text = "请先在表格里选中一条记录"; return; }
        StatusText.Text = CopyToClipboard(row.Record.Code)
            ? $"已复制 {row.Lic} 的激活码（{row.Record.Code.Length} 字符）"
            : "复制失败：剪贴板被别的程序占用了，请再点一次";
    }

    private void DeleteSelected()
    {
        if (MgmtGrid.SelectedItem is not LicenseRow row) { StatusText.Text = "请先在表格里选中一条记录"; return; }
        if (row.Record.Revoked) { StatusText.Text = $"{row.Lic} 已删除"; return; }
        var msg = $"确定删除 {row.Lic} 并让它失效吗？\n\n" +
                  "· 客户正在用的：中继立刻拒绝通信，被控端 60 秒内停机（含 P2P 直连）\n" +
                  "· 想重新装回来：服务器会直接拒绝，粘多少次都没用\n" +
                  "· 给同一客户新签发的码不受影响\n" +
                  "· 记录会保留在台账里（勾「显示已删除」可回看）";
        if (MessageBox.Show(msg, "删除激活码并使其失效", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _history.Revoke(row.Record, "在上位机中删除");
        SaveHistory();
        RefreshGrid();
        StatusText.Text = $"已删除 {row.Lic}，正在推送到服务器…";
        _ = SyncRevocationsAsync(quiet: false);
    }

    private void RestoreSelected()
    {
        if (MgmtGrid.SelectedItem is not LicenseRow row) { StatusText.Text = "请先在表格里选中一条记录"; return; }
        if (!row.Record.Revoked) { StatusText.Text = $"{row.Lic} 没有被删除，无需恢复"; return; }
        if (MessageBox.Show($"恢复 {row.Lic}？\n\n恢复后它会从服务器撤销名单里移除，可以重新安装（在此之前它一直是被拒绝的）。",
                "恢复激活码", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _history.Unrevoke(row.Record);
        SaveHistory();
        RefreshGrid();
        StatusText.Text = $"已恢复 {row.Lic}，正在同步到服务器…";
        _ = SyncRevocationsAsync(quiet: false);
    }

    private void ExportCsv()
    {
        if (_history.Records.Count == 0) { StatusText.Text = "台账为空"; return; }
        var dlg = new SaveFileDialog
        {
            Filter = "CSV|*.csv",
            FileName = $"激活码台账-{DateTime.Now:yyyyMMdd}.csv",
            Title = "导出台账",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("授权编号,客户/备注,绑定部署,绑定IP,生效(UTC),到期(UTC),生成时间,状态,使用/安装时间,删除时间,备注,激活码");
            foreach (var r in _history.Newest())
            {
                var row = new LicenseRow(r, _serverHash);
                sb.AppendLine(string.Join(",",
                    Csv(r.Lic), Csv(r.Sub), Csv(r.Srv), Csv(r.IP),
                    Csv(TimeText(r.Nbf)), Csv(TimeText(r.Exp)),
                    Csv(DateTimeOffset.FromUnixTimeSeconds(r.Iat).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(row.StatusText),
                    Csv(r.InstalledAt == 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(r.InstalledAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(r.RevokedAt == 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(r.RevokedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(r.Note), Csv(r.Code)));
            }
            // 带 BOM：Excel 打开中文才不乱码
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            StatusText.Text = "已导出：" + dlg.FileName;
        }
        catch (Exception ex) { StatusText.Text = "导出失败：" + ex.Message; }
    }

    private static string TimeText(long unix) =>
        unix == 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd HH:mm");

    private static string Csv(string? s)
    {
        s ??= "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // ---------------------------------------------------------------- 使用说明

    private void LoadHelp()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LicenseKeygen.Help.md");
            if (stream == null)
            {
                HelpViewer.Document = new FlowDocument(new Paragraph(new Run("内置使用说明缺失（资源 LicenseKeygen.Help.md 未找到）")));
                return;
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            HelpViewer.Document = MarkdownRenderer.Render(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            HelpViewer.Document = new FlowDocument(new Paragraph(new Run("使用说明渲染失败：" + ex.Message)));
        }
    }

    // ---------------------------------------------------------------- 密钥

    private void PickKey()
    {
        var dlg = new OpenFileDialog { Filter = "PEM 私钥|*.pem|所有文件|*.*", Title = "选择厂商私钥" };
        if (dlg.ShowDialog() == true) KeyBox.Text = dlg.FileName;
    }

    private void GenerateKey()
    {
        try
        {
            // 只填文件名时补成绝对路径：否则下面 GetDirectoryName 会拿到空串，CreateDirectory 直接抛异常
            var keyPath = string.IsNullOrWhiteSpace(KeyPath) ? DefaultKeyPath : Path.GetFullPath(KeyPath);
            if (File.Exists(keyPath) &&
                MessageBox.Show("私钥已存在，覆盖会导致旧激活码全部失效。确定覆盖？", "激活码生成器",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pem = ecdsa.ExportPkcs8PrivateKeyPem();
            KeyBox.Text = keyPath;
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllText(keyPath, pem);
            var p = ecdsa.ExportParameters(false);
            var raw = new byte[65];
            raw[0] = 0x04;
            p.Q.X!.CopyTo(raw, 1);
            p.Q.Y!.CopyTo(raw, 33);
            var pub = Convert.ToBase64String(raw);
            File.WriteAllText(keyPath + ".pub.txt", pub);
            MessageBox.Show($"已生成密钥对：\n{keyPath}\n\n请把下面这行公钥填到中继服务器源码\nServer/license.go 的 licensePublicKeyB64\n（以及 Agent.Common/LicenseCodec.cs 的 PublicKeyB64），然后重新编译：\n\n{pub}",
                "新公钥", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "已生成密钥对，新公钥需编进中继与被控端后重新发布";
        }
        catch (Exception ex)
        {
            StatusText.Text = "生成密钥对失败：" + ex.Message;
        }
    }

    // ---------------------------------------------------------------- 签发

    private void Sign()
    {
        var keyPath = KeyPath;
        if (!File.Exists(keyPath)) { StatusText.Text = "找不到私钥文件"; return; }
        if (string.IsNullOrWhiteSpace(LicBox.Text)) { StatusText.Text = "请填授权编号"; return; }
        var nbfDay = (NbfPicker.SelectedDate ?? LicenseTime.Today).Date;
        var expDay = (ExpPicker.SelectedDate ?? LicenseTime.Today.AddDays(90)).Date;
        if (expDay < nbfDay) { StatusText.Text = "到期日期必须晚于生效日期"; return; }

        try
        {
            var payload = new LicensePayload
            {
                Lic = LicBox.Text.Trim(),
                Sub = SubBox.Text.Trim(),
                Srv = SrvBox.Text.Trim(),
                IP = IpBox.Text.Trim(),
                MaxViewers = int.TryParse(MaxViewBox.Text.Trim(), out var mv) ? mv : 0,
                Nbf = LicenseTime.DayStart(nbfDay),   // 生效：所选日 00:00:00 UTC
                Exp = LicenseTime.DayEnd(expDay),     // 到期：所选日 23:59:59 UTC（当天仍有效）
            };
            var code = LicenseCodec.Sign(payload, File.ReadAllText(keyPath));
            CodeBox.Text = code;
            SaveSettings();     // 记住这次的服务器/客户/有效期，下次直接可用

            // 记进台账（同一串码重复点不再记一条）
            EnsureHistoryLoaded();
            if (_history.FindByCode(code) == null)
            {
                _history.Add(new LicenseRecord
                {
                    Code = code,
                    Lic = payload.Lic,
                    Sub = payload.Sub,
                    Srv = payload.Srv,
                    IP = payload.IP,
                    MaxViewers = payload.MaxViewers,
                    Nbf = payload.Nbf,
                    Exp = payload.Exp,
                    Iat = payload.Iat,
                });
                SaveHistory();
                RefreshGrid();
            }

            // 签完顺手把令牌/安装入口刷新一下，紧接着点「安装到服务器」就能用
            UpdateInstallLink();
            // 自己立刻验一遍，避免发出无效码
            var check = LicenseCodec.Verify(code, DateTime.UtcNow, payload.Srv, payload.IP);
            StatusText.Text = check.Ok
                ? $"已生成 {payload.Lic}，有效期至 {expDay:yyyy-MM-dd}（剩余 {payload.DaysLeft():F0} 天）"
                : "生成后自检失败：" + check.Error;
        }
        catch (Exception ex)
        {
            StatusText.Text = "签发失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 把当前激活码直接安装到中继服务器（省掉"复制 → 打开浏览器 → 粘贴"三步）。
    /// 走的是和网页同一个接口 /admin/install（令牌鉴权），装完再用 /api/license 确认真的生效。
    /// </summary>
    private async Task InstallToServerAsync()
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0) { StatusText.Text = "还没有激活码"; return; }
        var baseUrl = ServerBase();
        if (baseUrl.Length == 0) { StatusText.Text = "未填中继服务器"; return; }
        if (!EnsureToken()) return;
        var token = Token();
        if (MessageBox.Show($"安装到 {ServerBox.Text.Trim()}？立即生效，被控端最多 60 秒自动恢复。",
                "安装激活码", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{baseUrl}/admin/install?token={Uri.EscapeDataString(token)}");
            req.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("code", code) });
            using var resp = await http.SendAsync(req);

            // 服务器用 303 跳回 /admin?msg=...（PRG），失败原因也在 msg 里
            var msg = QueryValue(resp.RequestMessage?.RequestUri?.Query, "msg");
            if (!resp.IsSuccessStatusCode)
            {
                StatusText.Text = $"安装失败（HTTP {(int)resp.StatusCode}）" + (msg.Length > 0 ? "：" + msg : "");
                return;
            }

            await RefreshServerStatusAsync(quiet: true);
            var installed = _serverHash.Length > 0 && _serverHash == LicenseHistory.CodeHash(code);
            StatusText.Text = msg.Length > 0 ? msg : (installed ? "已安装" : "安装请求已发送");
            if (!installed && !msg.Contains("失败")) StatusText.Text += "（服务器状态未确认）";
            SaveSettings();
        }
        catch (Exception ex)
        {
            StatusText.Text = "安装失败：" + ex.Message;
        }
    }

    /// <summary>从查询串里取一个参数（服务器跳转回来的 msg 是 URL 编码的，+ 代表空格）。</summary>
    private static string QueryValue(string? query, string name)
    {
        if (string.IsNullOrEmpty(query)) return "";
        foreach (var kv in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            if (i <= 0 || kv[..i] != name) continue;
            return Uri.UnescapeDataString(kv[(i + 1)..].Replace('+', ' '));
        }
        return "";
    }

    /// <summary>读取服务器的 /api/license（这个接口在未授权时也能访问，排障用）</summary>
    private async Task<string?> FetchLicenseJsonAsync()
    {
        var input = ServerBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return null;
        var url = input.StartsWith("http") ? input : "http://" + input;
        url = url.TrimEnd('/') + "/api/license";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        return await http.GetStringAsync(url);
    }

    /// <summary>从服务器读取部署ID与授权状态（服务器地址填在「中继服务器」框）</summary>
    private async Task LoadServerStatus()
    {
        if (string.IsNullOrWhiteSpace(ServerBox.Text))
        {
            StatusText.Text = "请先填「中继服务器」，例如 " + DefaultServer;
            return;
        }
        try
        {
            var text = await FetchLicenseJsonAsync();
            if (text == null) { StatusText.Text = "读取失败：服务器地址为空"; return; }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string dep = root.TryGetProperty("deployment", out var d) ? d.GetString() ?? "" : "";
            string state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            string reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            string lic = root.TryGetProperty("lic", out var l) ? l.GetString() ?? "" : "";
            string expires = root.TryGetProperty("expires", out var e) ? e.GetString() ?? "" : "";
            SrvBox.Text = dep;   // 直接把部署ID填进绑定框
            if (!string.IsNullOrWhiteSpace(lic) && string.IsNullOrWhiteSpace(LicBox.Text))
                LicBox.Text = SuggestNextLic(lic);
            UpdateInstallLink();   // 服务器地址/令牌可能有变，安装入口链接跟着刷一遍
            VerifyResult.Text = $"服务器状态\n部署ID：{dep}\n授权状态：{StateText(state)} {(string.IsNullOrEmpty(reason) ? "" : "（" + reason + "）")}\n" +
                                (string.IsNullOrEmpty(lic) ? "" : $"当前授权：{lic}  到期：{expires}\n") +
                                $"安装入口：{(_adminUrl.Length > 0 ? _adminUrl : "见「③ 激活码」右下角的链接框")}";
            StatusText.Text = "已读取服务器状态";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败：" + ex.Message;
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(CodeBox.Text)) { StatusText.Text = "还没有激活码"; return; }
        var dlg = new SaveFileDialog
        {
            Filter = "文本|*.txt",
            FileName = $"license-{LicBox.Text.Trim()}-{DateTime.Now:yyyyMMdd}.txt",
            Title = "保存激活码",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var adminUrl = _adminUrl.Length > 0
                ? _adminUrl
                : $"http://{ServerBox.Text.Trim().TrimEnd('/')}/admin?token=<中继令牌>";
            File.WriteAllText(dlg.FileName,
                $"授权编号：{LicBox.Text}\n客户：{SubBox.Text}\n绑定部署：{SrvBox.Text}\n绑定IP：{IpBox.Text}\n" +
                $"有效期：{NbfPicker.SelectedDate:yyyy-MM-dd} ~ {ExpPicker.SelectedDate:yyyy-MM-dd}（UTC）\n\n" +
                $"安装方式（任选其一）：\n" +
                $"  ① 浏览器打开 {adminUrl} 粘贴安装（授权过期时也能进）\n" +
                $"  ② 服务器上执行：/opt/rcserver/rcctl install \"<下面这行>\"\n\n" +
                $"激活码：\n{CodeBox.Text}\n");
            StatusText.Text = "已保存：" + dlg.FileName;
        }
        catch (Exception ex) { StatusText.Text = "保存失败：" + ex.Message; }
    }

    private void Verify()
    {
        var code = VerifyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(code)) { VerifyResult.Text = "请粘贴激活码"; return; }
        var r = LicenseCodec.Verify(code, DateTime.UtcNow);

        // 顺便查一下这条码是不是已经被删掉（本地上次同步的名单 / 服务器当前状态）
        var local = _history.FindByCode(code);
        var revokedNote = local is { Revoked: true }
            ? $"⚠ 这条码在上位机里已删除（{(local.SyncedAt > 0 ? "已同步到服务器" : "尚未同步到服务器")}）\n"
            : "";

        var sb = new StringBuilder();
        sb.Append(revokedNote);
        if (r.Payload != null)
        {
            var pl = r.Payload;
            sb.AppendLine($"签名：{(r.State == LicenseState.Invalid ? "无效（非本厂商签发或已被篡改）" : "有效（本厂商签发）")}");
            sb.AppendLine($"授权编号：{pl.Lic}    客户：{pl.Sub}");
            sb.AppendLine($"绑定部署：{(string.IsNullOrEmpty(pl.Srv) ? "不绑定" : pl.Srv)}    绑定IP：{(string.IsNullOrEmpty(pl.IP) ? "不绑定" : pl.IP)}");
            sb.AppendLine($"有效期：{pl.NotBefore():yyyy-MM-dd HH:mm} ~ {pl.ExpiresAt():yyyy-MM-dd HH:mm} UTC");
            var days = pl.DaysLeft();
            sb.AppendLine(days >= 0 ? $"状态：有效，剩余 {days:F1} 天" : $"状态：已过期 {-days:F1} 天");
        }
        if (!r.Ok) sb.AppendLine("判定：" + r.Error);
        else sb.AppendLine("判定：当前可用");
        VerifyResult.Text = sb.ToString();
    }
}
