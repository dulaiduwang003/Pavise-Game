using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class HardThreadJournal
    {
        internal HardThreadTarget Target;

        internal long Revision;

        internal string SourceNonce;

        internal string SourceScopeHash;
        internal bool ControlDeclared;

        internal readonly List<HardThreadReceipt> Rows = new List<HardThreadReceipt>();

        internal HardThreadJournal Clone()
        {
            HardThreadJournal hardThreadJournal = new HardThreadJournal();
            hardThreadJournal.Target = Target;
            hardThreadJournal.Revision = Revision;
            hardThreadJournal.SourceNonce = SourceNonce;
            hardThreadJournal.SourceScopeHash = SourceScopeHash;
            hardThreadJournal.ControlDeclared = ControlDeclared;
            HardThreadJournal hardThreadJournal2 = hardThreadJournal;
            foreach (HardThreadReceipt row in Rows)
            {
                hardThreadJournal2.Rows.Add(row.Clone());
            }
            return hardThreadJournal2;
        }
    }
}
