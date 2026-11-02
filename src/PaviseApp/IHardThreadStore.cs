using System;

namespace PaviseApp
{
	internal interface IHardThreadStore : IDisposable
	{
		HardThreadJournal Load(HardThreadTarget target);

		void Save(long expectedRevision, HardThreadJournal journal);
	}
}
