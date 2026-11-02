namespace PaviseApp
{
	internal enum HardThreadFailureKind
	{
		None,
		ObservationUnavailable,
		AuthorityUnavailable,
		InvalidRequest,
		WriteOrDurabilityFault,
		RecoveryPending
	}
}
