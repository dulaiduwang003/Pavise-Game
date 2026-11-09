using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class RunningProgram
    {
        public string Path;

        public string Title;

        public string ProcessName;

        public long Memory;

        public readonly List<int> ProcessIds = new List<int>();

        internal static string FormatMemory(long bytes)
        {
            if (bytes >= 1073741824)
            {
                return ((double)bytes / 1073741824.0).ToString("0.0") + " GB";
            }
            if (bytes >= 1048576)
            {
                return ((double)bytes / 1048576.0).ToString("0") + " MB";
            }
            if (bytes <= 0)
            {
                return "";
            }
            return ((double)bytes / 1024.0).ToString("0") + " KB";
        }
    }
}
