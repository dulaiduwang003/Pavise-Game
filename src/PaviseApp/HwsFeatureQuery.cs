namespace PaviseApp
{
#pragma warning disable 0649 // written by native marshaling, never by managed code
	internal struct HwsFeatureQuery
	{
		internal uint Adapter;

		internal uint FeatureId;

		internal HwsFeatureResult Result;
	}
#pragma warning restore 0649
}
