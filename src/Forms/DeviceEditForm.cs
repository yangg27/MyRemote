using System;
using System.Drawing;
using System.Windows.Forms;
using MyRemote.Models;
using MyRemote.Services;

namespace MyRemote.Forms
{
    /// <summary>新增/编辑设备的对话框。密码单独由调用方存到 CredentialVault。</summary>
    public class DeviceEditForm : Form
    {
        private readonly Device _device;
        private readonly bool _isNew;
        private TextBox _name = null!; private TextBox _host = null!; private TextBox _user = null!;
        private TextBox _port = null!; private TextBox _share = null!; private TextBox _notes = null!;
        private TextBox _pass = null!;

        public DeviceEditForm(Device device, bool isNew)
        {
            _device = device;
            _isNew = isNew;
            InitializeComponent();
            _name.Text = device.Name; _host.Text = device.Host; _user.Text = device.UserName;
            _port.Text = device.RdpPort.ToString(); _share.Text = device.ShareName; _notes.Text = device.Notes;
        }

        private void InitializeComponent()
        {
            Text = _isNew ? "添加设备" : "编辑设备";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            UiScale.SetFormSize(this, 420, 400);

            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Padding = UiScale.S(new Padding(10)), AutoScroll = true };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _name = Field(tlp, "显示名：", 0);
            _host = Field(tlp, "地址/IP：", 1);
            _user = Field(tlp, "用户名：", 2);
            _port = Field(tlp, "RDP 端口：", 3);
            _share = Field(tlp, "共享名：", 4);
            _notes = Field(tlp, "备注：", 5);

            var passRow = 6;
            tlp.Controls.Add(new Label { Text = "登录密码：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, passRow);
            _pass = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            tlp.Controls.Add(_pass, 1, passRow);
            tlp.Controls.Add(new Label { Text = "提示：远程登录必须用密码，PIN 无法用于远程认证。", Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText }, 1, 7);

            var btnOk = new Button { Text = "保存", DialogResult = DialogResult.OK }.AutoFit(80);
            var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel }.AutoFit(80);
            btnOk.Click += (s, e) => { if (ValidateAndSave()) DialogResult = DialogResult.OK; };
            var btnFill = new Button { Text = "用本机信息填充" }.AutoFit(120);
            btnFill.Click += (s, e) => FillFromLocal();
            var flp = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, UiScale.ButtonRowHeight(12)), Padding = UiScale.S(new Padding(0, 4, 8, 0)) };
            flp.Controls.Add(btnCancel); flp.Controls.Add(btnOk); flp.Controls.Add(btnFill);
            Controls.Add(tlp); Controls.Add(flp);

            // 键盘操作：回车保存、ESC 取消
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private void FillFromLocal()
        {
            var ip = LocalMachineInfo.GetTailscaleIp();
            if (!string.IsNullOrEmpty(ip)) _host.Text = ip;
            _user.Text = LocalMachineInfo.UserName;
        }

        private TextBox Field(TableLayoutPanel tlp, string label, int row)
        {
            tlp.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            var tb = new TextBox { Dock = DockStyle.Fill };
            tlp.Controls.Add(tb, 1, row);
            return tb;
        }

        private bool ValidateAndSave()
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) { MessageBox.Show("请填写显示名。"); return false; }
            if (string.IsNullOrWhiteSpace(_host.Text)) { MessageBox.Show("请填写地址/IP。"); return false; }
            // 端口必须合法（1-65535），否则连接会以“无法解析”类错误失败，且难排查
            if (!string.IsNullOrWhiteSpace(_port.Text))
            {
                if (!int.TryParse(_port.Text.Trim(), out var port) || port < 1 || port > 65535)
                {
                    MessageBox.Show("RDP 端口应为 1-65535 之间的数字（默认 3389）。");
                    return false;
                }
                _device.RdpPort = port;
            }
            _device.Name = _name.Text.Trim();
            _device.Host = _host.Text.Trim();
            _device.UserName = _user.Text.Trim();
            _device.ShareName = string.IsNullOrWhiteSpace(_share.Text) ? "C$" : _share.Text.Trim();
            _device.Notes = _notes.Text.Trim();
            // 密码：仅在用户输入时才更新（避免清空已有密码）
            EditResultPassword = _pass.Text;
            return true;
        }

        /// <summary>用户输入的密码（可能为空，表示不修改）。</summary>
        public string EditResultPassword { get; private set; } = "";
    }
}
