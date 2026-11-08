using System;

namespace PaviseApp
{
	internal partial class GameMode
	{
		private int WriteOwnedGamePriority(IntPtr handle, int pid, BoostPass pass, Func<int> write)
		{
			const int WriteFault = 29;
			const int Canceled = 995;
			const int Retry = 1237;
			long? observed = ReadBoostAction("GamePriority", handle);
			if (!observed.HasValue) return Retry;
			uint previous = (uint)observed.Value;
			uint applied = pass.PriorityTarget;
			if (previous == applied) return 0;

			bool attempted = false;
			try
			{
				// Persist intent before writing and ownership after verified readback; matching values create neither.
				if (!CrashGuard.PrepareBoostPriorityWrite(pid, pass.RendererCreation, previous, applied))
					return FinishUnwrittenGamePriority(pid, pass, previous, applied, WriteFault);
				if (pass.WriteDenied || stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration
					|| BoostActionIdentity(handle, pass.RendererCreation) != 0)
					return FinishUnwrittenGamePriority(pid, pass, previous, applied, Canceled);
				long? fresh = ReadBoostAction("GamePriority", handle);
				if (!fresh.HasValue) return FinishUnwrittenGamePriority(pid, pass, previous, applied, Retry);
				if (fresh.Value != previous)
					return FinishUnwrittenGamePriority(pid, pass, previous, applied, fresh.Value == applied ? 0 : Retry);

				attempted = true;
				int status = write();
				// This callback performs the single SetPriorityClass call. A returned
				// error confirms it failed; a matching external value cannot make it ours.
				if (status != 0)
					return CrashGuard.CancelUnwrittenBoostPriority(pid, pass.RendererCreation, previous, applied) ? status : WriteFault;
				long? actual = ReadBoostAction("GamePriority", handle);
				if (actual.HasValue && actual.Value == applied)
					return CrashGuard.CompleteBoostPriorityWrite(pid, pass.RendererCreation, previous, applied) ? 0 : WriteFault;
				if (actual.HasValue && actual.Value == previous)
				{
					if (!CrashGuard.CancelUnwrittenBoostPriority(pid, pass.RendererCreation, previous, applied)) return WriteFault;
				}
				// A successful write with unknown readback retains intent until conditional restoration verifies it.
				return Retry;
			}
			catch
			{
				if (!attempted) FinishUnwrittenGamePriority(pid, pass, previous, applied, WriteFault);
				throw;
			}
		}

		private int FinishUnwrittenGamePriority(int pid, BoostPass pass, uint previous, uint applied, int status)
		{
			bool settled = CrashGuard.CancelUnwrittenBoostPriority(pid, pass.RendererCreation, previous, applied);
			SetBoostActionRecovery(pid, "GamePriority", !settled);
			return settled ? status : 29;
		}
	}
}
