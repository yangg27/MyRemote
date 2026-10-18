using System;
using System.Drawing;
using System.Windows.Forms;
using MyRemote.Controls;
using MyRemote.Models;
using MyRemote.Services;

namespace MyRemote.Forms
{
    /// <summary>
    /// 远程桌面窗口：内嵌 RDP 会话。连接成功后这块就是对方桌面，可全屏/缩放/调画质。
    /// </summary>
    public class SessionForm : Form
    {
        private readonly Device _device;
        private readonly string _password;
        private RdpHost? _rdp;
        private Panel? _panel;
        private Label? _status;
        private ToolStrip? _toolbar;
        private ToolStripButton? _bFull;
        private ToolStripButton? _bAuto;
        private ToolStripComboBox? _cmbQuality;
        private Timer? _timer;
        private Timer? _reconnectTimer;
        private bool _connectedOnce;
        private bool _fullscreen;
        private bool _autoReconnect;
        private DateTime _connectStart = DateTime.MinValue;
        private int _reconnectAttempts;
        private const int MaxReconnectAttempts = 5;
        private bool _watchdogTripped;
        private const int MaxConnectSeconds = 15;
        private bool _reconnectPending;   // 已安排自动重连，避免每秒 tick 重复计数把次数耗尽
        private FormBorderStyle _savedBorder = FormBorderStyle.Sizable;
        private Rectangle _savedBounds;

        /// <summary>
        /// 窗体会在显示时自动发起 RDP 连接（生产行为）。自动化测试可设为 false，
        /// 只验证界面而不真正去连对端。
        /// </summary>
        public static bool AutoConnectOnShown { get; set; } = true;

        public SessionForm(Device device, string password)
        {
            _device = device;
            _password = password;
            InitializeComponent();
            Text = $"远程桌面 - {device.Name} ({device.Host})";
        }

        private void InitializeComponent()
        {
            Icon = AppIcon.Create();
            AutoScaleMode = AutoScaleMode.None;
            UiScale.SetFormSize(this, 1000, 660, 720, 480);

            _panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = UiScale.S(26),
                Text = "正在初始化 RDP 控件…",
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = SystemColors.Control,
                ForeColor = SystemColors.ControlText,
                AutoEllipsis = true
            };

            _toolbar = new ToolStrip { Dock = DockStyle.Top, ImageScalingSize = UiScale.S(new Size(16, 16)), CanOverflow = false };
            // 高 DPI 下按钮文本变宽：允许换行，保证按钮不被藏进溢出菜单
            _toolbar.LayoutStyle = ToolStripLayoutStyle.Flow;
            _toolbar.AutoSize = true;
            var bDisconnect = new ToolStripButton("断开");
            bDisconnect.Click += (s, e) => Disconnect();
            _bFull = new ToolStripButton("全屏 (F11)");
            _bFull.Click += (s, e) => ToggleFullscreen();
            var bReconnect = new ToolStripButton("重连");
            bReconnect.Click += (s, e) => Reconnect();
            _bAuto = new ToolStripButton("自动重连:关");
            _bAuto.Click += (s, e) =>
            {
                _autoReconnect = !_autoReconnect;
                _bAuto.Text = _autoReconnect ? "自动重连:开" : "自动重连:关";
                if (_autoReconnect) _reconnectAttempts = 0;
            };
            _cmbQuality = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiScale.S(150), ToolTipText = "修改后自动重连" };
            _cmbQuality.Items.AddRange(new object[] { "自动(适配窗口)", "高速(省带宽)", "高清(1920x1080)" });
            _cmbQuality.SelectedIndex = 0;
            _cmbQuality.SelectedIndexChanged += (s, e) => Reconnect();
            _toolbar.Items.AddRange(new ToolStripItem[]
            {
                bDisconnect, _bFull, bReconnect, _bAuto, new ToolStripSeparator(),
                new ToolStripLabel("画质："), _cmbQuality
            });

            Controls.Add(_panel);
            Controls.Add(_status);
            Controls.Add(_toolbar);

