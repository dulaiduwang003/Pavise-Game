namespace PaviseApp
{
    internal static class GameNvPersistentRecovery
    {
        internal static void RequireClear(GameNvCanonicalStore store)
        {
            bool recoveryPending = false;
            foreach (string entryText in store.PendingJournals())
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
                    recoveryPending = true;
                }
            }
            if (recoveryPending || store.PendingJournals().Length != 0)
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
