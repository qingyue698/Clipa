using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Microsoft.Web.WebView2.WinForms;

namespace MuseClipStation
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    class MainForm : Form
    {
        WebView2 web;
        NotifyIcon tray;
        bool reallyExit;
        bool vertical;
        bool readyApplied;
        bool autoH = true;
        bool inPeek;
        bool allowCapture;
        bool recExcl;
        Rectangle peekRect;
        int peekPhysW, peekPhysH;
        double peekScale = 1;
        Rectangle animFrom, animTo;
        Size webFullSize;
        bool animToPeek, animating;
        long animStart;
        System.Windows.Forms.Timer animTimer;
        Timer dragTimer;
        Point dragGrabOffset;
        string dataDir;
        string posPath;
        Dictionary<string, int[]> posMem = new Dictionary<string, int[]>();
        Timer posTimer;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        const int WM_HOTKEY = 0x0312;
        const int HOTKEY_ID = 9001;
        const int HOTKEY_ID2 = 9002;
        uint curHkMods = 0x0001; int curHkVk = 0x44;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID)
            {
                if (!Visible) ShowDock();
                else Post(new { type = inPeek ? "exitPeek" : "enterPeek" });
            }
            else if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID2)
            {
                var _ = DoCapture();
            }
            base.WndProc(ref m);
        }

        public MainForm()
        {
            Text = "Clipa";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            ShowInTaskbar = true;
            TopMost = true;

            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuseClipStation");
            Directory.CreateDirectory(dataDir);
            posPath = Path.Combine(dataDir, "pos.json");
            LoadPos();

            Size = new Size(PosW("h6", 1600), 276);
            Location = StoredLoc("h6", DefaultLoc(false));

            try
            {
                var asm = typeof(MainForm).Assembly;
                using (var st = asm.GetManifestResourceStream("app.ico"))
                    if (st != null) Icon = new Icon(st);
            }
            catch { }

            BuildTray();
            Move += (s, e) => DebounceSavePos();
            FormClosing += (s, e) =>
            {
                if (!reallyExit) { e.Cancel = true; Hide(); }
                else if (tray != null) tray.Visible = false;
            };
            Shown += (s, e) =>
            {
                ApplyRounded(); TryAffinity();
                try { RegisterHotKey(Handle, HOTKEY_ID, 0x0001 | 0x0002, 0x4D); } catch { }
                try { RegisterHotKey(Handle, HOTKEY_ID2, curHkMods, (uint)curHkVk); } catch { }
            };
            FormClosed += (s, e) => { try { UnregisterHotKey(Handle, HOTKEY_ID); } catch { } try { UnregisterHotKey(Handle, HOTKEY_ID2); } catch { } };
            InitWeb();
        }

        Point DefaultLoc(bool v)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            if (v) return new Point(10, 10);
            return new Point(Math.Max(0, (wa.Width - Size.Width) / 2), 8);
        }

        void StartDrag()
        {
            dragGrabOffset = new Point(Cursor.Position.X - Location.X, Cursor.Position.Y - Location.Y);
            if (dragTimer == null)
            {
                dragTimer = new Timer { Interval = 12 };
                dragTimer.Tick += (s, e) =>
                {
                    if ((Control.MouseButtons & MouseButtons.Left) == 0) { dragTimer.Stop(); return; }
                    Location = new Point(Cursor.Position.X - dragGrabOffset.X, Cursor.Position.Y - dragGrabOffset.Y);
                };
            }
            dragTimer.Start();
        }

        void ApplyRounded()
        {
            try { int pref = 2; DwmSetWindowAttribute(Handle, 33, ref pref, 4); } catch { }
        }

        ToolStripMenuItem trayPinItem, trayAutoItem;

        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "Clipa";
        const string RunValueNameOld = "MuseCapturePaste";
        const string RunValueNameOld2 = "MuseClipStation";
        static bool IsAutoStart()
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath)) return k != null && (k.GetValue(RunValueName) != null || k.GetValue(RunValueNameOld) != null || k.GetValue(RunValueNameOld2) != null); }
            catch { return false; }
        }
        static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return;
                    if (on) { k.SetValue(RunValueName, "\"" + Environment.ProcessPath + "\""); k.DeleteValue(RunValueNameOld, false); k.DeleteValue(RunValueNameOld2, false); }
                    else { k.DeleteValue(RunValueName, false); k.DeleteValue(RunValueNameOld, false); k.DeleteValue(RunValueNameOld2, false); }
                }
            }
            catch { }
        }

        void BuildTray()
        {
            tray = new NotifyIcon { Text = "Clipa", Visible = true };
            if (Icon != null) tray.Icon = Icon;
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示 Clipa", null, (s, e) => ShowDock());
            menu.Items.Add(new ToolStripSeparator());
            trayPinItem = new ToolStripMenuItem("窗口置顶") { CheckOnClick = true, Checked = TopMost };
            trayPinItem.Click += (s, e) => { TopMost = trayPinItem.Checked; Post(new { type = "pinState", on = TopMost }); };
            trayAutoItem = new ToolStripMenuItem("开机自启动") { CheckOnClick = true, Checked = IsAutoStart() };
            trayAutoItem.Click += (s, e) => { SetAutoStart(trayAutoItem.Checked); Post(new { type = "autoStartState", on = trayAutoItem.Checked }); };
            menu.Items.Add(trayPinItem);
            menu.Items.Add(trayAutoItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出应用", null, (s, e) => { reallyExit = true; Close(); });
            menu.Opening += (s, e) => { if (trayPinItem != null) trayPinItem.Checked = TopMost; if (trayAutoItem != null) trayAutoItem.Checked = IsAutoStart(); };
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowDock();
        }

        static double EaseOut(double t) { return 1 - Math.Pow(1 - t, 3); }
        static int Lerp(int a, int b, double t) { return a + (int)Math.Round((b - a) * t); }

        // 幕布式窗口动画：展开先左右后上下，收起先上下后左右
        void StartBoundsAnim(Rectangle to, bool toPeek)
        {
            animFrom = Bounds; animTo = to; animToPeek = toPeek;
            animStart = Environment.TickCount64; animating = true;
            if (web != null)
            {
                try
                {
                    // Freeze the webview at its expanded size for the whole transition:
                    // the window clips it instead of Chromium reflowing on every step (that reflow was the flicker).
                    if (toPeek) { if (web.Dock == DockStyle.Fill) webFullSize = web.Size; }
                    else webFullSize = to.Size;
                    if (webFullSize.Width <= 0 || webFullSize.Height <= 0) webFullSize = ClientSize;
                    web.Dock = DockStyle.None;
                    LayoutWebDuringAnim();
                }
                catch { }
            }
            MorphRegion(toPeek ? 0 : 1);
            if (animTimer == null)
            {
                animTimer = new System.Windows.Forms.Timer { Interval = 16 };
                animTimer.Tick += AnimTick;
            }
            animTimer.Stop(); animTimer.Start();
        }

        void AnimTick(object sender, EventArgs e)
        {
            double t = Math.Min(1, (Environment.TickCount64 - animStart) / 460.0);
            double pw, ph;
            double p1 = EaseOut(Math.Min(1, t / 0.58));
            double p2 = EaseOut(Math.Max(0, (t - 0.32) / 0.68));
            if (animToPeek) { ph = p1; pw = p2; }
            else { pw = p1; ph = p2; }
            int w = Lerp(animFrom.Width, animTo.Width, pw);
            int h = Lerp(animFrom.Height, animTo.Height, ph);
            int cx = animFrom.Left + animFrom.Width / 2;
            int tcx = animTo.Left + animTo.Width / 2;
            int ccx = Lerp(cx, tcx, pw);
            Bounds = new Rectangle(ccx - w / 2, animTo.Top, w, h);
            LayoutWebDuringAnim();
            MorphRegion(animToPeek ? EaseOut(t) : 1 - EaseOut(t));
            if (t >= 1)
            {
                animTimer.Stop(); animating = false; Bounds = animTo;
                if (animToPeek)
                {
                    ApplyPeekRegion();
                    if (web != null) try { web.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height); } catch { }
                }
                else
                {
                    Region = null;
                    if (web != null) try { web.Dock = DockStyle.Fill; } catch { }
                }
                try { web.CoreWebView2.PostWebMessageAsString("{\"type\":\"reportSize\"}"); } catch { }
            }
        }

        void LayoutWebDuringAnim()
        {
            if (web == null) return;
            try
            {
                web.Size = webFullSize;
                web.Location = new Point((ClientSize.Width - webFullSize.Width) / 2, 0);
            }
            catch { }
        }

        static System.Drawing.Drawing2D.GraphicsPath PeekPath(int w, int h, int rTop, int rBottom)
        { return PeekPath(w, h, rTop, rBottom, 0); }

        // taper > 0: sides slant inward toward the bottom (rounded trapezoid / island silhouette)
        static System.Drawing.Drawing2D.GraphicsPath PeekPath(int w, int h, int rTop, int rBottom, int taper)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int rt = Math.Max(0, rTop), rb = Math.Max(0, rBottom);
            int tp = Math.Max(0, Math.Min(taper, w / 4));
            if (rt > 0) path.AddArc(0, 0, 2 * rt, 2 * rt, 180, 90);
            else path.AddLine(0, 0, 0, 0);
            path.AddLine(rt, 0, w - rt, 0);
            if (rt > 0) path.AddArc(w - 2 * rt, 0, 2 * rt, 2 * rt, 270, 90);
            else path.AddLine(w, 0, w, 0);
            path.AddLine(w, rt, w - tp, h - rb);
            if (rb > 0) path.AddArc(w - tp - 2 * rb, h - 2 * rb, 2 * rb, 2 * rb, 0, 90);
            path.AddLine(w - tp - rb, h, tp + rb, h);
            if (rb > 0) path.AddArc(tp, h - 2 * rb, 2 * rb, 2 * rb, 90, 90);
            path.AddLine(tp, h - rb, 0, rt);
            path.CloseFigure();
            return path;
        }

        void SetRegionPath(System.Drawing.Drawing2D.GraphicsPath path)
        {
            var old = Region;
            Region = path == null ? null : new Region(path);
            if (old != null) old.Dispose();
        }

        // Silhouette morph while the window animates: pk 0 = bar (soft corners), 1 = pill (flat top, round bottom)
        void MorphRegion(double pk)
        {
            try
            {
                int w = Width, h = Height;
                if (w <= 0 || h <= 0) return;
                double sc = DeviceDpi / 96.0;
                int rFull = Math.Max(8, (int)Math.Round(18 * sc));
                int rb = Lerp((int)Math.Round(13 * sc), Math.Min(rFull, h / 2), pk);
                int rt = Lerp((int)Math.Round(13 * sc), 0, pk);
                int tp = 0;
                using (var path = PeekPath(w, h, rt, rb, tp)) SetRegionPath(path);
            }
            catch { }
        }

        // 刘海窗口形状：只保留药丸本体（上边直、下边圆角），窗口其余部分不存在
        void ApplyPeekRegion()
        {
            try
            {
                int w = Width, h = Height;
                if (w <= 0 || h <= 0) return;
                int r = Math.Max(8, (int)Math.Round(24 * peekScale));
                if (2 * r > h) r = h / 2;
                using (var path = PeekPath(w, h, 0, r, 0)) SetRegionPath(path);
            }
            catch { }
        }

        // 原生拖拽：把页面拖出的内容变成系统拖拽，可直接放进微信/文件夹/其他软件

        // ================= 录屏：主屏幕 GDI 抓帧 → MediaStreamSource → H.264 MP4 =================
        bool recActive, recStopFlag;
        string recStationPath, recStationFile, recUserDir, recDisplayName;
        DateTime recT0;
        System.Threading.Thread recThread;
        MediaStreamSource recMss;
        readonly Queue<MediaStreamSample> recQueue = new Queue<MediaStreamSample>();
        readonly object recLock = new object();
        MediaStreamSourceSampleRequest recPending;
        MediaStreamSourceSampleRequestDeferral recPendingDeferral;
        Task<bool> recTask;
        IRandomAccessStream recOutStream;
        int recW, recH;

        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, uint step, IntPtr flicker, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public int x; public int y; }

        async void StartRecord(JsonElement root)
        {
            if (recActive) { Post(new { type = "recordFailed" }); return; }
            recUserDir = root.TryGetProperty("dir", out var dv0) ? dv0.GetString() : "";
            recDisplayName = root.TryGetProperty("name", out var nv0) ? nv0.GetString() : "recording.mp4";
            Rectangle area;
            try
            {
                await Task.Delay(60);
                var vb = SystemInformation.VirtualScreen;
                Rectangle picked = Rectangle.Empty;
                using (var bmp = new Bitmap(vb.Width, vb.Height))
                {
                    using (var g0 = Graphics.FromImage(bmp)) g0.CopyFromScreen(vb.Left, vb.Top, 0, 0, vb.Size);
                    using (var rf = new RegionForm(bmp, vb) { SelectOnly = true })
                    {
                        if (rf.ShowDialog(this) == DialogResult.OK) picked = rf.ResultScreenRect;
                    }
                }
                if (picked.IsEmpty) { Post(new { type = "recordCanceled" }); return; }
                area = Rectangle.Intersect(vb, picked);
                if (area.Width < 16 || area.Height < 16) { Post(new { type = "recordCanceled" }); return; }
            }
            catch { Post(new { type = "recordFailed" }); return; }
            try
            {
                double f = Math.Min(1.0, Math.Min(1920.0 / area.Width, 1080.0 / area.Height));
                recW = Math.Max(16, (int)(area.Width * f) / 2 * 2);
                recH = Math.Max(16, (int)(area.Height * f) / 2 * 2);
                string recDir = Path.Combine(dataDir, "rec");
                Directory.CreateDirectory(recDir);
                recStationFile = "rec-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mp4";
                recStationPath = Path.Combine(recDir, recStationFile);
                var inputProps = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)recW, (uint)recH);
                inputProps.FrameRate.Numerator = 24; inputProps.FrameRate.Denominator = 1;
                inputProps.Bitrate = (uint)((long)recW * recH * 4 * 8 * 24);
                var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
                profile.Video.Width = (uint)recW; profile.Video.Height = (uint)recH;
                profile.Video.FrameRate.Numerator = 24; profile.Video.FrameRate.Denominator = 1;
                profile.Video.Bitrate = (uint)(recW * recH >= 1920 * 1080 ? 10000000 : 6000000);
                recMss = new MediaStreamSource(new VideoStreamDescriptor(inputProps)) { BufferTime = TimeSpan.Zero };
                recMss.SampleRequested += RecSampleRequested;
                lock (recLock) { recQueue.Clear(); recPending = null; recPendingDeferral = null; }
                recStopFlag = false;
                var folder = await StorageFolder.GetFolderFromPathAsync(recDir);
                var file = await folder.CreateFileAsync(recStationFile, CreationCollisionOption.ReplaceExisting);
                recOutStream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var transcoder = new MediaTranscoder();
                var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(recMss, recOutStream, profile);
                if (!prep.CanTranscode) { Post(new { type = "recordFailed" }); try { recOutStream.Dispose(); } catch { } return; }
                var op = prep.TranscodeAsync();
                var tcs = new TaskCompletionSource<bool>();
                op.Completed = (info, status) => { tcs.TrySetResult(status == Windows.Foundation.AsyncStatus.Completed); };
                recTask = tcs.Task;
                recExcl = true; TryAffinity();
                recActive = true; recT0 = DateTime.Now;
                recThread = new System.Threading.Thread(RecCaptureLoop) { IsBackground = true };
                recThread.Start(area);
                Post(new { type = "recordStarted" });
            }
            catch { recActive = false; recExcl = false; TryAffinity(); Post(new { type = "recordFailed" }); }
        }

        void RecCaptureLoop(object state)
        {
            var area = (Rectangle)state;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long frameTicks = TimeSpan.TicksPerSecond / 24;
            long idx = 0;
            var full = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            var frameBmp = new Bitmap(recW, recH, PixelFormat.Format32bppArgb);
            try
            {
                while (!recStopFlag)
                {
                    long target = idx * frameTicks;
                    long wait = target - sw.Elapsed.Ticks;
                    if (wait > 30000) System.Threading.Thread.Sleep((int)(wait / 10000) - 1);
                    else if (wait < -frameTicks * 3) { idx = sw.Elapsed.Ticks / frameTicks; target = idx * frameTicks; }
                    using (var g = Graphics.FromImage(full))
                    {
                        g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
                        try
                        {
                            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                            if (GetCursorInfo(ref ci) && ci.flags == 1 && ci.hCursor != IntPtr.Zero)
                            {
                                IntPtr hdc = g.GetHdc();
                                DrawIconEx(hdc, ci.x - area.Left, ci.y - area.Top, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
                                g.ReleaseHdc(hdc);
                            }
                        }
                        catch { }
                    }
                    using (var g2 = Graphics.FromImage(frameBmp))
                    {
                        g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g2.DrawImage(full, 0, 0, recW, recH);
                    }
                    var bd = frameBmp.LockBits(new Rectangle(0, 0, recW, recH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    byte[] bytes = new byte[recW * recH * 4];
                    for (int yy = 0; yy < recH; yy++)
                        Marshal.Copy(bd.Scan0 + (recH - 1 - yy) * bd.Stride, bytes, yy * recW * 4, recW * 4);
                    frameBmp.UnlockBits(bd);
                    var sample = MediaStreamSample.CreateFromBuffer(CryptographicBuffer.CreateFromByteArray(bytes), TimeSpan.FromTicks(target));
                    sample.Duration = TimeSpan.FromTicks(frameTicks);
                    lock (recLock)
                    {
                        if (recPending != null) { recPending.Sample = sample; var d = recPendingDeferral; recPending = null; recPendingDeferral = null; if (d != null) d.Complete(); }
                        else recQueue.Enqueue(sample);
                    }
                    idx++;
                }
            }
            catch { }
            finally { full.Dispose(); frameBmp.Dispose(); }
        }

        void RecSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            lock (recLock)
            {
                if (recQueue.Count > 0) { args.Request.Sample = recQueue.Dequeue(); return; }
                if (recStopFlag) { args.Request.Sample = null; return; }
                recPending = args.Request;
                recPendingDeferral = args.Request.GetDeferral();
            }
        }

        async void StopRecord()
        {
            if (!recActive) return;
            recStopFlag = true;
            try { recThread?.Join(3000); } catch { }
            lock (recLock)
            {
                if (recPending != null) { recPending.Sample = null; var d = recPendingDeferral; recPending = null; recPendingDeferral = null; if (d != null) d.Complete(); }
            }
            double dur = (DateTime.Now - recT0).TotalSeconds;
            bool ok = false;
            try { ok = recTask != null && await recTask; } catch { ok = false; }
            try { recOutStream?.Dispose(); } catch { }
            recActive = false;
            recExcl = false; TryAffinity();
            if (!ok || recStationPath == null || !File.Exists(recStationPath)) { Post(new { type = "recordFailed" }); return; }
            long size = 0; try { size = new FileInfo(recStationPath).Length; } catch { }
            string userPath = "";
            if (!string.IsNullOrWhiteSpace(recUserDir))
            {
                try
                {
                    Directory.CreateDirectory(recUserDir);
                    string safe = recDisplayName;
                    foreach (var ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
                    userPath = Path.Combine(recUserDir, safe);
                    File.Copy(recStationPath, userPath, true);
                }
                catch { userPath = ""; }
            }
            Post(new { type = "recorded", name = recDisplayName, file = recStationFile, url = "https://app.local/rec/" + recStationFile, stationPath = recStationPath, userPath, dur, size });
        }

        void DoCopyPaths(JsonElement root)
        {
            int n = 0;
            try
            {
                var list = new StringCollection();
                if (root.TryGetProperty("paths", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var pe in arr.EnumerateArray()) { string pp = pe.GetString(); if (!string.IsNullOrEmpty(pp) && File.Exists(pp)) { list.Add(pp); n++; } }
                if (n > 0) Clipboard.SetFileDropList(list);
            }
            catch { n = 0; }
            Post(new { type = "copyFilesResult", ok = n > 0, count = n });
        }

        void OpenPath(JsonElement root)
        {
            try { string pp = root.TryGetProperty("path", out var pv) ? pv.GetString() : ""; if (!string.IsNullOrEmpty(pp) && File.Exists(pp)) Process.Start(new ProcessStartInfo(pp) { UseShellExecute = true }); } catch { }
        }

        void DeleteRecCache(JsonElement root)
        {
            try { string fn = Path.GetFileName(root.TryGetProperty("file", out var fv) ? fv.GetString() : ""); if (fn.Length > 0) { string fp = Path.Combine(dataDir, "rec", fn); if (File.Exists(fp)) File.Delete(fp); } } catch { }
        }

        void StartNativeDrag(JsonElement root)
        {
            try
            {
                string kind = root.TryGetProperty("kind", out var kv) ? kv.GetString() : "";
                var data = new DataObject();
                if (kind == "texts")
                {
                    string text = root.TryGetProperty("text", out var tv) ? tv.GetString() : "";
                    if (string.IsNullOrEmpty(text)) return;
                    data.SetText(text, TextDataFormat.UnicodeText);
                }
                else
                {
                    root.TryGetProperty("files", out var filesEl);
                    var dir = Path.Combine(Path.GetTempPath(), "MuseClipStation", "drag");
                    Directory.CreateDirectory(dir);
                    var list = new StringCollection();
                    string singleB64 = null; int count = 0;
                    if (filesEl.ValueKind == JsonValueKind.Array) foreach (var f in filesEl.EnumerateArray())
                    {
                        string name = f.TryGetProperty("name", out var nv) ? nv.GetString() : "file";
                        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
                        string b64 = f.TryGetProperty("b64", out var bv) ? bv.GetString() : "";
                        if (string.IsNullOrEmpty(b64)) continue;
                        string path = Path.Combine(dir, name);
                        File.WriteAllBytes(path, Convert.FromBase64String(b64));
                        list.Add(path);
                        singleB64 = b64; count++;
                    }
                    if (root.TryGetProperty("paths", out var pathsEl) && pathsEl.ValueKind == JsonValueKind.Array)
                        foreach (var pe in pathsEl.EnumerateArray()) { string pp = pe.GetString(); if (!string.IsNullOrEmpty(pp) && File.Exists(pp)) list.Add(pp); }
                    if (list.Count == 0) return;
                    data.SetFileDropList(list);
                    if (count == 1 && kind == "shots" && singleB64 != null)
                    {
                        // 单张截图再附一份 PNG 数据，兼容只认图片的输入框
                        try { data.SetData("PNG", new MemoryStream(Convert.FromBase64String(singleB64))); } catch { }
                    }
                }
                DoDragDrop(data, DragDropEffects.Copy);
            }
            catch { }
        }

        ViewForm viewForm;
        void ShowViewImage(JsonElement root)
        {
            try
            {
                string b64 = root.TryGetProperty("b64", out var bv) ? bv.GetString() : "";
                if (string.IsNullOrEmpty(b64)) return;
                var bytes = Convert.FromBase64String(b64);
                using (var ms = new MemoryStream(bytes))
                using (var bmp0 = new Bitmap(ms))
                {
                    if (viewForm != null && !viewForm.IsDisposed) viewForm.Close();
                    viewForm = new ViewForm(new Bitmap(bmp0));
                    viewForm.Show();
                }
            }
            catch { }
        }

        void OpenFileNative(JsonElement root)
        {
            try
            {
                string name = root.TryGetProperty("name", out var nv) ? nv.GetString() : "file";
                foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
                string b64 = root.TryGetProperty("b64", out var bv) ? bv.GetString() : "";
                if (string.IsNullOrEmpty(b64)) return;
                var dir = Path.Combine(Path.GetTempPath(), "MuseClipStation", "open");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, name);
                File.WriteAllBytes(path, Convert.FromBase64String(b64));
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }

        void ShowPin(JsonElement root)
        {
            try
            {
                string b64 = root.TryGetProperty("b64", out var bv) ? bv.GetString() : "";
                if (string.IsNullOrEmpty(b64)) return;
                var bytes = Convert.FromBase64String(b64);
                using (var ms = new MemoryStream(bytes))
                using (var bmp = new Bitmap(ms))
                {
                    var pin = new PinForm(new Bitmap(bmp));
                    pin.Show();
                }
            }
            catch { }
        }

        async Task DoOcr(string b64)
        {
            bool ok = false; string text = "";
            try
            {
                var bytes = Convert.FromBase64String(b64 ?? "");
                try
                {
                    using (var qms = new MemoryStream(bytes))
                    using (var qbmp = new Bitmap(qms))
                    {
                        var qrText = RegionForm.DecodeQr(qbmp);
                        if (!string.IsNullOrEmpty(qrText)) { Post(new { type = "ocrResult", ok = true, text = qrText, qr = true }); return; }
                    }
                }
                catch { }
                using (var ras = new InMemoryRandomAccessStream())
                {
                    using (var writer = new DataWriter(ras))
                    {
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync();
                        writer.DetachStream();
                    }
                    ras.Seek(0);
                    var decoder = await BitmapDecoder.CreateAsync(ras);
                    using (var sbmp = await decoder.GetSoftwareBitmapAsync())
                    {
                        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
                        if (engine != null)
                        {
                            var result = await engine.RecognizeAsync(sbmp);
                            text = result.Text ?? "";
                            ok = true;
                        }
                    }
                }
            }
            catch { ok = false; }
            Post(new { type = "ocrResult", ok, text });
        }

        void TryAffinity()
        {
            // 默认让本窗口不出现在屏幕捕获里：截图时无需隐藏窗口，画面不闪、也不挡图；
            // 用户在设置里打开「允许被截屏」后解除排除，方便用系统截图给面板拍照反馈。
            try { SetWindowDisplayAffinity(Handle, (allowCapture && !recExcl) ? 0u : 0x11u); } catch { }
        }

        void ShowDock()
        {
            TryAffinity();
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            ApplyRounded();
        }

        /* ---------------- 位置记忆（横/竖分开） ---------------- */
        void LoadPos()
        {
            try
            {
                if (File.Exists(posPath))
                    posMem = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(posPath)) ?? posMem;
            }
            catch { }
        }
        void SavePos()
        {
            try { File.WriteAllText(posPath, JsonSerializer.Serialize(posMem)); } catch { }
        }
        void DebounceSavePos()
        {
            if (posTimer == null)
            {
                posTimer = new Timer { Interval = 450 };
                posTimer.Tick += (s, e) =>
                {
                    posTimer.Stop();
                    if (inPeek || animating) return;
                    posMem[vertical ? "v" : "h6"] = new[] { Location.X, Location.Y, Size.Width };
                    SavePos();
                };
            }
            posTimer.Stop(); posTimer.Start();
        }
        int PosW(string key, int fallback)
        {
            int[] a; return posMem.TryGetValue(key, out a) && a.Length >= 3 ? a[2] : fallback;
        }
        Point StoredLoc(string key, Point fallback)
        {
            int[] a;
            if (posMem.TryGetValue(key, out a) && a.Length >= 2)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                int x = Math.Max(wa.Left - 40, Math.Min(a[0], wa.Right - 80));
                int y = Math.Max(wa.Top, Math.Min(a[1], wa.Bottom - 60));
                return new Point(x, y);
            }
            return fallback;
        }
        void ApplyLayout(bool v)
        {
            posMem[vertical ? "v" : "h6"] = new[] { Location.X, Location.Y, Size.Width };
            vertical = v;
            var wa = Screen.PrimaryScreen.WorkingArea;
            if (v)
            {
                Size = new Size(278, Math.Min(840, wa.Height - 16));
                Location = StoredLoc("v", new Point(10, 10));
            }
            else
            {
                Size = new Size(Math.Min(PosW("h6", 1600), wa.Width), 276);
                Location = StoredLoc("h6", DefaultLoc(false));
            }
            SavePos();
            ApplyRounded();
        }

        /* ---------------- WebView2 ---------------- */
        CoreWebView2Environment webEnv;

        async void InitWeb()
        {
            try
            {
                string htmlPath = Path.Combine(dataDir, "app.html");
                using (var rs = typeof(MainForm).Assembly.GetManifestResourceStream("app.html"))
                using (var fs = File.Create(htmlPath))
                    if (rs != null) rs.CopyTo(fs);

                webEnv = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "WebView2Data"));
                var env = webEnv;
                web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
                Controls.Add(web);
                await web.EnsureCoreWebView2Async(env);
                web.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", dataDir, CoreWebView2HostResourceAccessKind.Allow);
                web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.WebMessageReceived += OnWebMessage;
                web.CoreWebView2.NewWindowRequested += (s2, e2) => { e2.Handled = true; OpenExternal(e2.Uri); };
                web.CoreWebView2.NavigationStarting += (s2, e2) =>
                {
                    var u = e2.Uri ?? "";
                    if (!u.StartsWith("https://app.local/")) { e2.Cancel = true; OpenExternal(u); }
                };
                web.Source = new Uri("https://app.local/app.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.Message + "\n\n本应用需要 Microsoft Edge WebView2 运行时（Windows 11 已内置）。", "Clipa");
            }
        }

        internal void Post(object obj)
        {
            if (web == null || web.CoreWebView2 == null) return;
            web.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(obj));
        }

        internal void ApplyAutoStart(bool on)
        {
            SetAutoStart(on);
            if (trayAutoItem != null) trayAutoItem.Checked = on;
        }

        internal void PickSaveDir(string which)
        {
            string picked = null;
            try
            {
                using (var dlg = new FolderBrowserDialog { Description = "选择保存文件夹", UseDescriptionForTitle = true, ShowNewFolderButton = true })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) picked = dlg.SelectedPath;
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(picked))
            {
                Post(new { type = "saveDirPicked", path = picked, @for = which });
            }
        }

        internal static void OpenExternal(string uri)
        {
            try { if (!string.IsNullOrWhiteSpace(uri)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
        }

        async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); } catch { return; }
            if (string.IsNullOrEmpty(raw)) return;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); } catch { return; }
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
            switch (type)
            {
                case "getFonts":
                    try
                    {
                        var fams = new System.Drawing.Text.InstalledFontCollection().Families
                            .Select(f => f.Name).Where(n => !string.IsNullOrWhiteSpace(n))
                            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToArray();
                        Post(new { type = "fontList", fonts = fams });
                    }
                    catch { }
                    break;
                case "ready":
                    {
                        bool dark = root.TryGetProperty("dark", out var dd) && dd.GetBoolean();
                        SetBg(dark);
                        allowCapture = root.TryGetProperty("allowCap", out var ac) && ac.GetBoolean();
                        TryAffinity();
                        Post(new { type = "autoStartState", on = IsAutoStart() });
                        TopMost = !(root.TryGetProperty("pin", out var pn) && !pn.GetBoolean());
                        if (!readyApplied)
                        {
                            readyApplied = true;
                            var wa0 = Screen.PrimaryScreen.WorkingArea;
                            Width = Math.Min(PosW("h6", (int)Math.Round(1600 * (DeviceDpi / 96.0))), wa0.Width);
                            Location = StoredLoc("h6", DefaultLoc(false));
                        }
                        break;
                    }
                case "dragWindow":
                    StartDrag();
                    break;
                case "contentSize":
                    {
                        int hh = root.TryGetProperty("h", out var hv) ? hv.GetInt32() : 0;
                        bool force = root.TryGetProperty("force", out var fv) && fv.GetBoolean();
                        bool pk = root.TryGetProperty("peek", out var pv) && pv.GetBoolean();
                        int pwCss = root.TryGetProperty("pw", out var pwv) ? pwv.GetInt32() : 0;
                        int phCss = root.TryGetProperty("ph", out var phv) ? phv.GetInt32() : 0;
                        var wa2 = Screen.PrimaryScreen.WorkingArea;
                        double sc = DeviceDpi / 96.0;
                        if (animating) break;
                        if (pk)
                        {
                            int pw = pwCss > 0 ? (int)Math.Round(pwCss * sc) : (int)Math.Round(176 * sc);
                            int ph = phCss > 0 ? (int)Math.Round(phCss * sc) : Math.Max(26, (int)Math.Round(30 * sc));
                            peekPhysW = pw; peekPhysH = ph; peekScale = sc;
                            if (!inPeek)
                            {
                                inPeek = true; peekRect = Bounds;
                                int cx = peekRect.Left + peekRect.Width / 2;
                                int nx = Math.Max(wa2.Left, Math.Min(cx - pw / 2, wa2.Right - pw));
                                StartBoundsAnim(new Rectangle(nx, wa2.Top, pw, ph), true);
                            }
                            else if (Math.Abs(Width - pw) > 4 || Math.Abs(Height - ph) > 2)
                            {
                                int cx2 = Left + Width / 2;
                                int nx2 = Math.Max(wa2.Left, Math.Min(cx2 - pw / 2, wa2.Right - pw));
                                Bounds = new Rectangle(nx2, wa2.Top, pw, ph);
                                ApplyPeekRegion();
                                if (web != null && web.Dock != DockStyle.Fill) try { web.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height); } catch { }
                            }
                            break;
                        }
                        if (inPeek)
                        {
                            inPeek = false;
                            Region = null;
                            if (!peekRect.IsEmpty) { StartBoundsAnim(peekRect, false); break; }
                        }
                        if (force) autoH = true;
                        if (autoH && hh > 0)
                            Height = Math.Max(50, Math.Min((int)Math.Round(hh * sc), wa2.Height));
                        break;
                    }
                case "resizeWindow":
                    {
                        autoH = false;
                        var wa3 = Screen.PrimaryScreen.WorkingArea;
                        int w2 = root.TryGetProperty("w", out var wv2) ? wv2.GetInt32() : Width;
                        int h3 = root.TryGetProperty("h", out var hv3) ? hv3.GetInt32() : Height;
                        double sc3 = DeviceDpi / 96.0;
                        Size = new Size(Math.Max(420, Math.Min((int)Math.Round(w2 * sc3), wa3.Width)), Math.Max(50, Math.Min((int)Math.Round(h3 * sc3), wa3.Height)));
                        break;
                    }
                case "pin":
                    TopMost = root.TryGetProperty("on", out var on) && on.GetBoolean();
                    if (trayPinItem != null) trayPinItem.Checked = TopMost;
                    break;
                case "autoStart":
                    ApplyAutoStart(root.TryGetProperty("on", out var av2) && av2.GetBoolean());
                    break;
                case "captureAffinity":
                    allowCapture = root.TryGetProperty("on", out var ca) && ca.GetBoolean();
                    TryAffinity();
                    break;
                case "pickSaveDir":
                    PickSaveDir(root.TryGetProperty("for", out var _pf) ? _pf.GetString() : "shot");
                    break;
                case "saveCopy":
                    {
                        try
                        {
                            string dir = root.TryGetProperty("dir", out var dv) ? dv.GetString() : null;
                            string nm = root.TryGetProperty("name", out var nv) ? nv.GetString() : null;
                            string b6 = root.TryGetProperty("b64", out var bv) ? bv.GetString() : null;
                            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(b6))
                            {
                                Directory.CreateDirectory(dir);
                                string safe = string.IsNullOrEmpty(nm) ? "shot.png" : Path.GetFileName(nm);
                                File.WriteAllBytes(Path.Combine(dir, safe), Convert.FromBase64String(b6));
                            }
                        }
                        catch { }
                        break;
                    }
                case "hideWindow":
                    Hide();
                    break;
                case "themeBg":
                    if (root.TryGetProperty("color", out var tcol) && tcol.GetString() is string hex1 && hex1.Length > 3)
                    { try { BackColor = System.Drawing.ColorTranslator.FromHtml(hex1); if (web != null) web.DefaultBackgroundColor = BackColor; } catch { SetBg(root.TryGetProperty("dark", out var td) && td.GetBoolean()); } }
                    else SetBg(root.TryGetProperty("dark", out var td) && td.GetBoolean());
                    break;
                case "capture":
                    await DoCapture();
                    break;
                case "scanQr":
                    await ScanQrScreen();
                    break;
                case "pinImage":
                    ShowPin(root);
                    break;
                case "viewImage":
                    ShowViewImage(root);
                    break;
                case "openFile":
                    OpenFileNative(root);
                    break;
                case "startNativeDrag":
                    StartNativeDrag(root);
                    break;
                case "ocr":
                    await DoOcr(root.TryGetProperty("b64", out var ob) ? ob.GetString() : "");
                    break;
                case "pasteNative":
                    DoPasteNative();
                    break;
                case "copyFiles":
                    DoCopyFiles(root);
                    break;
                case "startRecord":
                    StartRecord(root);
                    break;
                case "stopRecord":
                    StopRecord();
                    break;
                case "copyPaths":
                    DoCopyPaths(root);
                    break;
                case "openPath":
                    OpenPath(root);
                    break;
                case "deleteRecCache":
                    DeleteRecCache(root);
                    break;
                case "setHotkey":
                    {
                        uint nm = root.TryGetProperty("mods", out var mv) ? (uint)mv.GetInt32() : 0;
                        int nv = root.TryGetProperty("vk", out var vv) ? vv.GetInt32() : 0;
                        bool okHk = true;
                        if (nv > 0 && !(nm == curHkMods && nv == curHkVk))
                        {
                            try { UnregisterHotKey(Handle, HOTKEY_ID2); } catch { }
                            okHk = RegisterHotKey(Handle, HOTKEY_ID2, nm, (uint)nv);
                            if (okHk) { curHkMods = nm; curHkVk = nv; }
                            else { try { RegisterHotKey(Handle, HOTKEY_ID2, curHkMods, (uint)curHkVk); } catch { } }
                        }
                        Post(new { type = "hotkeyResult", ok = okHk });
                        break;
                    }
                case "openUrl":
                    {
                        string u = root.TryGetProperty("url", out var uv) ? uv.GetString() : null;
                        if (!string.IsNullOrEmpty(u) && (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                        { try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } }
                        break;
                    }
                case "deleteLocalFiles":
                    {
                        int okN = 0, failN = 0;
                        try
                        {
                            if (root.TryGetProperty("paths", out var pv) && pv.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var el in pv.EnumerateArray())
                                {
                                    string fp = el.GetString();
                                    if (string.IsNullOrEmpty(fp)) continue;
                                    try
                                    {
                                        if (File.Exists(fp)) { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(fp, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); okN++; }
                                        else if (Directory.Exists(fp)) { Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(fp, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); okN++; }
                                        else failN++;
                                    }
                                    catch { failN++; }
                                }
                            }
                        }
                        catch { }
                        Post(new { type = "deleteLocalResult", ok = okN, failed = failN });
                        break;
                    }
            }
        }

        void SetBg(bool dark)
        {
            var c = dark ? Color.FromArgb(32, 32, 32) : Color.White;
            BackColor = c;
            if (web != null) web.DefaultBackgroundColor = c;
        }

        bool qrScanning;
        async Task ScanQrScreen()
        {
            if (qrScanning || capturing) return;
            qrScanning = true;
            try
            {
                await Task.Delay(80);
                var vb = SystemInformation.VirtualScreen;
                var raw = new Bitmap(vb.Width, vb.Height);
                using (var g = Graphics.FromImage(raw)) g.CopyFromScreen(vb.Left, vb.Top, 0, 0, vb.Size);
                var probe = (Bitmap)raw.Clone();
                var codes = await Task.Run(() => RegionForm.DecodeAllQr(probe));
                probe.Dispose();
                if (codes == null || codes.Count == 0)
                {
                    raw.Dispose();
                    Post(new { type = "qrScanResult", count = 0 });
                    return;
                }
                Post(new { type = "qrScanResult", count = codes.Count });
                var form = new QrScanForm(raw, vb, codes, txt => { try { Post(new { type = "qrPicked", text = txt }); } catch { } });
                form.Show();
                form.Activate();
            }
            catch { try { Post(new { type = "qrScanResult", count = 0 }); } catch { } }
            finally { qrScanning = false; }
        }

        bool capturing;
        async Task DoCapture()
        {
            if (capturing) return;
            capturing = true;
            try
            {
                await Task.Delay(80);
                var bounds = SystemInformation.VirtualScreen;
                Bitmap finalBmp = null;
                string rfQr = null;
                using (var bmp = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
                    using (var rf = new RegionForm(bmp, bounds))
                    {
                        if (rf.ShowDialog(this) == DialogResult.OK) { finalBmp = rf.ResultBitmap; rfQr = rf.ResultQrText; }
                    }
                }
                if (finalBmp == null) return;
                using (finalBmp)
                using (var ms = new MemoryStream())
                {
                    finalBmp.Save(ms, ImageFormat.Png);
                    Post(new { type = "captured", b64 = Convert.ToBase64String(ms.ToArray()) });
                    if (!string.IsNullOrEmpty(rfQr)) Post(new { type = "qrResult", text = rfQr });
                }
            }
            catch { }
            finally { capturing = false; }
        }

        static string MimeOf(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".webp": return "image/webp";
                case ".txt": return "text/plain";
                case ".pdf": return "application/pdf";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                case ".zip": return "application/zip";
                case ".mp3": return "audio/mpeg";
                case ".mp4": return "video/mp4";
                default: return "application/octet-stream";
            }
        }

        void DoPasteNative()
        {
            var files = new List<object>();
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    foreach (string path in Clipboard.GetFileDropList())
                    {
                        FileInfo fi;
                        try { fi = new FileInfo(path); } catch { continue; }
                        if (!fi.Exists) continue;
                        string mime = MimeOf(fi.Extension);
                        if (fi.Length <= 25L * 1024 * 1024)
                            files.Add(new { name = fi.Name, mime, size = fi.Length, path, b64 = Convert.ToBase64String(File.ReadAllBytes(path)) });
                        else
                            files.Add(new { name = fi.Name, mime, size = fi.Length, path, b64 = (string)null });
                    }
                }
                else if (Clipboard.ContainsImage())
                {
                    using (var img = Clipboard.GetImage())
                    using (var ms = new MemoryStream())
                    {
                        if (img != null)
                        {
                            img.Save(ms, ImageFormat.Png);
                            files.Add(new { name = "粘贴图片 " + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png", mime = "image/png", size = ms.Length, b64 = Convert.ToBase64String(ms.ToArray()) });
                        }
                    }
                }
            }
            catch { }
            if (files.Count > 0) Post(new { type = "nativeFiles", files });
        }

        void DoCopyFiles(JsonElement root)
        {
            int count = 0;
            try
            {
                string outDir = Path.Combine(Path.GetTempPath(), "MuseClipStation", "out");
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                Directory.CreateDirectory(outDir);
                var col = new StringCollection();
                if (root.TryGetProperty("files", out var arr))
                {
                    foreach (var f in arr.EnumerateArray())
                    {
                        string name = Path.GetFileName(f.TryGetProperty("name", out var n) ? n.GetString() ?? "file" : "file");
                        string b64 = f.TryGetProperty("b64", out var b) ? b.GetString() : null;
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(b64)) continue;
                        string dest = Path.Combine(outDir, name);
                        File.WriteAllBytes(dest, Convert.FromBase64String(b64));
                        col.Add(dest);
                        count++;
                    }
                }
                if (count > 0) Clipboard.SetFileDropList(col);
                Post(new { type = "copyFilesResult", ok = count > 0, count });
            }
            catch
            {
                Post(new { type = "copyFilesResult", ok = false, count = 0 });
            }
        }
    }

    // 贴图小窗：置顶、无边框、可拖动、滚轮缩放、双击/Esc 关闭，自身不进屏幕捕获
    sealed class PinForm : Form
    {
        readonly PictureBox pic;
        Point dragOff;
        bool dragging;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        public PinForm(Bitmap bmp)
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 32, 32);
            int w = Math.Min(520, Math.Max(160, bmp.Width));
            int h = (int)Math.Round(w * (double)bmp.Height / Math.Max(1, bmp.Width));
            Size = new Size(w, Math.Max(60, h));
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + 120, wa.Top + 90);
            pic = new PictureBox { Dock = DockStyle.Fill, Image = bmp, SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.SizeAll };
            Controls.Add(pic);
            KeyPreview = true;
            bool pinDragOut = false;
            pic.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragging = true; pinDragOut = false; dragOff = new Point(e.X, e.Y); } };
            pic.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                if (!pinDragOut && (e.X < -28 || e.Y < -28 || e.X > Width + 28 || e.Y > Height + 28))
                {
                    pinDragOut = true; dragging = false;
                    try
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), "MusePin-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
                        bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                        var data = new DataObject();
                        var files = new System.Collections.Specialized.StringCollection(); files.Add(tmp);
                        data.SetFileDropList(files);
                        using (var ms2 = new MemoryStream()) { bmp.Save(ms2, System.Drawing.Imaging.ImageFormat.Png); data.SetData("PNG", false, ms2.ToArray()); }
                        DoDragDrop(data, DragDropEffects.Copy);
                    }
                    catch { }
                    return;
                }
                Location = new Point(Location.X + e.X - dragOff.X, Location.Y + e.Y - dragOff.Y);
            };
            pic.MouseUp += (s, e) => { dragging = false; };
            pic.MouseDoubleClick += (s, e) => Close();
            pic.MouseWheel += (s, e) =>
            {
                double f = e.Delta > 0 ? 1.12 : 0.89;
                int nw = Math.Max(90, Math.Min(1400, (int)Math.Round(Width * f)));
                int nh = Math.Max(50, (int)Math.Round(nw * (double)bmp.Height / Math.Max(1, bmp.Width)));
                Size = new Size(nw, nh);
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("关闭贴图", null, (s, e) => Close());
            pic.ContextMenuStrip = menu;
            Shown += (s, e) =>
            {
                try { int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, sizeof(int)); } catch { }
                try { SetWindowDisplayAffinity(Handle, 0x11); } catch { }
            };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }
    }

    // 大图查看窗：置顶、无边框、居中打开；拖动移动、滚轮缩放（保比例改窗）、双击切换实际大小、Esc 关闭
    sealed class ViewForm : Form
    {
        readonly PictureBox pic;
        readonly Bitmap bmp;
        Point dragOff;
        bool dragging;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        public ViewForm(Bitmap b)
        {
            bmp = b;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 24, 24);
            var wa0 = Screen.PrimaryScreen.WorkingArea;
            double fit = Math.Min(wa0.Width * 0.88 / Math.Max(1, b.Width), wa0.Height * 0.88 / Math.Max(1, b.Height));
            if (fit > 1) fit = 1;
            Size = new Size(Math.Max(240, (int)Math.Round(b.Width * fit)), Math.Max(160, (int)Math.Round(b.Height * fit)));
            pic = new PictureBox { Dock = DockStyle.Fill, Image = b, SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.SizeAll, BackColor = Color.FromArgb(24, 24, 24) };
            Controls.Add(pic);
            var closeBtn = new Button { Text = "✕", Width = 34, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(52, 52, 52), ForeColor = Color.White, Cursor = Cursors.Hand, TabStop = false };
            closeBtn.FlatAppearance.BorderSize = 0;
            closeBtn.Location = new Point(Width - 42, 8);
            closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            closeBtn.Click += (s, e) => Close();
            Controls.Add(closeBtn);
            closeBtn.BringToFront();
            KeyPreview = true;
            pic.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragOff = new Point(e.X, e.Y); } };
            pic.MouseMove += (s, e) => { if (dragging) Location = new Point(Location.X + e.X - dragOff.X, Location.Y + e.Y - dragOff.Y); };
            pic.MouseUp += (s, e) => { dragging = false; };
            pic.MouseDoubleClick += (s, e) => ToggleActual();
            pic.MouseWheel += (s, e) => ResizeKeepAspect(e.Delta > 0 ? 1.14 : 0.88);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Shown += (s, e) =>
            {
                try { int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, sizeof(int)); } catch { }
                try { SetWindowDisplayAffinity(Handle, 0x11); } catch { }
            };
        }

        void ResizeKeepAspect(double f)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int nw = Math.Max(160, Math.Min((int)(wa.Width * 0.97), (int)Math.Round(Width * f)));
            int nh = Math.Max(100, (int)Math.Round(nw * (double)bmp.Height / Math.Max(1, bmp.Width)));
            if (nh > wa.Height * 0.97) { nh = (int)(wa.Height * 0.97); nw = Math.Max(160, (int)Math.Round(nh * (double)bmp.Width / Math.Max(1, bmp.Height))); }
            int cx = Left + Width / 2, cy = Top + Height / 2;
            Size = new Size(nw, nh);
            Location = new Point(Math.Max(wa.Left - nw + 60, Math.Min(cx - nw / 2, wa.Right - 60)), Math.Max(wa.Top, Math.Min(cy - nh / 2, wa.Bottom - 40)));
        }

        void ToggleActual()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            bool isActual = Math.Abs(Width - bmp.Width) < 4 && Math.Abs(Height - bmp.Height) < 4;
            int nw, nh;
            if (isActual)
            {
                double fit = Math.Min(wa.Width * 0.88 / Math.Max(1, bmp.Width), wa.Height * 0.88 / Math.Max(1, bmp.Height));
                if (fit > 1) fit = 1;
                nw = Math.Max(240, (int)Math.Round(bmp.Width * fit));
                nh = Math.Max(160, (int)Math.Round(bmp.Height * fit));
            }
            else
            {
                nw = Math.Min(bmp.Width, (int)(wa.Width * 0.97));
                nh = Math.Min(bmp.Height, (int)(wa.Height * 0.97));
            }
            int cx = Left + Width / 2, cy = Top + Height / 2;
            Size = new Size(nw, nh);
            Location = new Point(cx - nw / 2, cy - nh / 2);
        }
    }
    // Full-screen capture overlay, WeChat-style:
    // hovering auto-detects the window under the cursor (click = take it), dragging draws a custom region.
    // Once a region is set it can be moved (long-press inside) and resized (8 round handles), annotated
    // inline (pen / arrow / rect / mosaic / text) and QR-scanned — the finished image is what gets saved.
    sealed class QrScanForm : Form
    {
        readonly Bitmap shot;
        readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Rectangle>> codes;
        readonly Action<string> onPick;
        int hover = -1;

        public QrScanForm(Bitmap shot, Rectangle bounds, System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Rectangle>> codes, Action<string> onPick)
        {
            this.shot = shot; this.codes = codes; this.onPick = onPick;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true; ShowInTaskbar = false; KeyPreview = true; DoubleBuffered = true;
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImage(shot, 0, 0, shot.Width, shot.Height);
            using (var dim = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillRectangle(dim, ClientRectangle);
            var acc = Color.FromArgb(41, 121, 255);
            for (int i = 0; i < codes.Count; i++)
            {
                var r = codes[i].Value;
                using (var fill = new SolidBrush(Color.FromArgb(i == hover ? 80 : 42, acc))) g.FillRectangle(fill, r);
                using (var pen = new Pen(acc, i == hover ? 4 : 3)) g.DrawRectangle(pen, r);
                int bs = 26;
                var badge = new Rectangle(r.Left, Math.Max(0, r.Top - bs - 4), bs, bs);
                using (var bp = RoundRect(badge, 13))
                using (var bf = new SolidBrush(acc)) g.FillPath(bf, bp);
                using (var f = new Font("Segoe UI", 12, FontStyle.Bold))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString((i + 1).ToString(), f, Brushes.White, badge, sf);
                }
            }
            string hint = "点击二维码打开 · Click a QR code to open · Esc 取消";
            using (var f = new Font("Microsoft YaHei UI", 11))
            {
                var sz = g.MeasureString(hint, f);
                int pw = (int)sz.Width + 36, ph = 34;
                var pill = new Rectangle((ClientSize.Width - pw) / 2, ClientSize.Height - ph - 42, pw, ph);
                using (var pp = RoundRect(pill, 17))
                using (var pb = new SolidBrush(Color.FromArgb(165, 20, 20, 22))) g.FillPath(pb, pp);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(hint, f, Brushes.White, pill, sf);
            }
        }

        int Hit(Point p)
        {
            for (int i = 0; i < codes.Count; i++) if (codes[i].Value.Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = Hit(e.Location);
            if (hit != hover) { hover = hit; Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int hit = Hit(e.Location);
            string t = hit >= 0 ? codes[hit].Key : null;
            Close();
            if (t != null) { try { onPick?.Invoke(t); } catch { } }
            base.OnMouseClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { shot?.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }

    sealed class RegionForm : Form
    {
        readonly Bitmap shot;
        readonly Rectangle vScreen;
        Rectangle sel;
        Rectangle hoverRect;
        Point startPt;
        int state; // 0 hover, 1 drawing new selection, 2 selected
        readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Rectangle>> tbBtns = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Rectangle>>();
        public Bitmap ResultBitmap;
        public string ResultQrText;
        public bool SelectOnly;
        public Rectangle ResultScreenRect;

        // annotation state (ops are stored in IMAGE coordinates)
        sealed class AnnOp
        {
            public string Kind;
            public System.Collections.Generic.List<Point> Pts = new System.Collections.Generic.List<Point>();
            public Rectangle R;
            public string Text;
            public Color Col;
            public int W;
        }
        readonly System.Collections.Generic.List<AnnOp> ops = new System.Collections.Generic.List<AnnOp>();
        AnnOp tempOp;
        string tool = "";
        Color curColor = Color.FromArgb(230, 40, 30);
        static readonly Color[] Palette = { Color.FromArgb(230, 40, 30), Color.FromArgb(255, 140, 0), Color.FromArgb(30, 170, 60), Color.FromArgb(0, 120, 215), Color.FromArgb(20, 20, 20) };
        int dragMode; // 0 none, 1 move, 2 resize, 3 draw
        int resizeIdx = -1;
        Rectangle origSel;
        Point moveOff;
        TextBox tb;
        Point textPosImg;
        string statusText;
        System.Windows.Forms.Timer statusTimer;

        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int val);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT rc);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT rc, int size);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        public RegionForm(Bitmap shot, Rectangle vScreen)
        {
            this.shot = shot; this.vScreen = vScreen;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vScreen;
            TopMost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
            tb = new TextBox { Visible = false, Font = new Font("Microsoft YaHei UI", 13f), BorderStyle = BorderStyle.FixedSingle, Width = 280 };
            tb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitText(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; tb.Text = ""; tb.Visible = false; }
            };
            tb.Leave += (s, e) => { if (tb.Visible) CommitText(); };
            Controls.Add(tb);
            statusTimer = new System.Windows.Forms.Timer { Interval = 2300 };
            statusTimer.Tick += (s, e) => { statusTimer.Stop(); statusText = null; Invalidate(); };
        }

        double KX { get { return shot.Width / (double)Math.Max(1, Width); } }
        double KY { get { return shot.Height / (double)Math.Max(1, Height); } }
        int U { get { return Math.Max(1, (int)Math.Round(DeviceDpi / 96.0)); } }
        Point ImgPt(Point c) { return new Point((int)Math.Round(c.X * KX), (int)Math.Round(c.Y * KY)); }
        int PenWImg { get { return Math.Max(2, (int)Math.Round(3.2 * KX)); } }

        Rectangle Scaled(Rectangle r)
        {
            return Rectangle.FromLTRB((int)Math.Round(r.Left * KX), (int)Math.Round(r.Top * KY), (int)Math.Round(r.Right * KX), (int)Math.Round(r.Bottom * KY));
        }

        private sealed class GrayLum : ZXing.LuminanceSource
        {
            private readonly byte[] data;
            internal GrayLum(byte[] d, int w, int h) : base(w, h) { data = d; }
            public override byte[] getRow(int y, byte[] row)
            {
                if (row == null || row.Length < Width) row = new byte[Width];
                System.Array.Copy(data, y * Width, row, 0, Width);
                return row;
            }
            public override byte[] Matrix { get { return data; } }
        }

        private static byte[] QrGray(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride; int astep = System.Math.Abs(stride);
                var row = new byte[astep];
                for (int y = 0; y < h; y++)
                {
                    System.IntPtr src = bd.Scan0 + (stride >= 0 ? y * stride : (h - 1 - y) * astep);
                    System.Runtime.InteropServices.Marshal.Copy(src, row, 0, astep);
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        gray[o + x] = (byte)((row[i + 2] * 299 + row[i + 1] * 587 + row[i] * 114) / 1000);
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
            return gray;
        }

        private static readonly byte[] HiLut = BuildHiLut();
        private static byte[] BuildHiLut()
        {
            var lut = new byte[256];
            for (int v = 0; v < 256; v++) lut[v] = (byte)(v > 175 ? System.Math.Min(255, (v - 175) * 255 / 80) : v);
            return lut;
        }
        private static byte[] HiExpand(byte[] gray)
        {
            var o = new byte[gray.Length];
            for (int i = 0; i < gray.Length; i++) o[i] = HiLut[gray[i]];
            return o;
        }

        private static void QrAddHit(System.Collections.Generic.Dictionary<string, Rectangle> found, ZXing.Result r, int imgW, int imgH, double scale)
        {
            if (r == null || string.IsNullOrEmpty(r.Text) || found.ContainsKey(r.Text)) return;
            var pts = r.ResultPoints;
            if (pts == null || pts.Length == 0) return;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = 0, y1 = 0;
            foreach (var p in pts) { if (p.X < x0) x0 = p.X; if (p.Y < y0) y0 = p.Y; if (p.X > x1) x1 = p.X; if (p.Y > y1) y1 = p.Y; }
            double gx0 = x0 / scale, gy0 = y0 / scale, gx1 = x1 / scale, gy1 = y1 / scale;
            int pad = System.Math.Max(6, (int)((gx1 - gx0) * 0.22));
            var rect = Rectangle.FromLTRB(System.Math.Max(0, (int)gx0 - pad), System.Math.Max(0, (int)gy0 - pad), System.Math.Min(imgW, (int)gx1 + pad), System.Math.Min(imgH, (int)gy1 + pad));
            if (rect.Width > 4 && rect.Height > 4) found[r.Text] = rect;
        }

        // Decode pass over one gray image. Order matters (measured on real-world artistic codes):
        // highlight-expanded source rescues pale gradient/dot codes on white cards; GlobalHistogram
        // beats Hybrid on dot-matrix codes; the plain QRCodeReader finds codes the multi reader misses.
        private static void QrPass(System.Collections.Generic.Dictionary<string, Rectangle> found, byte[] gray, int w, int h, double scale, int imgW, int imgH, bool hiFirst, bool useMulti, bool stopAtFirst)
        {
            var hints = new System.Collections.Generic.Dictionary<ZXing.DecodeHintType, object>
            {
                { ZXing.DecodeHintType.POSSIBLE_FORMATS, new System.Collections.Generic.List<ZXing.BarcodeFormat> { ZXing.BarcodeFormat.QR_CODE } },
                { ZXing.DecodeHintType.TRY_HARDER, true }
            };
            var multi = useMulti ? new ZXing.Multi.GenericMultipleBarcodeReader(new ZXing.MultiFormatReader()) : null;
            foreach (bool hi in hiFirst ? new[] { true, false } : new[] { false, true })
            {
                byte[] srcBytes = hi ? HiExpand(gray) : gray;
                foreach (bool glb in new[] { true, false })
                {
                    foreach (bool inv in new[] { false, true })
                    {
                        if (stopAtFirst && found.Count > 0) return;
                        if (found.Count >= 6) return;
                        ZXing.LuminanceSource src = new GrayLum(srcBytes, w, h);
                        if (inv) src = new ZXing.InvertedLuminanceSource(src);
                        ZXing.Binarizer bin = glb ? (ZXing.Binarizer)new ZXing.Common.GlobalHistogramBinarizer(src) : new ZXing.Common.HybridBinarizer(src);
                        var bb = new ZXing.BinaryBitmap(bin);
                        try { QrAddHit(found, new ZXing.QrCode.QRCodeReader().decode(bb), imgW, imgH, scale); } catch { }
                        if (useMulti && glb && !inv)
                        {
                            try { var rs = multi.decodeMultiple(bb, hints); if (rs != null) foreach (var r in rs) QrAddHit(found, r, imgW, imgH, scale); } catch { }
                        }
                    }
                }
            }
        }

        private static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Rectangle>> DecodeQrCore(Bitmap bmp, bool wantMulti)
        {
            var found = new System.Collections.Generic.Dictionary<string, Rectangle>();
            try
            {
                int W = bmp.Width, H = bmp.Height;
                if (W >= 8 && H >= 8)
                {
                    var variants = new System.Collections.Generic.List<System.Tuple<Bitmap, double>>();
                    variants.Add(System.Tuple.Create(bmp, 1.0));
                    Bitmap halfBmp = null, dblBmp = null;
                    if (System.Math.Min(W, H) >= 900) { halfBmp = new Bitmap(bmp, new Size(W / 2, H / 2)); variants.Add(System.Tuple.Create(halfBmp, 0.5)); }
                    if (System.Math.Max(W, H) <= 700) { dblBmp = new Bitmap(bmp, new Size(W * 2, H * 2)); variants.Add(System.Tuple.Create(dblBmp, 2.0)); }
                    try
                    {
                        bool first = true;
                        foreach (var variant in variants)
                        {
                            if (!wantMulti && found.Count > 0) break;
                            var gray = QrGray(variant.Item1);
                            QrPass(found, gray, variant.Item1.Width, variant.Item1.Height, variant.Item2, W, H, hiFirst: wantMulti || !first, useMulti: wantMulti, stopAtFirst: !wantMulti);
                            first = false;
                        }
                    }
                    finally
                    {
                        if (halfBmp != null) halfBmp.Dispose();
                        if (dblBmp != null) dblBmp.Dispose();
                    }
                }
            }
            catch { }
            return new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Rectangle>>(found);
        }

        internal static string DecodeQr(Bitmap bmp)
        {
            var all = DecodeQrCore(bmp, false);
            return all.Count > 0 ? all[0].Key : null;
        }

        internal static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Rectangle>> DecodeAllQr(Bitmap bmp)
        {
            return DecodeQrCore(bmp, true);
        }

        Rectangle DetectWindow(Point screenPt)
        {
            try
            {
                int ex = GetWindowLong(Handle, -20);
                SetWindowLong(Handle, -20, ex | 0x20); // WS_EX_TRANSPARENT: WindowFromPoint sees through this overlay
                IntPtr hwnd = WindowFromPoint(new POINT(screenPt.X, screenPt.Y));
                SetWindowLong(Handle, -20, ex);
                if (hwnd == IntPtr.Zero) return Rectangle.Empty;
                hwnd = GetAncestor(hwnd, 2); // GA_ROOT
                if (hwnd == IntPtr.Zero || hwnd == Handle) return Rectangle.Empty;
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return Rectangle.Empty;
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return Rectangle.Empty;
                RECT rc;
                if (DwmGetWindowAttribute(hwnd, 9, out rc, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
                    if (!GetWindowRect(hwnd, out rc)) return Rectangle.Empty;
                var r = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
                if (r.Width < 24 || r.Height < 24) return Rectangle.Empty;
                if (r.Width >= vScreen.Width && r.Height >= vScreen.Height) return Rectangle.Empty; // the desktop itself
                r.Offset(-vScreen.Left, -vScreen.Top);
                return r;
            }
            catch { return Rectangle.Empty; }
        }

        Point[] Handles()
        {
            int cx = sel.Left + sel.Width / 2, cy = sel.Top + sel.Height / 2;
            return new[]
            {
                new Point(sel.Left, sel.Top), new Point(cx, sel.Top), new Point(sel.Right, sel.Top), new Point(sel.Right, cy),
                new Point(sel.Right, sel.Bottom), new Point(cx, sel.Bottom), new Point(sel.Left, sel.Bottom), new Point(sel.Left, cy)
            };
        }

        int HandleAt(Point pt)
        {
            if (state != 2) return -1;
            var hs = Handles();
            int tol = 12 * U;
            for (int i = 0; i < hs.Length; i++)
                if (Math.Abs(pt.X - hs[i].X) <= tol && Math.Abs(pt.Y - hs[i].Y) <= tol) return i;
            return -1;
        }

        string TbAt(Point pt)
        {
            foreach (var kv in tbBtns) if (kv.Value.Contains(pt)) return kv.Key;
            return null;
        }

        void SetStatus(string text)
        {
            statusText = text;
            statusTimer.Stop(); statusTimer.Start();
            Invalidate();
        }

        void CommitText()
        {
            if (!tb.Visible) return;
            string t = tb.Text.Trim();
            tb.Visible = false;
            if (t.Length > 0)
            {
                int fh = (int)Math.Max(16, Math.Min(120, Scaled(sel).Height * 0.06));
                ops.Add(new AnnOp { Kind = "text", Text = t, Col = curColor, W = fh, Pts = { textPosImg } });
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (state == 1)
            {
                sel = Norm(startPt, e.Location);
                Invalidate();
                return;
            }
            if (state == 0)
            {
                Cursor = Cursors.Cross;
                var r = DetectWindow(PointToScreen(e.Location));
                if (r != hoverRect) { hoverRect = r; Invalidate(); }
                return;
            }
            // state 2
            if (dragMode == 1) // move selection
            {
                int nx = Math.Max(0, Math.Min(e.Location.X - moveOff.X, Width - sel.Width));
                int ny = Math.Max(0, Math.Min(e.Location.Y - moveOff.Y, Height - sel.Height));
                sel.Location = new Point(nx, ny);
                Invalidate();
            }
            else if (dragMode == 2) // resize
            {
                int dx = e.Location.X - startPt.X, dy = e.Location.Y - startPt.Y;
                int l = origSel.Left, t = origSel.Top, rr = origSel.Right, b = origSel.Bottom;
                bool eL = resizeIdx == 0 || resizeIdx == 6 || resizeIdx == 7;
                bool eR = resizeIdx == 2 || resizeIdx == 3 || resizeIdx == 4;
                bool eT = resizeIdx == 0 || resizeIdx == 1 || resizeIdx == 2;
                bool eB = resizeIdx == 4 || resizeIdx == 5 || resizeIdx == 6;
                if (eL) l = Math.Max(0, Math.Min(origSel.Left + dx, rr - 24));
                if (eR) rr = Math.Max(l + 24, Math.Min(origSel.Right + dx, Width));
                if (eT) t = Math.Max(0, Math.Min(origSel.Top + dy, b - 24));
                if (eB) b = Math.Max(t + 24, Math.Min(origSel.Bottom + dy, Height));
                sel = Rectangle.FromLTRB(l, t, rr, b);
                Invalidate();
            }
            else if (dragMode == 3 && tempOp != null) // draw
            {
                var ip = ImgPt(e.Location);
                if (tempOp.Kind == "pen")
                {
                    var last = tempOp.Pts[tempOp.Pts.Count - 1];
                    if (Math.Abs(ip.X - last.X) + Math.Abs(ip.Y - last.Y) >= 2) tempOp.Pts.Add(ip);
                }
                else if (tempOp.Kind == "arrow") tempOp.Pts[1] = ip;
                else tempOp.R = NormImg(tempOp.Pts[0], ip);
                Invalidate();
            }
            else
            {
                int hi = HandleAt(e.Location);
                if (hi >= 0) Cursor = (hi == 0 || hi == 4) ? Cursors.SizeNWSE : (hi == 2 || hi == 6) ? Cursors.SizeNESW : (hi == 1 || hi == 5) ? Cursors.SizeNS : Cursors.SizeWE;
                else if (TbAt(e.Location) != null) Cursor = Cursors.Hand;
                else if (sel.Contains(e.Location)) Cursor = tool == "" ? Cursors.SizeAll : Cursors.Cross;
                else Cursor = Cursors.Cross;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (tb.Visible) { tb.Text = ""; tb.Visible = false; return; }
                if (state == 2) { state = 0; sel = Rectangle.Empty; ops.Clear(); tempOp = null; tool = ""; Invalidate(); }
                else { DialogResult = DialogResult.Cancel; Close(); }
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (tb.Visible) { CommitText(); return; }
            if (state == 2)
            {
                string hit = TbAt(e.Location);
                if (hit != null) { ToolbarAction(hit); return; }
                int hi = HandleAt(e.Location);
                if (hi >= 0) { dragMode = 2; resizeIdx = hi; origSel = sel; startPt = e.Location; return; }
                if (sel.Contains(e.Location))
                {
                    if (tool == "text")
                    {
                        textPosImg = ImgPt(e.Location);
                        tb.Text = ""; tb.ForeColor = curColor;
                        tb.Location = new Point(Math.Min(e.Location.X, Width - tb.Width - 4), Math.Max(0, e.Location.Y - 6));
                        tb.Visible = true; tb.Focus();
                        return;
                    }
                    if (tool != "")
                    {
                        dragMode = 3;
                        var ip = ImgPt(e.Location);
                        tempOp = new AnnOp { Kind = tool, Col = curColor, W = PenWImg, Pts = { ip, ip }, R = new Rectangle(ip.X, ip.Y, 0, 0) };
                        if (tool == "pen") tempOp.Pts.RemoveAt(1);
                        return;
                    }
                    dragMode = 1; moveOff = new Point(e.Location.X - sel.Left, e.Location.Y - sel.Top);
                    return;
                }
            }
            // start a brand-new selection
            state = 1; startPt = e.Location; sel = Rectangle.Empty;
            ops.Clear(); tempOp = null; tool = ""; dragMode = 0;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (state == 1)
            {
                if (sel.Width < 8 || sel.Height < 8)
                {
                    if (!hoverRect.IsEmpty) { sel = hoverRect; state = 2; Invalidate(); return; }
                    AcceptFull();
                    return;
                }
                state = 2;
                Invalidate();
                return;
            }
            if (dragMode == 3 && tempOp != null)
            {
                bool valid = tempOp.Kind == "pen" ? tempOp.Pts.Count >= 2
                    : tempOp.Kind == "arrow" ? tempOp.Pts.Count >= 2 && (Math.Abs(tempOp.Pts[1].X - tempOp.Pts[0].X) + Math.Abs(tempOp.Pts[1].Y - tempOp.Pts[0].Y) >= 6)
                    : tempOp.R.Width >= 6 && tempOp.R.Height >= 6;
                if (valid) ops.Add(tempOp);
                tempOp = null;
                Invalidate();
            }
            dragMode = 0;
        }

        void ToolbarAction(string id)
        {
            if (id.StartsWith("tool:"))
            {
                string t = id.Substring(5);
                tool = tool == t ? "" : t;
                Invalidate();
            }
            else if (id.StartsWith("color:"))
            {
                curColor = Palette[int.Parse(id.Substring(6))];
                Invalidate();
            }
            else if (id == "undo") { if (ops.Count > 0) ops.RemoveAt(ops.Count - 1); Invalidate(); }
            else if (id == "cancel") { DialogResult = DialogResult.Cancel; Close(); }
            else if (id == "ok") Accept(false);
            else if (id == "qr")
            {
                var src = Scaled(sel);
                using (var crop = new Bitmap(Math.Max(1, src.Width), Math.Max(1, src.Height)))
                {
                    using (var g = Graphics.FromImage(crop)) g.DrawImage(shot, new Rectangle(0, 0, crop.Width, crop.Height), src, GraphicsUnit.Pixel);
                    string txt = DecodeQr(crop);
                    if (!string.IsNullOrEmpty(txt)) { ResultQrText = txt; Accept(false); }
                    else SetStatus("未识别到二维码");
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (tb.Visible) return;
            if (e.KeyCode == Keys.Escape)
            {
                if (state == 2) { state = 0; sel = Rectangle.Empty; ops.Clear(); tempOp = null; tool = ""; Invalidate(); }
                else { DialogResult = DialogResult.Cancel; Close(); }
            }
            else if (e.KeyCode == Keys.Enter)
            {
                if (state == 2) Accept(false);
                else if (!hoverRect.IsEmpty) { sel = hoverRect; state = 2; Invalidate(); }
                else AcceptFull();
            }
            else if (e.Control && e.KeyCode == Keys.Z) { if (ops.Count > 0) { ops.RemoveAt(ops.Count - 1); Invalidate(); } }
        }

        void Accept(bool _)
        {
            if (SelectOnly)
            {
                var s2 = Scaled(sel);
                ResultScreenRect = new Rectangle(vScreen.Left + s2.Left, vScreen.Top + s2.Top, Math.Max(1, s2.Width), Math.Max(1, s2.Height));
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            var src = Scaled(sel);
            ResultBitmap = new Bitmap(Math.Max(1, src.Width), Math.Max(1, src.Height));
            using (var g = Graphics.FromImage(ResultBitmap))
            {
                g.DrawImage(shot, new Rectangle(0, 0, ResultBitmap.Width, ResultBitmap.Height), src, GraphicsUnit.Pixel);
                if (ops.Count > 0)
                {
                    g.SetClip(src);
                    g.TranslateTransform(-src.Left, -src.Top);
                    DrawOps(g, false);
                    g.ResetTransform();
                }
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        void AcceptFull()
        {
            if (SelectOnly) { ResultScreenRect = vScreen; DialogResult = DialogResult.OK; Close(); return; }
            ResultBitmap = new Bitmap(shot);
            DialogResult = DialogResult.OK;
            Close();
        }

        static Rectangle Norm(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }
        static Rectangle NormImg(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        void DrawOps(Graphics g, bool preview)
        {
            var all = new System.Collections.Generic.List<AnnOp>(ops);
            if (tempOp != null) all.Add(tempOp);
            foreach (var op in all)
            {
                if (op.Kind == "pen" && op.Pts.Count >= 2)
                {
                    using (var pen = new Pen(op.Col, op.W) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(pen, op.Pts.ToArray());
                }
                else if (op.Kind == "arrow" && op.Pts.Count >= 2)
                {
                    var a = op.Pts[0]; var b = op.Pts[1];
                    using (var pen = new Pen(op.Col, op.W) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(pen, a, b);
                        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
                        double dist = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                        double hl = Math.Min(op.W * 5.0, dist * 0.36);
                        var h1 = new PointF((float)(b.X - hl * Math.Cos(ang + 0.42)), (float)(b.Y - hl * Math.Sin(ang + 0.42)));
                        var h2 = new PointF((float)(b.X - hl * Math.Cos(ang - 0.42)), (float)(b.Y - hl * Math.Sin(ang - 0.42)));
                        g.DrawLine(pen, b, h1);
                        g.DrawLine(pen, b, h2);
                    }
                }
                else if (op.Kind == "rect")
                {
                    using (var pen = new Pen(op.Col, op.W)) g.DrawRectangle(pen, op.R);
                }
                else if (op.Kind == "mosaic")
                {
                    if (op.R.Width >= 4 && op.R.Height >= 4)
                    {
                        int sw = Math.Max(1, op.R.Width / 14), sh = Math.Max(1, op.R.Height / 14);
                        using (var small = new Bitmap(sw, sh))
                        {
                            using (var gg = Graphics.FromImage(small)) gg.DrawImage(shot, new Rectangle(0, 0, sw, sh), op.R, GraphicsUnit.Pixel);
                            var old = g.InterpolationMode;
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.DrawImage(small, op.R);
                            g.InterpolationMode = old;
                        }
                    }
                }
                else if (op.Kind == "text" && !string.IsNullOrEmpty(op.Text) && op.Pts.Count >= 1)
                {
                    using (var f = new Font("Microsoft YaHei UI", op.W, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var br = new SolidBrush(op.Col))
                        g.DrawString(op.Text, f, br, op.Pts[0]);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImage(shot, 0, 0, Width, Height);
            Rectangle active = state == 0 ? hoverRect : sel;
            bool hasActive = !active.IsEmpty && active.Width > 0 && active.Height > 0;
            using (var dim = new SolidBrush(Color.FromArgb(118, 0, 0, 0)))
            {
                if (hasActive)
                {
                    g.FillRectangle(dim, 0, 0, Width, active.Top);
                    g.FillRectangle(dim, 0, active.Bottom, Width, Height - active.Bottom);
                    g.FillRectangle(dim, 0, active.Top, active.Left, active.Height);
                    g.FillRectangle(dim, active.Right, active.Top, Width - active.Right, active.Height);
                    g.DrawImage(shot, active, Scaled(active), GraphicsUnit.Pixel);
                }
                else g.FillRectangle(dim, 0, 0, Width, Height);
            }
            if (hasActive && state != 0)
            {
                // annotations (stored in image coords) clipped to the selection
                g.SetClip(sel);
                g.MultiplyTransform(new Matrix(1f / (float)KX, 0, 0, 1f / (float)KY, 0, 0));
                DrawOps(g, true);
                g.ResetTransform();
                g.ResetClip();
            }
            if (hasActive)
            {
                using (var pen = new Pen(Color.FromArgb(0, 120, 215), 5))
                    g.DrawRectangle(pen, active);
                var sc = Scaled(active);
                DrawTag(g, sc.Width + " × " + sc.Height, Math.Max(4, Math.Min(active.Left, Width - 130)), active.Top > 34 ? active.Top - 29 : active.Bottom + 8);
            }
            if (state == 2)
            {
                foreach (var hp in Handles())
                    using (var fill = new SolidBrush(Color.White))
                    using (var ring = new Pen(Color.FromArgb(0, 120, 215), 3))
                    {
                        int r = 6 * U;
                        g.FillEllipse(fill, hp.X - r, hp.Y - r, r * 2, r * 2);
                        g.DrawEllipse(ring, hp.X - r, hp.Y - r, r * 2, r * 2);
                    }
                if (SelectOnly) DrawSelectBar(g); else DrawToolbar(g);
            }
            else if (state == 0)
            {
                DrawTag(g, hasActive ? "点击截取此区域 · 拖动可自选区域 · Esc 取消" : "拖动框选区域 · 单击 = 全屏 · Esc 取消", 0, 22, true);
            }
            if (statusText != null) DrawTag(g, statusText, 0, 56, true);
        }

        void DrawTag(Graphics g, string text, int x, int y, bool center = false)
        {
            using (var f = new Font("Microsoft YaHei UI", 10.5f))
            {
                var ts = g.MeasureString(text, f);
                int w = (int)ts.Width + 16, h = 24;
                if (center) x = Math.Max(4, (Width - w) / 2);
                if (y + h > Height) y = Height - h - 4;
                using (var bg = new SolidBrush(Color.FromArgb(230, 28, 28, 28)))
                using (var fg = new SolidBrush(Color.White))
                {
                    g.FillRectangle(bg, x, y, w, h);
                    g.DrawString(text, f, fg, x + 8, y + 3);
                }
            }
        }

        void DrawSelectBar(Graphics g)
        {
            tbBtns.Clear();
            int u = U;
            int bw = 56 * u, bh = 36 * u, gap = 8 * u, pad = 8 * u;
            int cardW = pad * 2 + bw * 2 + gap, cardH = pad * 2 + bh;
            int tx = Math.Max(4, Math.Min(sel.Left + sel.Width / 2 - cardW / 2, Width - cardW - 4));
            int ty = sel.Bottom + 10 * u + cardH <= Height ? sel.Bottom + 10 * u : Math.Max(4, sel.Top - 10 * u - cardH);
            var panel = new Rectangle(tx, ty, cardW, cardH);
            using (var bg = new SolidBrush(Color.FromArgb(242, 32, 32, 32)))
            using (var pp = Rounded(panel, 10 * u)) g.FillPath(bg, pp);
            var canR = new Rectangle(tx + pad, ty + pad, bw, bh);
            var okR = new Rectangle(canR.Right + gap, ty + pad, bw, bh);
            tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("cancel", canR));
            tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("ok", okR));
            using (var b2 = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
            using (var p2 = Rounded(canR, 6 * u)) g.FillPath(b2, p2);
            using (var b2 = new SolidBrush(Color.FromArgb(0, 120, 215)))
            using (var p2 = Rounded(okR, 6 * u)) g.FillPath(b2, p2);
            DrawIcon(g, "cancel", canR, u);
            DrawIcon(g, "ok", okR, u);
        }

        void DrawIcon(Graphics g, string kind, Rectangle r, int u)
        {
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            float s = 9f * u;
            using (var pen = new Pen(Color.White, 2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var br = new SolidBrush(Color.White))
            {
                if (kind == "pen")
                {
                    g.DrawPolygon(pen, new[] { new PointF(cx - s * 0.30f, cy + s * 0.80f), new PointF(cx + s * 0.80f, cy - s * 0.30f), new PointF(cx + s * 0.30f, cy - s * 0.80f), new PointF(cx - s * 0.80f, cy + s * 0.30f) });
                    g.DrawLine(pen, cx - s * 0.30f, cy + s * 0.80f, cx - s * 0.88f, cy + s * 0.88f);
                }
                else if (kind == "arrow")
                {
                    g.DrawLine(pen, cx - s * 0.75f, cy + s * 0.75f, cx + s * 0.7f, cy - s * 0.7f);
                    g.DrawLine(pen, cx + s * 0.02f, cy - s * 0.72f, cx + s * 0.72f, cy - s * 0.72f);
                    g.DrawLine(pen, cx + s * 0.72f, cy - s * 0.72f, cx + s * 0.72f, cy - s * 0.02f);
                }
                else if (kind == "rect")
                {
                    g.DrawRectangle(pen, cx - s * 0.8f, cy - s * 0.6f, s * 1.6f, s * 1.2f);
                }
                else if (kind == "mosaic")
                {
                    float q = s * 0.55f;
                    for (int ix = 0; ix < 3; ix++) for (int iy = 0; iy < 3; iy++)
                    {
                        float qx = cx - s * 0.82f + ix * q, qy = cy - s * 0.82f + iy * q;
                        if ((ix + iy) % 2 == 0) g.FillRectangle(br, qx, qy, q * 0.9f, q * 0.9f);
                        else g.DrawRectangle(pen, qx, qy, q * 0.9f, q * 0.9f);
                    }
                }
                else if (kind == "text")
                {
                    g.DrawLine(pen, cx - s * 0.8f, cy - s * 0.7f, cx + s * 0.8f, cy - s * 0.7f);
                    g.DrawLine(pen, cx, cy - s * 0.7f, cx, cy + s * 0.8f);
                    g.DrawLine(pen, cx - s * 0.32f, cy + s * 0.8f, cx + s * 0.32f, cy + s * 0.8f);
                }
                else if (kind == "undo")
                {
                    g.DrawArc(pen, cx - s * 0.75f, cy - s * 0.75f, s * 1.5f, s * 1.5f, 285, 240);
                    g.DrawLine(pen, cx + s * 0.22f, cy - s * 0.92f, cx + s * 0.8f, cy - s * 0.66f);
                    g.DrawLine(pen, cx + s * 0.8f, cy - s * 0.66f, cx + s * 0.55f, cy - s * 0.12f);
                }
                else if (kind == "qr")
                {
                    float q = s * 0.72f, d = s * 0.26f;
                    g.DrawRectangle(pen, cx - s * 0.85f, cy - s * 0.85f, q, q);
                    g.DrawRectangle(pen, cx + s * 0.85f - q, cy - s * 0.85f, q, q);
                    g.DrawRectangle(pen, cx - s * 0.85f, cy + s * 0.85f - q, q, q);
                    g.FillRectangle(br, cx - s * 0.85f + (q - d) / 2, cy - s * 0.85f + (q - d) / 2, d, d);
                    g.FillRectangle(br, cx + s * 0.85f - q + (q - d) / 2, cy - s * 0.85f + (q - d) / 2, d, d);
                    g.FillRectangle(br, cx - s * 0.85f + (q - d) / 2, cy + s * 0.85f - q + (q - d) / 2, d, d);
                    g.FillRectangle(br, cx + s * 0.3f, cy + s * 0.3f, d, d);
                    g.FillRectangle(br, cx + s * 0.64f, cy + s * 0.64f, d, d);
                }
                else if (kind == "cancel")
                {
                    g.DrawLine(pen, cx - s * 0.6f, cy - s * 0.6f, cx + s * 0.6f, cy + s * 0.6f);
                    g.DrawLine(pen, cx + s * 0.6f, cy - s * 0.6f, cx - s * 0.6f, cy + s * 0.6f);
                }
                else if (kind == "ok")
                {
                    g.DrawLines(pen, new[] { new PointF(cx - s * 0.75f, cy + s * 0.02f), new PointF(cx - s * 0.22f, cy + s * 0.58f), new PointF(cx + s * 0.8f, cy - s * 0.55f) });
                }
            }
        }

        void DrawToolbar(Graphics g)
        {
            tbBtns.Clear();
            int u = U;
            int pad = 8 * u, rowH = 36 * u, gap = 6 * u;
            int toolW = 52 * u, dotW = 26 * u, qrW = 56 * u, actW = 56 * u;
            int oneRowW = pad * 2 + toolW * 5 + gap * 4 + 8 * u + dotW * 5 + 8 * u + toolW + 8 * u + qrW + 8 * u + actW * 2 + gap;
            bool oneRow = oneRowW <= Width - 8;
            int cardW, cardH;
            if (oneRow) { cardW = oneRowW; cardH = pad * 2 + rowH; }
            else { cardW = pad * 2 + toolW * 5 + gap * 4 + 10 * u + dotW * 5 + 10 * u + toolW; cardH = pad * 2 + rowH * 2 + gap; }
            int tx = Math.Max(4, Math.Min(sel.Left + sel.Width / 2 - cardW / 2, Width - cardW - 4));
            int ty = sel.Bottom + 10 * u + cardH <= Height ? sel.Bottom + 10 * u : Math.Max(4, sel.Top - 10 * u - cardH);
            var panel = new Rectangle(tx, ty, cardW, cardH);
            using (var bg = new SolidBrush(Color.FromArgb(242, 32, 32, 32)))
            using (var pp = Rounded(panel, 10 * u)) g.FillPath(bg, pp);
            string[] tids = { "pen", "arrow", "rect", "mosaic", "text" };
            int x = tx + pad;
            int y1 = ty + pad;
            for (int i = 0; i < 5; i++)
            {
                var r = new Rectangle(x, y1, toolW, rowH);
                tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("tool:" + tids[i], r));
                using (var b2 = new SolidBrush(tool == tids[i] ? Color.FromArgb(0, 120, 215) : Color.FromArgb(26, 255, 255, 255)))
                using (var p2 = Rounded(r, 6 * u)) g.FillPath(b2, p2);
                DrawIcon(g, tids[i], r, u);
                x += toolW + gap;
            }
            x += 2 * u;
            for (int i = 0; i < 5; i++)
            {
                var slot = new Rectangle(x, y1, dotW, rowH);
                tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("color:" + i, slot));
                int cr = 9 * u;
                int cxp = slot.Left + slot.Width / 2, cyp = slot.Top + slot.Height / 2;
                if (curColor == Palette[i])
                    using (var ring = new Pen(Color.White, 2.5f * u)) g.DrawEllipse(ring, cxp - cr - 3 * u, cyp - cr - 3 * u, (cr + 3 * u) * 2, (cr + 3 * u) * 2);
                using (var fill = new SolidBrush(Palette[i])) g.FillEllipse(fill, cxp - cr, cyp - cr, cr * 2, cr * 2);
                x += dotW;
            }
            x += 8 * u;
            var undoR = new Rectangle(x, y1, toolW, rowH);
            tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("undo", undoR));
            using (var b2 = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
            using (var p2 = Rounded(undoR, 6 * u)) g.FillPath(b2, p2);
            DrawIcon(g, "undo", undoR, u);
            if (oneRow)
            {
                x += toolW + 8 * u;
                var qrR = new Rectangle(x, y1, qrW, rowH);
                tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("qr", qrR));
                using (var b2 = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                using (var p2 = Rounded(qrR, 6 * u)) g.FillPath(b2, p2);
                DrawIcon(g, "qr", qrR, u);
                x += qrW + 8 * u;
                var canR = new Rectangle(x, y1, actW, rowH);
                var okR = new Rectangle(x + actW + gap, y1, actW, rowH);
                tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("cancel", canR));
                tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("ok", okR));
                using (var b2 = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                using (var p2 = Rounded(canR, 6 * u)) g.FillPath(b2, p2);
                DrawIcon(g, "cancel", canR, u);
                using (var b2 = new SolidBrush(Color.FromArgb(0, 120, 215)))
                using (var p2 = Rounded(okR, 6 * u)) g.FillPath(b2, p2);
                DrawIcon(g, "ok", okR, u);
            }
            else
            {
                int y2 = y1 + rowH + gap;
                var qrR = new Rectangle(tx + pad, y2, 84 * u, rowH);
                tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("qr", qrR));
                using (var b2 = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                using (var p2 = Rounded(qrR, 6 * u)) g.FillPath(b2, p2);
                DrawIcon(g, "qr", qrR, u);
                var okR = new Rectangle(tx + cardW - pad - 76 * u, y2, 76 * u, rowH);
                var canR = new Rectangle(okR.Left - gap - 76 * u, y2, 76 * u, rowH);
                tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("cancel", canR));
                tbBtns.Add(new System.Collections.Generic.KeyValuePair<string, Rectangle>("ok", okR));
                using (var b2 = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                using (var p2 = Rounded(canR, 6 * u)) g.FillPath(b2, p2);
                DrawIcon(g, "cancel", canR, u);
                using (var b2 = new SolidBrush(Color.FromArgb(0, 120, 215)))
                using (var p2 = Rounded(okR, 6 * u)) g.FillPath(b2, p2);
                DrawIcon(g, "ok", okR, u);
            }
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, rad * 2);
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

}
