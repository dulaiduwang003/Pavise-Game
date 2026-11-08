using System;

namespace PaviseApp
{
	internal static class ReleaseNotes
	{
		private const string SeenKey = "LastSeenNotesVersion";

		public static readonly ReleaseNote[] All = new ReleaseNote[1]
		{
            new ReleaseNote("3.3.0.8", "2026-11-08", new string[][] {
                new[] { "渲染亲和性支持自动/手动选核与整核隔离，联动游戏核心范围；调整下局生效，关闭立即撤回。",
                    "Added automatic/manual render CPU selection with whole-core isolation. Choices follow the game CPU range; edits apply next session and disabling withdraws the binding immediately.",
                    "描画スレッドの自動・手動コア選択と物理コア単位の分離に対応。候補はゲームのCPU範囲に従い、変更は次のセッションから適用、無効化時は直ちに固定を撤回します。" },
                new[] { "减少线程采样、核心隔离和后台治理开销，移除自动后台节能显卡登记。",
                    "Reduced thread-sampling, core-isolation and background-governance overhead; removed automatic background power-saving GPU enrollment.",
                    "スレッド採取、コア分離、バックグラウンド制御の負荷を軽減。バックグラウンド向け省電力GPUの自動登録を削除しました。" },
                new[] { "新增动作/诊断日志，直观查看压制、落核、清核及恢复结果，支持分别导出。",
                    "Added Action/Diagnostic logs for suppression, CPU placement, core isolation and restoration, with separate exports.",
                    "動作・診断ログを追加。抑制、CPU配置、コア分離、復元の結果を確認でき、個別に出力できます。" },
                new[] { "优化鸣潮等游戏识别，减少内置网页被误认作游戏本体。",
                    "Improved game detection for Wuthering Waves and others to reduce embedded web views being mistaken for the game.",
                    "鳴潮などのゲーム検出を改善し、内蔵Web画面をゲーム本体と誤認するケースを減らしました。" },
                new[] { "改善笔记本厂商电源兼容，连续冲突时停止切换并恢复原计划。",
                    "Improved compatibility with laptop vendor power controls; repeated conflicts stop plan switching and restore the original plan.",
                    "ノートPCメーカーの電源制御との互換性を改善。競合が続く場合は電源プランの切り替えを停止し、元のプランに復元します。" },
                new[] { "新增安全软件兼容提示，启动时检查一次，支持“不再显示”。",
                    "Added a security-software compatibility notice checked once at startup, with a Don't show again option.",
                    "起動時に1回だけ確認するセキュリティソフト互換性通知を追加。「今後表示しない」に対応しました。" },
                new[] { "修复退局恢复、电源状态误报，减少重复日志和无效警告。",
                    "Fixed false restoration and power-state reports, and reduced repetitive logs and unnecessary warnings.",
                    "ゲーム終了後の復元と電源状態の誤報を修正。重複ログと不要な警告を削減しました。" },
            }),

		};

		public static ReleaseNote Current
		{
			get
			{
				ReleaseNote[] all = All;
				foreach (ReleaseNote releaseNote in all)
				{
					if (string.Equals(releaseNote.Version, App.Version, StringComparison.OrdinalIgnoreCase))
					{
						return releaseNote;
					}
				}
				return null;
			}
		}

		public static bool HasUnseen
		{
			get
			{
				return !string.Equals(Settings.LoadStr(SeenKey, ""), App.Version, StringComparison.OrdinalIgnoreCase);
			}
		}

		public static bool MarkSeen()
		{
			string saved;
			return Settings.SaveStrDurable(SeenKey, App.Version)
				&& Settings.TryLoadStr(SeenKey, out saved)
				&& string.Equals(saved, App.Version, StringComparison.OrdinalIgnoreCase);
		}
	}
}
