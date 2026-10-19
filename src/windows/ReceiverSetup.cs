using System;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;
namespace AtomClipUsb {
#if BLE
    sealed partial class MainForm {
        void OpenSetup()
        {
            if (KeyboardRoute) { editor.Focus(); return; }
            if (setupWindow == null) {
                setupWindow = new Form { Text = "设置内网接收", Font = Font, Size = new Size(780, 760), MinimumSize = new Size(520, 380), StartPosition = FormStartPosition.CenterParent, AutoScroll = true };
                var root = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Padding = new Padding(28) };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var title = setupModeHeading = WrappedLabel("内网接收设置"); title.Font = new Font(Font.FontFamily, 18, FontStyle.Bold); var header = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
                header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                header.Controls.Add(title, 0, 0); header.Controls.Add(new HelpButton(delegate { Theme.Help(setupWindow, "接收设置帮助", Guide.Receiver); }), 1, 0); AddRow(root, header, false);
                for (int i = 0; i < 4; i++) {
                    var page = new CardPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20), Margin = new Padding(0, 12, 0, 16), Visible = i == 0 };
                    page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); setupPages.Add(page); AddRow(root, page, false);
                }
                setupFirst = setupPages[0]; setupSecond = setupPages[1];
                var first = (TableLayoutPanel)setupPages[0];
                AddRow(first, WrappedLabel("第 1 步 / 共 4 步 · 准备启动内容"), false);
                setupDirection = WrappedLabel(""); setupDirection.ForeColor = Theme.Muted; AddRow(first, setupDirection, false);
                AddRow(first, WrappedLabel("内网：Win+R → 系统 ENG → 关闭大写锁定 → 清空运行框。"), false);
                prepareReceiver.Text = "写入启动内容"; FitButton(prepareReceiver); AddRow(first, prepareReceiver, false);
                AddRow(first, WrappedLabel("启动等待（秒）：默认 10；慢电脑可调至 20–30。配套固件支持可调启动等待。"), false);
                AddRow(first, startupDelay, false);
                AddRow(first, fastInput, false);
                typingPaceNote = WrappedLabel(""); typingPaceNote.ForeColor = Theme.Muted; AddRow(first, typingPaceNote, false);
                AddRow(first, WrappedLabel("写入时显示已用时间和预计范围；以开发板完成回执为准，期间请勿操作内网键鼠。"), false);
                var resume = new LinkLabel { Text = "已完成写入？继续第 2 步", AutoSize = true, Margin = new Padding(0, 8, 0, 12) };
                resume.LinkClicked += delegate { SetSetupStep(2); }; AddRow(first, resume, false);
                var second = (TableLayoutPanel)setupPages[1];
                AddRow(second, WrappedLabel("第 2 步 / 共 4 步 · 打开接收窗口"), false);
                AddRow(second, WrappedLabel("内网：再次打开 Win+R → 系统 ENG → 清空运行框。"), false);
                FitButton(openReceiver); AddRow(second, openReceiver, false);
                var back = new LinkLabel { Text = "返回第 1 步，重新写入", AutoSize = true, Margin = new Padding(0, 8, 0, 12) };
                back.LinkClicked += delegate { SetSetupStep(1); }; AddRow(second, back, false);
                var third = (TableLayoutPanel)setupPages[2];
                AddRow(third, WrappedLabel("第 3 步 / 共 4 步 · 粘贴一次"), false);
                AddRow(third, WrappedLabel("内网 PowerShell：Ctrl+V → Enter → 等待端口选择窗口。\r\n启动内容含配对信息，请勿截图或分享。"), false);
                var pasted = new SoftButton { Text = "已出现选择开发板窗口，下一步" }; FitButton(pasted);
                pasted.Click += delegate { SetSetupStep(4); }; AddRow(third, pasted, false);
                var retry = new LinkLabel { Text = "重新打开接收窗口", AutoSize = true, Margin = new Padding(0, 8, 0, 12) };
                retry.LinkClicked += delegate { SetSetupStep(2); }; AddRow(third, retry, false);
                var fourth = (TableLayoutPanel)setupPages[3];
                var retryPort = new LinkLabel { Text = "重新打开接收窗口", AutoSize = true, Margin = new Padding(0, 8, 0, 12) };
                retryPort.LinkClicked += delegate { SetSetupStep(2); }; AddRow(fourth, retryPort, false);
                AddRow(fourth, WrappedLabel("第 4 步 / 共 4 步 · 选择开发板"), false);
                setupFinal = WrappedLabel(""); AddRow(fourth, setupFinal, false);
                var check = new SoftButton { Text = "完成设置" }; FitButton(check);
                check.Click += delegate { if (peerReady) setupWindow.Hide(); else setupProgress.Text = "尚未收到开发板的内网连接状态。请核对 USB 端口和接收窗口。"; }; AddRow(fourth, check, false);
                var help = new LinkLabel { Text = "端口识别说明", AutoSize = true, Margin = new Padding(0, 8, 0, 12) };
                help.LinkClicked += delegate { MessageBox.Show(setupWindow, "COM 是 Windows 给 USB 数据通信端口取的编号。\r\n在内网右击开始 → 设备管理器 → 端口。拔下板子观察消失项，再插回确认。不要选蓝牙标准串行端口。\r\n重新插拔开发板后，新增的 USB 串行设备即为目标端口。", "选择开发板"); }; AddRow(fourth, help, false);
                setupProgress = WrappedLabel("准备好后再点击当前步骤的按钮。"); AddRow(root, setupProgress, false);
                var note = WrappedLabel("内网不用 EXE，也不用复制文件。需允许 PowerShell 与动态编译。若仅允许 USB 键盘，请返回主界面选择USB 键盘输入（GBK / VUC 中英文混排）。"); note.ForeColor = Theme.Muted;
                AddRow(root, note, false);
                setupWindow.Controls.Add(root);
                Theme.Primary(prepareReceiver); Theme.Primary(openReceiver); Theme.Primary(pasted); Theme.Primary(check); Theme.Apply(setupWindow);
                setupWindow.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; setupWindow.Hide(); } };
            }
            setupModeHeading.Text = "PowerShell 设置";
            setupWindow.Text = setupModeHeading.Text;
            setupDirection.Text = "默认双向；内网接收窗口按 B 可关闭或恢复内网回传。";
            setupFinal.Text = "内网：选择开发板的 USB 串行端口 → 连接。\r\n" +
                "默认双向，请重新复制内网文字测试回传。按 B 切换内网回传。" +
                "\r\nP 暂停，Q 退出；接收窗口可最小化，须保持运行。";
            typingPaceNote.Text = supportsFastTyping ? "快速模式只加速启动后的数据输入；启动命令和等待余量保留。" : "当前固件使用兼容节奏；更新本版配套固件后可快速输入。";
            for (int i = 0; i < setupPages.Count; i++) setupPages[i].Visible = i == setupStep - 1;
            if (!setupWindow.Visible) setupWindow.Show(this);
            if (setupWindow.WindowState == FormWindowState.Minimized) setupWindow.WindowState = FormWindowState.Normal;
            setupWindow.Activate(); UpdateEditorActions();
        }
        void SetSetupStep(int step)
        {
            if (typeBusy) return;
            OpenSetup();
            setupStep = step;
            for (int i = 0; i < setupPages.Count; i++) setupPages[i].Visible = i == step - 1;
            setupProgress.Text = step == 2 && !supportsReceiverStart ? "当前固件不支持此步骤。请通过“新开发板 / 固件更新”升级固件。" :
                step == 3 ? "在内网 PowerShell 中粘贴并执行启动内容。" : "准备好后再点击当前步骤的按钮。";
        }
    }
#endif
}
