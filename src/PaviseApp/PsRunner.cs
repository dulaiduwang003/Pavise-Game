using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace PaviseApp
{
	internal static class PsRunner
	{
		public static bool Run(string script, string label, int timeoutMs, out string stdout)
		{
			return Run(script, label, timeoutMs, null, out stdout);
		}

		public static bool Run(string script, string label, int timeoutMs, IDictionary<string, string> args, out string stdout)
		{
			string failure;
			bool flag = RunCore(script, label, timeoutMs, args, out stdout, out failure);
			if (!flag)
			{
				Logger.Warn(failure);
			}
			return flag;
		}

		internal static bool RunWithFailure(string script, string label, int timeoutMs, out string stdout, out string failure)
		{
			return RunCore(script, label, timeoutMs, null, out stdout, out failure);
		}

		private static bool RunCore(string script, string label, int timeoutMs, IDictionary<string, string> args, out string stdout, out string failure)
		{
			stdout = "";
			failure = "";
			try
			{
				string s2 = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\r\n$OutputEncoding = [Console]::OutputEncoding\r\n$ProgressPreference = 'SilentlyContinue'\r\n" + (script ?? "") + "\r\nif (!$?) { exit 1 }\r\n";
				string text = Convert.ToBase64String(Encoding.Unicode.GetBytes(s2));
				ProcessStartInfo processStartInfo = new ProcessStartInfo();
				processStartInfo.FileName = "powershell.exe";
				processStartInfo.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -Command \"& ([ScriptBlock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + text + "'))))\"";
				processStartInfo.UseShellExecute = false;
				processStartInfo.RedirectStandardOutput = true;
				processStartInfo.RedirectStandardError = true;
				processStartInfo.CreateNoWindow = true;
				ProcessStartInfo processStartInfo2 = processStartInfo;
				processStartInfo2.StandardOutputEncoding = new UTF8Encoding(false);
				processStartInfo2.StandardErrorEncoding = new UTF8Encoding(false);
				if (args != null)
				{
					foreach (KeyValuePair<string, string> arg in args)
					{
						processStartInfo2.EnvironmentVariables[arg.Key] = arg.Value ?? "";
					}
				}
				using (Process process = Process.Start(processStartInfo2))
				{
					using (IDisposable disposable = null)
					{
						StringBuilder outBuf = new StringBuilder();
						StringBuilder errBuf = new StringBuilder();
						process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
						{
							if (e.Data != null)
							{
								lock (outBuf)
								{
									outBuf.AppendLine(e.Data);
								}
							}
						};
						process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
						{
							if (e.Data != null)
							{
								lock (errBuf)
								{
									errBuf.AppendLine(e.Data);
								}
							}
						};
						process.BeginOutputReadLine();
						process.BeginErrorReadLine();
						Stopwatch stopwatch = Stopwatch.StartNew();
						bool flag = process.WaitForExit(timeoutMs);
						if (!flag && timeoutMs != timeoutMs)
						{
							disposable.Dispose();
							int milliseconds = (int)Math.Max(0L, timeoutMs - stopwatch.ElapsedMilliseconds);
							flag = process.WaitForExit(milliseconds);
						}
						if (!flag)
						{
							try
							{
								process.Kill();
							}
							catch
							{
							}
							try
							{
								process.WaitForExit(3000);
							}
							catch
							{
							}
							failure = label + Lang.T("log.psrunner.1") + timeoutMs + "ms";
							return false;
						}
						process.WaitForExit();
						lock (outBuf)
						{
							stdout = outBuf.ToString();
						}
						if (process.ExitCode != 0)
						{
							string text2;
							lock (errBuf)
							{
								text2 = errBuf.ToString().Trim();
							}
							failure = label + Lang.T("log.psrunner.2") + process.ExitCode + " " + NormalizeError(text2);
							return false;
						}
						return true;
					}
				}
			}
			catch (Exception ex)
			{
				failure = label + Lang.T("log.psrunner.3") + NormalizeError(ex.Message);
				return false;
			}
		}

		internal static string NormalizeError(string text)
		{
			text = (text ?? "").Replace("#< CLIXML", "").Trim();
			int num = text.IndexOf("<Objs", StringComparison.Ordinal);
			if (num >= 0)
			{
				int num2 = text.IndexOf("</Objs>", num, StringComparison.Ordinal);
				if (num2 < 0)
				{
					return "Unrecognized PowerShell error output";
				}
				try
				{
					XmlReaderSettings xmlReaderSettings = new XmlReaderSettings();
					xmlReaderSettings.DtdProcessing = DtdProcessing.Prohibit;
					xmlReaderSettings.XmlResolver = null;
					XmlReaderSettings settings = xmlReaderSettings;
					XmlDocument xmlDocument = new XmlDocument();
					xmlDocument.XmlResolver = null;
					XmlDocument xmlDocument2 = xmlDocument;
					using (StringReader input = new StringReader(text.Substring(num, num2 + 7 - num)))
					{
						using (XmlReader reader = XmlReader.Create(input, settings))
						{
							xmlDocument2.Load(reader);
						}
					}
					StringBuilder stringBuilder = new StringBuilder(text.Substring(0, num));
					foreach (XmlNode item in xmlDocument2.GetElementsByTagName("S"))
					{
						XmlAttribute xmlAttribute = ((item.Attributes == null) ? null : item.Attributes["S"]);
						if (xmlAttribute != null && (!(xmlAttribute.Value != "Error") || !(xmlAttribute.Value != "Warning")))
						{
							stringBuilder.Append(' ').Append(Regex.Replace(item.InnerText, "_x([0-9a-fA-F]{4})_", (Match match) => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString()));
						}
					}
					stringBuilder.Append(' ').Append(text.Substring(num2 + 7));
					text = stringBuilder.ToString();
				}
				catch (XmlException)
				{
					return "Unrecognized PowerShell error output";
				}
			}
			text = Regex.Replace(text, "\\s+", " ").Trim();
			if (text.Length > 2000)
			{
				return text.Substring(0, 2000) + "…";
			}
			return text;
		}
	}
}
