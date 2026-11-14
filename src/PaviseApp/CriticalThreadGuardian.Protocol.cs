using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PaviseApp
{
    internal static partial class CriticalThreadGuardian
    {

        private static string PipeName(Guid session, string nonce)
        {
            return "PaviseHardThread-" + session.ToString("N") + "-" + nonce;
        }

        private static byte[] AwaitRead(Stream stream, int count)
        {
            byte[] rawBytes = new byte[count];
            Task<int> task;
            for (int i = 0; i < count; i += task.Result)
            {
                task = stream.ReadAsync(rawBytes, i, count - i);
                if (!task.Wait(5000))
                {
                    stream.Dispose();
                    throw new TimeoutException("guardian live read timed out");
                }
                if (task.Result <= 0)
                {
                    throw new EndOfStreamException("guardian peer disconnected");
                }
            }
            return rawBytes;
        }

        private static byte[] ReadPacket(Stream stream)
        {
            int toInt32Result = BitConverter.ToInt32(AwaitRead(stream, 4), 0);
            if (toInt32Result < 1 || toInt32Result > 65536)
            {
                throw new InvalidDataException("guardian packet bound");
            }
            return AwaitRead(stream, toInt32Result);
        }

        private static void WritePacket(Stream stream, byte[] bytes)
        {
            if (bytes == null || bytes.Length < 1 || bytes.Length > 65536)
            {
                throw new InvalidDataException("guardian packet bound");
            }
            byte[] bytes2 = BitConverter.GetBytes(bytes.Length);
            Task task = stream.WriteAsync(bytes2, 0, 4);
            if (!task.Wait(5000))
            {
                stream.Dispose();
                throw new TimeoutException("guardian write timed out");
            }
            Task task2 = stream.WriteAsync(bytes, 0, bytes.Length);
            if (!task2.Wait(5000))
            {
                stream.Dispose();
                throw new TimeoutException("guardian write timed out");
            }
            stream.Flush();
        }

        private static async Task<byte[]> ReadStop(Stream stream)
        {
            try
            {
                byte[] prefix = new byte[4];
                int read2;
                for (int position = 0; position < 4; position += read2)
                {
                    read2 = await stream.ReadAsync(prefix, position, 4 - position).ConfigureAwait(false);
                    if (read2 <= 0)
                    {
                        return null;
                    }
                }
                int length = BitConverter.ToInt32(prefix, 0);
                if (length < 1 || length > 256)
                {
                    return null;
                }
                byte[] bytes = new byte[length];
                int read;
                for (int position = 0; position < length; position += read)
                {
                    read = await stream.ReadAsync(bytes, position, length - position).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        return null;
                    }
                }
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        private static string Text(byte[] bytes)
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        private static byte[] Text(string value)
        {
            return new UTF8Encoding(false, true).GetBytes(value);
        }

        private static void WriteNew(string path, byte[] bytes)
        {
            FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            HardThreadConstructionCleanup.AcquireAndRelease(delegate
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }, delegate
            {
                HardThreadConstructionCleanup.CloseOwned(stream);
            });
        }

    }
}
