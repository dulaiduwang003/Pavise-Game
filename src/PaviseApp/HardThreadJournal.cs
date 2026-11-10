using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class HardThreadJournal
    {
        internal HardThreadTarget Target;

        internal long Revision;

        internal string SourceNonce;

        internal string SourceScopeHash;

        internal readonly List<HardThreadReceipt> Rows = new List<HardThreadReceipt>();

        internal HardThreadJournal Clone()
        {
            HardThreadJournal currentHardThreadJournal = new HardThreadJournal();
            currentHardThreadJournal.Target = Target;
            currentHardThreadJournal.Revision = Revision;
            currentHardThreadJournal.SourceNonce = SourceNonce;
            currentHardThreadJournal.SourceScopeHash = SourceScopeHash;
            foreach (HardThreadReceipt row in Rows)
            {
                currentHardThreadJournal.Rows.Add(row.Clone());
            }
            return currentHardThreadJournal;
        }
    }
}
