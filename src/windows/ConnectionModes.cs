using System;
using System.Drawing;
using System.Windows.Forms;

namespace AtomClipUsb
{
#if BLE
    sealed partial class MainForm
    {
        readonly RadioButton subPowerShell = new SegmentButton { Text = "PowerShell", Appearance = Appearance.Button, AutoSize = true, Checked = true };
        readonly RadioButton subKeyboard = new SegmentButton { Text = "仅 USB 键盘", Appearance = Appearance.Button, AutoSize = true };
        readonly CheckBox fastInput = new CheckBox { Text = "快速输入（漏字时关闭，使用兼容速度）", AutoSize = true, Checked = true };
        readonly Panel modeOptions = new Panel { AutoSize = true, Dock = DockStyle.Fill };
        readonly Label routeHeading = new Label(), routeHint = new Label(), editorTitle = new Label();
        Label setupModeHeading, setupDirection, setupFinal, typingPaceNote;
        readonly Button startSetup = new SoftButton();
        readonly string[] routeDrafts = new string[4];
        int previousRoute = 2;
        bool supportsFastTyping;
        int ActiveRoute { get { return modeProgram.Checked ? 0 : subKeyboard.Checked ? 3 : 2; } }
        bool KeyboardRoute { get { return ActiveRoute == 3; } }
        bool DuplexRoute { get { return ActiveRoute == 2; } }
        static void FitDockedContent(Panel host, Control content)
        {
            host.AutoSize = false;
            EventHandler fit = delegate {
                int height = Math.Max(1, content.Height);
                if (host.MinimumSize.Height != height) { host.MinimumSize = new Size(0, height); host.Height = height; }
            };
            content.SizeChanged += fit; fit(null, EventArgs.Empty);
        }
        void BuildModeOptions(TableLayoutPanel root)
        {
            modeOptions.AccessibleName = "内网允许的方式"; modeOptions.AccessibleRole = AccessibleRole.Grouping;
            startupDelay.AccessibleName = "PowerShell 启动等待秒数";
            var row = new SegmentPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty, Padding = new Padding(4) };
            foreach (var item in new[] { subPowerShell, subKeyboard }) {
                item.Margin = Padding.Empty; item.Padding = new Padding(12, 7, 12, 7);
                item.AccessibleName = item.Text; row.Controls.Add(item);
                item.CheckedChanged += delegate { if (item.Checked) SwitchRoute(); };
            }
            modeOptions.Controls.Add(row); FitDockedContent(modeOptions, row);
            modeOptions.Margin = new Padding(0, 0, 0, 8); AddRow(root, modeOptions, false);
        }
        void BuildRouteCard(TableLayoutPanel card)
        {
            card.ColumnCount = 2; card.Padding = new Padding(14); card.ColumnStyles.Clear();
            card.RowCount = 2; card.RowStyles.Add(new RowStyle(SizeType.AutoSize)); card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            routeHeading.AutoSize = routeHint.AutoSize = true;
            routeHeading.Dock = DockStyle.None; routeHeading.Anchor = AnchorStyles.Left;
            routeHint.Dock = DockStyle.Fill;
            routeHeading.Font = new Font(Font, FontStyle.Bold);
            routeHeading.Margin = new Padding(0, 0, 0, 6);
            routeHint.Margin = new Padding(0, 6, 0, 0); routeHint.ForeColor = Theme.Muted;
            routeHint.UseMnemonic = false;
            FitButton(startSetup); startSetup.Click += delegate { OpenSetup(); };
            startSetup.Margin = new Padding(12, 0, 0, 0);
            card.Controls.Add(routeHeading, 0, 0); card.Controls.Add(startSetup, 1, 0);
            card.Controls.Add(routeHint, 0, 1); card.SetColumnSpan(routeHint, 2);
        }
        void SwitchRoute()
        {
            ApplyBleLayout(UpdateRoute);
        }
        void UpdateRoute()
        {
            int next = ActiveRoute;
            if (next != previousRoute) {
                routeDrafts[previousRoute] = editor.Text;
                previousRoute = next; pendingText = null; sequence = GetClipboardSequenceNumber();
                autoClipboard.Checked = false;
                ResetKeyboardProbe();
                editor.Text = routeDrafts[next] ?? ""; editorResult.Text = "";
                setupStep = 1;
                if (setupWindow != null) setupWindow.Hide();
            }
            UpdateEditorActions();
        }
        void UpdateRoutePresentation()
        {
            bool keyboard = KeyboardRoute;
            modeOptions.Visible = modeNoProgram.Checked;
            modeOptions.Enabled = modeProgram.Enabled = modeNoProgram.Enabled = !typeBusy && !sending;
            setupCard.Visible = modeNoProgram.Checked && !keyboard;
            routeHeading.Text = keyboard ? "USB 键盘输入" : "PowerShell · 剪贴板同步";
            string hint = keyboard ? "外网 → 内网。将光标放在内网文本框，直接输入英文、数字和符号；不读取剪贴板。" :
                "支持中文、多行和 emoji。默认双向，在内网 PowerShell 接收窗口按 B 切换内网回传。";
            routeHint.Text = hint;
            startSetup.Text = "设置剪贴板同步…";
            startSetup.Visible = !keyboard; startSetup.Enabled = !typeBusy && !sending;
            var card = (TableLayoutPanel)routeHeading.Parent;
            if (card.GetColumnSpan(routeHeading) != (keyboard ? 2 : 1)) card.SetColumnSpan(routeHeading, keyboard ? 2 : 1);
            editorTitle.Text = keyboard ? "要输入的文字" : "发送文字 · 内网复制后可回传";
            editorTitle.AutoSize = true; editorTitle.Dock = DockStyle.Fill;
            bool body = KeyboardBodyVisible;
            editorTitle.Visible = editorSize.Visible = editor.Parent.Visible = loadClipboard.Visible = body;
            if (editorRowStyle != null && editorRowStyle.Height != (body ? editorPanelHeight : 0)) editorRowStyle.Height = body ? editorPanelHeight : 0;
            directKeys.Visible = keyboard && body; sendEditor.Visible = !keyboard;
            counts.Visible = !keyboard;
            autoClipboard.Visible = !keyboard; autoClipboard.Enabled = !typeBusy;
            UpdateRouteCounts();
            if (fallbackItem != null) fallbackItem.Visible = modeNoProgram.Checked && !keyboard;
            if (pasteItem != null) pasteItem.Visible = modeNoProgram.Checked && !keyboard;
        }
        void UpdateRouteCounts()
        {
            counts.Text = KeyboardRoute ? "仅直接输入按键 · 不读写剪贴板" : "已发送 " + sentCount + " 次 · 已接收 " + receiveCount + " 次";
        }
    }
#endif
}
