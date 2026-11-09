namespace PaviseApp
{
    internal sealed class PrivacyAction
    {
        internal string Key;

        internal string Zh;

        internal string En;

        internal string DetailZh;

        internal string DetailEn;

        internal bool Advanced;

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

        internal PrivacyAction(string key, string zh, string en, string dz, string de, bool advanced)
        {
            Key = key;
            Zh = zh;
            En = en;
            DetailZh = dz;
            DetailEn = de;
            Advanced = advanced;
        }
    }
}
