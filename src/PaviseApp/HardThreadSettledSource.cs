namespace PaviseApp
{
    internal sealed class HardThreadSettledSource
    {
        internal readonly string Root;

        internal readonly string JournalHash;

        internal readonly HardThreadTarget Target;

        internal readonly long Revision;
        internal readonly string ControlHash;

        internal HardThreadSettledSource(string root, HardThreadTarget target, long revision, string hash, string controlHash = null)
        {
            Root = root;
            Target = target;
            Revision = revision;
            JournalHash = hash;
            ControlHash = controlHash;
        }
    }
}
