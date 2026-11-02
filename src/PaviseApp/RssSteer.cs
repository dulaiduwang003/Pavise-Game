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
				string text = Settings.LoadStr("RssSteerReceipt", "");
				if (text.Length == 0)
				{
					return true;
				}
				if (!RestoreWithReceipt(text))
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
			string[] array = output.Split('\n');
			foreach (string text in array)
			{
				string text2 = text.Trim();
				if (text2 == "NONE")
				{
					return "";
				}
				if (text2.StartsWith("RECEIPT:", StringComparison.Ordinal))
				{
					return text2.Substring("RECEIPT:".Length);
				}
			}
			return null;
		}
	}
}
