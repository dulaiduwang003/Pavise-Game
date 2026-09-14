// @author bdth 2074055628@qq.com
// File purpose Global strength tier for anti-cheat suppression; all three tiers share the same active ingredients, only the depth of intervention escalates
using System;

namespace PaviseApp
{
    // The active ingredients are EcoQoS, E-core frequency cap and disk IO downgrade; every tier has all of them
    //   What escalates is the depth: Gentle leaves scheduling priority alone, Balanced drops it below normal, Isolated adds very low IO and core pinning
    //   See the comment at the top of SuppressionCore.Apply: anti-cheat suppression must not starve it
    //   A scanning anti-cheat that suspends game threads gets no time slice itself, and the suspend window stretches from hundreds of ms to seconds
    internal enum AntiCheatMode
    {
        Gentle = 0,
        Balanced = 1,
        Isolated = 2,
    }

    internal static class AntiCheatModes
    {
        public const string Key = "AcModeV1";
        // Default stays Isolated; it equals what this feature has always done, so upgrades never change settings already in effect
        public const AntiCheatMode Default = AntiCheatMode.Isolated;

        public static AntiCheatMode Current
        {
            get { return Parse(Settings.LoadStr(Key, "")); }
        }

        public static void Save(AntiCheatMode mode) { Settings.SaveStr(Key, Token(mode)); }

        internal static string Token(AntiCheatMode mode)
        {
            switch (mode)
            {
                case AntiCheatMode.Gentle: return "gentle";
                case AntiCheatMode.Balanced: return "balanced";
                default: return "isolated";
            }
        }

        // Unreadable or unrecognized values fall back to the default; never guess which tier the user wanted
        internal static AntiCheatMode Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Default;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "gentle": return AntiCheatMode.Gentle;
                case "balanced": return AntiCheatMode.Balanced;
                case "isolated": return AntiCheatMode.Isolated;
                default: return Default;
            }
        }

        // Gentle only lowers IO and leaves priority alone; Balanced lowers priority; Isolated adds very low IO
        //   These three values are exactly the existing SuppressionLevel, so the suppression composition code needs no change
        public static SuppressionLevel LevelOf(AntiCheatMode mode)
        {
            switch (mode)
            {
                case AntiCheatMode.Gentle: return SuppressionLevel.Eco;
                case AntiCheatMode.Balanced: return SuppressionLevel.Restrained;
                default: return SuppressionLevel.Isolated;
            }
        }

        // Core pinning is the one of the three most likely to be refused by the anti-cheat's self-protection, so only the top tier does it
        public static bool PinsCores(AntiCheatMode mode) { return mode == AntiCheatMode.Isolated; }

        // Dropping from a pinning tier to a non-pinning one must actively release existing placements
        //   Merely stopping new ones is not enough: unless SqueezeAff zeroes DesiredAffinity it keeps returning the placement and every reconcile pass writes it back
        public static bool ShouldReleasePins(AntiCheatMode from, AntiCheatMode to)
        {
            return PinsCores(from) && !PinsCores(to);
        }

        public static string NameOf(AntiCheatMode mode) { return Lang.T("ac.mode." + Token(mode)); }
    }
}
