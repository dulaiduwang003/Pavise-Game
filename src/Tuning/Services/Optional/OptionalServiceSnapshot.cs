namespace PaviseApp
{
    internal sealed class OptionalServiceSnapshot
    {
        public bool Exists;

        public bool AcceptsStop;

        public bool HasActiveDependents;

        public int State;

        public int StartType;

        public string Configuration;
    }
}
