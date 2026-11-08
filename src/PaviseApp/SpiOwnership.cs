using System;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal sealed class SpiOwnership
	{
		private sealed class HeldMutex : IDisposable
		{
			private Mutex value;

			internal HeldMutex(Mutex mutex)
			{
				value = mutex;
			}

			public void Dispose()
			{
				if (value == null)
				{
					return;
				}
				try
				{
					value.ReleaseMutex();
				}
				finally
				{
					value.Dispose();
					value = null;
				}
			}
		}

		internal sealed class SpiOwnerAccess : IDisposable
		{
			private readonly SpiOwnership owner;

			private readonly int thread = Thread.CurrentThread.ManagedThreadId;

			private IDisposable gate;

			private readonly bool closed;

			internal readonly SpiOwnerStamp Stamp;

			internal readonly SpiOwnerOperation Operation;

			internal SpiOwnerAccess(SpiOwnership owner, SpiOwnerStamp stamp, SpiOwnerOperation operation, IDisposable gate, bool closed = false)
			{
				this.owner = owner;
				Stamp = stamp;
				Operation = operation;
				this.gate = gate;
				this.closed = closed;
			}

			internal void Assert(SpiOwnerStamp expected, bool writing)
			{
				if (gate == null || current != this || thread != Thread.CurrentThread.ManagedThreadId || !Stamp.Same(expected) || Operation == SpiOwnerOperation.Metadata)
				{
					throw new InvalidOperationException("SPI primitive has no exact held ownership capability");
				}
				owner.RequireHeld(Stamp, Operation);
			}

			internal void AssertMetadata(SpiOwnerStamp expected, bool requireClosed)
			{
				if (gate == null || current != this || thread != Thread.CurrentThread.ManagedThreadId || !Stamp.Same(expected) || requireClosed != closed)
				{
					throw new InvalidOperationException("SPI metadata has no exact held ownership capability");
				}
				if (closed)
				{
					owner.RequireClosed(Stamp);
				}
				else
				{
					owner.RequireHeld(Stamp, Operation);
				}
			}

			internal SpiOwnerRecord RecordForEvidence(bool requireClosed)
			{
				AssertMetadata(Stamp, requireClosed);
				if (!requireClosed)
				{
					return owner.RequireHeld(Stamp, Operation);
				}
				return owner.RequireClosed(Stamp);
			}

			internal string PublishClosure(byte[] bytes)
			{
				AssertMetadata(Stamp, false);
				if (Operation != SpiOwnerOperation.Restore)
				{
					throw new InvalidOperationException("Only restoration can publish SPI closure evidence");
				}
				return owner.store.PublishClosure(Stamp, bytes);
			}

			internal byte[] ReadClosedManifest()
			{
				SpiOwnerRecord spiOwnerRecord = RecordForEvidence(true);
				byte[] closure = owner.store.ReadClosure(Stamp);
				if (closure == null || SpiOwnerRecord.Digest(closure) != spiOwnerRecord.ClosureDigest)
				{
					throw new InvalidDataException("SPI closed manifest is missing or changed");
				}
				return closure;
			}

			public void Dispose()
			{
				if (gate != null)
				{
					if (thread != Thread.CurrentThread.ManagedThreadId || current != this)
					{
						throw new InvalidOperationException("SPI capability must close on its original thread");
					}
					current = null;
					IDisposable disposable = gate;
					gate = null;
					disposable.Dispose();
				}
			}
		}

		private readonly SpiOwnerFileStore store;

		private readonly string sid;

		private readonly Func<IDisposable> enter;

		private readonly bool isolatedStore;

		[ThreadStatic]
		private static SpiOwnerAccess current;

		internal static bool HasHeldCapability
		{
			get
			{
				return current != null;
			}
		}

		internal void InitializeFreshFromMain(SharedSpiBootstrap.BootstrapProof proof)
		{
			if (proof == null || proof.UserSid != sid || !string.Equals(proof.StorePath, store.PathName, StringComparison.OrdinalIgnoreCase))
			{
				throw new UnauthorizedAccessException("SPI bootstrap proof belongs to another store or user");
			}
			proof.RequireCurrent();
			using (enter())
			{
				proof.RequireCurrent();
				foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
				{
					if (item.Id == "MatchVisualEffectsV1")
					{
						throw new InvalidDataException("Visual recovery appeared before SPI bootstrap commit");
					}
				}
				string directoryName = Path.GetDirectoryName(store.PathName);
				NoReparse.CheckPath(directoryName);
				if (Directory.Exists(directoryName) || File.Exists(directoryName))
				{
					throw new InvalidDataException("SPI owner directory already exists; initialization cannot be repaired or repeated");
				}
				Directory.CreateDirectory(directoryName);
				NoReparse.CheckPath(directoryName);
				string closureDigest = store.PublishInitialization(proof.InitializationBytes());
				proof.RequireCurrent();
				store.Publish(null, new SpiOwnerRecord(sid, 0L, SpiOwnerPhase.Closed, null, closureDigest).Bytes());
				proof.RequireCurrent();
				store.ValidateInitialization(sid, SpiOwnerRecord.Parse(store.Read()));
			}
		}

		internal void InitializeLegacyFromMain(SharedSpiBootstrap.BootstrapProof proof)
		{
			if (proof == null || proof.Migration == null || proof.UserSid != sid || !string.Equals(proof.StorePath, store.PathName, StringComparison.OrdinalIgnoreCase))
			{
				throw new UnauthorizedAccessException("Legacy upgrade proof differs from the original store/user");
			}
			proof.RequireCurrent();
			using (enter())
			{
				proof.RequireCurrent();
				string directoryName = Path.GetDirectoryName(store.PathName);
				NoReparse.CheckPath(directoryName);
				if (File.Exists(store.PathName) || store.ReadInitialization() != null)
				{
					throw new InvalidDataException("Legacy upgrade cannot repair an existing or interrupted initialized owner");
				}
				bool existsSucceeded = Directory.Exists(directoryName);
				if (existsSucceeded && !SpiOwnerRecord.Equal(store.ReadLegacy("legacy-migration.tsv"), proof.Migration.Manifest))
				{
					throw new InvalidDataException("Existing canonical directory is not this exact upgrade transaction");
				}
				proof.Migration.RequireAllCurrent();
				if (!existsSucceeded)
				{
					Directory.CreateDirectory(directoryName);
					NoReparse.CheckPath(directoryName);
				}
				store.PublishLegacy("legacy-migration.tsv", proof.Migration.Manifest);
				proof.RequireCurrent();
				using (new SharedSpiLegacyMigration.PrimitiveScope(proof))
				{
					foreach (SpiLegacyMigrationMember value in proof.Migration.Members.Values)
					{
						if (!PerformanceActionStore.RestoreLegacyVisualBeforeInitialization(proof.Migration.Current(value.Target)))
						{
							throw new IOException("Legacy visual original restoration remains unconfirmed");
						}
					}
					foreach (SpiLegacyMigrationMember value2 in proof.Migration.Members.Values)
					{
						PerformanceActionStore.Receipt receipt = proof.Migration.Current(value2.Target);
						if (!receipt.VisualRestored || PerformanceActionBackend.Read(receipt.Id, receipt.Target) != receipt.Original)
						{
							throw new IOException("Legacy original readback changed before initialization");
						}
					}
				}
				string legacyClosure = store.PublishLegacy("legacy-close.tsv", proof.Migration.ClosureBytes());
				proof.RequireCurrent();
				string closureDigest = store.PublishInitialization(proof.InitializationBytes(legacyClosure));
				proof.RequireCurrent();
				store.Publish(null, new SpiOwnerRecord(sid, 0L, SpiOwnerPhase.Closed, null, closureDigest).Bytes());
				proof.RequireCurrent();
				store.ValidateInitialization(sid, SpiOwnerRecord.Parse(store.Read()));
				proof.Migration.ClearClosedMetadata(store);
			}
		}

		internal void FinishLegacyMetadataFromMain(Program.MainInstanceBootstrapLease originalMain)
		{
			originalMain.RequireCurrent();
			using (enter())
			{
				originalMain.RequireCurrent();
				SpiOwnerRecord spiOwnerRecord = SpiOwnerRecord.Parse(store.Read());
				RequireUser(spiOwnerRecord);
				store.ValidateInitialization(sid, spiOwnerRecord);
				string @string = new UTF8Encoding(false, true).GetString(store.ReadInitialization());
				if (@string.Contains("legacy_closure\tnone\n"))
				{
					return;
				}
				bool finishLegacyMetadataFromMainReady = false;
				foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
				{
					if (item.Id == "MatchVisualEffectsV1" && string.IsNullOrEmpty(item.VisualOwner))
					{
						finishLegacyMetadataFromMainReady = true;
					}
				}
				if (finishLegacyMetadataFromMainReady)
				{
					if (spiOwnerRecord.Generation != 0 || spiOwnerRecord.Phase != SpiOwnerPhase.Closed || spiOwnerRecord.Owner != null)
					{
						throw new InvalidDataException("Legacy leftovers cannot be adopted by a later owner epoch");
					}
					SpiLegacyMigrationPlan spiLegacyMigrationPlan = SpiLegacyMigrationPlan.Read(sid, store.ReadLegacy("legacy-migration.tsv"));
					spiLegacyMigrationPlan.ClearClosedMetadata(store);
				}
			}
		}

		internal SpiOwnerStamp ClaimManualFromClosed(SpiOwnerRecord expected)
		{
			using (enter())
			{
				SpiOwnerRecord spiOwnerRecord = SpiOwnerRecord.Parse(store.Read());
				RequireUser(spiOwnerRecord);
				if (expected == null || spiOwnerRecord.Phase != SpiOwnerPhase.Closed || !SpiOwnerRecord.Equal(expected.Bytes(), spiOwnerRecord.Bytes()))
				{
					throw new InvalidOperationException("SPI owner changed before manual admission");
				}
				if (spiOwnerRecord.Owner != null && spiOwnerRecord.Owner.Identity.Kind == SpiOwnerKind.LegacyManual)
				{
					throw new InvalidOperationException("Legacy ownership cannot be silently adopted");
				}
				if (spiOwnerRecord.Owner != null && spiOwnerRecord.Owner.Identity.Kind == SpiOwnerKind.Manual)
				{
					using (SpiOwnerAccess closedAccess = EnterClosedMetadata(spiOwnerRecord.Owner))
					{
						if (!PerformanceActionStore.ClearVisualTerminalAfterClose(closedAccess))
						{
							throw new InvalidOperationException("Closed SPI owner still has terminal metadata to settle");
						}
					}
				}
				return Claim(spiOwnerRecord, new SpiOwnerIdentity(SpiOwnerKind.Manual, sid, Guid.NewGuid().ToString("N")), SpiOwnerClaimEvidence.FromEmptyVisualIndex(spiOwnerRecord));
			}
		}

		internal static void RequireManualPrimitive(bool writing)
		{
			if (current == null || current.Stamp.Identity.Kind != 0)
			{
				throw new InvalidOperationException("Manual SPI primitive has no original owner capability");
			}
			current.Assert(current.Stamp, writing);
		}

		internal SpiOwnership(SpiOwnerFileStore store, string userSid)
			: this(store, userSid, false)
		{
		}

		private SpiOwnership(SpiOwnerFileStore store, string userSid, bool isolated)
		{
			if (store == null)
			{
				throw new ArgumentNullException("store");
			}
			SpiOwnerIdentity.Sid(userSid);
			if (!isolated && !string.Equals(store.PathName, SpiOwnerFileStore.CanonicalPath(userSid), StringComparison.OrdinalIgnoreCase))
			{
				throw new UnauthorizedAccessException("SPI owner store is not the canonical per-user location");
			}
			this.store = store;
			sid = userSid;
			isolatedStore = isolated;
			enter = () => EnterGlobal(userSid);
		}

		private static IDisposable EnterGlobal(string userSid)
		{
			using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
			{
				if (windowsIdentity.User == null || windowsIdentity.User.Value != userSid)
				{
					throw new UnauthorizedAccessException("SPI owner user changed");
				}
			}
			SecurityIdentifier user = new SecurityIdentifier(userSid);
			bool createdNew;
			Mutex mutex = new Mutex(false, "Global\\Pavise.SharedSpi.VisualEffects." + userSid, out createdNew, UserMutexSecurity.Create(user));
			bool enterGlobalReady = false;
			try
			{
				UserMutexSecurity.Verify(mutex.GetAccessControl(), user);
				try
				{
					enterGlobalReady = mutex.WaitOne(3000);
				}
				catch (AbandonedMutexException)
				{
					enterGlobalReady = true;
				}
				if (!enterGlobalReady)
				{
					throw new TimeoutException("Shared SPI mutation gate timed out; owner remains pending");
				}
				UserMutexSecurity.Verify(mutex.GetAccessControl(), user);
				return new HeldMutex(mutex);
			}
			catch
			{
				try
				{
					if (enterGlobalReady)
					{
						mutex.ReleaseMutex();
					}
				}
				finally
				{
					mutex.Dispose();
				}
				throw;
			}
		}

		private void RequireUser(SpiOwnerRecord record)
		{
			if (record == null || record.UserSid != sid)
			{
				throw new UnauthorizedAccessException("SPI link is absent or belongs to another user");
			}
			if (!isolatedStore)
			{
				store.ValidateInitialization(sid, record);
			}
		}

		private static void RequireEvidence(SpiOwnerRecord record, SpiOwnerEvidence evidence)
		{
			if (evidence == null || evidence.OwnerDigest != SpiOwnerRecord.Digest(record.Bytes()))
			{
				throw new InvalidDataException("SPI closure evidence is not bound to this exact owner state");
			}
		}

		internal SpiOwnerRecord Read()
		{
			using (enter())
			{
				SpiOwnerRecord spiOwnerRecord = SpiOwnerRecord.Parse(store.Read());
				RequireUser(spiOwnerRecord);
				return spiOwnerRecord;
			}
		}

		internal SpiOwnerStamp Claim(SpiOwnerRecord expectedClosed, SpiOwnerIdentity identity, SpiOwnerClaimEvidence evidence)
		{
			if (identity == null || identity.UserSid != sid)
			{
				throw new UnauthorizedAccessException("SPI claim user differs");
			}
			using (enter())
			{
				SpiOwnerRecord spiOwnerRecord = SpiOwnerRecord.Parse(store.Read());
				RequireUser(spiOwnerRecord);
				if (expectedClosed == null || !SpiOwnerRecord.Equal(spiOwnerRecord.Bytes(), expectedClosed.Bytes()) || spiOwnerRecord.Phase != SpiOwnerPhase.Closed)
				{
					throw new InvalidOperationException("SPI owner has not been confirmed closed");
				}
				if (evidence == null || evidence.ClosedDigest != SpiOwnerRecord.Digest(spiOwnerRecord.Bytes()))
				{
					throw new InvalidDataException("SPI claim has no exact terminal receipt settlement proof");
				}
				if (spiOwnerRecord.Owner != null && spiOwnerRecord.Owner.Identity.Same(identity))
				{
					throw new InvalidOperationException("A closed SPI identity cannot be reclaimed");
				}
				if (spiOwnerRecord.Generation == long.MaxValue)
				{
					throw new InvalidDataException("SPI ownership generation exhausted");
				}
				SpiOwnerStamp spiOwnerStamp = new SpiOwnerStamp(spiOwnerRecord.Generation + 1, Guid.NewGuid().ToString("N"), identity);
				store.Publish(spiOwnerRecord.Bytes(), new SpiOwnerRecord(sid, spiOwnerStamp.Generation, (identity.Kind == SpiOwnerKind.LegacyManual) ? SpiOwnerPhase.RestoreOnly : SpiOwnerPhase.Held, spiOwnerStamp, "").Bytes());
				return spiOwnerStamp;
			}
		}

		internal void RestrictToRestore(SpiOwnerStamp expected)
		{
			using (enter())
			{
				SpiOwnerRecord spiOwnerRecord = RequireHeld(expected, SpiOwnerOperation.Metadata);
				if (spiOwnerRecord.Phase != SpiOwnerPhase.RestoreOnly)
				{
					store.Publish(spiOwnerRecord.Bytes(), new SpiOwnerRecord(sid, spiOwnerRecord.Generation, SpiOwnerPhase.RestoreOnly, spiOwnerRecord.Owner, "").Bytes());
				}
			}
		}

		internal void Close(SpiOwnerStamp expected, SpiOwnerEvidence evidence)
		{
			using (enter())
			{
				SpiOwnerRecord spiOwnerRecord = RequireHeld(expected, SpiOwnerOperation.Restore);
				RequireEvidence(spiOwnerRecord, evidence);
				store.Publish(spiOwnerRecord.Bytes(), new SpiOwnerRecord(sid, spiOwnerRecord.Generation, SpiOwnerPhase.Closed, spiOwnerRecord.Owner, evidence.EvidenceDigest).Bytes());
			}
		}

		private SpiOwnerRecord RequireHeld(SpiOwnerStamp expected, SpiOwnerOperation operation)
		{
			if (operation != 0 && operation != SpiOwnerOperation.Restore && operation != SpiOwnerOperation.Metadata)
			{
				throw new InvalidDataException("Unknown SPI owner operation");
			}
			SpiOwnerRecord spiOwnerRecord = SpiOwnerRecord.Parse(store.Read());
			RequireUser(spiOwnerRecord);
			if (expected == null || !spiOwnerRecord.OwnerMatches(expected) || spiOwnerRecord.Phase == SpiOwnerPhase.Closed || (operation == SpiOwnerOperation.Apply && spiOwnerRecord.Phase == SpiOwnerPhase.RestoreOnly))
			{
				throw new InvalidOperationException("SPI owner epoch changed, closed or is restore-only");
			}
			return spiOwnerRecord;
		}

		private SpiOwnerRecord RequireClosed(SpiOwnerStamp expected)
		{
			SpiOwnerRecord spiOwnerRecord = SpiOwnerRecord.Parse(store.Read());
			RequireUser(spiOwnerRecord);
			if (expected == null || spiOwnerRecord.Phase != SpiOwnerPhase.Closed || !spiOwnerRecord.OwnerMatches(expected))
			{
				throw new InvalidOperationException("Terminal SPI metadata is not this closed epoch");
			}
			return spiOwnerRecord;
		}

		internal SpiOwnerAccess EnterClosedMetadata(SpiOwnerStamp expected)
		{
			IDisposable disposable = enter();
			try
			{
				RequireClosed(expected);
				if (current != null)
				{
					throw new InvalidOperationException("Nested SPI ownership capabilities are not allowed");
				}
				return current = new SpiOwnerAccess(this, expected, SpiOwnerOperation.Metadata, disposable, true);
			}
			catch
			{
				disposable.Dispose();
				throw;
			}
		}

		internal SpiOwnerAccess Enter(SpiOwnerStamp expected, SpiOwnerOperation operation)
		{
			IDisposable disposable = enter();
			try
			{
				RequireHeld(expected, operation);
				if (current != null)
				{
					throw new InvalidOperationException("Nested SPI ownership capabilities are not allowed");
				}
				return current = new SpiOwnerAccess(this, expected, operation, disposable);
			}
			catch
			{
				disposable.Dispose();
				throw;
			}
		}
	}
}
