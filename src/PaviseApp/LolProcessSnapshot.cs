namespace PaviseApp
{
    internal sealed class LolProcessSnapshot
    {
        public bool ScanSucceeded;

        public bool CoreIdentityIndeterminate;

        public bool ClientRunning;

        public bool GameRunning;

        public int GameProcessId;

        public int WeGameProcessCount;

        public bool WeGameMainRunning;

        public int CrossProcessCount;

        public int UxProcessCount;

        public int MainUxProcessCount;
    }
}
