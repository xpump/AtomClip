using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace AtomClipUsb
{
    // Shared visual language; native WinForms behavior and keyboard navigation are retained.
    static class Theme
    {
        public static readonly Color Background = Color.FromArgb(245, 245, 247);
        public static readonly Color Ink = Color.FromArgb(29, 29, 31);
        public static readonly Color Muted = Color.FromArgb(110, 110, 115);
        public static readonly Color Blue = Color.FromArgb(0, 113, 227);
        public static readonly Color Line = Color.FromArgb(224, 224, 229);
        public static readonly Color ControlFill = Color.FromArgb(233, 233, 238);
        public static readonly Icon Brand = LoadIcon();
        static Icon LoadIcon()
        {
#if USB
            // Reuse the native EXE icon instead of embedding a second ICO resource.
            return Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
#else
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AtomClip.Brand"))
            using (var icon = new Icon(stream, 32, 32)) return (Icon)icon.Clone();
#endif
        }
        public static GraphicsPath Round(RectangleF rect, float radius)
        {
            var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            path.AddArc(rect.X, rect.Y, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
        public static PictureBox Mark(int size)
        {
            return new PictureBox { Image = LoadMark(), SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(size, size), Margin = new Padding(0, 0, 12, 0), TabStop = false };
        }
        static Bitmap LoadMark()
        {
#if USB
            return Brand.ToBitmap();
#else
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AtomClip.Mark"))
            using (var image = new Bitmap(stream)) return new Bitmap(image);
#endif
        }
        public static void Primary(Button button) { button.Tag = "primary"; button.BackColor = Blue; button.ForeColor = Color.White; }
        public static void Apply(Form form)
        {
            form.Icon = Brand; form.BackColor = Background; form.ForeColor = Ink;
            Style(form);
        }
        static void Style(Control control)
        {
            var button = control as Button;
            if (button != null && !(button is HelpButton)) {
                button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0;
                button.UseVisualStyleBackColor = false;
                button.BackColor = Equals(button.Tag, "primary") ? Blue : ControlFill;
                button.ForeColor = Equals(button.Tag, "primary") ? Color.White : Ink;
                button.AutoSize = true; button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                button.Padding = new Padding(16, 7, 16, 7); button.Cursor = Cursors.Hand;
            }
            var text = control as TextBox;
            if (text != null) {
                text.BorderStyle = BorderStyle.None; text.BackColor = Color.White;
                text.ForeColor = text.ReadOnly ? Muted : Ink;
            }
            var link = control as LinkLabel;
            if (link != null) { link.LinkColor = Blue; link.ActiveLinkColor = Blue; link.VisitedLinkColor = Blue; link.LinkBehavior = LinkBehavior.HoverUnderline; }
            var combo = control as ComboBox;
            if (combo != null) { combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Color.White; combo.ForeColor = Ink; }
            foreach (Control child in control.Controls) Style(child);
        }
        public static void Help(Form owner, string title, string content)
        {
            using (var window = new Form { Text = title, Font = owner.Font, StartPosition = FormStartPosition.CenterParent,
                Size = new Size(660, 560), MinimumSize = new Size(400, 300) }) {
                var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Padding = new Padding(28) };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var heading = new Label { Text = title, AutoSize = true, Dock = DockStyle.Fill, Font = new Font(owner.Font.FontFamily, 18, FontStyle.Bold), Margin = new Padding(0, 0, 0, 20) };
                table.Controls.Add(heading, 0, 0);
                foreach (string block in content.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)) {
                    int split = block.IndexOf('\n');
                    bool section = split > 0 && split < 48;
                    if (section) table.Controls.Add(new Label { Text = block.Substring(0, split), AutoSize = true, Dock = DockStyle.Fill,
                        UseMnemonic = false, Font = new Font(owner.Font.FontFamily, owner.Font.Size + 3, FontStyle.Bold), ForeColor = Ink, Margin = new Padding(0, 16, 0, 8) });
                    string text = section ? block.Substring(split + 1) : block;
                    bool note = text.StartsWith("提示：") || text.StartsWith("注意：");
                    table.Controls.Add(new Label { Text = text.Replace("\n", "\r\n"), AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false,
                        ForeColor = note ? Muted : Ink, Margin = new Padding(0, 0, 0, 12) });
                }
                scroll.Controls.Add(table); window.Controls.Add(scroll);
                Apply(window); window.ShowDialog(owner);
            }
        }
        public static void Menu(ContextMenuStrip menu)
        {
            menu.Font = new Font("Microsoft YaHei UI", 10); menu.ShowImageMargin = false;
            menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
            menu.Padding = new Padding(6); menu.ForeColor = Ink;
        }
        sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Color.White; } }
            public override Color MenuItemSelected { get { return Color.FromArgb(230, 240, 254); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(230, 240, 254); } }
            public override Color MenuBorder { get { return Line; } }
        }
    }

    // A square outline and a question mark, with a larger keyboard/touch target.
    sealed class HelpButton : Button
    {
        public HelpButton(Action action)
        {
            Text = "?"; AccessibleName = "使用帮助"; Cursor = Cursors.Hand; AutoSize = true;
            Margin = new Padding(6, 0, 0, 0); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Click += delegate { action(); };
        }
        public override Size GetPreferredSize(Size proposedSize) { int side = Math.Max(38, Font.Height + 18); return new Size(side, side); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Theme.Background : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new Rectangle(7, 7, Width - 15, Height - 15);
            using (var path = Theme.Round(box, 3)) using (var pen = new Pen(Theme.Muted, 1.4f)) e.Graphics.DrawPath(pen, path);
            TextRenderer.DrawText(e.Graphics, "?", Font, box, Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
        }
    }

    static class Guide
    {
        public const string Common = "\r\n\r\n文本与隐私\r\n支持中文、换行和 emoji，仅传输纯文本。单条最多 16384 UTF-8 字节，超出时拒绝，不截断。不保存剪贴板历史；日志不记录正文或配对信息。\r\n\r\n连接与退出\r\n离线、暂停期间和连接前的旧剪贴板不会补发，恢复后请重新复制。关闭窗口后仍在托盘运行；右击托盘选择“退出”可完全关闭。日志自动保存最近 7 天。";
#if !USB
        public const string Ble = "选择使用方式\r\n有内网程序：内网运行 AtomClipUSB.exe，连接开发板后双向同步剪贴板。\r\n无内网程序：选择 PowerShell 或仅 USB 键盘。各模式保留自己的草稿，切换后自动发送关闭。\r\n\r\nPowerShell · 默认双向\r\n需允许系统 PowerShell、Add-Type 和 USB 串口。点击“设置剪贴板同步”，按四步向导操作一次。\r\n初始化后默认双向；内网 PowerShell 接收窗口按 B 关闭或恢复内网回传，P 暂停，Q 退出。窗口可最小化，须保持运行。\r\n\r\nUSB 键盘 · 选择输入方式\r\n不运行内网程序或 PowerShell。GBK 与 VUC 都支持中英文混排；仅键盘不能读取内网剪贴板，不支持 emoji。\r\n\r\n1 · GBK 高速\r\n切到 ENG 美式，开启 Num Lock，关闭大写锁定。目标程序出现乱码时改用 VUC。\r\n\r\n2 · VUC 微软拼音\r\n切到微软拼音全拼“中”、半角，保留默认 Shift 中英切换，关闭大写锁定。\r\n\r\n3 · 试输可以跳过\r\n默认直接显示正文，可直接发送。需要验证时点击“试输中英文”；勾选“发送前先试输”则须核对“病ABC123，中文!”并勾选结果正确，才能发送正文。\r\n切换方式、速度或断线会清除试输确认，草稿保留；未勾选试输检查时仍可直接发送。漏字时开启“兼容速度”。\r\n\r\n提示：跳过试输不会补齐旧固件缺失的能力。VUC 可用支持该任务的旧固件；GBK 需具备 GBK 能力的配套固件。此前已刷开发版 0.5.4 / 0.5.5 的用户无需重烧，原内网 USB EXE 仍兼容。\r\n\r\nPowerShell · 启动等待\r\n默认 10 秒，慢电脑可设 20–30 秒。快速输入只加速启动后的数据，保留系统启动余量。漏字时关闭快速输入。完成以开发板回执为准。" + Common;
#endif
        public const string Usb = "连接开发板\r\n将已烧录的 AtomS3U 插入内网电脑，刷新并选择对应 USB 串行端口，点击“连接”。COM 是 Windows 为数据通信端口分配的编号；设备管理器中随插拔出现或消失的 USB 串行端口即为目标。\r\n\r\n同步文字\r\n两端就绪后，重新复制纯文本即可自动发送到另一端剪贴板，在目标窗口按 Ctrl+V 粘贴。\r\n\r\n暂停与日志\r\n暂停仅影响新的传输；日志文件自动保存，可点击“打开日志”查看。" + Common;
#if !USB
        public const string Receiver = "1 · 准备启动内容\r\n内网打开 Win+R，切换系统 ENG 英语键盘，关闭大写锁定，清空运行框。设置启动等待后点击写入；完成以开发板回执为准。\r\n\r\n提示：启动等待默认 10 秒，慢电脑可设 20–30 秒；漏字时关闭“快速输入”。完整准备还包含按键传输时间。\r\n\r\n2 · 打开接收窗口\r\n内网再次打开并清空 Win+R，确认 ENG 后点击“打开内网接收窗口”。开发板输入固定的系统启动命令。\r\n\r\n3 · 粘贴并执行\r\n在内网 PowerShell 按 Ctrl+V、Enter，等待端口选择窗口。\r\n\r\n注意：启动内容含配对信息，不要截图或分享。\r\n\r\n4 · 连接开发板\r\n选择开发板对应 USB COM 端口并连接。默认双向，重新复制内网文字即可回传。\r\n\r\n内网快捷键\r\nB：关闭或恢复内网回传；外网发送不受此开关影响。\r\nP：暂停或继续传输。Q：退出接收器。\r\n窗口可最小化，须保持运行。\r\n\r\n系统要求\r\n需允许 PowerShell、动态编译和 USB 数据通信。系统编译组件可能创建临时文件。仅允许 USB 键盘时选择专用键盘界面。";
        public const string Firmware = "烧录说明\r\n烧录是把配套程序写入开发板，写入后断电仍保留。会覆盖现有程序和数据。仅适用于 AtomS3U（ESP32-S3 / 8 MB），不适用于带屏幕的 AtomS3。\r\n\r\n操作步骤\r\n1. 将 AtomS3U 插入当前 Windows 电脑，关闭其他串口软件。\r\n2. 长按 RESET 约 2 秒，绿灯亮后松开，进入下载模式。\r\n3. 刷新设备，选择 USB 端口，点击“识别并烧录”。\r\n4. 等待提示烧录与校验完成，再拔下并移至内网电脑。\r\n\r\n固件和离线工具已内置，不需要另行下载安装或输入命令。软件先识别芯片和容量，再写入并校验。烧录过程中保持 USB 连接。\r\n\r\n本次固件与开发版 0.5.4 / 0.5.5 相同，已烧录者无需重烧。Windows 和 Android 共用；原内网 USB EXE 仍兼容。";
#endif
    }

    sealed class SoftButton : Button
    {
        bool hover, pressed;
        public SoftButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
            return new Size(Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal + 6), Math.Max(MinimumSize.Height, text.Height + Padding.Vertical + 4));
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? Theme.Background : Parent.BackColor);
            bool primary = Equals(Tag, "primary");
            Color fill = !Enabled ? Theme.ControlFill : pressed ? ControlPaint.Dark(BackColor, .08f) : hover ? ControlPaint.Light(BackColor, .12f) : BackColor;
            using (var path = Theme.Round(new RectangleF(1, 1, Width - 2, Height - 2), 9))
            using (var brush = new SolidBrush(fill)) { e.Graphics.FillPath(brush, path); if (Focused && ShowFocusCues) using (var pen = new Pen(Theme.Blue, 2)) e.Graphics.DrawPath(pen, path); }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, !Enabled ? Theme.Muted : primary ? Color.White : ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

