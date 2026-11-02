namespace PaviseApp
{
	internal interface IHardThreadOriginPlatform
	{
		HardThreadOriginContext Current();

		byte[] Seal(byte[] original);

		byte[] Unseal(byte[] original);
	}
}
