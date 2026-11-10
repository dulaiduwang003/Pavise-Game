using System;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static class AtomicFile
    {
        public static bool WriteLines(string path, string[] lines, string label)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            string pathOffset = path + ".tmp";
            try
            {
                if (File.Exists(pathOffset))
                {
                    File.Delete(pathOffset);
                }
            }
            catch
            {
            }
            bool temporaryWritten = false;
            try
            {
                using (FileStream fileStream = new FileStream(pathOffset, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(false)))
                    {
                        foreach (string currentLinesOffset in lines ?? new string[0])
                        {
                            streamWriter.WriteLine(currentLinesOffset);
                        }
                        streamWriter.Flush();
                        fileStream.Flush(true);
                    }
                }
                temporaryWritten = true;
                if (!File.Exists(path))
                {
                    File.Move(pathOffset, path);
                    return true;
                }
                int processedCount = 0;
                while (true)
                {
                    try
                    {
                        File.Replace(pathOffset, path, null);
                        return true;
                    }
                    catch (IOException)
                    {
                        if (processedCount >= 2)
                        {
                            throw;
                        }
                        Thread.Sleep(80);
                    }
                    processedCount++;
                }
            }
            catch (Exception error)
            {
                try
                {
                    if (temporaryWritten && File.Exists(pathOffset))
                    {
                        try
                        {
                            File.Copy(path, path + ".stale.bak", true);
                        }
                        catch
                        {
                        }
                        File.Copy(pathOffset, path, true);
                        try
                        {
                            File.Delete(pathOffset);
                        }
                        catch
                        {
                        }
                        return true;
                    }
                }
                catch
                {
                }
                try
                {
                    if (File.Exists(pathOffset))
                    {
                        File.Delete(pathOffset);
                    }
                }
                catch
                {
                }
                Logger.LogFailure(label + Lang.T("log.atomicfile.1", 0), error);
                return false;
            }
        }
    }
}
