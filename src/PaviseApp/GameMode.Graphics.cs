using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal partial class GameMode
	{
		private bool GpuPrefResidueGate(bool hasResidue)
		{
			if (hasResidue)
			{
				if (enabled && gpuPrefStageOn)
				{
					return preStagedNvPath == null;
				}
				return true;
			}
			return false;
		}
		private void PreStageDriverTuning(List<GameProfile> copy, string armedName)
		{
			if (stopping || (sessionRecoveryCaptured && sessionRecoveryRoutes != null && !sessionRecoveryRoutes.Complete))
			{
				return;
			}
			GameProfile armed = null;
			foreach (GameProfile item in copy)
			{
				if (string.Equals(item.Name, armedName, StringComparison.OrdinalIgnoreCase))
				{
					armed = item;
					break;
				}
			}
			if (armed == null)
			{
				return;
			}
			PrewarmAutomatic(armed);
			string path = armed.PreferredExecutablePath;
			if (string.IsNullOrEmpty(path))
			{
				return;
			}
			PolicySnapshot policySnapshot;
			try
			{
				policySnapshot = PolicyResolver.For(armed);
			}
			catch
			{
				return;
			}
			NvGamePlan plan = new NvGamePlan
			{
				MaxPerf = policySnapshot.NvMaxPerf,
				LowLatMode = policySnapshot.NvLowLatMode,
				SmoothMotion = policySnapshot.NvSmoothMotion,
				ShaderCacheMax = policySnapshot.NvShaderCacheMax,
				Rebar = policySnapshot.NvRebar,
				DlssMode = policySnapshot.NvDlssMode,
				WindowedVrr = nvVrrWindowedOn
			};
			plan = AutomaticPreStagePlan(policySnapshot, Native.OnAcPower(), nvVrrWindowedOn);
			bool stageGpuPref = gpuPrefStageOn && GpuPrefStage.Supported;
			QueuePreStagedNvPlan(armed, path, plan, stageGpuPref);
		}
	}
}
