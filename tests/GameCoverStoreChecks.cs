#if PAVISE_SELFTEST && PAVISE_COVER_STORE_CHECKS
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace PaviseApp
{
    internal static class GameCoverStoreChecks
    {
        private static int assertions;
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); assertions++; }
        private static bool Near(Color left, Color right)
        { return Math.Abs(left.R - right.R) < 5 && Math.Abs(left.G - right.G) < 5 && Math.Abs(left.B - right.B) < 5; }
        private static void ImageFile(string path, ImageFormat format, Color color)
        {
            using (var image = new Bitmap(1280, 720))
            using (var graphics = Graphics.FromImage(image))
            { graphics.Clear(color); image.Save(path, format); }
        }
        private static void ExclusiveOpen(string path)
        { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { assertions++; } }

        private static void Run(string root)
        {
            Paths.Data = Path.Combine(root, "data");
            Directory.CreateDirectory(Paths.Data);
            string first = Path.Combine(root, "original.png"), second = Path.Combine(root, "replacement.jpg"), bmp = Path.Combine(root, "source.bmp");
            ImageFile(first, ImageFormat.Png, Color.Crimson);
            ImageFile(second, ImageFormat.Jpeg, Color.CornflowerBlue);
            ImageFile(bmp, ImageFormat.Bmp, Color.SeaGreen);
            byte[] originalBytes = File.ReadAllBytes(first);
            var profile = new GameProfile { Id = "../escape\\..\\:unsafe|?id", Name = "Fixture", ExecutablePath = "original.exe" };
            string error;
            Check(!GameCoverStore.HasCustom(profile) && GameCoverStore.Load(profile) == null, "New game should use default artwork");
            Check(GameCoverStore.TrySet(profile, first, out error) && error == null, "PNG import failed: " + error);
            Bitmap loaded = GameCoverStore.Load(profile);
            Check(GameCoverStore.HasCustom(profile) && loaded != null, "Imported cover missing");
            Check(loaded.Width == 960 && loaded.Height == 540 && Near(loaded.GetPixel(480, 270), Color.Crimson), "Cover was not normalized");
            Check(object.ReferenceEquals(loaded, GameCoverStore.Load(profile)), "Cover bitmap was not cached");
            Check(profile.ExecutablePath == "original.exe" && profile.Overrides.Count == 0, "Cover import changed executable icon source or profile settings");
            Check(Convert.ToBase64String(originalBytes) == Convert.ToBase64String(File.ReadAllBytes(first)), "Original source image was modified");
            ExclusiveOpen(first);
            string[] files = Directory.GetFiles(Path.Combine(Paths.Data, "GameCovers"));
            Check(files.Length == 1 && Path.GetFileName(files[0]).Length == 68 && Path.GetExtension(files[0]) == ".png", "Unsafe ID was not hashed to a contained file");
            string stored = files[0];
            ExclusiveOpen(stored);
            GameCoverStore.DropCache();
            Bitmap persisted = GameCoverStore.Load(profile);
            Check(persisted != null && Near(persisted.GetPixel(480, 270), Color.Crimson), "Cover did not persist after cache reset");
            ExclusiveOpen(stored);
            Check(GameCoverStore.TrySet(profile, second, out error), "JPEG replacement failed: " + error);
            Bitmap replaced = GameCoverStore.Load(profile);
            Check(replaced != null && Near(replaced.GetPixel(480, 270), Color.CornflowerBlue), "Replacement image was not visible");
            ExclusiveOpen(second);
            byte[] beforeFailure = File.ReadAllBytes(stored);
            string broken = Path.Combine(root, "broken.png");
            File.WriteAllText(broken, "This is not an image.");
            Check(!GameCoverStore.TrySet(profile, broken, out error) && !string.IsNullOrEmpty(error), "Corrupt image was accepted");
            Check(object.ReferenceEquals(replaced, GameCoverStore.Load(profile)), "Rejected import destroyed cached prior cover");
            Check(Convert.ToBase64String(beforeFailure) == Convert.ToBase64String(File.ReadAllBytes(stored)), "Rejected image changed saved cover");
            using (var hold = new FileStream(stored, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(!GameCoverStore.TrySet(profile, first, out error), "Locked destination should reject atomic replacement");
                Check(object.ReferenceEquals(replaced, GameCoverStore.Load(profile)), "Write failure destroyed prior cached cover");
                Check(!GameCoverStore.TryReset(profile, out error) && GameCoverStore.HasCustom(profile), "Failed reset should preserve prior cover");
            }
            Check(Convert.ToBase64String(beforeFailure) == Convert.ToBase64String(File.ReadAllBytes(stored)), "Failed atomic replace changed prior saved cover");
            Check(Directory.GetFiles(Path.GetDirectoryName(stored)).Length == 1, "Temporary files remained after failed write");
            string tooLarge = Path.Combine(root, "too-large.png");
            using (var output = new FileStream(tooLarge, FileMode.Create)) output.SetLength(GameCoverStore.MaximumSourceBytes + 1);
            Check(!GameCoverStore.TrySet(profile, tooLarge, out error), "Oversize file was accepted");
            byte[] giant = (byte[])originalBytes.Clone();
            giant[16] = 0; giant[17] = 0; giant[18] = 32; giant[19] = 1;
            File.WriteAllBytes(tooLarge, giant);
            Check(!GameCoverStore.TrySet(profile, tooLarge, out error), "Oversize PNG header was accepted");
            // A valid size in a truncated image must still fail validation and retain the old cover.
            var truncated = new byte[40]; Array.Copy(originalBytes, truncated, truncated.Length);
            File.WriteAllBytes(broken, truncated);
            Check(!GameCoverStore.TrySet(profile, broken, out error), "Truncated PNG was accepted");
            Check(GameCoverStore.TrySet(profile, bmp, out error) && Near(GameCoverStore.Load(profile).GetPixel(480, 270), Color.SeaGreen), "BMP import failed");
            GameCoverStore.Reset(profile);
            Check(!GameCoverStore.HasCustom(profile) && GameCoverStore.Load(profile) == null && !File.Exists(stored), "Reset did not restore default cover");
            GameCoverStore.Reset(profile);
            Check(!GameCoverStore.TrySet(null, first, out error), "Missing profile should reject import");
            Check(GameCoverStore.Load(null) == null && !GameCoverStore.HasCustom(null), "Missing profile should fall back cleanly");
            Check(GameCoverStore.TrySet(profile, first, out error), "Reimport after reset failed");
            GameCoverStore.DropCache(profile);
            File.WriteAllText(stored, "corrupted saved cover");
            Check(GameCoverStore.Load(profile) == null && GameCoverStore.Load(profile) == null, "Corrupted persisted cover did not fall back");
            ExclusiveOpen(stored);
            Check(GameCoverStore.TryReset(profile, out error), "Reset of corrupted cover failed");
        }

        [STAThread]
        private static int Main()
        {
            string temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Pavise-cover-check-" + Guid.NewGuid().ToString("N")));
            try
            {
                Directory.CreateDirectory(temporaryRoot);
                Run(temporaryRoot);
                Console.WriteLine("PASS GameCoverStore assertions=" + assertions + " import=PNG,JPEG,BMP original_preserved=true atomic_failure_preserves_old=true source_locks=false stored_locks=false application_started=false windows_shown=false");
                return 0;
            }
            catch (Exception failure) { Console.Error.WriteLine(failure); return 1; }
            finally
            {
                GameCoverStore.DropCache();
                string tempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (temporaryRoot.StartsWith(tempParent, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(temporaryRoot).StartsWith("Pavise-cover-check-", StringComparison.Ordinal))
                    Directory.Delete(temporaryRoot, true);
            }
        }
    }

    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Cover store checks cannot start application runtime"); }
    }
}
#endif
