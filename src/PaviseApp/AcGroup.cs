namespace PaviseApp
{
    internal class AcGroup
    {
        public readonly string Key;

        private readonly string nameKey;

        public readonly bool Default;

        public readonly string[] Procs;

        public readonly AcCategory Category;

        public string Name
        {
            get
            {
                return Lang.T(nameKey);
            }
        }

        public bool RecordOnly
        {
            get
            {
                return Category == AcCategory.RecordOnly;
            }
        }

        public bool Governed
        {
            get
            {
                return Category == AcCategory.Governed;
            }
        }

        public AcGroup(string key, string name, bool def, string[] procs, AcCategory category)
        {
            Key = key;
            nameKey = name;
            Default = def;
            Procs = procs;
            Category = category;
        }
    }
}
