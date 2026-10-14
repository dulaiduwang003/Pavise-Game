// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;

namespace PaviseApp
{
    // 设计系统令牌：尺寸、间距、字体、语义色。全部按访问时的 DPI 换算，禁止缓存像素值。
    internal static class DsSpace
    {
        public const int Hair = 1, XS = 2, S = 4, M = 8, L = 12, XL = 16, XXL = 24;

        public static int Px(int logical) { return Theme.S(logical); }

        // 线宽至少 1 像素，避免高 DPI 以外的缩放把线画没。
        public static float Line(int logical) { return Math.Max(1f, Theme.S(logical)); }
    }

    // 各组件的逻辑度量，数值与原控件逐像素一致。
    internal static class DsMetrics
    {
        internal static class Button
        {
            public const int Height = 34;
            public const int MinCut = 6;          // 切角 = max(MinCut, Height / 4)
            public const int FocusInset = 5;
            public const int EnergyInset = 10;     // 危险按钮左侧能量线上下留白
            public const int GlyphSize = 16;       // 图标按钮内图标边长
        }

        internal static class Toggle
        {
            public const int Width = 46, Height = 30;
            public const int TrackHeight = 22, TrackPad = 3;
            public const int TrackWithText = 44, MinTrack = 20;
            public const int TextGap = 12;
            public const int FocusInset = 2;
        }

        internal static class Tabs
        {
            public const int TabWidth = 122, Gap = 6, Cut = 9;
            public const int Top = 2, BottomPad = 5;
            public const int TextLeft = 14, NumberGap = 7, RightPad = 10;
            public const int HotDotX = 6, HotDotY = 5, HotDot = 5;
            public const int HintGap = 8, HintRightPad = 10, HintMinWidth = 60;
            public const int FocusInset = 4;
        }

        internal static class Tier
        {
            public const int Gap = 4, Cut = 6, FocusInset = 3;
        }

        internal static class Card
        {
            public const int Radius = 12;
            public const int PadLeft = 42;
            public const int HostRight = 18, HostGap = 14, HostTop = 10;
            public const int ChevronWidth = 20, ChevronArm = 4, ChevronTop = 22;
            public const int TitleTop = 11, TitleTopWithStatus = 9, TitleHeight = 22;
            public const int ValuePad = 16;
            public const int StatusLine = 17, StatusDot = 6, StatusDotTop = 5, StatusGap = 7;
            public const int MetaPad = 20, MetaBottom = 26, MetaHeight = 16;
            public const int DescTop = 2, DescBottom = 9;
            public const int RailX = 30, RailInset = 11, LiveTop = 17, LiveBottom = 37, LiveMargin = 12;
            public const int CodeX = 7, CodeY = 9, CodeW = 18, CodeH = 15;
            public const int BladeReach = 170, BladeTail = 48, BladeTopInset = 10, BladeDrop = 11;
            public const int TopLineReach = 58;
            public const int BadgeGap = 9, BadgeRightMargin = 8;
        }

        internal static class Badge
        {
            public const int Height = 17, Pad = 12, Cut = 4;
        }

        internal static class Link
        {
            public const int FrameTop = 2, FrameBottomInset = 4, Cut = 8;
            public const int BladeReach = 44, BladeShift = 5, BladeTail = 21;
            public const int Socket = 26, SocketX = 9, SocketCut = 5, GlyphInset = 6, Glyph = 14;
            public const int TextX = 43, RightPad = 36, CodeY = 7, CodeH = 12, TitleY = 18, TitleH = 22;
            public const int RailBottom = 4, RailBase = 30, RailReach = 44, Node = 4;
            public const int MarkSide = 9, ForwardX = 24, ExternalX = 26, Notch = 3;
            public const int DotSize = 7, DotRight = 17, DotTop = 9, DotRing = 2;
            public const int FocusInset = 4;
        }

        internal static class Menu
        {
            public const int Cut = 8, AccentLineStart = 2, AccentLineEnd = 26;
            public const int SeparatorInset = 12;
            public const int SelectionBar = 3;
            public const int TextLeft = 12, TextRightPad = 18, BadgeGap = 8;
            public const int ItemHeight = 34, Padding = 6, MinWidth = 170;
            public const int ScreenMargin = 20, EdgeMargin = 6, Gap = 4;
        }