            FormClosing += (s, e) =>
            {
                _autoReconnect = false;
                Disconnect();
            };
            // 自动化测试可关闭"显示即连接"：避免探针构造窗体时真的去连不存在的对端。
            // 生产运行恒为 true（打开窗口即发起连接）。
            if (AutoConnectOnShown) Shown += (s, e) => BeginConnect();
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F11) { ToggleFullscreen(); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape && _fullscreen) { ToggleFullscreen(); e.Handled = true; }
            };
        }

        private void BeginConnect()
        {
            _watchdogTripped = false;
            _reconnectPending = false;
            // 每次（重新）连接都视为“尚未连上”：这样重连如果卡住，15 秒看门狗同样会介入
            _connectedOnce = false;

            // 前置检查：本机是否存在可用的 RDP 客户端控件。
            // 部分系统（如 LTSC 裁剪版）未注册任何 MsRdpClient 控件，
            // 此时直接创建 AxHost 会抛 0x80040154（没有注册类），提示很不友好。
            if (!RdpHost.ControlAvailable)
            {
                _status!.Text = "本机缺少 RDP 客户端控件（MsRdpClient），无法建立会话。";
                SystemLog.Error("远程桌面", "本机未注册任何可用的 RDP 客户端控件（mstscax.dll / MsRdpClient*）。");
                MessageBox.Show(
                    "无法启动 RDP 会话：本机未注册任何可用的「远程桌面客户端控件」（MsRdpClient）。\n\n" +
                    "常见原因：\n" +
                    "①系统为精简版/裁剪版（如部分 LTSC、Server Core），移除了 mstscax.dll 的 COM 注册；\n" +
                    "②系统文件损坏导致控件注册丢失。\n\n" +
                    "解决方法：\n" +
                    "1) 以管理员身份运行命令提示符，执行：\n" +
                    "   regsvr32 %SystemRoot%\\System32\\mstscax.dll\n" +
                    "2) 若仍失败，运行系统修复：sfc /scannow 与 DISM /Online /Cleanup-Image /RestoreHealth\n" +
                    "3) 或改用系统自带的 mstsc.exe 手动连接目标。",
                    "缺少 RDP 控件", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                _panel!.Controls.Clear();
                _rdp = new RdpHost { Dock = DockStyle.Fill };
                _panel.Controls.Add(_rdp);
                _rdp.CreateControl();
                SystemLog.Info("远程桌面", $"使用 RDP 控件 CLSID：{RdpHost.ResolvedClsId}");

                dynamic oc = _rdp.Oc;
                oc.Server = _device.Host;

                // 账号：支持 “DOMAIN\user” / “host\user” / 纯用户名
                var user = _device.UserName.Trim();
                var bs = user.IndexOf('\\');
                if (bs > 0)
                {
                    TrySet(() => { oc.Domain = user.Substring(0, bs); });
                    TrySet(() => { oc.UserName = user.Substring(bs + 1); });
                }
                else
                {
                    TrySet(() => { oc.UserName = user; });
                }

                dynamic adv = GetAdvancedSettings(oc);
                if (adv != null)
                {
                    TrySet(() => { adv.RDPPort = _device.RdpPort; });
                    TrySet(() => { adv.ClearTextPassword = _password ?? ""; });
                    TrySet(() => { adv.RedirectDrives = true; });
                    TrySet(() => { adv.RedirectClipboard = true; });
                    TrySet(() => { adv.SmartSizing = true; });
                    TrySet(() => { adv.AuthenticationLevel = 2; });
                    TrySet(() => { adv.EnableCredSspSupport = true; });
                }

                var q = _cmbQuality!.SelectedIndex;
                int w, h, depth;
                // 注意：这里的分辨率是"远程桌面画面的像素尺寸"，不是本机 UI 尺寸，
                // 因此不乘 DPI 系数（远程端不需要跟随本机缩放）。
                // “自动(适配窗口)”直接用面板的实际像素大小（下限 640x480 仅作为远端最小可用桌面），
                // 上限不超过面板，避免小屏上强制请求更大的远端分辨率导致画面溢出/黑边。
                int pw = Math.Max(640, _panel.Width);
                int ph = Math.Max(480, _panel.Height);
                if (q == 1) { w = Math.Max(640, pw / 2); h = Math.Max(480, ph / 2); depth = 16; }
                else if (q == 2) { w = 1920; h = 1080; depth = 32; }
                else { w = pw; h = ph; depth = 32; }

                TrySet(() => { oc.ColorDepth = depth; });
                try { oc.DesktopWidth = w; } catch { }
                try { oc.DesktopHeight = h; } catch { }

                _connectStart = DateTime.Now;
                oc.Connect();
                _status!.Text = "正在连接…";
                StartPolling();
            }
            catch (Exception ex)
            {
                _status!.Text = "初始化失败：" + ex.Message;
                SystemLog.Exception("远程桌面", ex);
                MessageBox.Show("无法启动 RDP 会话：\n" + ex.Message +
                    "\n\n请确认：①本机已安装 Remote Desktop 客户端（Win10/11 自带）②目标已开启远程桌面③凭据为密码而非 PIN。",
                    "连接错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static dynamic? GetAdvancedSettings(dynamic oc)
        {
            try { return oc.AdvancedSettings9; } catch { }
            try { return oc.AdvancedSettings8; } catch { }
            try { return oc.AdvancedSettings7; } catch { }
            try { return oc.AdvancedSettings; } catch { }
            return null;
        }

        private static void TrySet(Action act) { try { act(); } catch { } }

        private void Reconnect()
        {
            InternalDisconnect();
            BeginConnect();
        }

        private void ToggleFullscreen()
        {
            if (!_fullscreen)
            {
                _savedBorder = FormBorderStyle;
                _savedBounds = Bounds;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
                // 注意：全屏时【保留工具栏可见】，否则 RDP 捕获键盘后 F11 可能传不回来，导致无法退出
                if (_bFull != null) _bFull.Text = "退出全屏 (F11/ESC)";
                _fullscreen = true;
            }
            else
            {
                FormBorderStyle = _savedBorder;
                WindowState = FormWindowState.Normal;
                Bounds = _savedBounds;
                if (_bFull != null) _bFull.Text = "全屏 (F11)";
                _fullscreen = false;
            }
        }

        private void StartPolling()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) =>
            {
                if (_rdp == null) return;

                // 首次连接看门狗：长时间连不上则主动断开并提示，避免永久卡在“正在连接”
                if (!_connectedOnce && !_watchdogTripped
                    && (DateTime.Now - _connectStart).TotalSeconds > MaxConnectSeconds)
                {
                    _watchdogTripped = true;
                    InternalDisconnect();
                    _status!.Text = $"连接超时（{MaxConnectSeconds} 秒未建立会话）：请检查 ①地址/IP 正确 ②目标已开机且「环境诊断」通过 ③凭据为密码而非 PIN；可点「重连」重试。";
                    if (_autoReconnect && !_disposed) ScheduleAutoReconnect();
                    return;
                }

                try
                {
                    dynamic oc = _rdp.Oc;
                    int state = oc.Connected;
                    switch (state)
                    {
                        case 2:
                            if (!_connectedOnce) { _connectedOnce = true; _reconnectAttempts = 0; }
                            _status!.Text = "● 已连接";
                            break;
                        case 1:
                            _status!.Text = "正在连接…";
                            CheckSlowConnect();
                            break;
                        case 3:
                            _status!.Text = "正在断开…";
                            break;
                        case 0:
                            if (_connectedOnce)
                            {
                                _status!.Text = "● 连接已断开";
                                ScheduleAutoReconnect();
                            }
                            else
                            {
                                _status!.Text = "未连接";
                                CheckSlowConnect();
                            }
                            break;
                        default:
                            _status!.Text = "状态：" + state;
                            break;
                    }
                }
                catch { /* 控件尚未就绪，忽略 */ }
            };
            _timer.Start();
        }

        // 连接后长时间无进展时给出可操作的排查提示，而不是干巴巴的“未连接”
        private void CheckSlowConnect()
        {
            if (_connectedOnce || _connectStart == DateTime.MinValue) return;
            var elapsed = (DateTime.Now - _connectStart).TotalSeconds;
            if (elapsed > 8)
            {
                _status!.Text = "连接较慢或失败：请检查 ①地址/IP 正确 ②目标已开机且“环境诊断”通过 ③凭据为密码而非 PIN；可点“重连”重试。";
            }
        }

        private void ScheduleAutoReconnect()
        {
            if (!_autoReconnect || _reconnectAttempts >= MaxReconnectAttempts) return;
            // 已经排过一次就直接返回：状态轮询每秒都会进来，
            // 若每次都重新计时/累加，5 次机会会在几秒内被白白耗尽。
            if (_reconnectPending) return;

            _reconnectPending = true;
            _reconnectAttempts++;
            _status!.Text = $"● 连接已断开，3 秒后自动重连（{_reconnectAttempts}/{MaxReconnectAttempts}）…";
            _reconnectTimer?.Stop();
            _reconnectTimer?.Dispose();
            _reconnectTimer = new Timer { Interval = 3000 };
            _reconnectTimer.Tick += (s, e) =>
            {
                _reconnectTimer?.Stop();
                _reconnectPending = false;
                if (_autoReconnect && !_disposed) Reconnect();
            };
            _reconnectTimer.Start();
        }

        private bool _disposed;
        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            _timer?.Stop(); _timer?.Dispose();
            _reconnectTimer?.Stop(); _reconnectTimer?.Dispose();
            base.Dispose(disposing);
        }

        private void StopPolling() => _timer?.Stop();

        // 仅断开连接本身，不动自动重连开关（供 Reconnect 内部复用）
        private void InternalDisconnect()
        {
            StopPolling();
            _reconnectPending = false;
            _reconnectTimer?.Stop();
            try { if (_rdp != null) { dynamic oc = _rdp.Oc; oc.Disconnect(); } }
            catch { }

            // 释放上一次会话的 RDP ActiveX 控件与 COM 对象。
            // 否则每次“重连 / 切换画质”都会遗留一个 MsRdpClient 实例，反复操作会持续占用内存与句柄。
            try
            {
                if (_rdp != null)
                {
                    _panel?.Controls.Remove(_rdp);
                    _rdp.Dispose();
                }
            }
            catch { }
            _rdp = null;

            // RDP 控件是 STA COM 对象，及时回收引用，避免累积
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // 主动断开（手动点击 / 关闭窗口）：关闭自动重连，避免循环重连
        private void Disconnect()
        {
            _autoReconnect = false;
            InternalDisconnect();
            // 让用户明确看到当前状态，而不是界面停留在“已连接”
            if (_status != null) _status.Text = "● 已断开（可点工具栏「重连」重新建立会话）";
            if (_bAuto != null) _bAuto.Text = "自动重连:关";
        }
    }
}
