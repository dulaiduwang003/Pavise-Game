using System;

namespace PaviseApp
{
	internal interface IGameNvProfileReader : IDisposable
	{
		void Reload();

		GameNvProfile FindApplication(string fullPathOrName);

		GameNvProfile FindProfile(string name);

		GameNvSetting Read(string profileName, uint id);

		GameNvSetting Reference(uint id);
	}
}
