using System;
using System.Drawing;
using System.Windows.Forms;

namespace AtomClipUsb
{
#if BLE
    sealed partial class MainForm
    {
        readonly ComboBox keyboardInput = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "USB 键盘输入方式" };
        readonly RadioButton keyboardGbk = new SegmentButton { Text = "GBK 高速 · 中英文", Appearance = Appearance.Button, AutoSize = true };
        readonly RadioButton keyboardVuc = new SegmentButton { Text = "VUC 微软拼音 · 中英文", Appearance = Appearance.Button, AutoSize = true };
        readonly RadioButton keyboardAscii = new SegmentButton { Text = "纯英文", Appearance = Appearance.Button, AutoSize = true };
        readonly CheckBox requireProbe = new CheckBox { Text = "发送前先试输（可选）", AutoSize = true };
        readonly TableLayoutPanel keyboardOptions = new CardPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16), Margin = new Padding(0, 0, 0, 16), AccessibleName = "USB 键盘输入设置", AccessibleRole = AccessibleRole.Grouping };
        readonly CheckBox pinyinConfirmed = new CheckBox { Text = "试输结果正确：病ABC123，中文!", AutoSize = true };
        readonly CheckBox pinyinCompatible = new CheckBox { Text = "兼容速度", AutoSize = true };
        readonly Button probePinyin = new SoftButton { Text = "重新试输…" };
        readonly LinkLabel switchChinese = new LinkLabel { Text = "切换到中英文输入", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        Label pinyinGuide, keyboardReady;
        Panel pinyinActionHost;
        bool supportsPinyin, supportsGbk, pinyinProbeInFlight, keyboardProbeSent;
        bool PinyinRoute { get { return KeyboardRoute && keyboardInput.SelectedIndex == 1; } }
        bool GbkRoute { get { return KeyboardRoute && keyboardInput.SelectedIndex == 2; } }
        bool ChineseKeyboardRoute { get { return PinyinRoute || GbkRoute; } }
        bool KeyboardBodyVisible { get { return !ChineseKeyboardRoute || !requireProbe.Checked || (keyboardProbeSent && pinyinConfirmed.Checked); } }
        bool SelectedKeyboardSupported { get { return GbkRoute ? supportsGbk : PinyinRoute ? supportsPinyin : supportsDirectKeys; } }
        void BuildKeyboardOptions(TableLayoutPanel root)
        {
            keyboardOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var heading = WrappedLabel("输入方式"); heading.Font = new Font(Font, FontStyle.Bold); heading.Margin = new Padding(0, 0, 0, 8); AddRow(keyboardOptions, heading, false);
            keyboardInput.Items.AddRange(new object[] { "纯英文 · 直接键盘", "中英文 · VUC 微软拼音", "中英文 · GBK 高速" });
            keyboardInput.SelectedIndex = 2; keyboardInput.Margin = new Padding(0, 0, 0, 8);
            // Keep the index adapter off the visual tree; all choices are visible native radio buttons.
            var methods = new SegmentPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Padding = new Padding(4), Margin = Padding.Empty };
            var buttons = new[] { keyboardAscii, keyboardVuc, keyboardGbk };
            foreach (int index in new[] { 2, 1, 0 }) {
                int selected = index; var item = buttons[index]; item.Padding = new Padding(12, 10, 12, 10); item.Margin = Padding.Empty;
                item.AccessibleName = item.Text; methods.Controls.Add(item);
                item.CheckedChanged += delegate { if (item.Checked) keyboardInput.SelectedIndex = selected; };
            }
            keyboardGbk.Checked = true;
            keyboardInput.SelectedIndexChanged += delegate { buttons[keyboardInput.SelectedIndex].Checked = true; ResetKeyboardProbe(); editorResult.Text = ""; UpdateEditorActions(); };
            var methodHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), AccessibleName = "选择输入方式", AccessibleRole = AccessibleRole.Grouping };
            methodHost.Controls.Add(methods); FitDockedContent(methodHost, methods); AddRow(keyboardOptions, methodHost, false);
            pinyinGuide = WrappedLabel("");
            pinyinGuide.ForeColor = Theme.Muted; AddRow(keyboardOptions, pinyinGuide, false);
            requireProbe.Margin = new Padding(0, 6, 0, 6); AddRow(keyboardOptions, requireProbe, false);
            requireProbe.CheckedChanged += delegate { ResetKeyboardProbe(); UpdateEditorActions(); };
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
            FitButton(probePinyin); actions.Controls.Add(probePinyin); actions.Controls.Add(pinyinCompatible);
            pinyinCompatible.Margin = new Padding(12, 8, 0, 6);
            pinyinCompatible.CheckedChanged += delegate { ResetKeyboardProbe(); UpdateEditorActions(); };
            pinyinActionHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            pinyinActionHost.Controls.Add(actions); FitDockedContent(pinyinActionHost, actions); AddRow(keyboardOptions, pinyinActionHost, false);
            pinyinConfirmed.Margin = new Padding(0, 4, 0, 8); AddRow(keyboardOptions, pinyinConfirmed, false);
            pinyinConfirmed.CheckedChanged += delegate { UpdateEditorActions(); };
            probePinyin.Click += async delegate { await ProbeKeyboard(); };
            keyboardReady = WrappedLabel(""); keyboardReady.ForeColor = Theme.Muted; keyboardReady.Margin = new Padding(0, 4, 0, 0); AddRow(keyboardOptions, keyboardReady, false);
            switchChinese.LinkClicked += delegate { keyboardInput.SelectedIndex = 2; }; AddRow(keyboardOptions, switchChinese, false);
            AddRow(root, keyboardOptions, false);
        }
        void ResetKeyboardProbe() { keyboardProbeSent = false; pinyinConfirmed.Checked = false; }
        async System.Threading.Tasks.Task ProbeKeyboard()
        {
            if (typeBusy || sending || !probePinyin.Enabled) return;
            ResetKeyboardProbe(); pinyinProbeInFlight = true;
            try { keyboardProbeSent = await SendTypeJob(Protocol.KeyboardProbeText, false, false, true) == 2; }
            finally { pinyinProbeInFlight = false; UpdateEditorActions(); }
        }
        void UpdateKeyboardOptions(bool connected)
        {
            keyboardOptions.Visible = KeyboardRoute;
            if (!KeyboardRoute) return;
            pinyinGuide.Text = GbkRoute ? "切到 ENG 美式，开启 Num Lock，关闭大写锁定。可先试输；乱码请换 VUC。" : PinyinRoute ? "切到微软拼音全拼“中”、半角，保留 Shift 中英切换，关闭大写锁定。" : "切到 ENG 美式，关闭大写锁定。此方式只输入英文、数字和符号。";
            pinyinGuide.Visible = true; pinyinActionHost.Visible = ChineseKeyboardRoute;
            requireProbe.Visible = ChineseKeyboardRoute; requireProbe.Enabled = !typeBusy && !sending;
            pinyinConfirmed.Visible = ChineseKeyboardRoute && requireProbe.Checked && keyboardProbeSent;
            probePinyin.Visible = true; probePinyin.Text = keyboardProbeSent ? "重新试输…" : "试输中英文…";
            probePinyin.Tag = KeyboardBodyVisible ? null : "primary"; probePinyin.BackColor = KeyboardBodyVisible ? Theme.ControlFill : Theme.Blue; probePinyin.ForeColor = KeyboardBodyVisible ? Theme.Ink : Color.White;
            keyboardInput.Enabled = !typeBusy && !sending;
            keyboardGbk.Enabled = keyboardVuc.Enabled = keyboardAscii.Enabled = keyboardInput.Enabled;
            pinyinCompatible.Enabled = !typeBusy && !sending;
            pinyinConfirmed.Enabled = keyboardProbeSent && !typeBusy && !sending;
            probePinyin.Enabled = connected && SelectedKeyboardSupported && !peerReady && !paused && !typeBusy && !sending;
            UpdateKeyboardReadiness(connected, EditorTextValid());
        }
        void UpdateKeyboardReadiness(bool connected, bool valid)
        {
            if (keyboardReady == null || !KeyboardRoute) return;
            keyboardReady.Text = !connected ? "连接开发板后可发送或试输。" : peerReady ? "请先退出内网接收器，再使用键盘输入。" : !SelectedKeyboardSupported ? "此方式需要更新固件，请使用顶部“开发板设置”。" : typeBusy || sending ? "按键发送中，请勿操作内网键鼠。" : paused ? "传输已暂停，请在“更多”中恢复。" : ChineseKeyboardRoute && requireProbe.Checked && !keyboardProbeSent ? "先试输“病ABC123，中文!”；确认后可发送正文。" : ChineseKeyboardRoute && requireProbe.Checked && !pinyinConfirmed.Checked ? "请核对内网试输文字，再勾选“试输结果正确”。" : editor.Text.Length == 0 ? "可直接输入正文，也可先试输；开始前有 3 秒准备时间。" : !valid ? "正文含不支持字符，请查看输入框下方提示。" : "已准备好，可直接发送；开始前有 3 秒准备时间。";
            switchChinese.Visible = KeyboardRoute && !ChineseKeyboardRoute && editor.Text.Length > 0 && !valid;
        }
    }
#endif
}
