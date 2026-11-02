namespace PaviseApp
{
	internal sealed class PrivacyPlan
	{
		internal bool Supported;

		internal bool Limited;

		internal bool Managed;

		internal string Note;

		internal string UnavailableReason;

		internal PrivacyTarget[] Targets;
	}
}
