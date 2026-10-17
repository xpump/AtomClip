using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using AtomClipUsb;
class HelpUiReview {
 [STAThread]static int Main(string[] args){
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  var assembly=typeof(Protocol).Assembly;string content=(string)assembly.GetType("AtomClipUsb.Guide").GetField("Ble").GetRawConstantValue();Exception error=null;
  using(var owner=new Form{Font=new Font("Microsoft YaHei UI",float.Parse(args[1]))})
  using(var timer=new Timer{Interval=100}){
   timer.Tick+=delegate{timer.Stop();try{
    Form help=null;foreach(Form f in Application.OpenForms)if(f.Text=="使用帮助")help=f;
    if(help==null)throw new Exception("Help window missing");
    help.Size=new Size(int.Parse(args[2]),int.Parse(args[3]));help.PerformLayout();Application.DoEvents();
    var scroll=(Panel)help.Controls[0];var table=(TableLayoutPanel)scroll.Controls[0];int headings=0;
    foreach(Control c in table.Controls){if(c.Font.Bold)headings++;if(c.Right>table.ClientSize.Width)throw new Exception("Help text clipped horizontally");}
    if(headings<8)throw new Exception("Help heading hierarchy missing");
    if(args.Length>4&&args[4]=="bottom")scroll.AutoScrollPosition=new Point(0,table.Height);
    Application.DoEvents();using(var bmp=new Bitmap(help.Width,help.Height)){help.DrawToBitmap(bmp,new Rectangle(Point.Empty,help.Size));bmp.Save(Path.GetFullPath(args[0]));}
    Console.WriteLine("PASS: help hierarchy headings="+headings+"; scroll="+scroll.VerticalScroll.Visible+"; font="+owner.Font.Size+"; no horizontal clipping");help.Close();
   }catch(Exception ex){error=ex;foreach(Form f in Application.OpenForms)if(f.Text=="使用帮助"){f.Close();break;}}};
   timer.Start();assembly.GetType("AtomClipUsb.Theme").GetMethod("Help").Invoke(null,new object[]{owner,"使用帮助",content});
  }
  if(error!=null){Console.Error.WriteLine(error);return 1;}return 0;
 }
}
