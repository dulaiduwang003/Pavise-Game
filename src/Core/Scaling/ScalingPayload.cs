using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace PaviseApp
{
    // Only the bytes embedded in this build may be executed. Hold a read lease
    // through Process.Start so a pre-existing payload cannot change during launch.
    internal sealed class ScalingPayload : IDisposable
    {
        internal const string ResourceName = "Pavise.ScaleHost.exe";
        internal readonly string Path;
        private FileStream lease;
        internal static bool Available
        {
            get { return Environment.Is64BitOperatingSystem && Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) != null; }
        }
        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
        internal ScalingPayload()
        {
            byte[] bytes;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (stream == null) throw new InvalidOperationException("scaling-component-missing");
                using (var memory = new MemoryStream()) { stream.CopyTo(memory); bytes = memory.ToArray(); }
            }
            string hash = Hash(bytes);
            string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pavise", "Scaling", hash);
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, ResourceName);
            if (!File.Exists(Path))
            {
                string temporary = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                    try { File.Move(temporary, Path); }
                    catch (IOException) { if (!File.Exists(Path)) throw; }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            lease = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                using (var sha = SHA256.Create())
                    if (!string.Equals(hash, BitConverter.ToString(sha.ComputeHash(lease)).Replace("-", ""), StringComparison.Ordinal))
                        throw new InvalidDataException("scaling-component-changed");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { if (lease != null) { lease.Dispose(); lease = null; } }
    }
}
