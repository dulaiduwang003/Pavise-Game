using System;

namespace PaviseApp
{
	[Flags]
	internal enum SuppressReason
	{
		None = 0,
		AntiCheat = 1,
		Background = 2
	}
}
