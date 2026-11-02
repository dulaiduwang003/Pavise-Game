namespace PaviseApp
{
	internal sealed class EnglishInputOutcome
	{
		internal readonly EnglishInputResult Result;

		internal readonly bool RequestAttempted;

		internal readonly int Error;

		internal EnglishInputOutcome(EnglishInputResult result)
			: this(result, false, 0)
		{
		}

		internal EnglishInputOutcome(EnglishInputResult result, bool attempted, int error)
		{
			Result = result;
			RequestAttempted = attempted;
			Error = error;
		}
	}
}
