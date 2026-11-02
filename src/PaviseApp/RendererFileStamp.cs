using System.IO;

namespace PaviseApp
{
	internal sealed class RendererFileStamp
	{
		internal long Length;

		internal long WriteTicks;

		internal static RendererFileStamp Read(string path)
		{
			try
			{
				FileInfo fileInfo = new FileInfo(path);
				object result;
				if (!fileInfo.Exists || fileInfo.Length <= 0)
				{
					result = null;
				}
				else
				{
					RendererFileStamp rendererFileStamp = new RendererFileStamp();
					rendererFileStamp.Length = fileInfo.Length;
					rendererFileStamp.WriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
					result = rendererFileStamp;
				}
				return (RendererFileStamp)result;
			}
			catch
			{
				return null;
			}
		}

		internal bool Same(RendererFileStamp other)
		{
			if (other != null && Length == other.Length)
			{
				return WriteTicks == other.WriteTicks;
			}
			return false;
		}
	}
}
