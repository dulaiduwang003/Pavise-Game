namespace PaviseApp
{
    internal sealed class GameProcessSnapshot
    {
        public int Pid;

        public int ParentPid;

        public long Creation;

        public string Name;

        public string Path;

        public bool Visible;

        public bool Foreground;

        public bool FullscreenLike;
    }
}
