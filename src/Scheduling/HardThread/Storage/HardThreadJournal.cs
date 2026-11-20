using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class HardThreadJournal
    {
        internal HardThreadTarget Target;

        internal long Revision;

        internal readonly List<HardThreadReceipt> Rows = new List<HardThreadReceipt>();

        internal HardThreadJournal Clone()
        {
            HardThreadJournal hardThreadJournal = new HardThreadJournal();
            hardThreadJournal.Target = Target;
            hardThreadJournal.Revision = Revision;
            HardThreadJournal hardThreadJournal2 = hardThreadJournal;
            foreach (HardThreadReceipt row in Rows)
            {
                hardThreadJournal2.Rows.Add(row.Clone());
            }
            return hardThreadJournal2;
        }
    }
}
