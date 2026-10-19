using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AtomClipUsb
{
#if BLE
    sealed partial class MainForm
    {
        readonly DeviceBindings deviceBindings;
        readonly BoardCatalog boardCatalog;
        readonly Button myBoards = new SoftButton { Text = "我的开发板" };
        readonly Label currentBoard = new Label { AutoSize = true, ForeColor = Theme.Muted, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        void UpdateBoardSummary()
        {
            var active = deviceBindings.Registry.Active;
            currentBoard.Text = deviceBindings.LoadError != null ? deviceBindings.LoadError : active != null ? "当前：" + active.Alias + " · " + boardCatalog.Name(active.DeviceInfo.Profile) + " · #" + active.DeviceInfo.ShortId + "（已绑定）" : "尚未选择开发板；请从“我的开发板”通过 USB 添加或导入配置。";
        }
        void ActivateBoardKey()
        {
            var active = deviceBindings.Registry.Active; Buffer.BlockCopy(active == null ? legacyKey : active.DataKey, 0, key, 0, 32);
            pendingText = null; autoClipboard.Checked = false; seen.Clear(); seenOrder.Clear(); discarded.Clear(); discardedOrder.Clear(); sequence = GetClipboardSequenceNumber(); UpdateBoardSummary();
        }
        static Button BoardAction(FlowLayoutPanel parent, string text, Action action)
        {
            var b = new SoftButton { Text = text, AutoSize = true, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 0, 8, 8) }; b.Click += delegate { action(); }; parent.Controls.Add(b); return b;
        }
        string BoardPrompt(IWin32Window owner, string title, string initial, bool secret)
        {
            using (var f = new Form { Text = title, Font = Font, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(480, secret ? 180 : 150), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, AutoSize = true };
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var hint = WrappedLabel(secret ? "配置口令需为12–128字符；导出与导入必须使用相同口令。" : "开发板别名，最多160个UTF-8字节。"); var value = new TextBox { Dock = DockStyle.Top, Text = initial ?? "", UseSystemPasswordChar = secret, MaxLength = secret ? 128 : 160 };
                var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top }; var ok = new SoftButton { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true }; var cancel = new SoftButton { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true }; row.Controls.Add(ok); row.Controls.Add(cancel); body.Controls.Add(hint); body.Controls.Add(value); body.Controls.Add(row); f.Controls.Add(body); f.AcceptButton = ok; f.CancelButton = cancel; Theme.Apply(f); return f.ShowDialog(owner) == DialogResult.OK ? value.Text : null;
            }
        }
        void OpenBoards()
        {
            if (sending || typeBusy) { MessageBox.Show(this, "请等待当前传输完成，再管理开发板。", "任务进行中"); return; }
            using (var w = new Form { Text = "我的开发板", Font = Font, StartPosition = FormStartPosition.CenterParent, Size = new Size(900, 600), MinimumSize = new Size(680, 480) })
            using (var cancel = new CancellationTokenSource())
            using (var moreMenu = new ContextMenuStrip())
            {
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                for (int i=0;i<3;i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(WrappedLabel("选择已绑定的板即可连接。扫描附近会按完整编号合并显示；新板请通过 USB 添加。"));
                var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
                list.Columns.Add("开发板",190); list.Columns.Add("状态",200); list.Columns.Add("信号",85); list.Columns.Add("型号",260); root.Controls.Add(list);
                var detail = WrappedLabel("选中开发板后显示完整编号。"); detail.AutoSize=false;detail.Height=56;root.Controls.Add(detail);
                var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true }; root.Controls.Add(actions);
                var status = WrappedLabel(deviceBindings.LoadError ?? "通过 USB 添加一次，以后可直接连接。"); root.Controls.Add(status); w.Controls.Add(root);
                var nearby = new List<BoardAdvertisement>();
                Func<BoardPresence.Row> selectedRow = () => list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as BoardPresence.Row : null;
                Func<V2Protocol.Binding> selectedBinding = () => selectedRow()==null ? null : selectedRow().Binding;
                Button connectBoard=null;
                Action selectionChanged = () => {
                    var r=selectedRow(); var b=r==null?null:r.Binding;
                    if(connectBoard!=null)connectBoard.Enabled=b!=null&&!b.Pending&&!r.Conflict;
                    detail.Text=r==null?"选中开发板后显示完整编号。":r.Number.Length>0?"完整编号："+r.Number+(r.Conflict?"\r\n多个蓝牙地址返回相同编号，请通过 USB 核对。":""):
                        "尚未读取完整编号 · 广播短号 #"+r.Nearby.ShortId+"\r\n请靠近后重扫，或通过 USB 添加。";
                };
                Action populate = () => {
                    if(w.IsDisposed)return;var previous=selectedRow();string selected=previous==null?null:previous.Binding!=null?"B"+previous.Number:"A"+previous.Nearby.Address;
                    var records=new Dictionary<string,V2Protocol.Binding>(deviceBindings.Registry.Confirmed);foreach(var pair in deviceBindings.Registry.Pending)records[pair.Key]=pair.Value;
                    var connected=link as BleLink;list.BeginUpdate();list.Items.Clear();
                    foreach(var r in BoardPresence.Merge(records.Values,nearby)){
                        var b=r.Binding;var n=r.Nearby;bool live=b!=null&&connected!=null&&connected.Connected&&connected.AuthenticatedInfo!=null&&connected.AuthenticatedInfo.Number==r.Number;
                        string state=r.Conflict?"编号冲突 · 请核对":b!=null&&b.Pending?"待 USB 确认":live?"已连接 · 已认证":b!=null?(n!=null?"附近 · 连接时验证":"已绑定"):(n!=null&&n.Info!=null?"附近 · 未绑定":"附近 · 编号未读取");
                        if(b!=null&&!b.Pending&&deviceBindings.Registry.SelectedId==r.Number&&!live)state="当前 · "+state;
                        int profile=b!=null?b.DeviceInfo.Profile:n.Info!=null?n.Info.Profile:n.Profile;
                        var item=new ListViewItem(new[]{b!=null?b.Alias:"开发板 #"+(n.Info!=null?n.Info.ShortId:n.ShortId),state,n!=null?n.Rssi+" dBm":"—",boardCatalog.Name(profile)}){Tag=r,ToolTipText=(b!=null?b.Alias:"附近开发板")+"\r\n"+r.Number};list.Items.Add(item);
                        string identity=b!=null?"B"+r.Number:"A"+n.Address;if(identity==selected||(selected==null&&b!=null&&deviceBindings.Registry.SelectedId==r.Number))item.Selected=true;
                    }
                    list.EndUpdate();selectionChanged();
                };
                list.SelectedIndexChanged+=delegate{selectionChanged();};
                list.Resize+=delegate{int available=Math.Max(560,list.ClientSize.Width-12);list.BeginUpdate();list.Columns[3].Width=0;list.Columns[0].Width=available*24/100;list.Columns[1].Width=available*30/100;list.Columns[2].Width=85;list.Columns[3].Width=available-list.Columns[0].Width-list.Columns[1].Width-85;list.EndUpdate();};
                Action<Action> safely = action => { try { action(); populate(); UpdateBoardSummary(); } catch (Exception e) { if(!w.IsDisposed)MessageBox.Show(w,e.Message,"操作未完成"); } };
                bool connectAfter=false;
                connectBoard=BoardAction(actions,"连接",()=>safely(()=>{var r=selectedRow();if(r==null||r.Binding==null||r.Binding.Pending||r.Conflict)return;Stop();deviceBindings.Change(reg=>reg.Select(r.Number));ActivateBoardKey();connectAfter=true;w.Close();}));
                Theme.Primary(connectBoard);
                BoardAction(actions,"添加开发板",()=>{OpenUsbBoard(w,0,null);populate();UpdateBoardSummary();});
                var scan=BoardAction(actions,"扫描附近",()=>{});
                scan.Click+=async delegate{
                    scan.Enabled=false;status.Text="正在扫描并读取完整编号…";
                    try{var found=await BoardDiscovery.Nearby(link as BleLink,cancel.Token);if(w.IsDisposed)return;nearby=found;populate();status.Text=nearby.Count==0?"没有发现广播；开发板可能未供电或正被其他客户端连接。":"相同完整编号已合并。未读到编号的广播保留单独显示；连接时进行身份认证。";}
                    catch(Exception e){if(!w.IsDisposed)status.Text=e is OperationCanceledException?"扫描已取消":e.Message;}
                    finally{if(!scan.IsDisposed)scan.Enabled=true;}
                };
                var more=BoardAction(actions,"更多 ▾",()=>{});Theme.Menu(moreMenu);more.Click+=delegate{moreMenu.Show(more,new Point(0,more.Height));};
                moreMenu.Items.Add("导入加密配置…", null, delegate { safely(() => {
                    using (var open = new OpenFileDialog { Filter = "AtomClip绑定配置|*.acbind", CheckFileExists = true }) { if (open.ShowDialog(w) != DialogResult.OK) return; if (new FileInfo(open.FileName).Length > 328) throw new IOException("配置文件过大"); string password = BoardPrompt(w, "导入配置口令", "", true); if (password == null) return; var b = V2Protocol.ImportBinding(File.ReadAllBytes(open.FileName), password); if (deviceBindings.Registry.Pending.ContainsKey(b.DeviceInfo.Number)) throw new IOException("此板有待确认记录，请先通过USB核对或明确忘记"); if (deviceBindings.Registry.Confirmed.ContainsKey(b.DeviceInfo.Number) && MessageBox.Show(w, "本地已有此编号，是否替换为导入配置？不会改写开发板。", "替换本地配置", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return; Stop(); deviceBindings.Change(r => { r.Confirmed[b.DeviceInfo.Number] = b; r.Select(b.DeviceInfo.Number); }); ActivateBoardKey(); }
                }); });
                moreMenu.Items.Add("导出选中绑定…", null, delegate { safely(() => { var b = selectedBinding(); if (b == null || b.Pending) throw new IOException("请选择已确认的绑定"); string password = BoardPrompt(w, "导出配置口令", "", true); if (password == null) return; string repeat = BoardPrompt(w, "再次输入导出口令", "", true); if (password != repeat) throw new IOException("两次口令不一致"); byte[] encrypted = V2Protocol.ExportBinding(b, password); using (var save = new SaveFileDialog { Filter = "AtomClip绑定配置|*.acbind", FileName = "AtomClip-" + b.DeviceInfo.ShortId + ".acbind", OverwritePrompt = true }) if (save.ShowDialog(w) == DialogResult.OK) File.WriteAllBytes(save.FileName, encrypted); }); });
                moreMenu.Items.Add("更名…",null,delegate{safely(()=>{var b=selectedBinding();if(b==null)throw new IOException("请选择已保存的开发板");string name=BoardPrompt(w,"开发板别名",b.Alias,false);if(name==null)return;var renamed=new V2Protocol.Binding(b.DeviceInfo,b.ControlKey,b.DataKey,name){Pending=b.Pending};deviceBindings.Change(r=>{if(b.Pending)r.Pending[b.DeviceInfo.Number]=renamed;else r.Confirmed[b.DeviceInfo.Number]=renamed;});});});
                moreMenu.Items.Add("识别选中的已连接板",null,async delegate{var b=selectedBinding();var connected=link as BleLink;if(b==null||connected==null||!connected.Connected||connected.AuthenticatedInfo.Number!=b.DeviceInfo.Number){MessageBox.Show(w,"请先连接选中的开发板。");return;}try{await connected.Identify(connectionCts.Token);}catch(Exception e){if(!w.IsDisposed)status.Text=e.Message;}});
                moreMenu.Items.Add(new ToolStripSeparator());
                Action<int,bool> recovery=(mode,pending)=>{var b=selectedBinding();if(b==null||b.Pending!=pending){MessageBox.Show(w,pending?"请选择待 USB 确认的记录。":"请选择已确认的绑定。");return;}OpenUsbBoard(w,mode,b);populate();UpdateBoardSummary();};
                moreMenu.Items.Add("通过 USB 确认待绑定",null,delegate{recovery(1,true);});
                moreMenu.Items.Add("通过 USB 重试待绑定写入",null,delegate{recovery(4,true);});
                moreMenu.Items.Add("通过 USB 恢复已有配置…",null,delegate{recovery(2,false);});
                moreMenu.Items.Add("清除板上绑定…",null,delegate{OpenUsbBoard(w,3,null);populate();UpdateBoardSummary();});
                moreMenu.Items.Add("忘记选中的本机记录…",null,delegate{safely(()=>{var b=selectedBinding();if(b==null)throw new IOException("请选择已保存的开发板");if(MessageBox.Show(w,"只删除本机记录，不改写开发板。若待确认密钥已写入板内，之后可能需要通过 USB 恢复。","忘记本机记录",MessageBoxButtons.OKCancel,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.OK)return;Stop();deviceBindings.Change(r=>{if(b.Pending)r.Pending.Remove(b.DeviceInfo.Number);else r.Forget(b.DeviceInfo.Number);});ActivateBoardKey();});});
                if(deviceBindings.LoadError!=null)moreMenu.Items.Add("清除不可读的本地配置…",null,delegate{if(MessageBox.Show(w,"保留不可读文件副本并创建空配置，之后需重新导入或通过 USB 添加。","恢复本地配置",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.OK)safely(()=>{Stop();deviceBindings.ResetUnreadable();ActivateBoardKey();});});
                w.FormClosed+=delegate{cancel.Cancel();};populate();Theme.Apply(w);w.ShowDialog(this);
                if(connectAfter){desired=true;var ignored=ConnectNow();}
            }
            UpdateBoardSummary();
        }
        sealed class BoardPort
        {
            public string Name,Label;public bool Espressif;
            public override string ToString(){return Label;}
        }
        static List<BoardPort> BoardPorts()
        {
            var names=SerialPort.GetPortNames();Array.Sort(names,StringComparer.OrdinalIgnoreCase);
            var result=names.Select(n=>new BoardPort{Name=n,Label=n}).ToList();
            try{using(var usb=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))if(usb!=null)
                foreach(string hardware in usb.GetSubKeyNames())using(var model=usb.OpenSubKey(hardware))if(model!=null)
                    foreach(string instance in model.GetSubKeyNames())using(var d=model.OpenSubKey(instance))using(var parameters=d==null?null:d.OpenSubKey("Device Parameters")){
                        string name=parameters==null?"":Convert.ToString(parameters.GetValue("PortName"));var port=result.FirstOrDefault(x=>string.Equals(x.Name,name,StringComparison.OrdinalIgnoreCase));if(port==null)continue;
                        string label=Convert.ToString(d.GetValue("FriendlyName"));port.Label=label.Length>0?label:port.Name+" · USB";port.Espressif=hardware.StartsWith("VID_303A",StringComparison.OrdinalIgnoreCase);
                    }
            }catch(System.Security.SecurityException){}catch(UnauthorizedAccessException){}catch(IOException){}
            return result;
        }
        // 0 add (reuse a known binding), 1 confirm pending, 2 restore, 3 reset, 4 retry, 5 verify existing.
        void OpenUsbBoard(Form owner,int mode,V2Protocol.Binding existing)
        {
            Stop();int requestedMode=mode;var requestedRecord=existing;
            using(var w=new Form{Text=mode==0?"添加开发板":"USB 开发板管理",Font=Font,StartPosition=FormStartPosition.CenterParent,ClientSize=new Size(640,480),MinimumSize=new Size(560,440)})
            using(var cancel=new CancellationTokenSource())
            {
                var viewport=new Panel{Dock=DockStyle.Fill,AutoScroll=true};var root=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(22),ColumnCount=1};root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                root.Controls.Add(WrappedLabel("用数据线接入开发板，读取后确认添加即可。已绑定的板会自动识别；现有正文配对会保留。"));
                var ports=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top};root.Controls.Add(ports);
                var controls=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=true};root.Controls.Add(controls);var refresh=BoardAction(controls,"刷新设备",()=>{});var read=BoardAction(controls,"读取开发板",()=>{});
                root.Controls.Add(WrappedLabel("名称（可修改）"));var alias=new TextBox{Dock=DockStyle.Top,Text=existing==null?"我的开发板":existing.Alias,Enabled=mode==0};root.Controls.Add(alias);
                var status=WrappedLabel("请接入运行 0.2.0 或更新固件的板，无需进入下载模式。");root.Controls.Add(status);
                var row=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=true};root.Controls.Add(row);var apply=BoardAction(row,"确认添加",()=>{});Theme.Primary(apply);apply.Enabled=false;
                BoardAction(row,"关闭",()=>w.Close());bool busy=false;UsbBoardClient.Material material=null;
                Action clearMaterial=()=>{if(material!=null)Array.Clear(material.DataKey,0,32);material=null;apply.Enabled=false;};
                Action labelAction=()=>{apply.Text=mode==1?"确认已有绑定":mode==5?"使用已有绑定":mode==2?"恢复配置…":mode==3?"清除绑定…":mode==4?"重试写入…":"确认添加";};labelAction();
                ports.SelectedIndexChanged+=delegate{clearMaterial();mode=requestedMode;existing=requestedRecord;alias.Text=existing==null?"我的开发板":existing.Alias;alias.Enabled=mode==0;labelAction();status.Text="设备已选择，点击“读取开发板”。";};
                Action refreshPorts=()=>{if(busy)return;clearMaterial();ports.Items.Clear();var found=BoardPorts();ports.Items.AddRange(found.ToArray());var known=found.Where(x=>x.Espressif).ToList();if(known.Count==1)ports.SelectedItem=known[0];status.Text=found.Count==0?"未发现 USB 串口。接入数据线后点击“刷新设备”。":known.Count==1?"已找到 USB 设备，点击“读取开发板”核对。":"请选择目标开发板的 USB 串口，然后读取。";};
                refresh.Click+=delegate{refreshPorts();};
                read.Click+=async delegate{
                    var selected=ports.SelectedItem as BoardPort;if(busy||selected==null)return;busy=true;read.Enabled=refresh.Enabled=apply.Enabled=ports.Enabled=false;clearMaterial();status.Text="正在读取开发板…";
                    try{
                        material=await Task.Run(()=>{using(var usb=new UsbBoardClient(selected.Name,cancel.Token))return usb.Read();},cancel.Token);
                        if(existing!=null&&!V2Protocol.Equal(existing.DeviceInfo.Id,material.Info.Id))throw new CryptographicException("USB 编号与所选记录不同，禁止修改");
                        if(requestedMode==0){mode=0;existing=null;V2Protocol.Binding found;if(deviceBindings.Registry.Pending.TryGetValue(material.Info.Number,out found)){existing=found;mode=1;}else if(deviceBindings.Registry.Confirmed.TryGetValue(material.Info.Number,out found)){existing=found;mode=5;}if(existing!=null)alias.Text=existing.Alias;}
                        alias.Enabled=mode==0;labelAction();status.Text=boardCatalog.Name(material.Info.Profile)+"\r\n完整编号："+material.Info.Number+"\r\n"+(mode==5?"已在本机绑定，核对后可直接使用。":mode==1?"找到待确认配置，将核对上次绑定结果。":material.Info.Configured?"已有其他控制绑定，添加时将提示确认替换。":"可以添加这块开发板。");apply.Enabled=true;
                    }catch(Exception e){status.Text=e.Message;clearMaterial();}finally{busy=false;read.Enabled=refresh.Enabled=ports.Enabled=!cancel.IsCancellationRequested;}
                };
                apply.Click+=async delegate{
                    var port=ports.SelectedItem as BoardPort;if(busy||material==null||port==null)return;bool replace=material.Info.Configured;
                    if(mode==3||mode==2||mode==4||(mode==0&&replace)){
                        string question=mode==3?"清除控制绑定并重新生成正文配对，现有客户端配置将失效。":mode==2||mode==4?"将所选配置写入这块板，替换板上现有控制绑定。":"这块板已有其他绑定。继续将替换控制绑定，正文配对保留。";
                        if(MessageBox.Show(w,question+"\r\n完整编号："+material.Info.Number,"确认 USB 目标",MessageBoxButtons.OKCancel,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.OK)return;
                    }
                    var expected=material.Info;V2Protocol.Binding candidate=null;
                    try{if(mode!=3)candidate=mode==0?new V2Protocol.Binding(expected,V2Protocol.Random(32),material.DataKey,alias.Text):V2Protocol.Binding.Decode(existing.Encode());}catch(Exception e){status.Text=e.Message;return;}
                    busy=true;read.Enabled=refresh.Enabled=apply.Enabled=ports.Enabled=alias.Enabled=false;status.Text="正在核对并保存…";
                    try{
                        await Task.Run(()=>{using(var usb=new UsbBoardClient(port.Name,cancel.Token)){
                            if(mode==3){usb.Reset(expected);deviceBindings.Change(r=>r.Forget(expected.Number));return;}
                            if(mode==1||mode==5){var info=usb.Confirm(candidate);deviceBindings.Change(r=>{if(mode==1){r.Pending[info.Number].DeviceInfo=info;r.Confirm(info.Number);}else{r.Confirmed[info.Number].DeviceInfo=info;r.Select(info.Number);}});return;}
                            deviceBindings.Change(r=>r.Stage(candidate));var verified=usb.Provision(candidate,replace);deviceBindings.Change(r=>{r.Pending[verified.Number].DeviceInfo=verified;r.Confirm(verified.Number);});
                        }},cancel.Token);
                        ActivateBoardKey();status.Text=mode==3?"板上绑定已清除。":"已完成。关闭窗口后选择这块板，点击“连接”即可使用。";
                    }catch(Exception e){status.Text=e.Message+"\r\n可在“更多”中通过 USB 确认、重试写入或恢复已有配置。";}
                    finally{if(candidate!=null){Array.Clear(candidate.ControlKey,0,32);Array.Clear(candidate.DataKey,0,32);}clearMaterial();busy=false;read.Enabled=refresh.Enabled=ports.Enabled=!cancel.IsCancellationRequested;}
                };
                w.FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){cancel.Cancel();e.Cancel=true;status.Text="正在中止；待确认配置会保留。";}};
                w.FormClosed+=delegate{cancel.Cancel();clearMaterial();};w.Shown+=delegate{refreshPorts();};viewport.Controls.Add(root);w.Controls.Add(viewport);Theme.Apply(w);w.ShowDialog(owner);
            }
        }
    }
#endif
}
