using System;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
	internal sealed class GameNvNativeAuthority : IDisposable
	{
		private readonly GameNvCanonicalStore store;

		private readonly int session;

		private readonly GameNvRecoveryIdentity recoveryOnly;

		private FileStream ownerPin;

		private bool disposed;

		internal string CanonicalStore
		{
			get
			{
				return store.Root;
			}
		}

		internal bool ResourcesClosed
		{
			get
			{
				if (disposed)
				{
					return ownerPin == null;
				}
				return false;
			}
		}

		private GameNvNativeAuthority(GameNvCanonicalStore root, GameNvRecoveryIdentity cleanup)
		{
			store = root;
			recoveryOnly = cleanup;
			using (Process process = Process.GetCurrentProcess())
			{
				session = process.SessionId;
			}
			ownerPin = store.PinNativeOwner();
		}

		internal static GameNvNativeAuthority ForRecovery(GameNvCanonicalStore store, GameNvRecoveryIdentity original)
		{
			if (store == null || original == null)
			{
				throw new ArgumentNullException("Original native recovery owner");
			}
			store.RequireNativeOwner();
			store.RequireReceipt(original);
			GameNvNativeAuthority gameNvNativeAuthority = new GameNvNativeAuthority(store, original);
			try
			{
				gameNvNativeAuthority.RequireRecovery(original);
				return gameNvNativeAuthority;
			}
			catch
			{
				gameNvNativeAuthority.Dispose();
				throw;
			}
		}

		internal void RequireForward(GameNvPrelaunchPlan originalPlan)
		{
			throw new NotSupportedException("Automatic NVIDIA result adoption has been removed; use the manual controls");
		}

		internal void RequireRecovery(GameNvRecoveryIdentity original)
		{
			if (disposed || ownerPin == null || !ownerPin.CanRead)
			{
				throw new ObjectDisposedException("Original native cleanup owner");
			}
			store.RequireNativeOwner();
			store.RequireReceipt(original);
			if (recoveryOnly == null || recoveryOnly.JournalDirectory != original.JournalDirectory || recoveryOnly.OriginalHash != original.OriginalHash)
			{
				throw new InvalidDataException("Native cleanup changed original resource ownership");
			}
			if (GameNvTargetProcesses.Phase(original.Executable, session) != GameNvTargetPhase.Closed)
			{
				throw new GameNvPreparationPending(GameNvPending.Recovery);
			}
		}

		public void Dispose()
		{
			disposed = true;
			CleanupSequence.Complete(null, delegate
			{
				if (ownerPin != null)
				{
					ownerPin.Dispose();
					ownerPin = null;
				}
			});
		}
	}
}
