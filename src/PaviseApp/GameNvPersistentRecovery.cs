namespace PaviseApp
{
	internal static class GameNvPersistentRecovery
	{
		internal static void RequireClear(GameNvCanonicalStore store)
		{
			bool flag = false;
			string[] array = store.PendingJournals();
			foreach (string text in array)
			{
				try
				{
					GameNvRecoveryIdentity original = store.ReadOriginal(text);
					using (GameNvNativeAuthority authority = GameNvNativeAuthority.ForRecovery(store, original))
					{
						GameNvProfileLease.RecoverNative(text, authority);
					}
				}
				catch
				{
					flag = true;
				}
			}
			if (flag || store.PendingJournals().Length != 0)
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