#if !USB
    sealed class SegmentButton : RadioButton
    {
        public SegmentButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.Clear(Theme.ControlFill);
            if (Checked) using (var path = Theme.Round(new RectangleF(1, 1, Width - 2, Height - 3), 8)) {
                using (var brush = new SolidBrush(Color.White)) e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(Theme.Line)) e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? Theme.Ink : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
        }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    sealed class SegmentPanel : FlowLayoutPanel
    {
        public SegmentPanel() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Theme.Background : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 11))
            using (var brush = new SolidBrush(Theme.ControlFill)) e.Graphics.FillPath(brush, path);
        }
    }

#endif
    sealed class CardPanel : TableLayoutPanel
    {
        public CardPanel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); BackColor = Color.White; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Theme.Background : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 14)) {
                using (var brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(Theme.Line)) e.Graphics.DrawPath(pen, path);
            }
        }
    }
#if BLE
    // Include native EDIT/scrollbar children when mode or result text reflows.
    // Local to the main content; no custom painting or redraw suppression on EDIT.
    sealed class BufferedViewport : Panel
    {
        public BufferedViewport() { DoubleBuffered = true; }
        protected override CreateParams CreateParams {
            get { var value = base.CreateParams; value.ExStyle |= 0x02000000; return value; }
        }
    }
    sealed class BufferedLayoutPanel : TableLayoutPanel
    {
        public BufferedLayoutPanel() { DoubleBuffered = true; }
    }
#endif
}
