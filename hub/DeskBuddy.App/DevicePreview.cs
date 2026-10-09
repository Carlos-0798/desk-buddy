using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DeskBuddy.App;

// An honest software preview of device appearance, not a captured hardware screen.
public sealed class DevicePreview : FrameworkElement
{
    public int Theme { get; set; }
    public int Face { get; set; }
    public bool Animate { get; set; } = true;
    public bool Blink { get; set; }
    public bool Armed { get; set; }
    public bool HidSuppressed { get; set; }
    public bool StatusKnown { get; set; } = true;
    public bool Chinese { get; set; }
    public string Profile { get; set; } = "MEDIA";
    public string Route { get; set; } = "USB";
    public bool Connected { get; set; }
    public int Battery { get; set; } = -1;
    public string KeysDown { get; set; } = "000000";
    public ushort Accent { get; set; }
    public bool Compact { get; set; }
    public bool PortraitOnly { get; set; }
    public int Mode { get; set; }
    public uint? PetInteractions { get; set; }
    public bool? PetActive { get; set; }
    public int IdleFrame { get; set; }
    public int PetMotion { get; set; }
    public int PetDirection { get; set; }
    public uint PetElapsedMilliseconds { get; set; }
    public double PetStatusAgeMilliseconds { get; set; }
    public bool PhysicalFeedback { get; set; }
    public bool? PetSleeping { get; set; }
    public bool PetHeart { get; set; }
    public uint? PetTaps { get; set; }
    public bool LocalDemo { get; set; }
    public AnimationPose? DemoPose { get; set; }
    public bool Sleeping => DemoPose?.Sleeping ?? ( ForcedFrame == "sleep" || ForcedFrame == null && PetSleeping == true);
    public bool Heart => DemoPose?.Heart ?? (ForcedFrame == "tap" || ForcedFrame == null && PetHeart);
    public string? ForcedFrame { get; set; }
    public DevicePreview() => RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

    public string SpriteFrame
    {
        get
        {
            if (DemoPose is { } pose) return pose.Frame;
            if (Sleeping) return "blink";
            // A lingering heart is an independent effect. A later turn may already
            // be walking (or idle with animation OFF) while that heart is visible.
            if (ForcedFrame is "tap" or "wake") return "happy";
            if (ForcedFrame is { } demo && CompanionSprites.Frames.Contains(demo))
                return !Animate && demo != "happy" ? "idle" : Mode == 0 && demo.StartsWith("walk_") ? "idle" : demo;
            if (PetActive == true && PetMotion == 1) return "happy";
            if (PhysicalFeedback && PetActive == true && PetMotion == 2)
                return Mode == 0 ? "happy" : ((int)(PetElapsedMilliseconds + PetStatusAgeMilliseconds) / 180) % 2 == 0 ? "walk_a" : "walk_b";
            if (!Animate) return "idle";
            if (Mode > 0 && PetActive == true && PetMotion == 2) return IdleFrame % 2 == 0 ? "walk_a" : "walk_b";
            return Blink ? "blink" : "idle";
        }
    }
    public static string CharacterLabel(int face, int mode, bool chinese)
    {
        return face switch
        {
            0 => chinese ? "掌机精灵" : "Pocket Buddy",
            1 => chinese ? "小黄鸡" : "Little Chick",
            10 => chinese ? "星夜团子" : "Starry Mochi",
            11 => chinese ? "眨眼星星" : "Wink Star",
            _ => chinese ? FaceChinese[face] : FaceNames[face]
        };
    }

