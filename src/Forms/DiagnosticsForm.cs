using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MyRemote.Services;

namespace MyRemote.Forms
{
    /// <summary>
    /// 环境诊断窗体：把 DiagnosticsService 跑出的每一项以“分组列表 + 状态图标”展示，
    /// 点击任意一项，下方显示该项的当前实际情况与解决指引；可一键修复不达标项，
    /// 修复前自动创建快照，支持一键恢复到修复前的状态。
    /// </summary>
    public class DiagnosticsForm : Form
    {
        private ListView? _list;
        private RichTextBox? _rtb;
        private Label? _stat;
        private ToolStrip? _toolbar;
        private ToolStripLabel? _dpiLabel;
        private Panel? _detailPanel;
        private ToolTip? _statTip;
        private Button? _fixBtn;
        private Label? _fixHint;
        private DiagnosticItem? _selected;

        public DiagnosticsForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "环境诊断 - MyRemote";
            AutoScaleMode = AutoScaleMode.None;   // 尺寸统一由 UiScale 换算，避免与框架自动缩放叠加
            UiScale.SetFormSize(this, 900, 640, 740, 480);
            StartPosition = FormStartPosition.CenterParent;

            _toolbar = new ToolStrip { Dock = DockStyle.Top, ImageScalingSize = UiScale.S(new Size(16, 16)) };
            // 按钮较多（6 个）：窄屏/高 DPI 下允许换行显示，而不是把按钮藏进溢出下拉菜单
            _toolbar.CanOverflow = false;
            _toolbar.LayoutStyle = ToolStripLayoutStyle.Flow;
            _toolbar.AutoSize = true;
            var bAuto = new ToolStripButton("一键检测并修复") { ToolTipText = "自动遍历：检测 → 修复 → 复检，循环直到全部通过（推荐）" };
            bAuto.Click += (s, e) => _ = RunAutoFixAllAsync();
            var bRun = new ToolStripButton("重新检测");
            bRun.Click += (s, e) => _ = RunDiagnosticsAsync();
            var bFixAll = new ToolStripButton("一键修复");
            bFixAll.Click += (s, e) => _ = RunRepairAllAsync();
            var bRestore = new ToolStripButton("一键恢复");
            bRestore.Click += (s, e) => _ = RunRestoreAsync();
            var bLog = new ToolStripButton("系统日志");
            bLog.Click += (s, e) => new SystemLogForm().Show();
            // 右对齐显示当前界面缩放：一眼就能确认本机 DPI 适配是否生效（排障用）
            _dpiLabel = new ToolStripLabel("界面缩放：" + UiScale.Describe())
            {
                Alignment = ToolStripItemAlignment.Right,
                ToolTipText = "当前界面按此比例自适应（可在环境变量 MYREMOTE_UI_SCALE 中手动覆盖）"
            };
            _toolbar.Items.AddRange(new ToolStripItem[] { bAuto, new ToolStripSeparator(), bRun, bFixAll, bRestore, bLog, _dpiLabel });

