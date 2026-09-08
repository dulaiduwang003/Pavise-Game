using System;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
    internal static class ScalingSettings
    {
        internal static string Key(string id, string option)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("profile id");
            using (var sha = SHA256.Create())
                return "WindowScaleV1_" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(id.ToLowerInvariant())))
                    .Replace("-", "") + "_" + option;
        }
        internal static bool Enabled(string id) { return Settings.LoadCached(Key(id, "Enabled"), false); }
        internal static bool MappedMouse(string id) { return Settings.LoadCached(Key(id, "MappedMouse"), false); }
        internal static bool Sharpen(string id) { return Settings.LoadCached(Key(id, "Sharpen"), true); }
        internal static bool Set(string id, string option, bool value)
        {
            if (option != "Enabled" && option != "MappedMouse" && option != "Sharpen") return false;
            bool saved = Settings.Save(Key(id, option), value);
            if (saved) ScalingService.ConfigurationChanged(id);
            return saved;
        }
    }
}
