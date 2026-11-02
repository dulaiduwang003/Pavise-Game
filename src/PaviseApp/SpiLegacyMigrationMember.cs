namespace PaviseApp
{
	internal sealed class SpiLegacyMigrationMember
	{
		internal readonly string Target;

		internal readonly string Slot;

		internal readonly string SourceHash;

		internal readonly string TerminalHash;

		internal SpiLegacyMigrationMember(string target, string source, string terminal)
		{
			Target = target;
			Slot = PerformanceActionStore.Slot("MatchVisualEffectsV1", target);
			SpiOwnerIdentity.Hash(source);
			SpiOwnerIdentity.Hash(terminal);
			SourceHash = source;
			TerminalHash = terminal;
		}
	}
}
