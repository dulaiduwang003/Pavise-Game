namespace PaviseApp
{
    internal sealed class PolicyItem
    {
        public readonly string Key;

        public readonly PolicyValueKind Kind;

        public readonly string Fallback;

        public readonly string LangKey;

        public readonly string GroupKey;

        public readonly string[] Choices;

        public PolicyItem(string key, PolicyValueKind kind, string fallback, string langKey, string groupKey, string[] choices)
        {
            Key = key;
            Kind = kind;
            Fallback = fallback;
            LangKey = langKey;
            GroupKey = groupKey;
            Choices = choices ?? new string[0];
        }
    }
}
