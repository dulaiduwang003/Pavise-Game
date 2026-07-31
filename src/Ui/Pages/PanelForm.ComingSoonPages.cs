// @author bdth 2074055628@qq.com
// 文件用途 构建三角洲行动与 CS2 的预留专栏页 上线前以模糊占位展示

using System.Drawing;

namespace AegisApp
{
    internal partial class PanelForm
    {
        private void BuildComingSoonPages()
        {
            BuildComingSoonPage(pageDelta, LolText("三角洲行动专栏"),
                LolText("深度接管正在开发。现在把游戏加入游戏库，即可获得压制、提优与帧证据链等全部通用优化。"),
                Color.FromArgb(96, 202, 128));
            BuildComingSoonPage(pageCs2, LolText("CS2 专栏"),
                LolText("平台治理与深度接管正在开发。现在把游戏加入游戏库，即可获得压制、提优与帧证据链等全部通用优化。"),
                Color.FromArgb(255, 150, 64));
        }

        private void BuildComingSoonPage(DBPanel page, string title, string subtitle, Color accent)
        {
            int y = PageHeader(page, title, subtitle, 2);
            var preview = new ComingSoonPreview(title, LolText("自测完毕后随 v1.6.0 发布"), accent);
            preview.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            page.Controls.Add(preview);
        }
    }
}
