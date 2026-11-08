using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class RssSteer
	{
		private const string ReceiptKey = "RssSteerReceipt";

		private static readonly object lk = new object();

		private static readonly string RestoreScript = string.Join("\r\n", "$ErrorActionPreference = 'SilentlyContinue'", "$ok = $true", "foreach ($rec in $env:PAVISE_RSS_RECEIPT -split ';') {", "    $f = $rec -split '\\|'", "    if ($f.Count -ne 3) { continue }", "    $n = $f[0]", "    if ($null -eq (Get-NetAdapter -Name $n -ErrorAction SilentlyContinue)) { continue }", "    try { Set-NetAdapterRss -Name $n -BaseProcessorNumber ([byte][int]$f[1]) -MaxProcessorNumber ([byte][int]$f[2]) -ErrorAction Stop } catch { $ok = $false; continue }", "    $chk = Get-NetAdapterRss -Name $n", "    if ($null -eq $chk -or $chk.BaseProcessorNumber -ne [int]$f[1]) { $ok = $false }", "}", "if ($ok) { Write-Output 'RESTOREOK' } else { Write-Output 'RESTOREFAIL' }");

		public static bool HasResidue
		{
			get
			{
				return Settings.LoadStr("RssSteerReceipt", "").Length > 0;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				string rssSteerReceiptValue = Settings.LoadStr("RssSteerReceipt", "");
				if (rssSteerReceiptValue.Length == 0)
				{
					return true;
				}
				if (!RestoreWithReceipt(rssSteerReceiptValue))
				{
					Logger.Log(Lang.T("log.rsssteer.5"));
					return false;
				}
				Settings.SaveStr("RssSteerReceipt", "");
				Logger.Log(Lang.T("log.rsssteer.6"));
				return true;
			}
		}

		public static void HealFromCrash()
		{
			if (HasResidue)
			{
				Restore();
			}
		}

		private static bool RestoreWithReceipt(string receipt)
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>();
			dictionary.Add("PAVISE_RSS_RECEIPT", receipt);
			Dictionary<string, string> args = dictionary;
			string stdout;
			if (!PsRunner.Run(RestoreScript, "rss-steer-restore", 30000, args, out stdout))
			{
				return false;
			}
			return stdout.Contains("RESTOREOK");
		}

		internal static string ExtractReceipt(string output)
		{
			if (string.IsNullOrEmpty(output))
			{
				return null;
			}
			string[] outputParts = output.Split('\n');
			foreach (string entryText in outputParts)
			{
				string entryTextTrimmed = entryText.Trim();
				if (entryTextTrimmed == "NONE")
				{
					return "";
				}
				if (entryTextTrimmed.StartsWith("RECEIPT:", StringComparison.Ordinal))
				{
					return entryTextTrimmed.Substring("RECEIPT:".Length);
				}
			}
			return null;
		}
	}
}
