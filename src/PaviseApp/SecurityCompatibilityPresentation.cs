using System.Collections.Generic;
using System.Text;

namespace PaviseApp
{
    internal static class SecurityCompatibilityPresentation
    {
        internal const string HiddenSettingKey = "SecurityCompatibilityWarningHidden";

        internal static string T(string zh, string en)
        { return Lang.Cur == 1 ? en : zh; }

        internal static bool NeedsWarning(SecurityProductSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.ScanCompleted || snapshot.HasUnknownState) return true;
            foreach (SecurityProductInfo product in snapshot.Products)
                if (product == null || product.State != SecurityProductState.Inactive) return true;
            return false;
        }

        private static bool HasPotentiallyActiveProduct(SecurityProductSnapshot snapshot)
        {
            if (snapshot != null)
                foreach (SecurityProductInfo product in snapshot.Products)
                    if (product != null && product.State != SecurityProductState.Inactive) return true;
            return false;
        }

        internal static string Title(SecurityProductSnapshot snapshot)
        {
            return HasPotentiallyActiveProduct(snapshot)
                ? T("性能可能受限", "Performance may be limited")
                : T("安全软件状态未知", "Security software status unknown");
        }

        internal static string Summary(SecurityProductSnapshot snapshot)
        {
            var names = new List<string>();
            int relevant = 0;
            if (snapshot != null)
                foreach (SecurityProductInfo product in snapshot.Products)
                    if (product != null && product.State != SecurityProductState.Inactive)
                    {
                        relevant++;
                        if (names.Count < 3) names.Add(Name(product));
                    }
            string found = names.Count == 0 ? "" : string.Join(" / ", names.ToArray());
            if (relevant > names.Count) found += T(" 等", " and others");
            return Title(snapshot) + (found.Length == 0 ? "" : " · " + found) + "\n"
                + T("点击查看处理说明。仅启动时检查，局内不检测。",
                    "Click for guidance. Checked only at startup, never during matches.");
        }

        private static string Name(SecurityProductInfo product)
        {
            string name = (product.Name ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (name.Length == 0) return T("未命名安全产品", "Unnamed security product");
            return name.Length > 100 ? name.Substring(0, 100) + "…" : name;
        }

        private static string State(SecurityProductState state)
        {
            if (state == SecurityProductState.Active) return T("已启用", "Active");
            if (state == SecurityProductState.Inactive) return T("已停用", "Inactive");
            return T("状态未知", "Unknown");
        }

        internal static string Details(SecurityProductSnapshot snapshot)
        {
            var stringBuilder = new StringBuilder();
            var names = new List<string>();
            SecurityProductInfo[] products = snapshot == null ? new SecurityProductInfo[0] : snapshot.Products;
            foreach (SecurityProductInfo product in products)
                if (product != null) names.Add(Name(product) + (product.State == SecurityProductState.Active ? "" : " (" + State(product.State) + ")"));
            if (names.Count != 0)
                stringBuilder.AppendLine(T("检测到：", "Detected: ") + string.Join(T("、", ", "), names.ToArray()));
            if (snapshot == null || !snapshot.ScanCompleted || snapshot.HasUnknownState)
                stringBuilder.AppendLine(T("状态未能完整确认，不视为未安装。",
                    "Some states could not be confirmed; this does not mean no antivirus is installed."));
            else if (products.Length == 0)
                stringBuilder.AppendLine(T("本次未识别到杀毒软件。", "No antivirus was identified in this check."));
            stringBuilder.AppendLine();
            stringBuilder.AppendLine(T("安全软件可能影响 Pavise 的优化效果。",
                "Security software may affect Pavise's optimizations."));
            stringBuilder.AppendLine(T("将 Pavise 加入安全软件白名单；仍有异常时，查看行为防护和拦截记录。",
                "Add Pavise to its allowlist. If issues persist, check behavior-protection rules and blocking logs."));
            stringBuilder.AppendLine();
            stringBuilder.Append(T("加白不保证所有操作放行。",
                "Allowlisting may not permit every operation."));
            return stringBuilder.ToString();
        }
    }
}
