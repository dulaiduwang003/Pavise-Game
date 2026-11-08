namespace PaviseApp
{
	internal static class GameNvPersistentRecovery
	{
		internal static void RequireClear(GameNvCanonicalStore store)
		{
			bool requireClearReady = false;
			string[] pendingJournalsResult = store.PendingJournals();
			foreach (string entryText in pendingJournalsResult)
			{
				try
				{
					GameNvRecoveryIdentity original = store.ReadOriginal(entryText);
					using (GameNvNativeAuthority authority = GameNvNativeAuthority.ForRecovery(store, original))
					{
						GameNvProfileLease.RecoverNative(entryText, authority);
					}
				}
				catch
				{
					requireClearReady = true;
				}
			}
			if (requireClearReady || store.PendingJournals().Length != 0)
			{
				throw new GameNvPreparationPending(GameNvPending.Recovery);
			}
		}

		internal static bool Restore(string dataRoot)
		{
			if (GameNvCanonicalStore.RootConfirmedAbsent(dataRoot))
			{
				return true;
			}
			try
			{
				RequireClear(GameNvCanonicalStore.Open(dataRoot));
				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}
