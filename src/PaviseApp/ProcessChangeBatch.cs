namespace PaviseApp
{
    internal sealed class ProcessChangeBatch
    {
        public readonly ProcessChange[] Changes;

        public readonly bool Overflowed;

        public ProcessChangeBatch(ProcessChange[] changes, bool overflowed)
        {
            Changes = changes ?? new ProcessChange[0];
            Overflowed = overflowed;
        }
    }
}
