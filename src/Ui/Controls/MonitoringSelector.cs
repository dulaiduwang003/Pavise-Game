// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    // 监控页下拉选择器：TechSelect 的薄封装，保留历史 API、按语言实时取的"当前"徽章与 Escape 始终吞掉的键盘行为。
    internal sealed class MonitoringSelector : TechSelect
    {
        internal MonitoringSelector() { }

        public override string CurrentBadge { get { return DsLabels.Pick("当前", "Current"); } }

        protected override bool ConsumeEscapeWithoutMenu { get { return true; } }

        internal Rectangle SelectionTextBounds { get { return TextBounds; } }

#if PAVISE_SELFTEST
        // 返回托管中的缓存菜单（无则构建），不定位不显示，禁用时也可用。
        internal ContextMenuStrip CreateMenuForTest() { return IsDisposed ? null : EnsureMenu(); }
#endif
    }
}
