using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
    internal static class GameNvText
    {
        internal static string Hash(byte[] bytes)
        {
            using (SHA256 sHA = SHA256.Create())
            {
                return BitConverter.ToString(sHA.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static bool IsHash(string value)
        {
            if (value != null && value.Length == 64)
            {
                return value.All((char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
            }
            return false;
        }

        internal static byte[] Encode(IDictionary<string, string> values)
        {
            return new UTF8Encoding(false, true).GetBytes(string.Concat(from x in values.OrderBy((KeyValuePair<string, string> x) => x.Key, StringComparer.Ordinal)
                select x.Key + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(x.Value)) + "\n"));
        }

        internal static Dictionary<string, string> Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 1 || bytes.Length > 1048576)
            {
                throw new InvalidDataException("Invalid game NVIDIA journal size");
            }
            Dictionary<string, string> textsByName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string entryText in new UTF8Encoding(false, true).GetString(bytes).Split('\n'))
            {
                if (entryText.Length != 0)
                {
                    string[] entryTextParts = entryText.Split('\t');
                    if (entryTextParts.Length != 2 || entryTextParts[0].Length == 0 || textsByName.ContainsKey(entryTextParts[0]))
                    {
                        throw new InvalidDataException("Invalid game NVIDIA journal fields");
                    }
                    textsByName.Add(entryTextParts[0], new UTF8Encoding(false, true).GetString(Convert.FromBase64String(entryTextParts[1])));
                }
            }
            if (!bytes.SequenceEqual(Encode(textsByName)))
            {
                throw new InvalidDataException("Noncanonical game NVIDIA journal");
            }
            return textsByName;
        }

        internal static void Create(string path, byte[] bytes)
        {
            NoReparse.CheckPath(path);
            using (FileStream fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                fileStream.Write(bytes, 0, bytes.Length);
                fileStream.Flush(true);
            }
        }
    }
}
