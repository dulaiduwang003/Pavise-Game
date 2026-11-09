namespace PaviseApp
{
    internal sealed class PerformanceAction
    {
        internal string Key;

        internal string Zh;

        internal string En;

        internal string DetailZh;

        internal string DetailEn;

        internal bool Environment;

        internal string Title
        {
            get
            {
                return PerformancePolicy.Text(Zh, En);
            }
        }

        internal string Detail
        {
            get
            {
                return PerformancePolicy.Text(DetailZh, DetailEn);
            }
        }

        internal PerformanceAction(string key, string zh, string en, string dz, string de, bool environment)
        {
            Key = key;
            Zh = zh;
            En = en;
            DetailZh = dz;
            DetailEn = de;
            Environment = environment;
        }
    }
}
