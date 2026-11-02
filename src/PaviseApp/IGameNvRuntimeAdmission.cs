namespace PaviseApp
{
	internal interface IGameNvRuntimeAdmission
	{
		GameNvTargetPhase Recheck(GameNvPrelaunchPlan originalPlan);

		void RequireRecovery(GameNvRecoveryIdentity original);
	}
}
