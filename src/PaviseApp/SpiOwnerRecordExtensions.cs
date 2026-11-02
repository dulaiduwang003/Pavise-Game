namespace PaviseApp
{
	internal static class SpiOwnerRecordExtensions
	{
		internal static bool OwnerMatches(this SpiOwnerRecord record, SpiOwnerStamp stamp)
		{
			if (record.Owner != null)
			{
				return record.Owner.Same(stamp);
			}
			return false;
		}
	}
}
