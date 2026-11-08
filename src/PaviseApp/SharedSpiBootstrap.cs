using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal static class SharedSpiBootstrap
	{
		internal sealed class BootstrapProof
		{
			private readonly Program.MainInstanceBootstrapLease main;

			internal readonly string UserSid;

			internal readonly string StorePath;

			internal readonly SpiLegacyMigrationPlan Migration;

			private readonly byte[] initialization;

			internal BootstrapProof(object witness, Program.MainInstanceBootstrapLease originalMain, string sid, string storePath, byte[] bytes, SpiLegacyMigrationPlan migration = null)
			{
				if (!object.ReferenceEquals(witness, mintWitness))
				{
					throw new UnauthorizedAccessException("Only complete startup preflight can mint SPI bootstrap proof");
				}
				main = originalMain;
				UserSid = sid;
				StorePath = storePath;
				Migration = migration;
				initialization = (byte[])bytes.Clone();
				main.RequireCurrent();
			}

			internal void RequireCurrent()
			{
				main.RequireCurrent();
			}

			internal byte[] InitializationBytes(string legacyClosure = "none")
			{
				RequireCurrent();
				if (legacyClosure != "none")
				{
					SpiOwnerIdentity.Hash(legacyClosure);
				}
				return new UTF8Encoding(false, true).GetBytes(new UTF8Encoding(false, true).GetString(initialization).Replace("legacy_closure\tnone\n", "legacy_closure\t" + legacyClosure + "\n"));
			}
		}

		private static readonly object mintWitness = new object();

		private static SpiBootstrapResult lastResult = new SpiBootstrapResult(false, false, "SPI ownership startup admission has not run");

		internal static SpiBootstrapResult LastResult
		{
			get
			{
				return Volatile.Read(ref lastResult);
			}
		}

		internal static SpiBootstrapResult Prepare(Program.MainInstanceBootstrapLease originalMain)
		{
			SpiBootstrapResult spiBootstrapResult;
			try
			{
				spiBootstrapResult = Initialize(originalMain);
			}
			catch (Exception ex)
			{
				spiBootstrapResult = new SpiBootstrapResult(false, false, "SPI ownership initialization remains unknown: " + ex.GetType().Name + ": " + ex.Message);
			}
			Volatile.Write(ref lastResult, spiBootstrapResult);
			return spiBootstrapResult;
		}

		internal static SpiBootstrapResult Initialize(Program.MainInstanceBootstrapLease originalMain)
		{
			if (originalMain == null)
			{
				throw new ArgumentNullException("originalMain");
			}
			originalMain.ClaimBootstrap();
			using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
			{
				if (windowsIdentity.User == null)
				{
					throw new UnauthorizedAccessException("SPI bootstrap user is unknown");
				}
				string value = windowsIdentity.User.Value;
				SpiOwnerFileStore store = new SpiOwnerFileStore(SpiOwnerFileStore.CanonicalPath(value));
				return InitializeCore(originalMain, value, store, new SpiOwnership(store, value));
			}
		}

		private static SpiBootstrapResult InitializeCore(Program.MainInstanceBootstrapLease originalMain, string sid, SpiOwnerFileStore store, SpiOwnership owner)
		{
			originalMain.RequireCurrent();
			SpiOwnerIdentity.Sid(sid);
			string directoryName = Path.GetDirectoryName(store.PathName);
			bool initializeCoreReady = false;
			if (Directory.Exists(directoryName))
			{
				if (File.Exists(store.PathName))
				{
					SpiOwnerRecord record = owner.Read();
					store.ValidateInitialization(sid, record);
					owner.FinishLegacyMetadataFromMain(originalMain);
					return new SpiBootstrapResult(false, true, "Existing SPI owner link retained; only bound terminal metadata was eligible for cleanup");
				}
				if (store.ReadInitialization() != null || store.ReadLegacy("legacy-migration.tsv") == null)
				{
					throw new InvalidDataException("Existing SPI directory has no valid owner; initialization remains unknown");
				}
				initializeCoreReady = true;
			}
			NoReparse.CheckPath(directoryName);
			List<PerformanceActionStore.Receipt> list = new List<PerformanceActionStore.Receipt>();
			foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
			{
				if (item.Id == "MatchVisualEffectsV1")
				{
					if (!string.IsNullOrEmpty(item.VisualOwner))
					{
						return new SpiBootstrapResult(false, false, "Owned visual receipts witness a missing protocol owner; no bootstrap repair");
					}
					list.Add(item);
				}
			}
			SpiLegacyMigrationPlan spiLegacyMigrationPlan = ((list.Count == 0 && !initializeCoreReady) ? null : SpiLegacyMigrationPlan.Prepare(sid, store, list, initializeCoreReady));
			originalMain.RequireCurrent();
			byte[] bytes = new UTF8Encoding(false, true).GetBytes("pavise-spi-initialization-3\nuser_sid\t" + sid + "\nnonce\t" + Guid.NewGuid().ToString("N") + "\nlegacy_closure\tnone\nmain\toriginal-single-instance-lease-v1\n");
			BootstrapProof proof = new BootstrapProof(mintWitness, originalMain, sid, store.PathName, bytes, spiLegacyMigrationPlan);
			if (spiLegacyMigrationPlan == null)
			{
				owner.InitializeFreshFromMain(proof);
			}
			else
			{
				owner.InitializeLegacyFromMain(proof);
			}
			return new SpiBootstrapResult(true, false, (spiLegacyMigrationPlan == null) ? "Verified never-initialized SPI ownership became closed generation zero" : "Legacy restore-only upgrade confirmed originals before closed generation zero");
		}
	}
}
