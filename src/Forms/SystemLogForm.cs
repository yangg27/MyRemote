using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MyRemote.Services;

namespace MyRemote.Forms
{
    /// <summary>
    /// 系统日志查看器：实时显示 SystemLog 的内存缓冲，并可直接打开落盘日志文件。
    /// 用于故障排查与系统升级时回溯。
    /// </summary>
    public class SystemLogForm : Form
    {
        private ListView? _list;
        private Label? _stat;
        private Timer? _timer;
        private int _shown;

        public SystemLogForm()
        {
            InitializeComponent();
            LoadEntries();
        }

        private void InitializeComponent()
        {
            Text = "系统日志 - MyRemote";
            AutoScaleMode = AutoScaleMode.None;
            UiScale.SetFormSize(this, 820, 520, 640, 380);
            StartPosition = FormStartPosition.CenterParent;
            Icon = AppIcon.Create();

            var toolbar = new ToolStrip { Dock = DockStyle.Top, ImageScalingSize = UiScale.S(new Size(16, 16)), CanOverflow = false };
            // 高 DPI 下按钮文本变宽：允许换行，保证按钮不被藏进溢出菜单
            toolbar.LayoutStyle = ToolStripLayoutStyle.Flow;
            toolbar.AutoSize = true;
            var bRefresh = new ToolStripButton("刷新");
            bRefresh.Click += (s, e) => LoadEntries();
            var bOpen = new ToolStripButton("打开日志文件");
            bOpen.Click += (s, e) => OpenLogFile();
            var bCopy = new ToolStripButton("复制选中");
            bCopy.Click += (s, e) => CopySelected();
            var bAuto = new ToolStripButton("自动刷新:开");
            bAuto.Click += (s, e) =>
            {
                if (_timer!.Enabled) { _timer.Stop(); bAuto.Text = "自动刷新:关"; }
                else { _timer.Start(); bAuto.Text = "自动刷新:开"; }
            };
            toolbar.Items.AddRange(new ToolStripItem[] { bRefresh, bOpen, bCopy, bAuto });

            _stat = new Label
            {
                Dock = DockStyle.Top, Height = UiScale.S(26), Text = "加载中…",
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f),
                BackColor = SystemColors.Control, TextAlign = ContentAlignment.MiddleLeft,
                Padding = UiScale.S(new Padding(8, 0, 0, 0)),
                AutoEllipsis = true
            };

            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                MultiSelect = true, HideSelection = false, VirtualMode = false
            };
            // 末列（消息）自动补满剩余宽度，长日志不再被截断
            UiScale.SetColumns(_list,
                ("时间", 150), ("级别", 70), ("组件", 140), ("消息", 460));

            Controls.Add(_list);
            Controls.Add(_stat);
            Controls.Add(toolbar);

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => LoadEntries();
            _timer.Start();
            FormClosed += (s, e) => { _timer?.Stop(); _timer?.Dispose(); };
        }

        private LogEntry? _first;

        private void LoadEntries()
        {
            if (_list == null || _stat == null) return;
            var entries = SystemLog.Entries;

            // 增量刷新：只有当缓冲被清空过、发生过滚动淘汰、或首条记录变化时才整体重建，
            // 否则只追加新增记录。避免每秒重建上千行导致列表闪烁与卡顿。
            bool fullReload = _shown == 0
                              || entries.Count < _shown
                              || _list.Items.Count != _shown
                              || (_shown > 0 && !ReferenceEquals(_first, entries[0]));

            if (fullReload)
            {
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var e in entries) _list.Items.Add(MakeItem(e));
                _list.EndUpdate();
                _first = entries.Count > 0 ? entries[0] : null;
            }
            else if (entries.Count > _shown)
            {
                _list.BeginUpdate();
                for (int i = _shown; i < entries.Count; i++) _list.Items.Add(MakeItem(entries[i]));
                _list.EndUpdate();
                // 自动滚到最新一条
                if (_list.Items.Count > 0) _list.EnsureVisible(_list.Items.Count - 1);
            }

            _shown = entries.Count;
            _stat.Text = $"共 {_shown} 条（内存缓冲，最新在下）；日志文件：{SystemLog.LogFilePath}";
        }

        private static ListViewItem MakeItem(LogEntry e) => new(new[] {
                e.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                SystemLog.LevelText(e.Level),
                e.Component,
                e.Message
            })
        {
            ForeColor = e.Level == LogLevel.Error ? Color.FromArgb(200, 0, 0)
                      : e.Level == LogLevel.Warn ? Color.FromArgb(200, 120, 0)
                      : Color.FromArgb(0, 90, 200)
        };

        private void OpenLogFile()
        {
            try
            {
                var path = SystemLog.LogFilePath;
                if (!File.Exists(path))
                {
                    MessageBox.Show("日志文件尚不存在（还没有产生任何记录）。", "提示");
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开日志文件：" + ex.Message, "错误");
            }
        }

        private void CopySelected()
        {
            if (_list == null || _list.SelectedItems.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (ListViewItem it in _list.SelectedItems)
                sb.AppendLine(string.Join(" | ", it.Text, it.SubItems[1].Text, it.SubItems[2].Text, it.SubItems[3].Text));
            try { Clipboard.SetText(sb.ToString()); }
            catch { }
        }
    }
}
