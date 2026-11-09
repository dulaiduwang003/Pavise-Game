namespace PaviseApp
{
    internal static class DsLabels
    {
        public static string Pick(string zh, string en)
        {
            if (Lang.Cur != 0)
            {
                return en;
            }
            return zh;
        }
    }
}
