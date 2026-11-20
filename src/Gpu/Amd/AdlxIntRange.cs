namespace PaviseApp
{
#pragma warning disable 0649
    internal struct AdlxIntRange
    {
        public int Min;

        public int Max;

        public int Step;

        internal bool Contains(long value)
        {
            if (value < Min || value > Max) return false;
            if (Step == 0) return Min == Max;
            return Step > 0 && (value - Min) % Step == 0;
        }
    }
#pragma warning restore 0649
}
