namespace PaviseApp
{
	internal sealed class SpiBootstrapResult
	{
		internal readonly bool Initialized;

		internal readonly bool Existing;

		internal readonly string Detail;

		internal SpiBootstrapResult(bool initialized, bool existing, string detail)
		{
			Initialized = initialized;
			Existing = existing;
			Detail = detail;
		}
	}
}
