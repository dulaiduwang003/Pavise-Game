using System.Globalization;

namespace PaviseApp
{
	internal static class ManualMemoryText
	{
		internal static string Title
		{
			get
			{
				return T("极限内存清理", "Extreme memory cleanup", "強力メモリ解放");
			}
		}

		internal static string Button
		{
			get
			{
				return T("极限清理", "Deep clean", "強力解放");
			}
		}

		internal static string Hint
		{
			get
			{
				return T("全量回收", "Full cleanup", "全体を解放");
			}
		}

		internal static string Permission
		{
			get
			{
				return T("需要管理员权限", "Administrator rights required", "管理者権限が必要です");
			}
		}
		internal static string GameButton { get { return T("轻量清理", "Light clean", "軽量解放"); } }
		internal static string GameHint { get { return T("游戏中 · 仅回收 Pavise 自身内存", "In game · Pavise memory only", "ゲーム中 · Pavise自身のメモリのみ"); } }

		internal static string T(string chinese, string english, string japanese)
		{
			if (Lang.Cur != 0)
			{
				if (Lang.Cur != 2)
				{
					return english;
				}
				return japanese;
			}
			return chinese;
		}

		internal static string Stage(ManualMemoryStep step)
		{
			switch (step)
			{
			case ManualMemoryStep.PaviseMemory:
				return GameHint;
			case ManualMemoryStep.CombinePages:
				return T("合并重复页", "Combining duplicate pages", "重複ページを統合");
			case ManualMemoryStep.WorkingSets:
				return T("回收全局工作集", "Trimming all working sets", "全体のワーキングセットを縮小");
			case ManualMemoryStep.FileCache:
				return T("整理系统文件缓存", "Trimming system file cache", "システムファイルキャッシュを解放");
			case ManualMemoryStep.RegistryCache:
				return T("整理注册表缓存", "Reconciling registry cache", "レジストリキャッシュを整理");
			case ManualMemoryStep.ModifiedPages:
				return T("请求修改页写回", "Requesting modified-page writeback", "変更済みページの書き戻しを要求");
			default:
				return T("清空全部待机缓存", "Purging all standby lists", "すべてのスタンバイリストを解放");
			}
		}

		internal static string Capacity(ulong bytes)
		{
			return ((double)bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
		}

		internal static string Delta(long value)
		{
			return ((value >= 0) ? "+" : "-") + Capacity((ulong)((value >= 0) ? value : (-value)));
		}

		internal static string Outcome(ManualMemoryReport report)
		{
			if (report.Busy)
			{
				return T("已有清理正在运行", "A cleanup is already running", "解放処理は実行中です");
			}
			if (report.Cancelled)
			{
				return T("清理已中止", "Cleanup stopped", "解放を中止しました");
			}
			if (report.GameProtected && report.Completed)
				return T("轻量回收完成", "Light cleanup completed", "軽量解放完了");
			if (!report.Completed)
			{
				if (report.SucceededCount <= 0)
				{
					return T("清理未完成", "Cleanup incomplete", "未完了");
				}
				return T("部分完成", "Partially completed", "一部完了");
			}
			return T("清理完成", "Completed", "完了");
		}

		internal static string Summary(ManualMemoryReport report)
		{
			if (report.GameProtected) return Outcome(report) + " · " + GameHint;
			long? availableChangeBytes = report.AvailableChangeBytes;
			if (!availableChangeBytes.HasValue)
			{
				return Outcome(report) + " · " + T("内存读数不可用", "Memory readings unavailable", "メモリ値を取得できません");
			}
			return T("可用 ", "Available ", "利用可能 ") + Delta(availableChangeBytes.Value) + (report.Completed ? "" : (" · " + Outcome(report)));
		}
	}
}
