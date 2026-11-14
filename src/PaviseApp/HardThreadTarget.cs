using System;
using System.IO;

namespace PaviseApp
{
    internal sealed class HardThreadTarget
    {
        internal readonly Guid SessionId;

        internal readonly int ProcessId;

        internal readonly long ProcessCreation;

        internal readonly string ExecutablePath;

        // 零表示未授予任何亲和性写入权限，只承载身份绑定的优先级恢复；非零沿用既有核心范围。
        internal readonly ulong AllowedMask;

        internal HardThreadTarget(Guid session, int pid, long creation, string path, ulong allowed)
        {
            if (session == Guid.Empty || pid <= 0 || creation <= 0 || string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            {
                throw new ArgumentException("original thread target");
            }
            SessionId = session;
            ProcessId = pid;
            ProcessCreation = creation;
            ExecutablePath = Path.GetFullPath(path);
            AllowedMask = allowed;
        }

        internal bool Same(HardThreadTarget other)
        {
            if (other != null && SessionId == other.SessionId && ProcessId == other.ProcessId && ProcessCreation == other.ProcessCreation && AllowedMask == other.AllowedMask)
            {
                return string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
    }
}
