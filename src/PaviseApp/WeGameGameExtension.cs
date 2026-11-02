using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
	internal sealed class WeGameGameExtension : GameExtensionModule
	{
		internal const string PreferencePrefix = "WgShellProfileV2_";
		private readonly object gate = new object();
		private WeGameShellService service;
		private bool started;
		private bool stopped;

		internal static bool IsLeague(GameProfile profile)
		{
			return profile != null && (LolGameExtension.MentionsLol(profile.Name)
				|| LolGameExtension.MentionsLol(profile.Root)
				|| LolGameExtension.MentionsLol(profile.ExecutablePath)
				|| LolGameExtension.MentionsLol(profile.LearnedExecutablePath));
		}

		internal static bool HasWeGameLocation(GameProfile profile)
		{
			return profile != null && (HasGameDirectory(profile.Root)
				|| HasGameDirectory(profile.ExecutablePath) || HasGameDirectory(profile.LearnedExecutablePath));
		}

		private static bool HasGameDirectory(string path)
		{
			if (string.IsNullOrEmpty(path)) return false;
			try
			{
				if (!Path.IsPathRooted(path)) return false;
				string normalized = Path.GetFullPath(path).Replace('/', '\\');
				string[] parts = normalized.Split('\\');
				for (int i = 0; i < parts.Length - 1; i++)
					if (string.Equals(parts[i], "WeGameApps", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(parts[i], "WeGameLauncher", StringComparison.OrdinalIgnoreCase)) return true;
			}
			catch (Exception) { }
			return false;
		}

		internal static string ProfileBinding(GameProfile profile)
		{
			if (profile == null || string.IsNullOrEmpty(profile.Id)) return "";
			try
			{
				if ((!string.IsNullOrEmpty(profile.Root) && !Path.IsPathRooted(profile.Root))
					|| (!string.IsNullOrEmpty(profile.ExecutablePath) && !Path.IsPathRooted(profile.ExecutablePath))) return "";
				string root = string.IsNullOrEmpty(profile.Root) ? "" : Path.GetFullPath(profile.Root).TrimEnd('\\', '/').ToUpperInvariant();
				string exe = string.IsNullOrEmpty(profile.ExecutablePath) ? "" : Path.GetFullPath(profile.ExecutablePath).ToUpperInvariant();
				if (root.Length == 0 && exe.Length == 0) return "";
				using (SHA256 hash = SHA256.Create())
					return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(root + "\n" + exe))).Replace("-", "");
			}
			catch (Exception) { return ""; }
		}

		internal static bool AppliesToProfile(GameProfile profile)
		{
			if (profile == null || string.IsNullOrEmpty(profile.Id) || IsLeague(profile)) return false;
			string binding = ProfileBinding(profile);
			if (binding.Length == 0) return false;
			string saved = Settings.LoadStr(PreferencePrefix + profile.Id, "");
			if (saved == "1|" + binding) return true;
			if (saved == "0|" + binding) return false;
			return HasWeGameLocation(profile);
		}

		internal static bool SetProfileEnabled(GameProfile profile, bool enabled)
		{
			string binding = ProfileBinding(profile);
			if (binding.Length == 0 || IsLeague(profile)) return false;
			string value = (enabled ? "1|" : "0|") + binding;
			if (!Settings.SaveStr(PreferencePrefix + profile.Id, value)) return false;
			if (Settings.LoadStr(PreferencePrefix + profile.Id, "") != value) return false;
			CompositeGameExtension composite = GameExtension.Current as CompositeGameExtension;
			if (composite != null)
				foreach (GameExtensionModule part in composite.Modules)
				{
					WeGameGameExtension module = part as WeGameGameExtension;
					if (module != null) module.CancelIneligibleSession(profile);
				}
			return true;
		}

		internal static bool CanAutoDetachRenderer(GameDetection detection, bool observed)
		{
			if (!observed || detection == null || detection.Profile == null || detection.Profile.ForceTrigger
				|| !detection.RendererCandidateSelected || detection.RendererSafetyOnly || detection.RequiresGpuConfirm
				|| detection.RendererPid <= 4 || detection.RendererCreation <= 0 || !AppliesToProfile(detection.Profile)) return false;
			try
			{
				if (string.IsNullOrEmpty(detection.RendererPath) || string.IsNullOrEmpty(detection.Profile.ExecutablePath)
					|| !Path.IsPathRooted(detection.RendererPath) || !Path.IsPathRooted(detection.Profile.ExecutablePath)
					|| !string.Equals(Path.GetFullPath(detection.RendererPath), Path.GetFullPath(detection.Profile.ExecutablePath), StringComparison.OrdinalIgnoreCase)
					|| !string.Equals(detection.RendererName, Path.GetFileNameWithoutExtension(detection.RendererPath), StringComparison.OrdinalIgnoreCase)) return false;
				return !GameSessionDetector.ElectionVetoed(detection.RendererName, detection.RendererPath)
					&& !AntiCheatCatalog.IsAntiCheatProcess(detection.RendererName, detection.RendererPath);
			}
			catch (Exception) { return false; }
		}

		private void CancelIneligibleSession(GameProfile profile)
		{
			lock (gate)
				if (service != null && !AppliesToProfile(profile))
					service.CancelProfile(profile);
		}

		private WeGameShellService GetService()
		{
			lock (gate)
			{
				if (stopped) return null;
				if (service == null) service = new WeGameShellService();
				if (started) service.Start();
				return service;
			}
		}

		public override void Start()
		{
			lock (gate)
			{
				if (stopped) return;
				started = true;
				if (service != null) service.Start();
			}
		}

		public override bool AppliesTo(GameProfile profile) { return AppliesToProfile(profile); }

		public override GameCardExtension CreateCard(GameProfile profile)
		{
			if (!AppliesToProfile(profile)) return null;
			WeGameShellService current = GetService();
			return current == null ? null : new WeGameCardPanel(current, profile);
		}

		public override void NotifySession(GameProfile profile, int rendererPid, long rendererCreation, bool active)
		{
			if (active && AppliesToProfile(profile))
			{
				WeGameShellService current = GetService();
				if (current != null) current.NotifySession(profile, rendererPid, rendererCreation, true);
			}
			else lock (gate)
				if (service != null) service.NotifySession(null, 0, 0, false);
		}

		public override void Shutdown(int waitMs)
		{
			WeGameShellService current;
			lock (gate) { stopped = true; started = false; current = service; service = null; }
			if (current != null) current.Dispose();
		}
	}
}
