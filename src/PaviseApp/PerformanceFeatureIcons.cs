using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
	internal static class PerformanceFeatureIcons
	{
		private enum Kind
		{
			Fallback,
			Preset,
			Boost,
			Cpu,
			Memory,
			Io,
			Gpu,
			Core,
			Thread,
			Awake,
			Background,
			Family,
			GpuDown,
			Download,
			Update,
			Maintenance,
			Service,
			Power,
			CacheWarm,
			CleanMemory,
			Desktop,
			English,
			NvPerformance,
			Shader,
			LowLatency,
			Rebar,
			Fullscreen,
			Intel,
			GpuPower,
			Laptop,
			Smooth,
			Upscale,
			AntiLag,
			Interpolate,
			RenderLane,
			FrameSync,
			Escalate,
			Idle,
			Trim,
			Mitigations,
			ShortNames,
			BackgroundFps,
			OpenGl,
			Visual,
			Multimedia,
			GpuPreference,
			GpuEconomy,
			Hags,
			Windowed,
			Vrr,
			NvVrr,
			Sam,
			Recording,
			TimerTick,
			GlobalTimer,
			CpuMitigations,
			LastAccess,
			Quantum,
			StorageIdle,
			StorageRuntime,
			StorageCold,
			Audio,
			Environment,
			Storage,
			NtfsMemory,
			Diagnostics,
			Ceip,
			Appraiser,
			ErrorReport,
			SecurityReport,
			InputPrivacy,
			Personalization,
			Compatibility,
			Archives,
			LegacyDefender,
			LegacySession,
			Games,
			Whitelist,
			AntiCheat,
			Recovery,
			EppYield,
			GpuClockLock,
			GpuPowerCeiling,
			AudioBuffer,
			CpuIdlePolicy,
            FileFilterReduction,
            ClockConfigurationRepair,
            AudioEffectsReduction
		}

		private static readonly Dictionary<string, Kind> Designs = BuildDesigns();

		private static readonly Dictionary<string, string> Aliases = BuildAliases();

		private static readonly string[] ControlPrefixes = new string[4] { "manualPerformance_", "manualDestination_", "privacyToggle", "environment" };

		internal static string[] IconKeys
		{
			get
			{
				string[] textEntries = new string[Designs.Count];
				Designs.Keys.CopyTo(textEntries, 0);
				Array.Sort(textEntries, StringComparer.Ordinal);
				return textEntries;
			}
		}

		internal static bool Supports(string key)
		{
			return Designs.ContainsKey(Canonical(key));
		}

		internal static void Draw(Graphics g, Rectangle bounds, string key, Color ink)
		{
			if (g == null || bounds.Width <= 0 || bounds.Height <= 0)
			{
				return;
			}
			Kind value;
			if (!Designs.TryGetValue(Canonical(key), out value))
			{
				value = Kind.Fallback;
			}
			GraphicsState gstate = g.Save();
			try
			{
				g.SetClip(bounds, CombineMode.Intersect);
				float minRatio = (float)Math.Min(bounds.Width, bounds.Height) / 32f;
				g.TranslateTransform((float)bounds.Left + ((float)bounds.Width - 32f * minRatio) / 2f, (float)bounds.Top + ((float)bounds.Height - 32f * minRatio) / 2f);
				g.ScaleTransform(minRatio, minRatio);
				g.SmoothingMode = SmoothingMode.AntiAlias;
				g.PixelOffsetMode = PixelOffsetMode.HighQuality;
				using (Pen pen = new Pen(ink, 1.65f))
				{
					using (Pen pen2 = new Pen(Color.FromArgb(Math.Max(1, ink.A * 3 / 5), ink), 1.15f))
					{
						using (SolidBrush fill = new SolidBrush(ink))
						{
							LineCap startCap = (pen.EndCap = LineCap.Round);
							pen.StartCap = startCap;
							pen.LineJoin = LineJoin.Round;
							LineCap startCap2 = (pen2.EndCap = LineCap.Round);
							pen2.StartCap = startCap2;
							pen2.LineJoin = LineJoin.Round;
							Paint(g, value, pen, pen2, fill);
						}
					}
				}
			}
			finally
			{
				g.Restore(gstate);
			}
		}

		private static Dictionary<string, Kind> BuildDesigns()
		{
			Dictionary<string, Kind> dictionary = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase);
			Add(dictionary, Kind.Preset, "PerformancePreset");
			Add(dictionary, Kind.Boost, "GmBoost");
			Add(dictionary, Kind.Cpu, "GmBoostCpuV1");
			Add(dictionary, Kind.Memory, "GmBoostMemoryV1");
			Add(dictionary, Kind.Io, "GmBoostIoV1");
			Add(dictionary, Kind.Gpu, "GmBoostGpuV1");
			Add(dictionary, Kind.Core, "GmAutoCoresV2");
			Add(dictionary, Kind.Thread, "GmThreadBoostStrategyV1");
			Add(dictionary, Kind.Awake, "GmAwake");
			Add(dictionary, Kind.Background, "GmSuppress", "EnvPrivacyBackgroundGlobalV1");
			Add(dictionary, Kind.Family, "GameFamilyBackground");
			Add(dictionary, Kind.GpuDown, "GmGpuDemote");
			Add(dictionary, Kind.Download, "GmPauseDl");
			Add(dictionary, Kind.Update, "GmPauseUpdate");
			Add(dictionary, Kind.Maintenance, "GmPauseMaintenanceV1");
			Add(dictionary, Kind.Service, "GmPauseServices", "EnvPrivacyServiceV1");
			Add(dictionary, Kind.Power, "PowerPlanOn");
			Add(dictionary, Kind.CacheWarm, "GmCacheWarmV2");
			Add(dictionary, Kind.CleanMemory, "GmStandbyCleanerV2");
			Add(dictionary, Kind.Desktop, "GmDwmBoost");
			Add(dictionary, Kind.English, "GmEnglishInputV1");
			Add(dictionary, Kind.NvPerformance, "NvMaxPerf");
			Add(dictionary, Kind.Shader, "NvShaderCache");
			Add(dictionary, Kind.LowLatency, "NvLowLat");
			Add(dictionary, Kind.Rebar, "NvRebar", "GameNvRebarV1");
			Add(dictionary, Kind.Fullscreen, "GameDisableFullscreenOptimizationsV1");
			Add(dictionary, Kind.Intel, "GmIntelEnduranceV1");
			Add(dictionary, Kind.GpuPower, "GmGpuPowerMax");
			Add(dictionary, Kind.Laptop, "GmLaptopPerfV1", "AutoLaptopPerformanceV2");
			Add(dictionary, Kind.Smooth, "NvSmoothMotion");
			Add(dictionary, Kind.Upscale, "NvDlss");
			Add(dictionary, Kind.AntiLag, "AmdAntiLag");
			Add(dictionary, Kind.Interpolate, "AmdAfmf");
			Add(dictionary, Kind.RenderLane, "GmRenderLane");
			Add(dictionary, Kind.FrameSync, "FrameCoordination");
			Add(dictionary, Kind.Escalate, "GmAggressive", "GmAdaptiveEscalateV1");
			Add(dictionary, Kind.Idle, "GmIdlePolicyV1");
			Add(dictionary, Kind.Trim, "GmWsTrimV1");
			Add(dictionary, Kind.Mitigations, "AutoGameMitigationsV1");
			Add(dictionary, Kind.ShortNames, "AutoNtfsShortNamesV1");
			Add(dictionary, Kind.BackgroundFps, "AutoBackgroundFpsV1");
			Add(dictionary, Kind.OpenGl, "AutoOpenGlThreadsV1");
			Add(dictionary, Kind.Visual, "MatchVisualEffectsV1");
			Add(dictionary, Kind.Multimedia, "GmMmcss");
			Add(dictionary, Kind.GpuPreference, "GpuPrefStageOn");
			Add(dictionary, Kind.Hags, "Hags");
			Add(dictionary, Kind.Windowed, "WindowedOptimizations");
			Add(dictionary, Kind.Vrr, "VariableRefreshRate");
			Add(dictionary, Kind.NvVrr, "NvVrrWindowed");
			Add(dictionary, Kind.Sam, "AmdSam");
			Add(dictionary, Kind.Recording, "GameDvrOff");
			Add(dictionary, Kind.TimerTick, "AutoTimerTick");
			Add(dictionary, Kind.GlobalTimer, "AutoGlobalTimer");
			Add(dictionary, Kind.CpuMitigations, "EnvCpuMitigationsV1");
			Add(dictionary, Kind.LastAccess, "EnvNtfsLastAccessV1");
			Add(dictionary, Kind.Quantum, "EnvQuantumProfileV1");
			Add(dictionary, Kind.StorageIdle, "EnvStorageIdleV1");
			Add(dictionary, Kind.StorageRuntime, "EnvStorageRuntimeV1");
			Add(dictionary, Kind.StorageCold, "EnvStorageD3ColdV1");
			Add(dictionary, Kind.Audio, "EnvAudioIdleV1");
			Add(dictionary, Kind.Environment, "EnvPerformanceBundleV1");
			Add(dictionary, Kind.Storage, "EnvStoragePowerGroupV1");
			Add(dictionary, Kind.NtfsMemory, "EnvPrivacyNtfsMemoryV1");
			Add(dictionary, Kind.Diagnostics, "EnvPrivacyDiagnosticsV1");
			Add(dictionary, Kind.Ceip, "EnvPrivacyCeipV1");
			Add(dictionary, Kind.Appraiser, "EnvPrivacyAppraiserV1");
			Add(dictionary, Kind.ErrorReport, "EnvPrivacyErrorReportsV1");
			Add(dictionary, Kind.SecurityReport, "EnvPrivacySecurityReportsV1");
			Add(dictionary, Kind.InputPrivacy, "EnvPrivacyInputV1");
			Add(dictionary, Kind.Personalization, "EnvPrivacyPersonalizedV1");
			Add(dictionary, Kind.Compatibility, "EnvPrivacyCompatibilityV1");
			Add(dictionary, Kind.Archives, "EnvPrivacyArchivesV1");
			Add(dictionary, Kind.LegacyDefender, "AutoDefenderResourcesV1");
			Add(dictionary, Kind.LegacySession, "AutoDefenderSessionV1");
			Add(dictionary, Kind.Games, "GameSettings");
			Add(dictionary, Kind.Whitelist, "WhitelistSettings");
			Add(dictionary, Kind.AntiCheat, "AntiCheatSettings");
			Add(dictionary, Kind.Recovery, "ProtectionSettings");
			Add(dictionary, Kind.EppYield, "LabEppYieldV1");
			Add(dictionary, Kind.GpuClockLock, "LabGpuClockMaxV1");
			Add(dictionary, Kind.GpuPowerCeiling, "LabGpuPowerMaxV1");
			Add(dictionary, Kind.AudioBuffer, "LabAudioLowBufferV1");
			Add(dictionary, Kind.CpuIdlePolicy, "LabCpuIdleStrategyV1");
            Add(dictionary, Kind.FileFilterReduction, "LabFileFilterV1");
            Add(dictionary, Kind.ClockConfigurationRepair, "LabClockRepairV1");
            Add(dictionary, Kind.AudioEffectsReduction, "LabAudioEffectsV1");
			return dictionary;
		}

		private static void Add(Dictionary<string, Kind> map, Kind kind, params string[] keys)
		{
			foreach (string key in keys)
			{
				map.Add(key, kind);
			}
		}

		private static Dictionary<string, string> BuildAliases()
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			dictionary.Add("WindowedOpt", "WindowedOptimizations");
			dictionary.Add("VrrOpt", "VariableRefreshRate");
			dictionary.Add("NvVrr", "NvVrrWindowed");
			dictionary.Add("GameLowLatency", "NvLowLat");
			dictionary.Add("VisualEffects", "MatchVisualEffectsV1");
			dictionary.Add("ThreadBoost", "GmThreadBoostStrategyV1");
			dictionary.Add("CoreScheduling", "GmAutoCoresV2");
			dictionary.Add("CacheWarm", "GmCacheWarmV2");
			dictionary.Add("TimerTick", "AutoTimerTick");
			dictionary.Add("GlobalTimer", "AutoGlobalTimer");
			dictionary.Add("EnvironmentBundle", "EnvPerformanceBundleV1");
			dictionary.Add("StorageGroup", "EnvStoragePowerGroupV1");
			return dictionary;
		}

		private static string Canonical(string key)
		{
			if (string.IsNullOrEmpty(key))
			{
				return "";
			}
			string keyTrimmed = key.Trim();
			if (Designs.ContainsKey(keyTrimmed))
			{
				return keyTrimmed;
			}
			string value;
			if (Aliases.TryGetValue(keyTrimmed, out value))
			{
				return value;
			}
			string[] controlPrefixes = ControlPrefixes;
			foreach (string entryText in controlPrefixes)
			{
				if (keyTrimmed.StartsWith(entryText, StringComparison.OrdinalIgnoreCase))
				{
					keyTrimmed = keyTrimmed.Substring(entryText.Length);
					break;
				}
			}
			if (!Aliases.TryGetValue(keyTrimmed, out value))
			{
				return keyTrimmed;
			}
			return value;
		}

		private static void Paint(Graphics g, Kind kind, Pen p, Pen d, Brush fill)
		{
			switch (kind)
			{
			case Kind.Preset:
				g.DrawArc(p, 5, 6, 22, 22, 155, 230);
				Line(g, p, 16f, 17f, 22f, 10f);
				g.DrawEllipse(p, 13.5f, 14.5f, 5f, 5f);
				Line(g, d, 7f, 19f, 9f, 19f);
				Line(g, d, 16f, 8f, 16f, 10f);
				Line(g, d, 23f, 19f, 25f, 19f);
				break;
			case Kind.Boost:
				Chip(g, p, 7f, 7f, 18f, 18f);
				Bolt(g, p, 12f, 10f, 9f, 13f);
				break;
			case Kind.Cpu:
				Chip(g, p, 6f, 9f, 18f, 18f);
				g.DrawRectangle(d, 11, 14, 8, 8);
				Arrow(g, p, 26f, 13f, 26f, 4f);
				break;
			case Kind.Memory:
				Memory(g, p, 4f, 11f, 24f, 13f);
				Arrow(g, p, 16f, 9f, 16f, 3f);
				break;
			case Kind.Io:
				Drive(g, p, 4f, 12f, 23f, 14f);
				Arrow(g, p, 19f, 15f, 25f, 6f);
				break;
			case Kind.Gpu:
				Gpu(g, p, 4f, 10f, 23f, 16f);
				Arrow(g, p, 24f, 11f, 24f, 4f);
				break;
			case Kind.Core:
			{
				Chip(g, p, 6f, 6f, 20f, 20f);
				for (int l = 0; l < 2; l++)
				{
					for (int m = 0; m < 2; m++)
					{
						g.DrawRectangle(d, 10 + m * 7, 10 + l * 7, 4, 4);
					}
				}
				break;
			}
			case Kind.Thread:
				Gear(g, p, 11f, 16f, 6f);
				Line(g, d, 17f, 16f, 23f, 16f);
				Line(g, p, 23f, 7f, 23f, 25f);
				Node(g, p, 23f, 7f);
				Node(g, p, 23f, 16f);
				Node(g, p, 23f, 25f);
				break;
			case Kind.Awake:
				Monitor(g, p, 3f, 10f, 23f, 16f);
				Crescent(g, p, 23f, 8f, 6f);
				Line(g, d, 9f, 15f, 15f, 15f);
				break;
			case Kind.Background:
				Window(g, d, 4f, 5f, 20f, 16f);
				Window(g, p, 8f, 10f, 20f, 16f);
				Line(g, p, 16f, 15f, 16f, 21f);
				Line(g, p, 21f, 15f, 21f, 21f);
				break;
			case Kind.Family:
				g.DrawRectangle(p, 12, 4, 8, 6);
				Line(g, p, 16f, 10f, 16f, 17f);
				Line(g, p, 7f, 17f, 25f, 17f);
				Line(g, p, 7f, 17f, 7f, 22f);
				Line(g, p, 25f, 17f, 25f, 22f);
				g.DrawRectangle(d, 3, 22, 8, 6);
				g.DrawRectangle(d, 21, 22, 8, 6);
				Shield(g, p, 12f, 20f, 8f, 10f);
				break;
			case Kind.GpuDown:
				Gpu(g, p, 3f, 5f, 23f, 16f);
				Arrow(g, p, 24f, 19f, 24f, 29f);
				break;
			case Kind.Download:
				Line(g, p, 5f, 23f, 5f, 27f);
				Line(g, p, 5f, 27f, 21f, 27f);
				Arrow(g, p, 12f, 4f, 12f, 20f);
				Pause(g, p, 23f, 8f, 8f);
				break;
			case Kind.Update:
				g.DrawArc(p, 4, 5, 23, 23, 30, 285);
				Poly(g, p, 21f, 4f, 27f, 6f, 25f, 12f);
				Pause(g, p, 13f, 12f, 9f);
				break;
			case Kind.Maintenance:
				Poly(g, p, 7f, 5f, 11f, 9f, 9f, 13f, 5f, 14f, 3f, 9f);
				Poly(g, p, 10f, 13f, 21f, 25f, 25f, 21f, 13f, 10f);
				Pause(g, p, 22f, 4f, 7f);
				break;
			case Kind.Service:
				Gear(g, p, 13f, 15f, 8f);
				Pause(g, p, 23f, 21f, 8f);
				break;
			case Kind.Power:
				Cut(g, p, 4f, 8f, 23f, 17f, 3f);
				Line(g, p, 28f, 13f, 28f, 20f);
				Bolt(g, p, 11f, 10f, 9f, 13f);
				break;
			case Kind.CacheWarm:
				Cylinder(g, p, 4f, 9f, 18f, 19f);
				Heat(g, p, 25f, 6f, 3);
				Heat(g, d, 14f, 1f, 2);
				break;
			case Kind.CleanMemory:
				Memory(g, p, 3f, 7f, 24f, 13f);
				Poly(g, p, 13f, 26f, 21f, 18f, 28f, 24f, 23f, 29f, 16f, 29f, 13f, 26f);
				Line(g, d, 18f, 23f, 24f, 28f);
				break;
			case Kind.Desktop:
				Window(g, d, 4f, 4f, 17f, 14f);
				Window(g, p, 9f, 10f, 20f, 17f);
				Arrow(g, p, 18f, 24f, 18f, 14f);
				break;
			case Kind.English:
			{
				Cut(g, p, 3f, 14f, 26f, 13f, 2f);
				Poly(g, p, 11f, 11f, 16f, 3f, 21f, 11f);
				Line(g, p, 13f, 8f, 19f, 8f);
				for (int k = 0; k < 5; k++)
				{
					Line(g, d, 7 + k * 4, 18f, 8 + k * 4, 18f);
				}
				Line(g, d, 9f, 23f, 23f, 23f);
				break;
			}
			case Kind.NvPerformance:
				Gpu(g, p, 3f, 10f, 24f, 17f);
				g.DrawArc(p, 13, 3, 14, 14, 185, 175);
				Line(g, p, 20f, 10f, 24f, 5f);
				break;
			case Kind.Shader:
				Poly(g, p, 4f, 10f, 16f, 3f, 28f, 10f, 16f, 17f, 4f, 10f);
				Poly(g, d, 4f, 16f, 16f, 23f, 28f, 16f);
				Cylinder(g, p, 18f, 20f, 11f, 10f);
				break;
			case Kind.LowLatency:
				Stopwatch(g, p, 14f, 17f, 10f);
				Bolt(g, p, 16f, 10f, 9f, 13f);
				break;
			case Kind.Rebar:
				Chip(g, p, 3f, 9f, 12f, 13f);
				Memory(g, p, 20f, 9f, 9f, 13f);
				Arrow(g, p, 10f, 27f, 25f, 27f);
				Line(g, d, 10f, 24f, 25f, 24f);
				break;
			case Kind.Fullscreen:
				Monitor(g, p, 3f, 5f, 26f, 20f);
				Poly(g, d, 7f, 13f, 7f, 9f, 11f, 9f);
				Poly(g, d, 21f, 9f, 25f, 9f, 25f, 13f);
				Line(g, p, 6f, 28f, 27f, 4f);
				break;
			case Kind.Intel:
				Cut(g, p, 3f, 7f, 22f, 16f, 3f);
				Line(g, p, 27f, 12f, 27f, 18f);
				Chip(g, p, 10f, 13f, 14f, 14f);
				Line(g, d, 16f, 17f, 16f, 23f);
				break;
			case Kind.GpuPower:
				Gpu(g, p, 3f, 12f, 24f, 16f);
				Bolt(g, p, 11f, 3f, 11f, 13f);
				break;
			case Kind.Laptop:
				Cut(g, p, 6f, 5f, 20f, 17f, 2f);
				Poly(g, p, 6f, 23f, 3f, 27f, 29f, 27f, 26f, 23f);
				Bolt(g, p, 13f, 8f, 7f, 11f);
				break;
			case Kind.Smooth:
				Window(g, d, 3f, 5f, 18f, 17f);
				Window(g, p, 10f, 11f, 19f, 16f);
				Poly(g, p, 3f, 25f, 8f, 20f, 12f, 25f, 16f, 20f, 20f, 25f);
				break;
			case Kind.Upscale:
				Cut(g, p, 4f, 5f, 24f, 23f, 3f);
				g.DrawRectangle(d, 8, 17, 7, 7);
				Arrow(g, p, 15f, 17f, 24f, 8f);
				break;
			case Kind.AntiLag:
				Poly(g, p, 4f, 20f, 11f, 11f, 17f, 20f, 25f, 8f);
				Poly(g, p, 20f, 8f, 25f, 8f, 25f, 13f);
				Line(g, d, 4f, 27f, 28f, 27f);
				break;
			case Kind.Interpolate:
				g.DrawRectangle(d, 3, 7, 8, 18);
				g.DrawRectangle(p, 21, 7, 8, 18);
				DashRectangle(g, p, 12f, 10f, 7f, 12f);
				break;
			case Kind.RenderLane:
				Line(g, d, 5f, 5f, 5f, 27f);
				Line(g, d, 16f, 5f, 16f, 27f);
				Line(g, d, 27f, 5f, 27f, 27f);
				Poly(g, p, 10f, 9f, 15f, 14f, 10f, 19f);
				Poly(g, p, 19f, 14f, 24f, 19f, 19f, 24f);
				break;
			case Kind.FrameSync:
				Monitor(g, p, 3f, 5f, 26f, 19f);
				Poly(g, p, 6f, 17f, 10f, 17f, 13f, 10f, 17f, 21f, 20f, 14f, 26f, 14f);
				break;
			case Kind.Escalate:
				Poly(g, p, 4f, 26f, 11f, 18f, 17f, 21f, 27f, 6f);
				Poly(g, p, 20f, 6f, 27f, 6f, 27f, 13f);
				break;
			case Kind.Idle:
				Poly(g, p, 8f, 4f, 24f, 4f, 24f, 9f, 12f, 23f, 12f, 27f);
				Poly(g, p, 8f, 27f, 24f, 27f, 24f, 23f, 12f, 9f, 12f, 4f);
				Line(g, d, 13f, 8f, 20f, 8f);
				break;
			case Kind.Trim:
				Memory(g, p, 3f, 6f, 26f, 14f);
				Line(g, p, 11f, 26f, 23f, 26f);
				break;
			case Kind.Mitigations:
				Shield(g, p, 5f, 3f, 22f, 27f);
				Bolt(g, p, 11f, 9f, 11f, 15f);
				break;
			case Kind.ShortNames:
				File(g, p, 6f, 3f, 20f, 26f);
				Digit(g, p, 8f, 13f, true);
				g.FillEllipse(fill, 15f, 21f, 1.8f, 1.8f);
				Digit(g, p, 19f, 13f, false);
				break;
			case Kind.BackgroundFps:
				Window(g, d, 3f, 4f, 21f, 16f);
				Monitor(g, p, 8f, 10f, 21f, 15f);
				Line(g, p, 13f, 19f, 25f, 19f);
				Line(g, p, 16f, 16f, 16f, 22f);
				break;
			case Kind.OpenGl:
				Chip(g, p, 4f, 9f, 13f, 14f);
				Line(g, p, 17f, 16f, 22f, 16f);
				Line(g, p, 22f, 7f, 22f, 25f);
				Line(g, p, 22f, 7f, 28f, 7f);
				Line(g, p, 22f, 16f, 28f, 16f);
				Line(g, p, 22f, 25f, 28f, 25f);
				break;
			case Kind.Visual:
				Monitor(g, p, 3f, 9f, 25f, 17f);
				Spark(g, p, 23f, 8f, 6f);
				Spark(g, d, 11f, 15f, 3f);
				break;
			case Kind.Multimedia:
				Poly(g, p, 9f, 23f, 9f, 8f, 23f, 5f, 23f, 20f);
				Line(g, d, 9f, 12f, 23f, 9f);
				g.DrawEllipse(p, 3, 21, 6, 5);
				g.DrawEllipse(p, 17, 18, 6, 5);
				Line(g, d, 27f, 14f, 27f, 25f);
				break;
			case Kind.GpuPreference:
				Gpu(g, p, 3f, 11f, 24f, 16f);
				Spark(g, p, 24f, 7f, 5f);
				break;
			case Kind.GpuEconomy:
				Gpu(g, p, 3f, 5f, 24f, 16f);
				Leaf(g, p, 20f, 20f, 9f);
				break;
			case Kind.Hags:
				Chip(g, p, 4f, 6f, 21f, 21f);
				Line(g, p, 10f, 12f, 20f, 12f);
				Line(g, p, 10f, 16f, 17f, 16f);
				Line(g, p, 10f, 20f, 14f, 20f);
				Arrow(g, p, 27f, 23f, 27f, 8f);
				break;
			case Kind.Windowed:
				Window(g, d, 3f, 4f, 20f, 17f);
				Window(g, p, 8f, 10f, 21f, 17f);
				Poly(g, p, 13f, 20f, 17f, 24f, 25f, 16f);
				break;
			case Kind.Vrr:
				Monitor(g, p, 3f, 5f, 26f, 20f);
				Wave(g, p, 6f, 16f, 20f, 4f);
				break;
			case Kind.NvVrr:
				Window(g, d, 3f, 4f, 20f, 16f);
				Window(g, p, 8f, 10f, 21f, 17f);
				Wave(g, p, 11f, 20f, 15f, 3f);
				break;
			case Kind.Sam:
				Gpu(g, p, 3f, 10f, 24f, 16f);
				Arrow(g, p, 8f, 5f, 24f, 5f);
				Arrow(g, d, 24f, 29f, 8f, 29f);
				break;
			case Kind.Recording:
				Cut(g, p, 4f, 8f, 19f, 18f, 2f);
				Poly(g, p, 23f, 13f, 29f, 9f, 29f, 25f, 23f, 21f);
				g.DrawEllipse(d, 8, 13, 8, 8);
				Line(g, p, 4f, 29f, 28f, 3f);
				break;
			case Kind.TimerTick:
				Stopwatch(g, p, 16f, 18f, 10f);
				Poly(g, p, 9f, 20f, 12f, 20f, 15f, 13f, 18f, 23f, 22f, 16f);
				break;
			case Kind.GlobalTimer:
				g.DrawEllipse(p, 4, 4, 24, 24);
				g.DrawArc(d, 10, 4, 12, 24, 0, 360);
				Line(g, d, 5f, 16f, 27f, 16f);
				Line(g, p, 16f, 8f, 16f, 16f);
				Line(g, p, 16f, 16f, 22f, 19f);
				break;
			case Kind.CpuMitigations:
				Chip(g, p, 3f, 6f, 17f, 19f);
				Shield(g, p, 17f, 14f, 12f, 15f);
				Line(g, d, 23f, 18f, 23f, 23f);
				break;
			case Kind.LastAccess:
				File(g, p, 5f, 3f, 18f, 25f);
				Clock(g, p, 23f, 22f, 7f);
				Line(g, d, 9f, 10f, 14f, 10f);
				break;
			case Kind.Quantum:
				Window(g, d, 3f, 4f, 17f, 15f);
				Window(g, p, 8f, 9f, 19f, 16f);
				Clock(g, p, 24f, 24f, 6f);
				break;
			case Kind.StorageIdle:
				Drive(g, p, 3f, 12f, 24f, 15f);
				Crescent(g, p, 23f, 8f, 6f);
				break;
			case Kind.StorageRuntime:
				Drive(g, p, 3f, 5f, 24f, 17f);
				PowerMark(g, p, 23f, 24f, 6f);
				break;
			case Kind.StorageCold:
				Drive(g, p, 3f, 5f, 24f, 16f);
				Snow(g, p, 23f, 24f, 6f);
				Line(g, d, 15f, 29f, 29f, 15f);
				break;
			case Kind.Audio:
				Poly(g, p, 4f, 13f, 9f, 13f, 16f, 7f, 16f, 25f, 9f, 20f, 4f, 20f, 4f, 13f);
				g.DrawArc(p, 12, 10, 12, 13, -60, 120);
				Spark(g, p, 25f, 7f, 4f);
				break;
			case Kind.Environment:
				Shield(g, p, 4f, 3f, 24f, 27f);
				Chip(g, p, 10f, 10f, 12f, 12f);
				break;
			case Kind.Storage:
				Drive(g, p, 3f, 6f, 25f, 19f);
				Line(g, d, 8f, 10f, 23f, 10f);
				PowerMark(g, p, 25f, 25f, 5f);
				break;
			case Kind.NtfsMemory:
				File(g, p, 4f, 3f, 18f, 23f);
				Memory(g, p, 13f, 18f, 17f, 11f);
				break;
			case Kind.Diagnostics:
				Poly(g, p, 4f, 20f, 8f, 20f, 12f, 9f, 17f, 26f, 21f, 15f, 28f, 15f);
				Line(g, d, 4f, 5f, 27f, 5f);
				break;
			case Kind.Ceip:
				Node(g, p, 16f, 7f);
				Node(g, p, 6f, 24f);
				Node(g, p, 26f, 24f);
				Line(g, d, 14f, 10f, 8f, 21f);
				Line(g, d, 18f, 10f, 24f, 21f);
				Line(g, d, 10f, 24f, 22f, 24f);
				break;
			case Kind.Appraiser:
				Window(g, p, 3f, 4f, 19f, 20f);
				g.DrawEllipse(p, 15, 15, 11, 11);
				Line(g, p, 24f, 24f, 29f, 29f);
				break;
			case Kind.ErrorReport:
				File(g, p, 6f, 3f, 20f, 26f);
				Line(g, p, 16f, 12f, 16f, 20f);
				g.FillEllipse(fill, 15, 23, 2, 2);
				break;
			case Kind.SecurityReport:
				Shield(g, p, 4f, 3f, 24f, 27f);
				Poly(g, p, 10f, 16f, 14f, 20f, 22f, 11f);
				break;
			case Kind.InputPrivacy:
			{
				Cut(g, p, 3f, 7f, 26f, 18f, 3f);
				for (int j = 0; j < 5; j++)
				{
					g.DrawRectangle(d, 7 + j * 4, 12, 1, 1);
				}
				Line(g, p, 10f, 20f, 22f, 20f);
				Line(g, p, 5f, 29f, 27f, 3f);
				break;
			}
			case Kind.Personalization:
				g.DrawEllipse(p, 8, 4, 9, 9);
				g.DrawArc(p, 3, 15, 19, 17, 180, 180);
				Cut(g, p, 19f, 15f, 10f, 11f, 2f);
				Line(g, d, 22f, 18f, 26f, 23f);
				Line(g, d, 26f, 18f, 22f, 23f);
				break;
			case Kind.Compatibility:
				Poly(g, p, 5f, 5f, 13f, 5f, 13f, 9f, 18f, 9f, 18f, 5f, 26f, 5f, 26f, 13f, 22f, 13f, 22f, 18f, 26f, 18f, 26f, 26f, 18f, 26f, 18f, 22f, 13f, 22f, 13f, 26f, 5f, 26f, 5f, 5f);
				break;
			case Kind.Archives:
			{
				File(g, p, 6f, 3f, 20f, 26f);
				for (int i = 0; i < 5; i++)
				{
					Line(g, d, 14 + i % 2 * 3, 8 + i * 3, 17 + i % 2 * 3, 8 + i * 3);
				}
				g.DrawRectangle(p, 14, 24, 6, 3);
				break;
			}
			case Kind.LegacyDefender:
				Poly(g, p, 3f, 9f, 13f, 9f, 16f, 12f, 29f, 12f, 29f, 27f, 3f, 27f, 3f, 9f);
				Shield(g, p, 10f, 3f, 12f, 16f);
				break;
			case Kind.LegacySession:
				Shield(g, p, 4f, 3f, 22f, 27f);
				Clock(g, p, 23f, 23f, 7f);
				break;
			case Kind.Games:
				Cut(g, p, 4f, 8f, 24f, 17f, 5f);
				Line(g, p, 9f, 16f, 15f, 16f);
				Line(g, p, 12f, 13f, 12f, 19f);
				g.FillEllipse(fill, 22f, 13f, 2.5f, 2.5f);
				g.FillEllipse(fill, 24f, 18f, 2.5f, 2.5f);
				break;
			case Kind.Whitelist:
				Cut(g, p, 4f, 4f, 24f, 24f, 4f);
				Poly(g, p, 9f, 16f, 14f, 21f, 24f, 10f);
				break;
			case Kind.AntiCheat:
				Shield(g, p, 4f, 3f, 24f, 27f);
				g.DrawArc(p, 10, 9, 12, 13, 185, 275);
				g.DrawArc(d, 13, 12, 6, 11, 180, 230);
				break;
			case Kind.Recovery:
				g.DrawArc(p, 4, 5, 24, 23, 70, 280);
				Poly(g, p, 3f, 5f, 3f, 13f, 10f, 13f);
				Line(g, p, 11f, 20f, 21f, 10f);
				Line(g, p, 18f, 9f, 22f, 13f);
				break;
			case Kind.EppYield:
				Chip(g, p, 4f, 5f, 12f, 12f);
				Bolt(g, d, 8f, 8f, 4f, 6f);
				Poly(g, p, 5f, 26f, 13f, 26f, 20f, 19f, 28f, 19f);
				Poly(g, p, 24f, 15f, 28f, 19f, 24f, 23f);
				Arrow(g, d, 20f, 8f, 28f, 8f);
				break;
			case Kind.GpuClockLock:
				Gpu(g, p, 3f, 14f, 24f, 13f);
				Poly(g, d, 4f, 10f, 7f, 10f, 10f, 5f, 16f, 5f);
				Cut(g, p, 19f, 6f, 10f, 9f, 1.5f);
				g.DrawArc(p, 21f, 1f, 6f, 9f, 180f, 180f);
				Line(g, p, 24f, 9f, 24f, 12f);
				break;
			case Kind.GpuPowerCeiling:
				Gpu(g, p, 3f, 14f, 24f, 13f);
				Poly(g, p, 5f, 8f, 5f, 4f, 27f, 4f, 27f, 8f);
				Bolt(g, p, 12f, 8f, 10f, 17f);
				break;
			case Kind.AudioBuffer:
				Poly(g, p, 3f, 13f, 7f, 13f, 10f, 5f, 14f, 21f, 18f, 7f, 22f, 17f, 25f, 13f, 29f, 13f);
				Poly(g, d, 11f, 24f, 11f, 28f, 21f, 28f, 21f, 24f);
				Line(g, p, 14f, 25f, 18f, 25f);
				break;
            case Kind.FileFilterReduction:
                Poly(g, p, 4f, 3f, 14f, 3f, 19f, 8f, 19f, 16f);
                Poly(g, p, 4f, 3f, 4f, 20f, 11f, 20f);
                Poly(g, d, 14f, 3f, 14f, 8f, 19f, 8f);
                Poly(g, p, 12f, 14f, 28f, 14f, 22f, 21f, 22f, 28f, 18f, 26f, 18f, 21f, 12f, 14f);
                Poly(g, d, 3f, 25f, 9f, 25f, 12f, 28f);
                break;
            case Kind.ClockConfigurationRepair:
                g.DrawArc(p, 4f, 4f, 23f, 23f, 15f, 285f);
                Poly(g, p, 7f, 3f, 4f, 8f, 10f, 8f);
                Poly(g, p, 15f, 9f, 15f, 16f, 20f, 19f);
                Poly(g, d, 22f, 26f, 25f, 29f, 30f, 23f);
                Node(g, d, 15f, 16f);
                break;
            case Kind.AudioEffectsReduction:
                Poly(g, p, 3f, 12f, 7f, 12f, 12f, 7f, 12f, 25f, 7f, 20f, 3f, 20f, 3f, 12f);
                Line(g, d, 18f, 5f, 18f, 27f);
                Line(g, d, 25f, 5f, 25f, 27f);
                Node(g, p, 18f, 11f); Node(g, p, 25f, 21f);
                Line(g, p, 15f, 29f, 29f, 3f);
                break;
			case Kind.CpuIdlePolicy:
				Chip(g, p, 5f, 6f, 17f, 20f);
				Crescent(g, p, 13f, 15f, 5f);
				Line(g, d, 28f, 7f, 28f, 25f);
				Line(g, p, 26f, 7f, 30f, 7f);
				Node(g, p, 28f, 16f);
				Line(g, p, 26f, 25f, 30f, 25f);
				break;
			default:
				Poly(g, p, 10f, 3f, 22f, 3f, 29f, 16f, 22f, 29f, 10f, 29f, 3f, 16f, 10f, 3f);
				Line(g, p, 10f, 12f, 22f, 12f);
				Line(g, p, 10f, 20f, 22f, 20f);
				Node(g, p, 14f, 12f);
				Node(g, p, 19f, 20f);
				break;
			}
		}

		private static void Line(Graphics g, Pen p, float x1, float y1, float x2, float y2)
		{
			g.DrawLine(p, x1, y1, x2, y2);
		}

		private static void Poly(Graphics g, Pen p, params float[] coordinates)
		{
			PointF[] pointFItems = new PointF[coordinates.Length / 2];
			for (int i = 0; i < pointFItems.Length; i++)
			{
				pointFItems[i] = new PointF(coordinates[i * 2], coordinates[i * 2 + 1]);
			}
			if (pointFItems.Length > 1)
			{
				g.DrawLines(p, pointFItems);
			}
		}

		private static void Cut(Graphics g, Pen p, float x, float y, float w, float h, float cut)
		{
			Poly(g, p, x + cut, y, x + w - cut, y, x + w, y + cut, x + w, y + h - cut, x + w - cut, y + h, x + cut, y + h, x, y + h - cut, x, y + cut, x + cut, y);
		}

		private static void Chip(Graphics g, Pen p, float x, float y, float w, float h)
		{
			Cut(g, p, x, y, w, h, 2f);
			for (int i = 1; i <= 3; i++)
			{
				float xOffset = x + w * (float)i / 4f;
				float yOffset = y + h * (float)i / 4f;
				Line(g, p, xOffset, y - 3f, xOffset, y);
				Line(g, p, xOffset, y + h, xOffset, y + h + 3f);
				Line(g, p, x - 3f, yOffset, x, yOffset);
				Line(g, p, x + w, yOffset, x + w + 3f, yOffset);
			}
		}

		private static void Memory(Graphics g, Pen p, float x, float y, float w, float h)
		{
			Cut(g, p, x, y, w, h, 1.5f);
			float width = Math.Min(h * 0.4f, w / 5f);
			for (int i = 0; i < 3; i++)
			{
				g.DrawRectangle(p, x + w * (float)(i + 1) / 4f - width / 2f, y + h * 0.25f, width, width);
			}
			for (int j = 1; j < 5; j++)
			{
				Line(g, p, x + w * (float)j / 5f, y + h, x + w * (float)j / 5f, y + h + 2f);
			}
		}

		private static void Gpu(Graphics g, Pen p, float x, float y, float w, float h)
		{
			Cut(g, p, x, y, w, h, 2f);
			float width = h * 0.55f;
			g.DrawEllipse(p, x + w * 0.3f - width / 2f, y + (h - width) / 2f, width, width);
			Line(g, p, x + w * 0.67f, y + 4f, x + w * 0.67f, y + h - 4f);
			Line(g, p, x + w * 0.83f, y + 4f, x + w * 0.83f, y + h - 4f);
			Line(g, p, x + 3f, y + h + 2f, x + w - 3f, y + h + 2f);
		}

		private static void Drive(Graphics g, Pen p, float x, float y, float w, float h)
		{
			Cut(g, p, x, y, w, h, 2f);
			Line(g, p, x + 3f, y + h - 4f, x + w - 3f, y + h - 4f);
			g.DrawEllipse(p, x + w - 6f, y + h - 3f, 1f, 1f);
		}

		private static void Window(Graphics g, Pen p, float x, float y, float w, float h)
		{
			Cut(g, p, x, y, w, h, 2f);
			Line(g, p, x + 1f, y + 5f, x + w - 1f, y + 5f);
		}

		private static void Monitor(Graphics g, Pen p, float x, float y, float w, float h)
		{
			Cut(g, p, x, y, w, h, 2f);
			Line(g, p, x + w / 2f, y + h, x + w / 2f, y + h + 4f);
			Line(g, p, x + w / 2f - 5f, y + h + 4f, x + w / 2f + 5f, y + h + 4f);
		}

		private static void Shield(Graphics g, Pen p, float x, float y, float w, float h)
		{
			Poly(g, p, x + w / 2f, y, x + w, y + h * 0.18f, x + w * 0.88f, y + h * 0.66f, x + w / 2f, y + h, x + w * 0.12f, y + h * 0.66f, x, y + h * 0.18f, x + w / 2f, y);
		}

		private static void File(Graphics g, Pen p, float x, float y, float w, float h)
		{
			Poly(g, p, x, y, x + w - 6f, y, x + w, y + 6f, x + w, y + h, x, y + h, x, y);
			Poly(g, p, x + w - 6f, y, x + w - 6f, y + 6f, x + w, y + 6f);
		}

		private static void Cylinder(Graphics g, Pen p, float x, float y, float w, float h)
		{
			g.DrawEllipse(p, x, y, w, 6f);
			Line(g, p, x, y + 3f, x, y + h - 3f);
			Line(g, p, x + w, y + 3f, x + w, y + h - 3f);
			g.DrawArc(p, x, y + h - 6f, w, 6f, 0f, 180f);
			g.DrawArc(p, x, y + h / 2f - 3f, w, 6f, 0f, 180f);
		}

		private static void Arrow(Graphics g, Pen p, float x1, float y1, float x2, float y2)
		{
			Line(g, p, x1, y1, x2, y2);
			double atan2Result = Math.Atan2(y2 - y1, x2 - x1);
			for (int i = -1; i <= 1; i += 2)
			{
				double atan2ResultOffset = atan2Result + (double)i * Math.PI * 0.75;
				Line(g, p, x2, y2, x2 + (float)Math.Cos(atan2ResultOffset) * 4f, y2 + (float)Math.Sin(atan2ResultOffset) * 4f);
			}
		}

		private static void Bolt(Graphics g, Pen p, float x, float y, float w, float h)
		{
			Poly(g, p, x + w * 0.6f, y, x, y + h * 0.55f, x + w * 0.48f, y + h * 0.55f, x + w * 0.34f, y + h, x + w, y + h * 0.36f, x + w * 0.52f, y + h * 0.36f, x + w * 0.6f, y);
		}

		private static void Pause(Graphics g, Pen p, float x, float y, float h)
		{
			Line(g, p, x, y, x, y + h);
			Line(g, p, x + 4f, y, x + 4f, y + h);
		}

		private static void Node(Graphics g, Pen p, float x, float y)
		{
			g.DrawEllipse(p, x - 2f, y - 2f, 4f, 4f);
		}

		private static void Gear(Graphics g, Pen p, float x, float y, float radius)
		{
			PointF[] pointFItems = new PointF[32];
			for (int i = 0; i < pointFItems.Length; i++)
			{
				double ratio = (double)i * Math.PI / 16.0;
				float scaledRadius = radius * ((i % 4 < 2) ? 1f : 0.78f);
				pointFItems[i] = new PointF(x + (float)Math.Cos(ratio) * scaledRadius, y + (float)Math.Sin(ratio) * scaledRadius);
			}
			g.DrawPolygon(p, pointFItems);
			g.DrawEllipse(p, x - radius * 0.3f, y - radius * 0.3f, radius * 0.6f, radius * 0.6f);
		}

		private static void Crescent(Graphics g, Pen p, float x, float y, float radius)
		{
			g.DrawArc(p, x - radius, y - radius, radius * 2f, radius * 2f, 65f, 270f);
			g.DrawArc(p, x - radius * 0.2f, y - radius * 1.03f, radius * 1.35f, radius * 1.35f, 80f, 210f);
		}

		private static void Heat(Graphics g, Pen p, float x, float y, int length)
		{
			Poly(g, p, x, y, x - 2f, y + (float)length, x + 1f, y + (float)(length * 2), x - 1f, y + (float)(length * 3));
		}

		private static void Spark(Graphics g, Pen p, float x, float y, float radius)
		{
			Poly(g, p, x, y - radius, x + radius * 0.25f, y - radius * 0.25f, x + radius, y, x + radius * 0.25f, y + radius * 0.25f, x, y + radius, x - radius * 0.25f, y + radius * 0.25f, x - radius, y, x - radius * 0.25f, y - radius * 0.25f, x, y - radius);
		}

		private static void Stopwatch(Graphics g, Pen p, float x, float y, float radius)
		{
			g.DrawEllipse(p, x - radius, y - radius, radius * 2f, radius * 2f);
			Line(g, p, x, y - radius - 4f, x, y - radius);
			Line(g, p, x - 4f, y - radius - 4f, x + 4f, y - radius - 4f);
			Line(g, p, x + radius * 0.75f, y - radius * 0.75f, x + radius, y - radius);
		}

		private static void Clock(Graphics g, Pen p, float x, float y, float radius)
		{
			g.DrawEllipse(p, x - radius, y - radius, radius * 2f, radius * 2f);
			Line(g, p, x, y - radius * 0.55f, x, y);
			Line(g, p, x, y, x + radius * 0.45f, y + radius * 0.25f);
		}

		private static void Wave(Graphics g, Pen p, float x, float y, float w, float amplitude)
		{
			Poly(g, p, x, y, x + w * 0.18f, y, x + w * 0.32f, y - amplitude, x + w * 0.48f, y + amplitude, x + w * 0.65f, y - amplitude, x + w * 0.8f, y, x + w, y);
		}

		private static void Leaf(Graphics g, Pen p, float x, float y, float size)
		{
			Poly(g, p, x - size * 0.5f, y + size * 0.5f, x - size * 0.4f, y - size * 0.25f, x + size * 0.2f, y - size * 0.5f, x + size, y - size * 0.5f, x + size * 0.7f, y + size * 0.2f, x + size * 0.1f, y + size * 0.5f, x - size * 0.5f, y + size * 0.5f);
			Line(g, p, x - size * 0.5f, y + size * 0.5f, x + size * 0.55f, y - size * 0.12f);
		}

		private static void PowerMark(Graphics g, Pen p, float x, float y, float radius)
		{
			g.DrawArc(p, x - radius, y - radius, radius * 2f, radius * 2f, -50f, 280f);
			Line(g, p, x, y - radius - 1f, x, y);
		}

		private static void Snow(Graphics g, Pen p, float x, float y, float radius)
		{
			for (int i = 0; i < 3; i++)
			{
				double ratio = (double)i * Math.PI / 3.0;
				float scaledCos = (float)Math.Cos(ratio) * radius;
				float scaledSin = (float)Math.Sin(ratio) * radius;
				Line(g, p, x - scaledCos, y - scaledSin, x + scaledCos, y + scaledSin);
			}
		}

		private static void Digit(Graphics g, Pen p, float x, float y, bool eight)
		{
			Line(g, p, x, y, x + 5f, y);
			Line(g, p, x, y + 5f, x + 5f, y + 5f);
			Line(g, p, x, y + 10f, x + 5f, y + 10f);
			Line(g, p, x + 5f, y, x + 5f, y + 10f);
			if (eight)
			{
				Line(g, p, x, y, x, y + 10f);
			}
		}

		private static void DashRectangle(Graphics g, Pen p, float x, float y, float w, float h)
		{
			Line(g, p, x, y, x + 2f, y);
			Line(g, p, x + w - 2f, y, x + w, y);
			Line(g, p, x, y + h, x + 2f, y + h);
			Line(g, p, x + w - 2f, y + h, x + w, y + h);
			Line(g, p, x, y + 3f, x, y + h - 3f);
			Line(g, p, x + w, y + 3f, x + w, y + h - 3f);
		}
	}
}
