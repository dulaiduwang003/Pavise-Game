using System;

namespace PaviseApp
{
    internal static class QuantumProfiles
    {
        internal const string SettingKey = "EnvQuantumSelectedProfileV1";

        internal static readonly int[] Values = new int[9] { -1, 38, 37, 36, 40, 22, 21, 20, 24 };

        internal static int Selected
        {
            get
            {
                if (!Settings.LoadCached("EnvQuantumProfileV1", false))
                {
                    return 0;
                }
                int result;
                if (int.TryParse(Settings.LoadStr("EnvQuantumSelectedProfileV1", "38"), out result))
                {
                    for (int i = 1; i < Values.Length; i++)
                    {
                        if (Values[i] == result)
                        {
                            return i;
                        }
                    }
                }
                return 1;
            }
        }

        internal static int Target
        {
            get
            {
                return Values[Math.Max(1, Selected)];
            }
        }
    }
}