    public static readonly string[] ThemeNames = ["Midnight", "Paper", "Terminal", "Lavender", "Ocean", "Sunset", "Garden", "Blueprint"];
    public static readonly string[] ThemeChinese = ["静谧午夜", "温润纸张", "荧绿终端", "薰衣草", "深海微澜", "暖色日落", "清新花园", "工程蓝图"];
    public static readonly string[] FaceNames = ["Pixel", "Round", "Cat", "Robot", "Bunny", "Ghost", "Mochi", "Orbit", "Fox", "Panda", "Sleepy", "Wink"];
    public static readonly string[] FaceChinese = ["像素笑脸", "圆圆笑脸", "小猫", "机器人", "长耳兔", "小幽灵", "软糯麻薯", "太空伙伴", "小狐狸", "熊猫", "瞌睡团子", "俏皮眨眼"];
    public static readonly string[] Backgrounds = ["#102A36", "#F3EFE4", "#101D18", "#29233B", "#102D46", "#FFF1DB", "#EDF3E5", "#142F58"];
    public static readonly string[] Accents = ["#91E0C5", "#247D78", "#A5EF7B", "#D5BBFA", "#7EDFF5", "#AE4029", "#366747", "#B8E0FF"];
    public static bool LightTheme(int theme) => theme is 1 or 5 or 6;
    static Brush B(string s) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
    static Brush WithOpacity(Brush brush, double opacity) { var value = brush.Clone(); value.Opacity = opacity; return value; }
    static StreamGeometry Shape(Action<StreamGeometryContext> draw)
    { var g = new StreamGeometry(); using (var c = g.Open()) draw(c); g.Freeze(); return g; }
    static void Curve(DrawingContext d, Brush color, double x, double y, double cx, double cy, double ex, double ey, double width = 2.6)
    { d.DrawGeometry(null, new Pen(color, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Shape(c => { c.BeginFigure(new Point(x, y), false, false); c.QuadraticBezierTo(new Point(cx, cy), new Point(ex, ey), true, false); })); }
    static void Spark(DrawingContext d, Brush color, double x, double y, double r)
    { d.DrawGeometry(color, null, Shape(c => { c.BeginFigure(new Point(x, y - r), true, true); c.LineTo(new Point(x + r * .3, y - r * .3), true, false); c.LineTo(new Point(x + r, y), true, false); c.LineTo(new Point(x + r * .3, y + r * .3), true, false); c.LineTo(new Point(x, y + r), true, false); c.LineTo(new Point(x - r * .3, y + r * .3), true, false); c.LineTo(new Point(x - r, y), true, false); c.LineTo(new Point(x - r * .3, y - r * .3), true, false); })); }
    static void Decor(DrawingContext d, int theme, Brush accent)
    {
        var subtle = WithOpacity(accent, .22); var faint = WithOpacity(accent, .11);
        switch (theme)
        {
            case 4:
                for (int row = 0; row < 3; row++) for (int x = 27; x < 292; x += 45)
                    Curve(d, x < 80 || x > 225 ? subtle : faint, x, 133 + row * 5, x + 11, 127 + row * 5, x + 23, 133 + row * 5, 1.2);
                d.DrawEllipse(null, new Pen(subtle, 1.1), new Point(66, 80), 4, 4); d.DrawEllipse(null, new Pen(subtle, 1.1), new Point(251, 106), 6, 6); break;
            case 5:
                d.DrawEllipse(faint, null, new Point(267, 99), 22, 22);
                for (int i = 0; i < 3; i++) d.DrawLine(new Pen(subtle, 1), new Point(239, 123 + i * 5), new Point(295, 123 + i * 5));
                Curve(d, subtle, 29, 109, 49, 102, 71, 109, 1.3); Curve(d, subtle, 41, 117, 60, 111, 82, 117, 1.3); break;
            case 6:
                for (int side = 0; side < 2; side++)
                {
                    double x = side == 0 ? 48 : 274;
                    Curve(d, subtle, x, 142, x - 5, 104, x + 9, 75, 1.5);
                    for (int i = 0; i < 3; i++)
                    { d.PushTransform(new RotateTransform(side == 0 ? -32 : 32, x, 94 + i * 16)); d.DrawEllipse(faint, new Pen(subtle, .8), new Point(x, 94 + i * 16), 9, 4); d.Pop(); }
                }
                break;
            case 7:
                for (int x = 28; x <= 292; x += 16) d.DrawLine(new Pen(faint, .6), new Point(x, 68), new Point(x, 140));
                for (int y = 68; y <= 140; y += 16) d.DrawLine(new Pen(faint, .6), new Point(28, y), new Point(292, y));
                d.DrawEllipse(null, new Pen(subtle, .8), new Point(59, 99), 18, 18);
                d.DrawLine(new Pen(subtle, .8), new Point(35, 99), new Point(83, 99)); d.DrawLine(new Pen(subtle, .8), new Point(59, 75), new Point(59, 123));
                break;
        }
    }
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d);
        d.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        double sx = ActualWidth / (PortraitOnly ? 160 : 320), sy = ActualHeight / (PortraitOnly ? 92 : 240);
        d.PushTransform(new ScaleTransform(sx, sy));
        if (PortraitOnly) d.PushTransform(new TranslateTransform(-80, -58));
        int t = Math.Clamp(Theme, 0, 7);
        Brush bg = B(Backgrounds[t]), accent = Accent == 0 ? B(Accents[t]) : new SolidColorBrush(Color.FromRgb(
            (byte)(((Accent >> 11) & 31) * 255 / 31), (byte)(((Accent >> 5) & 63) * 255 / 63), (byte)((Accent & 31) * 255 / 31)));
        Brush fg = B(LightTheme(t) ? "#203735" : "#ECF4F4"), muted = B(LightTheme(t) ? "#52655E" : "#B0C7CF");
        d.DrawRoundedRectangle(bg, new Pen(B(LightTheme(t) ? "#CAD4C8" : "#36505C"), 1), new Rect(0, 0, 320, 240), 12, 12);
        if (!PortraitOnly) Decor(d, t, accent);
        void T(string value, double x, double y, double size, Brush brush, bool bold = false)
        {
            var f = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush, 1);
            d.DrawText(f, new Point(x, y));
        }
        if (!PortraitOnly) {
        T("DESK BUDDY", 16, 12, 10, muted, true);
        T(!StatusKnown ? (Chinese ? "输出 未知" : "OUT —") : Armed ? (HidSuppressed ? (Chinese ? "开 · 暂停" : "ON · PAUSED") : (Chinese ? "输出 开" : "OUT ON")) : (Chinese ? "输出 关" : "OUT OFF"), 225, 12, 10, accent, true);
        d.DrawLine(new Pen(muted, .5), new Point(16, 37), new Point(304, 37));
        T(Route + (!StatusKnown ? "  •  " + (Chinese ? "读取中" : "READING") : Connected ? "  •  " + (Chinese ? "已连接" : "CONNECTED") : "  •  " + (Chinese ? "未连接" : "NO LINK")), 16, 44, 10, muted);
        T(Battery < 0 ? "~ --%" : $"~ {Battery}%", 263, 44, 10, muted);
        }
        var sprite = Mode == 0 ? CompanionPortraits.Get(Face, SpriteFrame) : CompanionSprites.Get(Face, SpriteFrame);
        int scale = Mode == 0 ? 2 : 1;
        double w = sprite.PixelWidth * scale, h = sprite.PixelHeight * scale;
        int feedbackX = 0, feedbackY = 0;
        bool mirror = false;
        if (PhysicalFeedback && DemoPose == null && ForcedFrame == null && !Sleeping && PetActive == true)
        {
            int elapsed = (int)(PetElapsedMilliseconds + PetStatusAgeMilliseconds) % 1800, phase = elapsed / 180;
            feedbackY = phase % 2 == 1 ? 1 : -1;
            if (PetMotion == 2 && PetDirection != 0)
            {
                feedbackX = PetDirection * (Mode == 0 ? 2 : Math.Min(phase * 2, 18));
                mirror = Mode > 0 && PetDirection < 0;
            }
        }
        double spriteX = Math.Clamp(Math.Round(160 - w / 2 + (DemoPose?.X ?? feedbackX)), 82, 238 - w);
        double spriteY = Math.Clamp(Math.Round(118 - h / 2 + (DemoPose?.Y ?? feedbackY)), 87, 149 - h);
        if (mirror) d.PushTransform(new ScaleTransform(-1, 1, spriteX + w / 2, spriteY + h / 2));
        d.DrawImage(sprite, new Rect(spriteX, spriteY, w, h));
        if (mirror) d.Pop();
        if (Sleeping)
        {
            if (DemoPose is { } sleepPose) T(sleepPose.ZCount == 1 ? "Z" : sleepPose.ZCount == 2 ? "Zz" : "Zzz", 210, 122, 12, accent, true);
            else { T("Z", 217, 72, 14, accent, true); if ((DemoPose?.ZCount ?? 3) >= 2) T("z", 232, 66, 11, accent, true); if ((DemoPose?.ZCount ?? 3) >= 3) T("z", 244, 61, 9, accent, true); }
        }
        if (Heart)
        {
            // Static pixel heart remains visible with Animation OFF.
            string[] pixels = ["01100110", "11111111", "11111111", "01111110", "00111100", "00011000"];
            var pink = B(LightTheme(t) ? "#B93054" : "#FF91B0");
            for (int y = 0; y < pixels.Length; y++) for (int x = 0; x < pixels[y].Length; x++)
                if (pixels[y][x] == '1') d.DrawRectangle(pink, null, new Rect(220 + x * 2, 79 + y * 2 + (DemoPose?.HeartY ?? 0), 2, 2));
            Spark(d, accent, 111, 80, 3);
        }
        if (!PortraitOnly) {
        T(LocalDemo ? (Chinese ? "动作试看" : "ACTION PREVIEW") : Profile.Length > 15 ? Profile[..15] : Profile, 16, 149, 11, fg, true);
        if (Mode == 2)
        {
            T((Chinese ? "本次互动 " : "SESSION ") + (PetInteractions?.ToString() ?? "—"), 184, 149, 10, accent, true);
            T(Sleeping ? (Chinese ? "休息一下 · 下次输入会唤醒" : "Resting · next input wakes me") : PetActive == true ? (PetMotion == 2 ? (Chinese ? "动一动，继续陪你" : "A little walk with you.") : (Chinese ? "刚刚和你打了个招呼" : "Happy to see you!")) : PetActive == false ? (Chinese ? "陪你专注，等待下一次互动" : "Keeping you company.") : (Chinese ? "互动状态未知 · 仅本次会话" : "Activity unknown · session only"), 16, 166, 11, muted);
        }
        else if (Sleeping) T(Chinese ? "休息一下 · 下次输入会唤醒" : "Resting · next input wakes me", 16, 166, 11, muted);
        else if (Heart) T(Chinese ? "收到你的招呼了" : "A little hello, just for you", 16, 166, 11, muted);
        else T(Mode == 1 ? (Chinese ? "桌面伙伴 · 静静陪伴" : "Desk companion · keeping you company") : Chinese ? "你的桌面小伙伴" : "A little focus, a little joy.", 16, 166, 11, muted);
        string[] caps = Chinese ? ["上一首", "播放 / 暂停", "下一首"] : ["PREV", "PLAY / PAUSE", "NEXT"];
        for (int i = 0; i < 3; i++)
        {
            double x = 16 + i * 99;
            d.DrawRoundedRectangle(null, new Pen(muted, .7), new Rect(x, 198, 90, 28), 5, 5);
            T(caps[i], x + (i == 1 ? 9 : 24), 206, 9, fg);
        }
        }
        if (PortraitOnly) d.Pop();
        d.Pop(); d.Pop();
    }
}
