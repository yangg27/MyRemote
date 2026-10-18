using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using MyRemote.Models;
using MyRemote.Services;

namespace MyRemote.Forms
{
    /// <summary>主窗体：设备面板 + 本机信息。所有设备在这里增删改、导入、查看状态、一键连接。</summary>
    public class MainForm : Form
    {
        private ListView? _list;
        private NotifyIcon? _tray;
        private Label? _localInfo;
        private readonly Dictionary<string, ListViewItem> _itemMap = new();
        private Timer? _autoTimer;
        private bool _forceExit;

        /// <summary>
        /// 是否创建系统托盘图标。
        /// 生产运行必须为 true（关闭窗口后常驻托盘）；自动化测试/探针可设为 false，
        /// 避免反复构造窗体时在托盘区残留大量图标。
        /// </summary>
        public static bool CreateTrayIcon { get; set; } = true;

        public MainForm()
        {
            InitializeComponent();
            LoadDevices();
            RefreshLocalInfo();
            RefreshStatus();
            _autoTimer = new Timer { Interval = 30000 };
            _autoTimer.Tick += (s, e) => RefreshStatus();
            _autoTimer.Start();

            FormClosing += (s, e) =>
            {
                if (_forceExit) return;
                e.Cancel = true;   // 关闭按钮改为最小化到托盘，保持常驻
                Hide();
            };
        }

        /// <summary>
        /// 释放托管资源。托盘图标与定时器必须显式清理 ——
        /// NotifyIcon 不会随窗体 Dispose 自动从通知区域移除，否则会在托盘残留"幽灵图标"。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _autoTimer?.Stop(); _autoTimer?.Dispose(); } catch { }
                try
                {
                    if (_tray != null)
                    {
                        _tray.Visible = false;   // 先从通知区域移除，再释放
                        _tray.Dispose();
                    }
                }
                catch { }
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            Text = "MyRemote - 我的远程控制台";
            // 界面全部由代码构建，统一用 UiScale 显式换算，因此关闭框架自动缩放，避免双重缩放
            AutoScaleMode = AutoScaleMode.None;
            UiScale.SetFormSize(this, 760, 520, 620, 420);
            Icon = AppIcon.Create();

            // 工具栏
            var toolbar = new ToolStrip { Dock = DockStyle.Top, ImageScalingSize = UiScale.S(new Size(16, 16)) };
            // 按钮多（11 个）：窄屏/高 DPI 下若内容超出，允许换到第二行显示，
            // 而不是把按钮藏进溢出下拉菜单（那样用户会以为按钮"不见了"）
            toolbar.LayoutStyle = ToolStripLayoutStyle.Flow;
            toolbar.CanOverflow = false;
            toolbar.AutoSize = true;   // Flow 布局下随内容换行自动增高，保证每个按钮都可见
            var bAutoFix = new ToolStripButton("一键检测并修复")
            {
                ToolTipText = "自动遍历：检测 → 修复 → 复检，直到全部通过（含自动安装 Tailscale、注册 RDP 控件等）",
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
            };
            bAutoFix.Click += (s, e) => OpenAutoFix();
            var bAdd = new ToolStripButton("添加设备"); bAdd.Click += (s, e) => AddDevice();
            var bImport = new ToolStripButton("从Tailscale导入"); bImport.Click += (s, e) => ImportFromTailscale();
            var bEdit = new ToolStripButton("编辑"); bEdit.Click += (s, e) => EditDevice();
            var bDel = new ToolStripButton("删除"); bDel.Click += (s, e) => DeleteDevice();
            var bConn = new ToolStripButton("远程桌面"); bConn.Click += (s, e) => OpenSession();
            var bFiles = new ToolStripButton("文件管理"); bFiles.Click += (s, e) => OpenFiles();
            var bRefresh = new ToolStripButton("刷新状态"); bRefresh.Click += (s, e) => RefreshStatus();
            var bDiag = new ToolStripButton("环境诊断"); bDiag.Click += (s, e) => new DiagnosticsForm().Show();
            var bLog = new ToolStripButton("系统日志"); bLog.Click += (s, e) => new SystemLogForm().Show();
            toolbar.Items.AddRange(new ToolStripItem[] {
                bAutoFix, bDiag, new ToolStripSeparator(),
                bAdd, bImport, bEdit, bDel, new ToolStripSeparator(),
                bConn, bFiles, new ToolStripSeparator(), bLog, bRefresh });