        internal static class Select
        {
            public const int Width = 220, Height = 32, Cut = 7;
            public const int TextLeft = 12, TextRightPad = 46;
            public const int ArrowRight = 19, ArrowHalf = 4;
        }

        internal static class Dialog
        {
            public const int Width = 468, PadX = 26;
            public const int TagTop = 17, TagHeight = 16, TagWidth = 74, TagSkew = 9;
            public const int TitleTop = 43, BodyTop = 75;
            public const int ButtonHeight = 34, ButtonGap = 10, BottomPad = 22;
            public const int AcceptWidth = 108, CancelWidth = 96;
            public const int ExtraGap = 12, FooterPad = 16, SeparatorRise = 14;
            public const int BarHeight = 4, SweepBand = 150;
            public const int MinBody = 28, BodySlack = 10, ScreenMargin = 40;
            public const int FeedbackHeight = 58;
        }
    }

    // 字体角色：界面字体用 Theme.UI，等宽字体用 Theme.Mono，中文自动回退。
    internal enum DsText { Body, BodyLarge, Title, Heading, Label, Description, Status, Hint, Tag, Caption }
    internal enum DsMono { Code, Badge, TabNumber, Meta, Channel, Surface }

    internal static class DsType
    {
        public static float Size(DsText role)
        {
            switch (role)
            {
                case DsText.BodyLarge: return 9.5f;
                case DsText.Title: return 9.75f;
                case DsText.Heading: return 12.5f;
                case DsText.Label: return 8.25f;
                case DsText.Description: return 8.5f;
                case DsText.Status: return 8.2f;
                case DsText.Hint: return 7.8f;
                case DsText.Tag: return 7.2f;
                case DsText.Caption: return 7.9f;
                default: return 9f;
            }
        }

        public static float MonoSize(DsMono role)
        {
            switch (role)
            {
                case DsMono.Badge: return 7.0f;
                case DsMono.TabNumber: return 7.5f;
                case DsMono.Meta: return 7f;
                case DsMono.Channel: return 5.8f;
                case DsMono.Surface: return 5.4f;
                default: return 5.5f;
            }
        }

        public static Font Font(DsText role, bool bold) { return Theme.UI(Size(role), bold); }
        public static Font Font(DsText role) { return Theme.UI(Size(role), false); }
        public static Font Mono(DsMono role) { return Theme.Mono(MonoSize(role)); }
        public static Font MonoFor(DsMono role, string text) { return Theme.MonoFor(text, MonoSize(role)); }

        // 单行文本高度，用于说明文字的整行裁切。
        public static int LineHeight(DsText role)
        {
            return System.Windows.Forms.TextRenderer.MeasureText("Ag", Font(role)).Height;
        }
    }

    // 语义色调：随主题与模式色实时变化，不得缓存。
    internal enum DsTone { Neutral, Accent, Success, Warning, Danger }

    internal static class DsPalette
    {
        public static Color Ink(DsTone tone)
        {
            switch (tone)
            {
                case DsTone.Accent: return Theme.Accent;
                case DsTone.Success: return Theme.Green;
                case DsTone.Warning: return Theme.Warning;
                case DsTone.Danger: return Theme.Danger;
                default: return Theme.Fg;
            }
        }

        // 对话框警示条沿用的高亮橙色，深浅主题共用。
        public static Color Alert { get { return Color.FromArgb(255, 176, 32); } }

        public static Color KnobOn { get { return Color.White; } }
        public static Color KnobOff { get { return Color.FromArgb(178, 183, 192); } }

        // 深色主题下危险按钮的底色区间。
        public static Color DangerTopDark { get { return Color.FromArgb(38, 19, 24); } }
        public static Color DangerBottomDark { get { return Color.FromArgb(21, 15, 18); } }
    }

    // 透明度令牌：禁用遮罩、按压压暗、悬停高光。
    internal static class DsAlpha
    {
        public const int VeilButton = 158;
        public const int VeilToggle = 105;
        public const int VeilTierLight = 104, VeilTierDark = 138;
        public const int PressDarken = 45;
        public const int HoverLight = 16;
        public const int KnobShadow = 70;
        public const int BusyRail = 110, BusySweep = 235;

        public static int VeilTier { get { return Theme.LightMode ? VeilTierLight : VeilTierDark; } }
    }
}
