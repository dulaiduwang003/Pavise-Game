using System;

namespace PaviseApp
{
	internal interface IGameNvProfileWriter : IGameNvProfileReader, IDisposable
	{
		void CreateProfile(string name);

		void Bind(string name, string exactPath);

		void SetUserOverride(string name, uint id, uint? value);

		void Unbind(string name, string exactPath);

		void DeleteEmptyProfile(string name);

		void Save();
	}
}