            _stat = new Label
            {
                Dock = DockStyle.Top,
                Height = UiScale.S(28),
                Text = "准备检测…",
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 10f),
                BackColor = SystemColors.Control,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = UiScale.S(new Padding(8, 0, 0, 0)),
                AutoEllipsis = true
            };

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false
            };
            UiScale.SetColumns(_list,
                ("判定", 70), ("类别", 140), ("检测项", 240), ("当前情况", 360));
            _list.SelectedIndexChanged += (s, e) =>
            {
                if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is DiagnosticItem di)
                    ShowDetail(di);
            };

            // 详情面板：提示 + 富文本 + “立即修复此项”按钮
            _detailPanel = new Panel { Dock = DockStyle.Bottom, Height = UiScale.S(240) };
            var lbl = new Label
            {
                Dock = DockStyle.Top,
                Height = UiScale.S(24),
                Text = "详情与解决指引（点击上方任意一项查看）",
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f, FontStyle.Bold),
                Padding = UiScale.S(new Padding(8, 4, 0, 0))
            };
            _rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                // 用等宽字体族但尺寸走 pt 并跟随系统字体缩放；不写死 9.5pt，避免高 DPI 下偏小
                Font = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
                DetectUrls = false
            };
            // 窗口后续若被拉高，详情区按比例多分一些，正文可读性更好
            var fixPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = UiScale.S(38), FlowDirection = FlowDirection.LeftToRight, Padding = UiScale.S(new Padding(6, 4, 0, 0)), WrapContents = false, AutoScroll = true };
            _fixBtn = new Button { Text = "立即修复此项", Enabled = false }.AutoFit(130);
            _fixBtn.Click += (s, e) => _ = RunRepairOneAsync();
            _fixHint = new Label { Text = "（选中的项若可自动修复，可点此单独修复）", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
            fixPanel.Controls.Add(_fixBtn);
            fixPanel.Controls.Add(_fixHint);
            _detailPanel.Controls.Add(fixPanel);
            _detailPanel.Controls.Add(_rtb);
            _detailPanel.Controls.Add(lbl);

            // 注意添加顺序：WinForms 中后添加的控件优先占据边缘，
// Dock=Fill 的列表必须先添加（最后才占剩余空间），
// 否则先添加的 Dock=Bottom 详情面板会被 Fill 抢光空间、整块消失。
            Controls.Add(_list);
            Controls.Add(_detailPanel);
            Controls.Add(_stat);
            Controls.Add(_toolbar);

            // 详情区高度随窗口高度自适应：窗口越大占比越高，小屏时自动收敛到较小值，
            // 保证列表区不被压缩到看不见内容
            Resize += (s, e) => AdjustDetailHeight();
            AdjustDetailHeight();
        }

        /// <summary>按当前窗高分配详情区高度（约占 38%，并限制在设计 150～360 像素之间）。</summary>
        private void AdjustDetailHeight()
        {
            if (_detailPanel == null) return;
            try
            {
                // 小屏 + 高 DPI 时窗口会被裁到屏幕内：此处下限也要跟着收敛，
                // 否则详情区会吃掉列表区空间，让"检测项列表"只剩一两行
                int min = Math.Min(UiScale.S(150), (int)(ClientSize.Height * 0.22));
                int max = UiScale.S(360);
                int want = (int)(ClientSize.Height * 0.38);
                _detailPanel.Height = Math.Max(min, Math.Min(max, want));
            }
            catch { }
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            await RunDiagnosticsAsync();

            // 由主界面「一键检测并修复」拉起时，载入后自动开始全流程
            if (_autoStart)
            {
                _autoStart = false;
                await RunAutoFixAllAsync();
            }
        }

        private bool _autoStart;

        /// <summary>请求本窗体打开后立即开始「一键检测并修复」全流程。</summary>
        public void RequestAutoStart() => _autoStart = true;

        private async Task RunDiagnosticsAsync()
        {
            if (_list == null || _rtb == null || _stat == null) return;
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.EndUpdate();
            _rtb.Text = "正在检测环境，请稍候…（约需数秒）";
            _stat.Text = "检测中…";
            if (_toolbar != null) _toolbar.Enabled = false;

            var items = await Task.Run(() => DiagnosticsService.RunAll());

            RenderItems(items);
            if (_toolbar != null) _toolbar.Enabled = true;

            var pass = items.Count(x => x.Status == DiagStatus.Pass);
            var warn = items.Count(x => x.Status == DiagStatus.Warn);
            var fail = items.Count(x => x.Status == DiagStatus.Fail);
            var info = items.Count(x => x.Status == DiagStatus.Info);
            var unknown = items.Count(x => x.Status == DiagStatus.Unknown);
            _stat!.Text = $"通过 {pass}  |  需关注 {warn}  |  未满足 {fail}  |  提示 {info}  |  未知 {unknown}    （未满足/需关注项可点击后「立即修复」或「一键修复」）";
            // 窗口窄时该行会被省略号截断，挂上悬停提示以便查看全文
            _statTip ??= new ToolTip { AutoPopDelay = 15000, InitialDelay = 300, ReshowDelay = 100 };
            _statTip.SetToolTip(_stat, _stat.Text);

            if (_list.Items.Count > 0)
            {
                var focus = _list.Items.Cast<ListViewItem>()
                    .FirstOrDefault(x => x.Tag is DiagnosticItem di && di.Status is DiagStatus.Fail or DiagStatus.Warn);
                _list.Items[0].Selected = true;
                if (focus != null) { focus.Selected = true; focus.EnsureVisible(); }
                if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is DiagnosticItem sel)
                    ShowDetail(sel);
            }
        }

        private void ShowDetail(DiagnosticItem it)
        {
            _selected = it;
            if (_rtb == null || _fixBtn == null || _fixHint == null) return;
            var sb = new StringBuilder();
            sb.AppendLine("【检测项】" + it.Title);
            sb.AppendLine("【判定】" + StatusText(it.Status));
            sb.AppendLine();
            sb.AppendLine("【当前情况】");
            sb.AppendLine(string.IsNullOrEmpty(it.Detail) ? "（无）" : it.Detail);
            sb.AppendLine();
            sb.AppendLine("【解决指引】");
            sb.AppendLine(string.IsNullOrEmpty(it.FixGuide)
                ? "无需处理，已符合要求。"
                : it.FixGuide);
            _rtb.Text = sb.ToString();

            // 可修复且未达标：开放“立即修复此项”
            var repairable = it.CanRepair && it.Status is DiagStatus.Fail or DiagStatus.Warn;
            _fixBtn.Enabled = repairable;
            _fixHint.Text = repairable
                ? $"将执行修复：「{RepairService.ItemName(it.RepairId)}」（修复前会自动创建快照，可一键恢复）"
                : (it.CanRepair ? "该项已满足，无需修复。" : "该项不支持自动修复，请按上方指引手动处理。");
        }

        // ---------- 一键遍历：检测 → 修复 → 复检，循环直到全部通过 ----------
        /// <summary>
        /// 全自动遍历修复：反复执行「检测 → 汇总不达标且可修复项 → 修复 → 再检测」，
        /// 直到没有可修复的不达标项，或达到最大轮次。这样用户只需点一次按钮，
        /// 依赖关系（如"先装 Tailscale 再检测在线状态"）也能在后续轮次自动收敛。
        /// </summary>
        private async Task RunAutoFixAllAsync()
        {
            if (_toolbar == null || _stat == null) return;

            if (MessageBox.Show(
                "将自动执行：检测 → 修复 → 复检，循环进行，直到所有可修复项通过。\n\n" +
                "• 修复前会自动创建系统快照（可「一键恢复」回退）\n" +
                "• 涉及系统设置的操作需要管理员权限，会弹出 UAC，请点「是」\n" +
                "• 已内置安装包的项目（如 Tailscale）会自动安装，无需手动操作\n\n" +
                "是否开始？",
                "一键检测并修复", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            const int maxRounds = 4;
            var log = new StringBuilder();
            var totalFixed = new List<string>();
            var totalFailed = new List<(string name, string msg)>();
            bool anyCancel = false;

            _toolbar.Enabled = false;

            for (int round = 1; round <= maxRounds; round++)
            {
                _stat.Text = $"第 {round}/{maxRounds} 轮：正在检测…";
                var items = await Task.Run(() => DiagnosticsService.RunAll());
                RenderItems(items);

                var needFix = items
                    .Where(x => x.CanRepair && x.Status is DiagStatus.Fail or DiagStatus.Warn)
                    .Select(x => string.IsNullOrEmpty(x.RepairId) ? x.Id : x.RepairId)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct().ToList();

                if (needFix.Count == 0)
                {
                    var fails = items.Count(x => x.Status == DiagStatus.Fail);
                    var warns = items.Count(x => x.Status == DiagStatus.Warn);
                    _stat.Text = fails == 0 && warns == 0
                        ? $"✓ 全部通过（共 {items.Count} 项），无需修复。"
                        : $"✓ 可自动修复项均已完成；剩余 {fails} 项未满足、{warns} 项需关注需手动处理（见列表）。";
                    break;
                }

                if (round == maxRounds)
                {
                    log.AppendLine($"（已达最大轮次 {maxRounds}，停止自动修复）");
                    break;
                }

                _stat.Text = $"第 {round}/{maxRounds} 轮：正在修复 {needFix.Count} 项…（如弹 UAC 请点“是”）";
                log.AppendLine($"— 第 {round} 轮修复：{string.Join("、", needFix.Select(RepairService.ItemName))}");

                var result = await RepairService.RequestRepairAsync(needFix);
                if (result.UserCancelled)
                {
                    anyCancel = true;
                    log.AppendLine("  已取消（未授予管理员权限），自动流程终止。");
                    break;
                }

                foreach (var ir in result.Items)
                {
                    log.AppendLine($"  [{ir.Status}] {RepairService.ItemName(ir.Id)}：{ir.Message}");
                    if (ir.Status == "fixed") totalFixed.Add(RepairService.ItemName(ir.Id));
                    else if (ir.Status == "failed") totalFailed.Add((RepairService.ItemName(ir.Id), ir.Message));
                }

                // 下一轮会重新检测，从而反映修复是否真正生效
                await Task.Delay(600);
            }

            _toolbar.Enabled = true;

            // 最终复检，刷新列表
            _stat.Text = "正在做最终复检…";
            var finalItems = await Task.Run(() => DiagnosticsService.RunAll());
            RenderItems(finalItems);

            var failLeft = finalItems.Count(x => x.Status == DiagStatus.Fail);
            var warnLeft = finalItems.Count(x => x.Status == DiagStatus.Warn);
            var passCount = finalItems.Count(x => x.Status == DiagStatus.Pass);

            var sb = new StringBuilder();
            sb.AppendLine(anyCancel ? "⚠ 流程被取消（未授予管理员权限）。" : "✓ 自动检测修复流程结束。");
            sb.AppendLine();
            sb.AppendLine($"最终结果：通过 {passCount} 项 ｜ 未满足 {failLeft} 项 ｜ 需关注 {warnLeft} 项");
            if (totalFixed.Count > 0)
                sb.AppendLine($"成功修复：{string.Join("、", totalFixed.Distinct())}");
            if (totalFailed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("以下项修复未成功（需手动处理）：");
                foreach (var (name, msg) in totalFailed.Distinct())
                    sb.AppendLine($"  • {name}：{msg}");
            }
            if (failLeft == 0 && warnLeft == 0)
                sb.AppendLine("\n本机已完全就绪，可以开始使用远程控制功能。");

            sb.AppendLine();
            sb.AppendLine("（详细过程见「系统日志」）");
            MessageBox.Show(sb.ToString(), "一键检测并修复 - 结果", MessageBoxButtons.OK,
                failLeft == 0 && totalFailed.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

            _stat.Text = $"流程结束：通过 {passCount} ｜ 未满足 {failLeft} ｜ 需关注 {warnLeft}";
        }

        /// <summary>把诊断结果渲染到列表（供检测/复检复用）。</summary>
        private void RenderItems(List<DiagnosticItem> items)
        {
            if (_list == null) return;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var it in items)
            {
                var lvi = new ListViewItem(new[]
                {
                    Symbol(it.Status),
                    it.Category,
                    it.Title,
                    Short(it.Detail)
                })
                { Tag = it, ForeColor = ColorOf(it.Status) };
                _list.Items.Add(lvi);
            }
            _list.EndUpdate();
        }

        // ---------- 一键修复（全部可修复的不达标项） ----------
        private async Task RunRepairAllAsync()
        {
            if (_toolbar == null || _stat == null) return;
            var items = _list!.Items.Cast<ListViewItem>()
                .Select(x => x.Tag as DiagnosticItem).Where(x => x != null).Cast<DiagnosticItem>().ToList();
            var ids = items
                .Where(x => x.CanRepair && x.Status is DiagStatus.Fail or DiagStatus.Warn)
                .Select(x => string.IsNullOrEmpty(x.RepairId) ? x.Id : x.RepairId)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct().ToList();

            if (ids.Count == 0)
            {
                MessageBox.Show("当前没有需要自动修复的问题（未满足/需关注项中无可修复项）。", "一键修复");
                return;
            }

            var names = string.Join("、", ids.Select(RepairService.ItemName));
            if (MessageBox.Show($"将修复以下 {ids.Count} 项：\n{names}\n\n修复前会自动创建系统快照，之后可「一键恢复」回退。\n部分操作需要管理员权限，将弹出 UAC 请求，请点击「是」。",
                "一键修复确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            await RunRepairCore(ids, $"正在修复 {ids.Count} 项…");
        }

        // ---------- 立即修复（单个选中项） ----------
        private async Task RunRepairOneAsync()
        {
            if (_selected == null || !_selected.CanRepair) return;
            var rid = string.IsNullOrEmpty(_selected.RepairId) ? _selected.Id : _selected.RepairId;
            if (string.IsNullOrEmpty(rid)) return;
            if (MessageBox.Show($"将修复：「{RepairService.ItemName(rid)}」\n修复前会自动创建系统快照，之后可「一键恢复」回退。",
                "修复确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            await RunRepairCore(new[] { rid }, $"正在修复「{RepairService.ItemName(rid)}」…");
        }

        private async Task RunRepairCore(IEnumerable<string> ids, string waitingText)
        {
            if (_toolbar == null || _stat == null) return;
            _toolbar.Enabled = false;
            _stat.Text = waitingText + "（请在 UAC 弹窗点击“是”；非管理员时会自动请求提权）";
            SystemLog.Info("Diagnostics", waitingText);

            var result = await RepairService.RequestRepairAsync(ids);

            _toolbar.Enabled = true;
            if (result.UserCancelled)
            {
                _stat.Text = "已取消（未授予管理员权限）。";
                MessageBox.Show("未授予管理员权限，修复未执行。\n如需修复，请以管理员身份运行 MyRemote（右键→以管理员身份运行）后重试。",
                    "已取消", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(result.Success ? "✓ 修复完成。" : "⚠ 修复完成，但部分项未成功：");
            foreach (var ir in result.Items)
                sb.AppendLine($"  [{ir.Status}] {RepairService.ItemName(ir.Id)}：{ir.Message}");
            sb.AppendLine("\n详细过程已记录到系统日志（诊断窗体 → 系统日志）。");
            MessageBox.Show(sb.ToString(), "修复结果", MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

            _stat.Text = "修复结束，正在重新检测…";
            await RunDiagnosticsAsync();
        }

        // ---------- 一键恢复（回退到最近一次修复前的快照） ----------
        private async Task RunRestoreAsync()
        {
            if (_toolbar == null || _stat == null) return;
            var snaps = RepairService.ListSnapshots();
            if (snaps.Count == 0)
            {
                MessageBox.Show("当前没有可恢复的快照。\n（只有在执行过「一键修复 / 立即修复」之后，才会生成修复前快照。）",
                    "一键恢复", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var latest = snaps[0];
            if (MessageBox.Show($"将把系统恢复到最近一次修复前的状态（快照时间：{latest.Time:yyyy-MM-dd HH:mm:ss}）。\n恢复后之前被修改的设置会还原。\n需要管理员权限，将弹出 UAC 请求。",
                "一键恢复确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            _toolbar.Enabled = false;
            _stat.Text = "正在恢复到修复前的状态…（请点击 UAC 的“是”）";
            SystemLog.Info("Diagnostics", "用户触发一键恢复，快照：" + latest.Path);

            var result = await RepairService.RequestRestoreAsync(latest.Path);

            _toolbar.Enabled = true;
            if (result.UserCancelled)
            {
                _stat.Text = "已取消（未授予管理员权限）。";
                MessageBox.Show("未授予管理员权限，恢复未执行。", "已取消", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            MessageBox.Show(result.Success ? "✓ 已恢复到修复前的状态。" : "⚠ 恢复过程遇到问题：" + result.Message,
                "恢复结果", MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            _stat.Text = "恢复结束，正在重新检测…";
            await RunDiagnosticsAsync();
        }

        // ---------- 展示辅助 ----------
        private static string Symbol(DiagStatus s) => s switch
        {
            DiagStatus.Pass => "✔ 通过",
            DiagStatus.Warn => "⚠ 需关注",
            DiagStatus.Fail => "✘ 未满足",
            DiagStatus.Info => "ℹ 提示",
            _ => "? 未知"
        };

        private static string StatusText(DiagStatus s) => s switch
        {
            DiagStatus.Pass => "已满足",
            DiagStatus.Warn => "需关注（建议处理）",
            DiagStatus.Fail => "未满足（必须处理）",
            DiagStatus.Info => "可选建议",
            _ => "未知"
        };

        private static Color ColorOf(DiagStatus s) => s switch
        {
            DiagStatus.Pass => Color.FromArgb(0, 128, 0),
            DiagStatus.Warn => Color.FromArgb(200, 120, 0),
            DiagStatus.Fail => Color.FromArgb(200, 0, 0),
            DiagStatus.Info => Color.FromArgb(0, 90, 200),
            _ => Color.Gray
        };

        private static string Short(string s)
        {
            const int max = 80;
            if (string.IsNullOrEmpty(s)) return "";
            var firstLine = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return firstLine.Length > max ? firstLine.Substring(0, max) + "…" : firstLine;
        }
    }
}
