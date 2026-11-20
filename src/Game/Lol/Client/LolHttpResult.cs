namespace PaviseApp
{
    internal sealed class LolHttpResult
    {
        public bool Reached;

        public int Status;

        public string Body;

        public bool Success
        {
            get
            {
                if (Reached && Status >= 200)
                {
                    return Status < 300;
                }
                return false;
            }
        }
    }
}
