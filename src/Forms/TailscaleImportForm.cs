using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using MyRemote.Services;

namespace MyRemote.Forms
{
    /// <summary>
    /// 从 Tailscale 网络导入设备：列出同账号的其他机器，勾选后一键加入设备列表，
    /// 免去皮毛手抄 100.x.x.x 虚拟 IP。
    ///
    /// 读取 tailscale status 需要调用外部进程，可能耗时数秒，
    /// 因此窗口先显示“正在读取…”，在后台线程取数据后再填充，避免窗口一打开就“未响应”。
    /// </summary>
    public class TailscaleImportForm : Form
    {
        private readonly CheckedListBox _clb = new() { Dock = DockStyle.Fill, CheckOnClick = true };
        private readonly Label _tip;
        private List<TailscalePeer> _peers = new();
        private readonly Button _ok;

        public List<TailscalePeer> SelectedPeers { get; } = new();

        public TailscaleImportForm()
        {
            Text = "从 Tailscale 导入设备";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            UiScale.SetFormSize(this, 480, 420);

            _tip = new Label
            {
                Dock = DockStyle.Top, Height = UiScale.S(62),
                Text = "正在读取 Tailscale 设备列表，请稍候…",
                Padding = UiScale.S(new Padding(8))
            };

            var flp = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, UiScale.ButtonRowHeight(12)), Padding = UiScale.S(new Padding(0, 4, 8, 0)) };
            _ok = new Button { Text = "导入", Enabled = false }.AutoFit(80);
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel }.AutoFit(80);
            _ok.Click += (s, e) => { Collect(); DialogResult = DialogResult.OK; };
            flp.Controls.Add(cancel); flp.Controls.Add(_ok);

            Controls.Add(_clb); Controls.Add(_tip); Controls.Add(flp);
            Shown += async (s, e) => await LoadPeersAsync();
        }

        private async Task LoadPeersAsync()
        {
            _peers = await Task.Run(() => TailscaleImporter.GetPeers());

            _clb.Items.Clear();
            if (_peers.Count == 0)
            {
                _tip.Text = "未检测到 Tailscale 对端。请确认：①已安装并登录 Tailscale（同一账号）" +
                            "②tailscale.exe 在 PATH 中 ③其它设备已开机上线。";
                return;
            }

            foreach (var p in _peers)
                _clb.Items.Add($"{p.Hostname}   ({p.Ip})   {(p.IsOnline ? "在线" : "离线")}", true);

            _tip.Text = "勾选要加入的设备后点「导入」。用户名已预填本机名，若不同请之后编辑。";
            _ok.Enabled = true;
        }

        private void Collect()
        {
            for (int i = 0; i < _clb.Items.Count && i < _peers.Count; i++)
                if (_clb.GetItemChecked(i)) SelectedPeers.Add(_peers[i]);
        }
    }
}
