using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FrierenPet
{
    public enum Mood { Idle, Blink, Happy, Magic, Sleep, Read }

    public enum PointerResult { None, Click, DoubleClick, Drag }
    public sealed class PointerGesture
    {
        public bool Pressed, Dragging;
        public Point Position;
        Point start, origin;
        bool secondClick;
        public void Begin(Point cursor, Point location, bool second)
        { start = cursor; origin = location; Position = location; secondClick = second; Pressed = true; Dragging = false; }
        public bool Move(Point cursor, double minX, double minY)
        {
            if (!Pressed) return false;
            Vector delta = cursor - start;
            if (Math.Abs(delta.X) >= minX || Math.Abs(delta.Y) >= minY) Dragging = true;
            if (Dragging) Position = origin + delta;
            return Dragging;
        }
        public PointerResult End(bool allowClick)
        {
            if (!Pressed) return PointerResult.None;
            PointerResult result = Dragging ? PointerResult.Drag : !allowClick ? PointerResult.None : secondClick ? PointerResult.DoubleClick : PointerResult.Click;
            Pressed = false; Dragging = false;
            return result;
        }
    }

    // Animation time is independent of frame rate. No state ever changes window position.
    public sealed class PetModel
    {
        public Mood Mood = Mood.Idle;
        public double Time, StateTime, Until, NextBlink = 3.2, NextActivity = 24;
        public bool Paused;
        public int Pats;
        readonly Random random = new Random();
        public void Set(Mood mood, double seconds)
        { Mood = mood; StateTime = 0; Until = Time + seconds; }
        public void Tick(double dt)
        {
            if (Paused) return;
            Time += dt; StateTime += dt;
            if (Mood != Mood.Idle && Time >= Until)
            {
                bool wasBlink = Mood == Mood.Blink;
                Mood = Mood.Idle; StateTime = 0;
                NextBlink = Time + 2.5 + random.NextDouble() * 3;
                if (!wasBlink) NextActivity = Time + 24 + random.NextDouble() * 20;
            }
            if (Mood == Mood.Idle && Time >= NextBlink) Set(Mood.Blink, .16);
            if (Mood == Mood.Idle && Time >= NextActivity)
                Set(random.Next(2) == 0 ? Mood.Read : Mood.Sleep, 9);
        }
        public void Pat() { Paused = false; Pats++; Set(Mood.Happy, 2.8); }
    }

    [DataContract]
    public sealed class Settings
    {
        [DataMember] public double Scale = 1;
        [DataMember] public bool Topmost = true;
        [DataMember] public bool HasCustomPosition;
        [DataMember] public double Left;
        [DataMember] public double Top;
        public static string FileName { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"); } }
        public static Settings Load()
        {
            try
            {
                using (var stream = File.OpenRead(FileName))
                {
                    var s = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                    if (s == null) return new Settings();
                    if (s.Scale != .8 && s.Scale != 1 && s.Scale != 1.2) s.Scale = 1;
                    if (!Finite(s.Left) || !Finite(s.Top)) s.HasCustomPosition = false;
                    return s;
                }
            }
            catch { return new Settings(); }
        }
        public static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        public void Save()
        {
            try
            {
                string temp = FileName + ".tmp";
                using (var stream = File.Create(temp)) new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, this);
                if (File.Exists(FileName)) File.Replace(temp, FileName, null); else File.Move(temp, FileName);
            }
            catch { /* Read-only portable locations still run with in-memory preferences. */ }
        }
    }

    public sealed class SpriteAtlas
    {
        public readonly BitmapSource[] Frames = new BitmapSource[6];
        readonly byte[][] pixels = new byte[6][];
        public SpriteAtlas()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Frieren.Atlas"))
            {
                if (stream == null) throw new FileNotFoundException("缺少角色动画素材，请重新运行 build.ps1。");
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource source = decoder.Frames[0];
                int w = source.PixelWidth / 3, h = source.PixelHeight / 2;
                for (int i = 0; i < 6; i++)
                {
                    var crop = new CroppedBitmap(source, new Int32Rect((i % 3) * w, (i / 3) * h, w, h));
                    var frame = new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);
                    frame.Freeze(); Frames[i] = frame;
                    pixels[i] = new byte[w * h * 4]; frame.CopyPixels(pixels[i], w * 4, 0);
                }
            }
        }
        public bool IsSolid(Mood mood, double u, double v)
        {
            int i = (int)mood, w = Frames[i].PixelWidth, h = Frames[i].PixelHeight;
            int x = (int)(u * w), y = (int)(v * h);
            return x >= 0 && y >= 0 && x < w && y < h && pixels[i][(y * w + x) * 4 + 3] > 24;
        }
    }

    public sealed class PetSurface : FrameworkElement
    {
        public readonly PetModel Model;
        public readonly SpriteAtlas Atlas;
        public string Bubble;
        public double BubbleUntil;
        public static readonly Rect SpriteRect = new Rect(20, 100, 280, 280);
        static readonly Typeface Font = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Dictionary<string, SolidColorBrush> BrushesByColor = new Dictionary<string, SolidColorBrush>();
        static SolidColorBrush Brush(string hex)
        {
            SolidColorBrush b;
            if (!BrushesByColor.TryGetValue(hex, out b))
            { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); BrushesByColor.Add(hex, b); }
            return b;
        }
        static readonly Brush Ink = Brush("#43554F"), Teal = Brush("#6F9D8D"), Gold = Brush("#DCC18D"), Pink = Brush("#E7A0AE");
        static readonly FormattedText[] SleepLetters = {
            new FormattedText("z", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 15, Teal, 1.0),
            new FormattedText("z", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 18, Teal, 1.0),
            new FormattedText("z", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 21, Teal, 1.0)
        };
        public PetSurface(PetModel model, SpriteAtlas atlas)
        { Model = model; Atlas = atlas; Width = 320; Height = 380; Cursor = Cursors.Hand; }
        public void Say(string text, double seconds)
        { Bubble = text; BubbleUntil = Model.Time + seconds; InvalidateVisual(); }
        public Transform BodyTransform()
        {
            double t = Model.Time;
            double breath = Math.Sin(t * 2.1), bounce = 0, rotate = Math.Sin(t * 1.2) * .45;
            if (Model.Mood == Mood.Happy) { bounce = -Math.Abs(Math.Sin(Model.StateTime * 8)) * 7 * Math.Exp(-Model.StateTime * .6); rotate = Math.Sin(Model.StateTime * 7) * 2; }
            if (Model.Mood == Mood.Magic) { bounce = -3 * Math.Sin(Model.StateTime * 3); rotate = Math.Sin(Model.StateTime * 2) * 1.2; }
            if (Model.Mood == Mood.Sleep) rotate = Math.Sin(t * .9) * 1.5;
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(1 + breath * .003, 1 + breath * .008, 160, 365));
            group.Children.Add(new RotateTransform(rotate, 160, 340));
            group.Children.Add(new TranslateTransform(0, bounce));
            return group;
        }
        public bool OnCharacter(Point p)
        {
            var inv = BodyTransform().Inverse;
            if (inv != null) p = inv.Transform(p);
            return Atlas.IsSolid(Model.Mood, (p.X - SpriteRect.X) / SpriteRect.Width, (p.Y - SpriteRect.Y) / SpriteRect.Height);
        }
        protected override HitTestResult HitTestCore(PointHitTestParameters p)
        { return OnCharacter(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null; }
        protected override void OnRender(DrawingContext dc)
        {
            double t = Model.Time;
            dc.DrawEllipse(Brush("#1665917D"), null, new Point(161, 365), 58 + Math.Sin(t * 2) * 2, 6);
            dc.PushTransform(BodyTransform());
            dc.DrawImage(Atlas.Frames[(int)Model.Mood], SpriteRect);
            dc.Pop();
            if (Model.Mood == Mood.Happy)
            {
                for (int i = 0; i < 5; i++)
                {
                    double phase = (Model.StateTime * .45 + i * .19) % 1;
                    dc.PushOpacity(Math.Sin(phase * Math.PI) * .85);
                    Heart(dc, 67 + i * 46 + Math.Sin(i * 2 + t) * 7, 175 - phase * 65, 5 + i % 3, Pink);
                    dc.Pop();
                }
            }
            if (Model.Mood == Mood.Magic)
            {
                dc.PushOpacity(.5 + .2 * Math.Sin(t * 3));
                dc.DrawEllipse(null, new Pen(Teal, 1.1), new Point(162, 359), 80, 13);
                dc.DrawEllipse(null, new Pen(Gold, .8), new Point(162, 359), 69, 9);
                dc.Pop();
                for (int i = 0; i < 14; i++)
                {
                    double phase = (Model.StateTime * .28 + i / 14.0) % 1;
                    double a = i * 2.399 + Model.StateTime * .65;
                    dc.PushOpacity(Math.Sin(phase * Math.PI));
                    Star(dc, 160 + Math.Cos(a) * (70 + phase * 50), 330 - phase * 190, 3 + i % 3, i % 2 == 0 ? Teal : Gold);
                    dc.Pop();
                }
            }
            if (Model.Mood == Mood.Sleep)
            {
                for (int i = 0; i < 3; i++)
                {
                    double phase = (t * .22 + i * .33) % 1;
                    dc.PushOpacity(Math.Sin(phase * Math.PI));
                    dc.DrawText(SleepLetters[i], new Point(233 + phase * 19, 158 - phase * 67));
                    dc.Pop();
                }
            }
            if (Bubble != null && Model.Time < BubbleUntil)
            {
                double opacity = Math.Min(1, (BubbleUntil - Model.Time) * 3);
                dc.PushOpacity(opacity);
                var ft = new FormattedText(Bubble, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, Font, 13, Ink, 1.0);
                ft.MaxTextWidth = 231; ft.LineHeight = 21;
                double bh = Math.Max(50, ft.Height + 26), bw = Math.Min(261, Math.Max(160, ft.Width + 30));
                double bx = (320 - bw) / 2, by = 91 - bh;
                dc.DrawRoundedRectangle(Brush("#14000000"), null, new Rect(bx, by + 3, bw, bh), 15, 15);
                dc.DrawRoundedRectangle(Brush("#F9FFFCF5"), new Pen(Brush("#C1D6C9"), 1), new Rect(bx, by, bw, bh), 15, 15);
                var tail = Geometry.Parse("M 178,90 L 187,101 L 192,90 Z");
                dc.DrawGeometry(Brush("#FFFCF5"), null, tail);
                dc.DrawText(ft, new Point(bx + 15, by + 12));
                dc.Pop();
            }
        }
        static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush)
        { dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush, 1.0), new Point(x, y)); }
        static void Star(DrawingContext dc, double x, double y, double r, Brush brush)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x, y - r), true, true);
                c.LineTo(new Point(x + r * .3, y - r * .3), true, false); c.LineTo(new Point(x + r, y), true, false);
                c.LineTo(new Point(x + r * .3, y + r * .3), true, false); c.LineTo(new Point(x, y + r), true, false);
                c.LineTo(new Point(x - r * .3, y + r * .3), true, false); c.LineTo(new Point(x - r, y), true, false);
                c.LineTo(new Point(x - r * .3, y - r * .3), true, false);
            }
            dc.DrawGeometry(brush, null, g);
        }
        static void Heart(DrawingContext dc, double x, double y, double r, Brush brush)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x, y + r), true, true);
                c.BezierTo(new Point(x - r * 2, y - r * .3), new Point(x - r, y - r * 1.5), new Point(x, y - r * .5), true, false);
                c.BezierTo(new Point(x + r, y - r * 1.5), new Point(x + r * 2, y - r * .3), new Point(x, y + r), true, false);
            }
            dc.DrawGeometry(brush, null, g);
        }
    }

    public sealed class PetWindow : Window
    {
        readonly PetModel model = new PetModel();
        readonly Settings settings = Settings.Load();
        readonly PetSurface surface;
        readonly DispatcherTimer timer, singleClick;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Forms.NotifyIcon tray;
        readonly PointerGesture pointer = new PointerGesture();
        double last, nextAnchor;
        IntPtr hwnd;
        bool closing;
        string diagnostics;
        public PetWindow(SpriteAtlas atlas, string diagnosticPath)
        {
            diagnostics = diagnosticPath;
            Title = "芙莉莲 · 旅途小憩";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize; ShowInTaskbar = diagnosticPath != null; ShowActivated = false; Topmost = settings.Topmost;
            Width = 320 * settings.Scale; Height = 380 * settings.Scale;
            surface = new PetSurface(model, atlas);
            var view = new Viewbox { Stretch = Stretch.Uniform, Child = surface }; Content = view;
            surface.Say("按住我，就能带我换个位置。\n单击摸头 · 双击施法 · 右键菜单", 6.5);
            surface.MouseLeftButtonDown += ClickPet;
            surface.MouseMove += delegate { UpdatePointer(); };
            surface.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            { UpdatePointer(); FinishPointer(true); e.Handled = true; };
            surface.LostMouseCapture += delegate { FinishPointer(false); };
            surface.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) { OpenMenu(); e.Handled = true; };
            singleClick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime) };
            singleClick.Tick += delegate { singleClick.Stop(); Act("pat"); };
            timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 24) };
            timer.Tick += Tick;
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                int style = GetWindowLong(hwnd, -20); SetWindowLong(hwnd, -20, style | 0x08000000 | (diagnostics == null ? 0x00000080 : 0));
                HwndSource.FromHwnd(hwnd).AddHook(WndProc); Anchor();
            };
            Loaded += delegate { last = clock.Elapsed.TotalSeconds; timer.Start(); WriteDiagnostic("started"); };
            tray = new Forms.NotifyIcon { Text = "芙莉莲 · 按住拖动 / 单击摸头 / 双击施法", Visible = true };
            using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location)) tray.Icon = (System.Drawing.Icon)icon.Clone();
            var tm = new Forms.ContextMenuStrip();
            tm.Items.Add("芙莉莲 · 旅途小憩").Enabled = false;
            tm.Items.Add("显示 / 隐藏", null, delegate { Dispatcher.Invoke(new Action(ToggleVisible)); });
            tm.Items.Add("摸摸头", null, delegate { Dispatcher.Invoke(new Action(delegate { Show(); Act("pat"); })); });
            tm.Items.Add("施个小魔法", null, delegate { Dispatcher.Invoke(new Action(delegate { Show(); Act("magic"); })); });
            tm.Items.Add("回到右下角", null, delegate { Dispatcher.Invoke(new Action(delegate { Show(); ResetPosition(); })); });
            tm.Items.Add("退出", null, delegate { Dispatcher.Invoke(new Action(Close)); });
            tray.ContextMenuStrip = tm;
            tray.DoubleClick += delegate { Dispatcher.Invoke(new Action(delegate { Show(); Anchor(); })); };
            Closed += delegate
            {
                closing = true; timer.Stop(); singleClick.Stop(); tray.Visible = false;
                if (tray.Icon != null) tray.Icon.Dispose(); tray.Dispose(); settings.Save();
                Application.Current.Shutdown();
            };
        }
        void ClickPet(object sender, MouseButtonEventArgs e)
        {
            bool second = e.ClickCount == 2 && singleClick.IsEnabled;
            singleClick.Stop();
            Point cursor;
            if (TryCursorPosition(out cursor))
            {
                pointer.Begin(cursor, new Point(Left, Top), second);
                surface.CaptureMouse();
            }
            e.Handled = true;
        }
        Matrix FromDevice()
        {
            var source = HwndSource.FromHwnd(hwnd);
            return source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
        }
        bool TryCursorPosition(out Point cursor)
        {
            NativePoint point;
            bool success = GetCursorPos(out point);
            cursor = FromDevice().Transform(new Point(point.X, point.Y));
            return success;
        }
        void UpdatePointer()
        {
            Point cursor;
            if (!pointer.Pressed || !TryCursorPosition(out cursor)) return;
            if (pointer.Move(cursor, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
            { Left = pointer.Position.X; Top = pointer.Position.Y; surface.Cursor = Cursors.SizeAll; }
        }
        void FinishPointer(bool allowClick)
        {
            PointerResult result = pointer.End(allowClick);
            if (surface.IsMouseCaptured) surface.ReleaseMouseCapture();
            surface.Cursor = Cursors.Hand;
            if (result == PointerResult.Drag)
            {
                settings.HasCustomPosition = true; settings.Left = Left; settings.Top = Top;
                Anchor(); settings.Save(); WriteDiagnostic("drag-complete");
            }
            else if (result == PointerResult.DoubleClick) Act("magic");
            else if (result == PointerResult.Click) singleClick.Start();
        }
        void ResetPosition()
        {
            settings.HasCustomPosition = false; Anchor(); settings.Save();
            surface.Say("好，我回到这里等你。", 3);
            WriteDiagnostic("reset-position");
        }
        void Act(string action)
        {
            model.Paused = false;
            if (action == "pat")
            {
                model.Pat();
                string[] lines = { "唔……再摸一下也可以。", "原来这也是一种魔法啊。", "今天也辛苦你了。", "我会在这里，慢慢陪着你。" };
                surface.Say(lines[(model.Pats - 1) % lines.Length], 3.4);
            }
            if (action == "magic") { model.Set(Mood.Magic, 4.8); surface.Say("让今天变得轻松一点的魔法。", 4.8); }
            if (action == "sleep") { model.Set(Mood.Sleep, 18); surface.Say("只睡一小会儿……", 3.5); }
            if (action == "read") { model.Set(Mood.Read, 15); surface.Say("这本魔导书，好像很有趣。", 3.5); }
            surface.InvalidateVisual(); WriteDiagnostic(action);
        }
        void Tick(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds, dt = Math.Min(.1, now - last); last = now;
            if (!IsVisible) return;
            if (pointer.Pressed)
            {
                // Keep following a held press even if a non-activating window could not capture the mouse.
                UpdatePointer();
                if ((GetAsyncKeyState(0x01) & 0x8000) == 0) FinishPointer(true);
            }
            model.Tick(dt);
            if (!model.Paused) surface.InvalidateVisual();
            if (now >= nextAnchor) { nextAnchor = now + 2; Anchor(); }
        }
        public static Rect AnchoredBounds(Rect area, double scale)
        { return new Rect(area.Right - 320 * scale - 12, area.Bottom - 380 * scale, 320 * scale, 380 * scale); }
        public static Rect ClampBounds(Rect bounds, Rect area)
        {
            double x = Math.Max(area.Left, Math.Min(bounds.X, area.Right - bounds.Width));
            double y = Math.Max(area.Top, Math.Min(bounds.Y, area.Bottom - bounds.Height));
            return new Rect(x, y, bounds.Width, bounds.Height);
        }
        Rect WorkAreaFor(Rect bounds)
        {
            Matrix toDevice = FromDevice(); toDevice.Invert();
            Point topLeft = toDevice.Transform(bounds.TopLeft), bottomRight = toDevice.Transform(bounds.BottomRight);
            var rectangle = new System.Drawing.Rectangle((int)topLeft.X, (int)topLeft.Y,
                Math.Max(1, (int)(bottomRight.X - topLeft.X)), Math.Max(1, (int)(bottomRight.Y - topLeft.Y)));
            var area = Forms.Screen.FromRectangle(rectangle).WorkingArea;
            return new Rect(FromDevice().Transform(new Point(area.Left, area.Top)), FromDevice().Transform(new Point(area.Right, area.Bottom)));
        }
        void Anchor()
        {
            if (closing || pointer.Pressed) return;
            Rect bounds = AnchoredBounds(SystemParameters.WorkArea, settings.Scale);
            if (settings.HasCustomPosition)
            {
                bounds = new Rect(settings.Left, settings.Top, Width, Height);
                bounds = ClampBounds(bounds, WorkAreaFor(bounds));
                settings.Left = bounds.Left; settings.Top = bounds.Top;
            }
            if (Math.Abs(Left - bounds.X) > .5 || double.IsNaN(Left)) Left = bounds.X;
            if (Math.Abs(Top - bounds.Y) > .5 || double.IsNaN(Top)) Top = bounds.Y;
        }
        IntPtr WndProc(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (msg == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE: keep keyboard focus in the user's app.
            if (msg == 0x84)
            {
                if (pointer.Pressed) { handled = true; return new IntPtr(1); }
                long value = lp.ToInt64();
                var screen = new Point((short)(value & 0xffff), (short)((value >> 16) & 0xffff));
                Point p = surface.PointFromScreen(screen);
                if (!surface.OnCharacter(p)) { handled = true; return new IntPtr(-1); }
            }
            if (msg == 0x7E || msg == 0x1A || msg == 0x02E0) Dispatcher.BeginInvoke(new Action(Anchor));
            if (msg == 0x112 && ((wp.ToInt64() & 0xfff0) == 0xf010 || (wp.ToInt64() & 0xfff0) == 0xf000)) { handled = true; return IntPtr.Zero; }
            return IntPtr.Zero;
        }
        void OpenMenu()
        {
            var menu = new ContextMenu { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 13,
                Background = new SolidColorBrush(Color.FromRgb(250, 252, 248)), Foreground = new SolidColorBrush(Color.FromRgb(58, 78, 69)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(206, 220, 208)), Padding = new Thickness(6), MinWidth = 218 };
            menu.Items.Add(new MenuItem { Header = "芙莉莲  ·  旅途小憩", IsEnabled = false });
            menu.Items.Add(new Separator());
            AddItem(menu, "♡   摸摸头", delegate { Act("pat"); });
            AddItem(menu, "✧   施个小魔法", delegate { Act("magic"); });
            AddItem(menu, "☾   打个盹", delegate { Act("sleep"); });
            AddItem(menu, "▤   看魔导书", delegate { Act("read"); });
            menu.Items.Add(new Separator());
            var sizes = new MenuItem { Header = "角色大小" };
            double[] scales = { .8, 1, 1.2 }; string[] labels = { "小巧", "标准", "大一点" };
            for (int i = 0; i < scales.Length; i++)
            {
                double scale = scales[i];
                var item = new MenuItem { Header = labels[i], IsCheckable = true, IsChecked = Math.Abs(settings.Scale - scale) < .01 };
                item.Click += delegate { settings.Scale = scale; Width = 320 * scale; Height = 380 * scale; Anchor(); settings.Save(); WriteDiagnostic("resize"); };
                sizes.Items.Add(item);
            }
            menu.Items.Add(sizes);
            var top = new MenuItem { Header = "保持置顶", IsCheckable = true, IsChecked = Topmost };
            top.Click += delegate { Topmost = top.IsChecked; settings.Topmost = Topmost; settings.Save(); }; menu.Items.Add(top);
            AddItem(menu, model.Paused ? "继续动画" : "暂停动画", delegate { model.Paused = !model.Paused; WriteDiagnostic("pause"); });
            AddItem(menu, "暂时隐藏（托盘找回）", ToggleVisible);
            menu.Items.Add(new Separator());
            AddItem(menu, "回到右下角", ResetPosition);
            menu.Items.Add(new MenuItem { Header = "按住左键拖动 · 自动记住位置", IsEnabled = false });
            AddItem(menu, "退出桌宠", Close);
            menu.Closed += delegate { WriteDiagnostic("menu-closed"); };
            menu.PlacementTarget = surface; menu.IsOpen = true; WriteDiagnostic("menu-open");
        }
        static void AddItem(ContextMenu menu, string text, Action action)
        { var item = new MenuItem { Header = text, Padding = new Thickness(9, 6, 9, 6) }; item.Click += delegate { action(); }; menu.Items.Add(item); }
        void ToggleVisible() { if (IsVisible) Hide(); else { Show(); Anchor(); } WriteDiagnostic("visibility"); }
        void WriteDiagnostic(string action)
        {
            if (diagnostics == null) return;
            try
            {
                string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:o} action={1} mood={2} left={3:F2} top={4:F2} width={5:F2} height={6:F2} paused={7} visible={8}\n",
                    DateTime.Now, action, model.Mood, Left, Top, Width, Height, model.Paused, IsVisible);
                File.AppendAllText(diagnostics, line);
            } catch { }
        }
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    }

    public static class Program
    {
        static Mutex mutex;
        [STAThread] public static void Main(string[] args)
        {
            try
            {
                // A tiny alpha window does not benefit from a large Direct3D render cache.
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                if (args.Length > 0 && args[0] == "--self-test") { SelfTest(args.Length > 1 ? args[1] : "tests"); return; }
                bool created; mutex = new Mutex(true, "Local\\FrierenPet.QuietCorner.v1", out created);
                if (!created) { mutex.Dispose(); return; }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                { Report(e.Exception); e.Handled = true; app.Shutdown(1); };
                string diagnostics = args.Length == 2 && args[0] == "--diagnostics" ? args[1] : null;
                var window = new PetWindow(new SpriteAtlas(), diagnostics);
                app.Run(window);
                mutex.ReleaseMutex(); mutex.Dispose();
            }
            catch (Exception ex) { Report(ex); Environment.ExitCode = 1; }
        }
        static void Report(Exception ex)
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"), ex.ToString()); } catch { }
            MessageBox.Show("桌宠启动遇到问题：\n" + ex.Message + "\n\n详情保存在 error.log。", "芙莉莲", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void SelfTest(string dir)
        {
            Directory.CreateDirectory(dir);
            var atlas = new SpriteAtlas();
            var m = new PetModel();
            m.Tick(3.3); Assert(m.Mood == Mood.Blink, "Idle should blink");
            m.Tick(.2); Assert(m.Mood == Mood.Idle, "Blink should return to idle");
            m.Pat(); Assert(m.Mood == Mood.Happy && m.Pats == 1, "Pat interaction");
            m.Tick(3); Assert(m.Mood == Mood.Idle, "Pat should finish");
            m.Set(Mood.Magic, 4.8); m.Tick(2); Assert(m.Mood == Mood.Magic, "Magic duration");
            m.Paused = true; double old = m.Time; m.Tick(9); Assert(m.Time == old, "Pause freezes animation clock");
            m.Paused = false; m.Tick(3); Assert(m.Mood == Mood.Idle, "Magic should finish");
            m.NextBlink = m.Time + 100; m.NextActivity = m.Time; m.Tick(.1);
            Assert(m.Mood == Mood.Read || m.Mood == Mood.Sleep, "Quiet automatic activity");
            var idle = new PetModel(); bool activity = false;
            for (int step = 0; step < 350; step++) { idle.Tick(.1); if (idle.Mood == Mood.Read || idle.Mood == Mood.Sleep) activity = true; }
            Assert(activity, "Repeated blinking must not postpone quiet activities forever");
            foreach (double scale in new[] { .8, 1.0, 1.2 })
            {
                Rect area = new Rect(0, 0, 1920, 1040); Rect b = PetWindow.AnchoredBounds(area, scale);
                Assert(Math.Abs(b.Right - 1908) < .01 && b.Bottom == 1040, "Fixed right/bottom anchor");
                Assert(area.Contains(b), "Window remains inside work area");
            }
            var gesture = new PointerGesture();
            gesture.Begin(new Point(100, 100), new Point(800, 500), false);
            Assert(!gesture.Move(new Point(102, 101), 4, 4), "Small pointer jitter remains a click");
            Assert(gesture.End(true) == PointerResult.Click, "Single click still pats");
            gesture.Begin(new Point(100, 100), new Point(800, 500), true);
            Assert(gesture.End(true) == PointerResult.DoubleClick, "Second click still casts magic");
            gesture.Begin(new Point(100, 100), new Point(800, 500), false);
            Assert(gesture.Move(new Point(160, 125), 4, 4), "Held movement begins drag");
            Assert(gesture.Position == new Point(860, 525), "Drag preserves the grab offset");
            gesture.Move(new Point(100, 100), 4, 4);
            Assert(gesture.End(true) == PointerResult.Drag, "Dragging back to origin must not trigger a click");
            Assert(gesture.End(true) == PointerResult.None, "Repeated release is harmless");
            gesture.Begin(new Point(100, 100), new Point(800, 500), true);
            gesture.Move(new Point(40, 60), 4, 4);
            Assert(gesture.End(true) == PointerResult.Drag, "Second press can drag without casting magic");
            gesture.Begin(new Point(100, 100), new Point(800, 500), false);
            Assert(gesture.End(false) == PointerResult.None, "Lost capture cancels an unfinished click");
            Rect desktop = new Rect(0, 0, 1920, 1040);
            Rect placed = new Rect(400, 300, 320, 380);
            Assert(PetWindow.ClampBounds(placed, desktop) == placed, "Custom position must not snap back");
            Assert(PetWindow.ClampBounds(new Rect(1800, 900, 320, 380), desktop) == new Rect(1600, 660, 320, 380), "Off-screen drag is clamped");
            Assert(PetWindow.ClampBounds(new Rect(-1400, 100, 320, 380), new Rect(-1920, 0, 1920, 1040)).X == -1400, "Negative monitor coordinates remain valid");
            using (var oldSettings = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"Scale\":0.8,\"Topmost\":true}")))
            {
                var restored = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(oldSettings);
                Assert(restored.Scale == .8 && restored.Topmost && !restored.HasCustomPosition, "Old preferences remain compatible");
            }
            using (var saved = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(Settings));
                serializer.WriteObject(saved, new Settings { HasCustomPosition = true, Left = -420.5, Top = 210.25, Scale = .8 });
                saved.Position = 0; var restored = (Settings)serializer.ReadObject(saved);
                Assert(restored.HasCustomPosition && restored.Left == -420.5 && restored.Top == 210.25, "Custom position survives a restart");
            }
            var gallery = new DrawingVisual();
            string[] labels = { "待机", "眨眼", "摸摸头", "小魔法", "打个盹", "看魔导书" };
            var font = new Typeface("Microsoft YaHei UI");
            using (var dc = gallery.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(239, 244, 238)), null, new Rect(0, 0, 960, 840));
                dc.DrawText(new FormattedText("芙莉莲 · 旅途小憩", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, font, 26, Brushes.DarkSlateGray, 1.0), new Point(30, 20));
                for (int i = 0; i < 6; i++)
                {
                    var model = new PetModel { Mood = (Mood)i, Time = 1, StateTime = 1 };
                    var surface = new PetSurface(model, atlas);
                    surface.Measure(new Size(320, 380)); surface.Arrange(new Rect(0, 0, 320, 380)); surface.UpdateLayout();
                    var bmp = new RenderTargetBitmap(320, 380, 96, 96, PixelFormats.Pbgra32); bmp.Render(surface);
                    dc.DrawImage(bmp, new Rect((i % 3) * 320, 45 + (i / 3) * 390, 320, 380));
                    dc.DrawText(new FormattedText(labels[i], System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, font, 15, Brushes.DarkSlateGray, 1.0), new Point((i % 3) * 320 + 128, 82 + (i / 3) * 390));
                }
            }
            var preview = new RenderTargetBitmap(960, 840, 96, 96, PixelFormats.Pbgra32); preview.Render(gallery);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(preview));
            using (var stream = File.Create(Path.Combine(dir, "preview.png"))) png.Save(stream);
            for (int i = 0; i < 6; i++) Assert(!atlas.IsSolid((Mood)i, 0, 0), "Sprite corners must be transparent");
            File.WriteAllText(Path.Combine(dir, "self-test.txt"), "PASS: embedded atlas, transparency, animation lifecycle, three sizes, default anchor, drag threshold, grab offset, click/double-click separation, canceled capture, off-screen recovery, negative monitor coordinates, old-settings compatibility, saved-position round trip.\r\n");
        }
    }
}
