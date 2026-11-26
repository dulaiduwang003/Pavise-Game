using System;
using System.IO;

namespace PaviseApp
{
    internal static class NativeLoaderChecks
    {
        private static int checks;
        private static void Check(bool condition, string reason)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(reason);
        }

        public static void Main()
        {
            // 1. Validate search and local path identification
            Check(NativeImageFile.IsLocalCandidate(AppDomain.CurrentDomain.BaseDirectory), "BaseDirectory should be recognized as local");
            Check(NativeImageFile.IsLocalCandidate(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test.dll")), "File in BaseDirectory should be local");
            Check(NativeImageFile.IsLocalCandidate(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native", "test.dll")), "File in native subfolder should be local");
            Check(!NativeImageFile.IsLocalCandidate(Path.GetTempPath()), "TempPath should not be recognized as local candidate");

            // 2. Embedded image contract checks (CPU, ETW, HID)
            Check(!string.IsNullOrEmpty(NativeCpuImage.Base64), "NativeCpuImage Base64 must not be empty");
            Check(NativeCpuImage.Length > 0, "NativeCpuImage Length must be > 0");
            Check(NativeCpuImage.Sha256.Length == 64, "NativeCpuImage Sha256 length must be 64");

            Check(!string.IsNullOrEmpty(NativeEtwImage.Base64), "NativeEtwImage Base64 must not be empty");
            Check(NativeEtwImage.Length > 0, "NativeEtwImage Length must be > 0");
            Check(NativeEtwImage.Sha256.Length == 64, "NativeEtwImage Sha256 length must be 64");

            Check(!string.IsNullOrEmpty(NativeHidImage.Base64), "NativeHidImage Base64 must not be empty");
            Check(NativeHidImage.Length > 0, "NativeHidImage Length must be > 0");
            Check(NativeHidImage.Sha256.Length == 64, "NativeHidImage Sha256 length must be 64");

            // 3. Isolated side-by-side loading and tamper fallback verification
            string isolatedAppDir = Path.Combine(Path.GetTempPath(), "PaviseLoaderTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(isolatedAppDir);
            try
            {
                NativeImageFile.RootForTest = isolatedAppDir;

                // 3a. Valid companion DLL loads directly without AppData extraction
                byte[] cpuDll = Convert.FromBase64String(NativeCpuImage.Base64);
                string cpuPath = Path.Combine(isolatedAppDir, "PaviseCpu.dll");
                File.WriteAllBytes(cpuPath, cpuDll);

                string path;
                string reason;
                using (FileStream held = NativeImageFile.Prepare(NativeCpuImage.Base64, NativeCpuImage.Length, NativeCpuImage.Sha256, "PaviseCpu.dll", out path, out reason))
                {
                    Check(held != null, "Prepare with side-by-side companion DLL should succeed: " + reason);
                    Check(string.Equals(path, cpuPath, StringComparison.OrdinalIgnoreCase), "Prepared target must match companion DLL path: " + path);
                }

                // 3b. Missing companion DLL falls back to AppData unpack
                using (FileStream held = NativeImageFile.Prepare(NativeHidImage.Base64, NativeHidImage.Length, NativeHidImage.Sha256, "PaviseHid.dll", out path, out reason))
                {
                    Check(held != null, "Prepare without side-by-side DLL should fall back to unpack: " + reason);
                    Check(File.Exists(path), "Unpacked target must exist: " + path);
                    Check(path.Contains("native"), "Unpacked target must be under native data subfolder: " + path);
                }

                // 3c. Tampered/corrupt companion DLL is rejected and falls back safely
                string etwPath = Path.Combine(isolatedAppDir, "PaviseEtw.dll");
                File.WriteAllBytes(etwPath, new byte[] { 0x50, 0x41, 0x56, 0x49, 0x53, 0x45 }); // Corrupt bytes
                using (FileStream held = NativeImageFile.Prepare(NativeEtwImage.Base64, NativeEtwImage.Length, NativeEtwImage.Sha256, "PaviseEtw.dll", out path, out reason))
                {
                    Check(held != null, "Prepare with tampered companion DLL must fall back to valid embedded image: " + reason);
                    Check(!string.Equals(path, etwPath, StringComparison.OrdinalIgnoreCase), "Tampered companion DLL must not be loaded: " + path);
                    Check(File.Exists(path), "Valid fallback target must exist: " + path);
                }
            }
            finally
            {
                NativeImageFile.RootForTest = null;
                try { Directory.Delete(isolatedAppDir, true); } catch { }
            }

            Console.WriteLine("PASS Native loader checks=" + checks + "; clean_loader=true; local_resolution=verified; product_started=false");
        }
    }
}