            // 本机信息面板
            // 高度不写死：本机信息有 3～4 行文本，高 DPI 下会明显变高，
            // 固定高度会把最后一行挤出面板。改为按字体行高动态计算。
            var localBox = new GroupBox { Text = "本机信息（添加设备时参考）", Dock = DockStyle.Top };
            localBox.Height = LocalBoxHeight();
            _localInfo = new Label { Dock = DockStyle.Fill, UseMnemonic = false, Padding = UiScale.S(new Padding(8)),
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 10f) };
            // 按钮宽度不写死：按文字自动撑开（高 DPI 下中文变宽，固定宽度会把字切掉）
            var bLocalRefresh = new Button { Text = "刷新", Dock = DockStyle.Right }.AutoFit(70);
            bLocalRefresh.Click += (s, e) => RefreshLocalInfo();
            var bTsLogin = new Button { Text = "Tailscale 登录", Dock = DockStyle.Right }.AutoFit(110);
            bTsLogin.Click += (s, e) => OpenTailscaleLogin();
            localBox.Controls.Add(_localInfo);
            localBox.Controls.Add(bTsLogin);
            localBox.Controls.Add(bLocalRefresh);

            // 设备列表
            // 外面套一层 Panel：极端小屏 + 高 DPI 下，若本机信息面板把窗口挤满，
            // 列表区仍能靠滚动条保证内容可达，不会被裁掉
            var listHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
            // 末列（备注）自动补满剩余宽度：高 DPI 下"状态/备注"不再被挤到看不见
            UiScale.SetColumns(_list,
                ("显示名", 150), ("地址/IP", 150), ("用户名", 130), ("状态", 90), ("备注", 200));
            _list.DoubleClick += (s, e) => OpenSession();
            _list.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && _list.SelectedItems.Count > 0)
                {
                    var cm = new ContextMenuStrip();
                    cm.Items.Add("远程桌面", null, (_, _) => OpenSession());
                    cm.Items.Add("文件管理", null, (_, _) => OpenFiles());
                    cm.Items.Add("编辑", null, (_, _) => EditDevice());
                    cm.Show(_list, e.Location);
                }
            };

            listHost.Controls.Add(_list);
            Controls.Add(listHost); Controls.Add(localBox); Controls.Add(toolbar);

            // 托盘（自动化测试时可关闭，避免残留图标）
            if (CreateTrayIcon)
            {
                _tray = new NotifyIcon { Icon = AppIcon.Create() ?? SystemIcons.Application, Text = "MyRemote", Visible = true };
                _tray.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; };
                var menu = new ContextMenuStrip();
                menu.Items.Add("打开", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; });
                var autoItem = new ToolStripMenuItem("开机自启") { Checked = IsAutoStart() };
                autoItem.Click += (s, e) => { var v = !autoItem.Checked; SetAutoStart(v); autoItem.Checked = v; };
                menu.Items.Add(autoItem);
                menu.Items.Add("退出", null, (_, _) => { _forceExit = true; Application.Exit(); });
                _tray.ContextMenuStrip = menu;
            }

            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        }

        // ---------- 本机信息 ----------
        /// <summary>
        /// 本机信息面板的高度：按"最多 5 行文本 + 内边距 + 分组框标题"实测计算，
        /// 不写死像素值，确保高 DPI 下最后一行不会被挤出面板。
        /// </summary>
        private static int LocalBoxHeight()
        {
            try
            {
                var font = new Font(SystemFonts.DefaultFont.FontFamily, 10f);
                int line = font.Height;
                font.Dispose();
                // 5 行正文（含"配置模式"可能的额外行）+ 上下留白 + 标题栏高度
                var want = line * 5 + UiScale.S(30) + SystemInformation.CaptionHeight / 2;
                // 极端小屏 + 高 DPI 下不能让本机信息面板吃掉整个窗口：
                // 上限约为屏幕可用高度的 1/4，超出时由面板内部自身换行/裁剪兜底
                var wa = UiScale.WorkingArea();
                return Math.Min(want, Math.Max(UiScale.S(90), wa.Height / 4));
            }
            catch { return UiScale.S(104); }
        }

        /// <summary>首页一键检测并修复：打开诊断窗体并自动启动全流程。</summary>
        private void OpenAutoFix()
        {
            var f = new DiagnosticsForm();
            f.RequestAutoStart();
            f.Show();
        }

        /// <summary>首页快捷操作：打开 Tailscale 登录界面。</summary>
        private void OpenTailscaleLogin()
        {
            var (ok, msg) = PackageService.OpenTailscaleLogin();
            MessageBox.Show(msg, "Tailscale 登录", MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private async void RefreshLocalInfo()
        {
            if (_localInfo == null) return;
            _localInfo.Text = "正在读取本机信息…";
            var user = LocalMachineInfo.UserName;
            var rdp = LocalMachineInfo.IsRdpEnabled;
            var ip = await Task.Run(() => LocalMachineInfo.GetTailscaleIp());
            var lines = new List<string>
            {
                $"本机名：{LocalMachineInfo.MachineName}",
                $"远程登录用户名：{user}",
                $"Tailscale 虚拟 IP：{(string.IsNullOrEmpty(ip) ? "未检测到（请确认已装 Tailscale 并登录同一账号）" : ip)}",
                $"远程桌面被控端：{(rdp ? "已开启 ✓" : "未开启（请右键运行「一键部署.ps1」）")}"
            };
            if (AppPaths.IsPortable) lines.Add("配置模式：便携（配置随 exe 走）");
            _localInfo.Text = string.Join("\n", lines);
        }

        // ---------- 设备列表 ----------
        private void LoadDevices()
        {
            _list!.Items.Clear(); _itemMap.Clear();
            var devices = DeviceStore.Load();
            if (devices.Count == 0)
            {
                var it = new ListViewItem(new[] { "（暂无设备）按「添加设备」或「从Tailscale导入」开始；首次使用见使用说明.md", "", "", "", "" });
                _list.Items.Add(it);
                return;
            }
            foreach (var d in devices)
            {
                var it = new ListViewItem(new[] { d.Name, d.Host, d.UserName, "未知", d.Notes }) { Tag = d.Id };
                _itemMap[d.Id] = it; _list.Items.Add(it);
            }
        }

        private List<Device> Devices() => DeviceStore.Load();

        private Device? SelectedDevice()
        {
            if (_list!.SelectedItems.Count == 0) return null;
            var id = _list.SelectedItems[0].Tag as string;
            if (string.IsNullOrEmpty(id)) return null;
            return Devices().FirstOrDefault(d => d.Id == id);
        }

        private void AddDevice()
        {
            var d = new Device();
            using var f = new DeviceEditForm(d, true);
            if (f.ShowDialog() == DialogResult.OK)
            {
                var devices = Devices(); devices.Add(d); DeviceStore.Save(devices);
                if (!string.IsNullOrEmpty(f.EditResultPassword)) CredentialVault.SavePassword(d.Id, f.EditResultPassword);
                LoadDevices();
            }
        }

        private void ImportFromTailscale()
        {
            using var f = new TailscaleImportForm();
            if (f.ShowDialog() != DialogResult.OK) return;
            var devices = Devices();
            var added = 0;
            foreach (var peer in f.SelectedPeers)
            {
                if (devices.Any(d => d.Host.Equals(peer.Ip, StringComparison.OrdinalIgnoreCase))) continue;
                devices.Add(new Device { Name = peer.Hostname, Host = peer.Ip, UserName = LocalMachineInfo.UserName, ShareName = "C$" });
                added++;
            }
            if (added > 0) { DeviceStore.Save(devices); LoadDevices(); }
            MessageBox.Show(added > 0 ? $"已导入 {added} 台设备（用户名已预填本机名，如不同请编辑修改）。" : "没有新的设备可导入。", "导入完成");
        }

        private void EditDevice()
        {
            var d = SelectedDevice(); if (d == null) { MessageBox.Show("请先选择一台设备。"); return; }
            var oldHost = d.Host;
            using var f = new DeviceEditForm(d, false);
            if (f.ShowDialog() == DialogResult.OK)
            {
                var devices = Devices();
                var idx = devices.FindIndex(x => x.Id == d.Id); if (idx >= 0) devices[idx] = d;
                DeviceStore.Save(devices);
                if (!string.IsNullOrEmpty(f.EditResultPassword)) CredentialVault.SavePassword(d.Id, f.EditResultPassword);
                // 主机地址变了：清掉旧主机在凭据管理器里的网络密码，避免指向错误目标
                if (!string.Equals(oldHost, d.Host, StringComparison.OrdinalIgnoreCase))
                    NetworkShareHelper.RemoveCredential(oldHost);
                LoadDevices();
            }
        }

        private void DeleteDevice()
        {
            var d = SelectedDevice(); if (d == null) { MessageBox.Show("请先选择一台设备。"); return; }
            if (MessageBox.Show($"删除设备「{d.Name}」？", "确认", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            var devices = Devices(); devices.RemoveAll(x => x.Id == d.Id); DeviceStore.Save(devices);
            CredentialVault.DeletePassword(d.Id);
            // 同步清掉系统凭据管理器里为该主机保存的网络密码，避免残留
            NetworkShareHelper.RemoveCredential(d.Host);
            LoadDevices();
        }

        private string? ResolvePassword(Device d)
        {
            var pwd = CredentialVault.GetPassword(d.Id);
            if (!string.IsNullOrEmpty(pwd)) return pwd;
            using var box = new PasswordPrompt($"请输入「{d.Name}」的登录密码：");
            if (box.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(box.Password))
            {
                CredentialVault.SavePassword(d.Id, box.Password);
                return box.Password;
            }
            return null;
        }

        private void OpenSession()
        {
            var d = SelectedDevice(); if (d == null) { MessageBox.Show("请先选择一台设备。"); return; }
            var pwd = ResolvePassword(d); if (pwd == null) return;
            new SessionForm(d, pwd).Show();
        }

        private void OpenFiles()
        {
            var d = SelectedDevice(); if (d == null) { MessageBox.Show("请先选择一台设备。"); return; }
            var pwd = ResolvePassword(d); if (pwd == null) return;
            new FileManagerForm(d, pwd).Show();
        }

        private void RefreshStatus()
        {
            foreach (var d in Devices())
            {
                if (!_itemMap.TryGetValue(d.Id, out var it)) continue;
                var id = d.Id; var host = d.Host; var port = d.RdpPort;
                Task.Run(async () =>
                {
                    var ok = await ConnectivityProbe.IsPortOpenAsync(host, port);
                    try
                    {
                        if (_list == null || _list.IsDisposed || !_list.IsHandleCreated) return;
                        Invoke(() =>
                        {
                            if (_itemMap.TryGetValue(id, out var item) && item.SubItems.Count > 3)
                                item.SubItems[3].Text = ok ? "● 在线" : "○ 离线";
                        });
                    }
                    catch { /* 窗体关闭/句柄未就绪时忽略，避免退出时抛未处理异常 */ }
                });
            }
        }

        // ---------- 开机自启 ----------
        private static bool IsAutoStart()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue("MyRemote") != null;
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;
                if (enable) key.SetValue("MyRemote", Application.ExecutablePath);
                else key.DeleteValue("MyRemote", false);
            }
            catch { }
        }
    }

    /// <summary>简单的密码输入对话框。</summary>
    public class PasswordPrompt : Form
    {
        private readonly TextBox _tb = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        public string Password => _tb.Text;

        public PasswordPrompt(string message)
        {
            Text = "输入密码"; AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            UiScale.SetFormSize(this, 360, 160);
            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = UiScale.S(new Padding(12)) };
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, UiScale.ButtonRowHeight()));
            tlp.Controls.Add(new Label { Text = message, Dock = DockStyle.Fill }, 0, 0);
            tlp.Controls.Add(_tb, 0, 1);
            var flp = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK }.AutoFit(80);
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel }.AutoFit(80);
            flp.Controls.Add(cancel); flp.Controls.Add(ok);
            tlp.Controls.Add(flp, 0, 2);
            Controls.Add(tlp);
        }
    }
}
