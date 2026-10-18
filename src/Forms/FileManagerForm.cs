using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MyRemote.Models;
using MyRemote.Services;

namespace MyRemote.Forms
{
    /// <summary>
    /// 独立窗口的文件管理器：左本地、右远程（UNC）。基于 Windows SMB，
    /// 与远程桌面会话完全解耦，可单独使用、可后台传输。
    /// </summary>
    public class FileManagerForm : Form
    {
        private readonly Device _device;
        private readonly string _password;

        private TextBox? _localPath;
        private TextBox? _remotePath;
        private ListView? _localList;
        private ListView? _remoteList;
        private ProgressBar? _progress;
        private Label? _status;
        private Button? _btnCancel;
        private CancellationTokenSource? _cts;

        public FileManagerForm(Device device, string password)
        {
            _device = device;
            _password = password;
            InitializeComponent();
            Text = $"文件管理 - {device.Name} ({device.Host})";
        }

        private void InitializeComponent()
        {
            AutoScaleMode = AutoScaleMode.None;
            UiScale.SetFormSize(this, 1100, 660, 820, 480);

            // 左右分栏：左侧宽度按比例分配（并在窗口变化时重算），
            // 这样窄屏上右栏不会被左栏固定宽度挤到几乎看不见
            _leftPanel = new Panel { Dock = DockStyle.Left, Width = UiScale.S(430), AutoScroll = true };
            var localGrp = new GroupBox { Text = "本地（本机）", Dock = DockStyle.Fill };
            _localPath = new TextBox { Dock = DockStyle.Top, ReadOnly = true };
            var localNav = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, UiScale.ButtonRowHeight(4)), FlowDirection = FlowDirection.LeftToRight };
            var localUp = new Button { Text = "↑上级" }.AutoFit(70);
            localUp.Click += (s, e) => NavigateLocal(ParentOf(_localPath!.Text));
            localNav.Controls.Add(localUp);
            _localList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true };
            UiScale.SetColumns(_localList, ("名称", 200), ("类型", 70), ("大小", 90), ("修改时间", 130));
            _localList.DoubleClick += (s, e) => { var it = _localList.SelectedItems.Cast<ListViewItem>().FirstOrDefault(); if (it != null && (bool)it.Tag!) NavigateLocal(Path.Combine(_localPath.Text, it.Text)); };
            localGrp.Controls.Add(_localList); localGrp.Controls.Add(localNav); localGrp.Controls.Add(_localPath);
            _leftPanel.Controls.Add(localGrp);

            var remoteGrp = new GroupBox { Text = "远程（" + _device.Host + "）", Dock = DockStyle.Fill };
            _remotePath = new TextBox { Dock = DockStyle.Top, ReadOnly = true };
            var remoteNav = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, UiScale.ButtonRowHeight(4)), FlowDirection = FlowDirection.LeftToRight };
            var remoteUp = new Button { Text = "↑上级" }.AutoFit(70);
            remoteUp.Click += (s, e) => NavigateRemote(ParentOf(_remotePath!.Text));
            var remoteRefresh = new Button { Text = "刷新" }.AutoFit(70);
            remoteRefresh.Click += (s, e) => NavigateRemote(_remotePath.Text);
            remoteNav.Controls.Add(remoteUp); remoteNav.Controls.Add(remoteRefresh);
            _remoteList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true };
            UiScale.SetColumns(_remoteList, ("名称", 200), ("类型", 70), ("大小", 90), ("修改时间", 130));
            _remoteList.DoubleClick += (s, e) => { var it = _remoteList.SelectedItems.Cast<ListViewItem>().FirstOrDefault(); if (it != null && (bool)it.Tag!) NavigateRemote(Path.Combine(_remotePath.Text, it.Text).Replace('/', '\\')); };
            remoteGrp.Controls.Add(_remoteList); remoteGrp.Controls.Add(remoteNav); remoteGrp.Controls.Add(_remotePath);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, UiScale.ButtonRowHeight(8)), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, Padding = UiScale.S(new Padding(0, 4, 0, 0)) };
            var btnUpload = new Button { Text = "→ 上传到远程" }.AutoFit(120); btnUpload.Click += (s, e) => Upload();
            var btnDownload = new Button { Text = "← 下载到本地" }.AutoFit(120); btnDownload.Click += (s, e) => Download();
            var btnDelete = new Button { Text = "删除所选" }.AutoFit(90); btnDelete.Click += (s, e) => DeleteSelected();
            var btnNewLocal = new Button { Text = "+本地文件夹" }.AutoFit(110); btnNewLocal.Click += (s, e) => NewFolder(false);
            var btnNewRemote = new Button { Text = "+远程文件夹" }.AutoFit(110); btnNewRemote.Click += (s, e) => NewFolder(true);
            _btnCancel = new Button { Text = "取消传输", Enabled = false }.AutoFit(90); _btnCancel.Click += (s, e) => _cts?.Cancel();
            toolbar.Controls.Add(btnUpload); toolbar.Controls.Add(btnDownload); toolbar.Controls.Add(btnDelete);
            toolbar.Controls.Add(btnNewLocal); toolbar.Controls.Add(btnNewRemote); toolbar.Controls.Add(_btnCancel);

            _progress = new ProgressBar { Dock = DockStyle.Bottom, Height = UiScale.S(22), Style = ProgressBarStyle.Continuous };
            _status = new Label { Dock = DockStyle.Bottom, Height = UiScale.S(26), Text = "就绪", TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

            Controls.Add(remoteGrp); Controls.Add(_leftPanel); Controls.Add(toolbar); Controls.Add(_progress); Controls.Add(_status);

            // 左栏宽度跟随窗口变化（约占 40%，并夹在设计 320～520 之间），保证右栏始终有可用空间
            Resize += (s, e) => AdjustSplit();
            AdjustSplit();

            Shown += (s, e) => OnShown();
            // 关闭窗口时取消进行中的传输，避免后台仍在往已关闭界面的会话写数据
            FormClosing += (s, e) => { try { _cts?.Cancel(); } catch { } };
        }

        private Panel? _leftPanel;

        /// <summary>左右分栏按比例自适应：窄屏上自动收窄左栏，避免右栏被挤没。</summary>
        private void AdjustSplit()
        {
            if (_leftPanel == null) return;
            try
            {
                // 下限随客户区宽度收敛：小屏上左栏若仍占 300+ 像素，右栏（远程）会几乎看不见
                int min = Math.Min(UiScale.S(300), (int)(ClientSize.Width * 0.28));
                int max = UiScale.S(520);
                int want = (int)(ClientSize.Width * 0.40);
                _leftPanel.Width = Math.Max(min, Math.Min(max, want));
            }
            catch { }
        }

        private void OnShown()
        {
            // 把远控凭据推进 Windows 凭据管理器，使 UNC 访问免密
            if (!string.IsNullOrEmpty(_password))
                NetworkShareHelper.AddCredential(_device.Host, _device.UserName, _password);

            _localPath!.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            NavigateLocal(_localPath.Text);
            _remotePath!.Text = $"\\\\{_device.Host}\\{_device.ShareName}";
            NavigateRemote(_remotePath.Text);
        }

        private string ParentOf(string path)
        {
            try { var p = Directory.GetParent(path); return p?.FullName ?? path; }
            catch { return path; }
        }

        private void NavigateLocal(string path)
        {
            _localPath!.Text = path;
            Task.Run(() => ListEntries(path, _localList!, false));
        }

        private void NavigateRemote(string path)
        {
            _remotePath!.Text = path;
            Task.Run(() => ListEntries(path, _remoteList!, true));
        }

        private void ListEntries(string path, ListView list, bool remote)
        {
            try
            {
                var dirs = Directory.GetDirectories(path).OrderBy(x => x).ToArray();
                var files = Directory.GetFiles(path).OrderBy(x => x).ToArray();
                var items = new List<ListViewItem>();
                foreach (var d in dirs)
                {
                    var di = new DirectoryInfo(d);
                    var it = new ListViewItem(new[] { di.Name, "文件夹", "", "" }) { Tag = true };
                    items.Add(it);
                }
                foreach (var f in files)
                {
                    var fi = new FileInfo(f);
                    var it = new ListViewItem(new[] { fi.Name, "文件", FormatSize(fi.Length), fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") }) { Tag = false };
                    items.Add(it);
                }
                InvokeIfNeeded(() =>
                {
                    list.Items.Clear();
                    list.Items.AddRange(items.ToArray());
                    _status!.Text = $"{(remote ? "远程" : "本地")}：{path}（{items.Count} 项）";
                });
            }
            catch (Exception ex)
            {
                SystemLog.Exception("文件管理", ex);
                InvokeIfNeeded(() => _status!.Text = $"{(remote ? "远程" : "本地")}列出失败：{ex.Message}");
            }
        }

        private async void Upload()
        {
            if (_localList!.SelectedItems.Count == 0) { MessageBox.Show("请先在左侧选择要上传的文件或文件夹。"); return; }
            await Transfer(selectedPaths(_localList, _localPath!.Text), _remotePath!.Text, localToRemote: true);
        }

        private async void Download()
        {
            if (_remoteList!.SelectedItems.Count == 0) { MessageBox.Show("请先在右侧选择要下载的文件或文件夹。"); return; }
            await Transfer(selectedPaths(_remoteList, _remotePath!.Text), _localPath!.Text, localToRemote: false);
        }

        private List<string> selectedPaths(ListView list, string basePath)
        {
            return list.SelectedItems.Cast<ListViewItem>().Select(it => Path.Combine(basePath, it.Text)).ToList();
        }

        private async Task Transfer(List<string> sources, string destDir, bool localToRemote)
        {
            // 覆盖/合并确认：目标已存在同名项时先问一次，避免静默覆盖造成数据丢失
            var conflicts = new List<string>();
            foreach (var src in sources)
            {
                var name = Path.GetFileName(src.TrimEnd('\\', '/'));
                var dest = Path.Combine(destDir, name);
                if (File.Exists(dest) || Directory.Exists(dest)) conflicts.Add(name);
            }
            if (conflicts.Count > 0)
            {
                var msg = $"目标位置已存在以下同名项，继续将覆盖文件 / 合并文件夹：\n\n{string.Join("\n", conflicts.Take(10))}" +
                          (conflicts.Count > 10 ? $"\n… 共 {conflicts.Count} 项" : "") + "\n\n确定继续？";
                if (MessageBox.Show(msg, "目标已存在", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                    return;
            }

            _cts = new CancellationTokenSource();
            _btnCancel!.Enabled = true;
            try
            {
                var progress = new Progress<SmbTransferService.ProgressInfo>(p =>
                {
                    InvokeIfNeeded(() =>
                    {
                        if (p.BytesTotal > 0)
                            _progress!.Value = (int)(100 * p.BytesDone / p.BytesTotal);
                        _status!.Text = $"传输中：{p.CurrentFile}  ({FormatSize(p.BytesDone)}/{FormatSize(p.BytesTotal)})";
                    });
                });

                foreach (var src in sources)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(src.TrimEnd('\\', '/'));
                    var dest = Path.Combine(destDir, name);
                    if (Directory.Exists(src))
                        await SmbTransferService.CopyDirectoryAsync(src, dest, progress, _cts.Token);
                    else
                        await SmbTransferService.CopyFileAsync(src, dest, progress, _cts.Token);
                }
                InvokeIfNeeded(() => _status!.Text = "传输完成。");
            }
            catch (OperationCanceledException)
            {
                InvokeIfNeeded(() => _status!.Text = "已取消传输。");
            }
            catch (Exception ex)
            {
                InvokeIfNeeded(() => _status!.Text = "传输失败：" + ex.Message);
                SystemLog.ShowError("文件传输", ex);
            }
            finally
            {
                _btnCancel!.Enabled = false;
                _progress!.Value = 0;
            }
        }

        private void DeleteSelected()
        {
            var localSel = _localList!.SelectedItems.Cast<ListViewItem>().ToArray();
            var remoteSel = _remoteList!.SelectedItems.Cast<ListViewItem>().ToArray();
            if (localSel.Length == 0 && remoteSel.Length == 0) return;
            var detail = new List<string>();
            if (localSel.Length > 0) detail.Add($"本地 {localSel.Length} 项");
            if (remoteSel.Length > 0) detail.Add($"远程 {remoteSel.Length} 项");
            if (MessageBox.Show($"确定删除所选的 {string.Join("、", detail)}？\n此操作不可恢复（文件夹将连同内容一并删除）。",
                "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            try
            {
                foreach (var it in localSel) { var p = Path.Combine(_localPath!.Text, it.Text); if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p); }
                foreach (var it in remoteSel) { var p = Path.Combine(_remotePath!.Text, it.Text); if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p); }
                NavigateLocal(_localPath!.Text); NavigateRemote(_remotePath!.Text);
            }
            catch (Exception ex) { MessageBox.Show("删除失败：" + ex.Message); }
        }

        private void NewFolder(bool remote)
        {
            using var input = new NameInputBox("新建文件夹", "请输入新文件夹名称：");
            if (input.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(input.Value)) return;
            var basePath = remote ? _remotePath!.Text : _localPath!.Text;
            try
            {
                var newPath = Path.Combine(basePath, input.Value.Trim());
                Directory.CreateDirectory(newPath);
                if (remote) NavigateRemote(_remotePath!.Text); else NavigateLocal(_localPath!.Text);
            }
            catch (Exception ex) { MessageBox.Show("创建失败：" + ex.Message); }
        }

        private void InvokeIfNeeded(Action action)
        {
            // 窗体可能已在传输过程中被关闭：此时静默放弃 UI 更新，避免抛 ObjectDisposedException
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) Invoke(action); else action();
            }
            catch { }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }
    }

    /// <summary>通用文本输入小窗，用于“新建文件夹”等场景。</summary>
    public class NameInputBox : Form
    {
        private readonly TextBox _tb = new() { Dock = DockStyle.Fill };
        public string Value => _tb.Text;

        public NameInputBox(string title, string message)
        {
            Text = title; AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            UiScale.SetFormSize(this, 360, 150);
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
