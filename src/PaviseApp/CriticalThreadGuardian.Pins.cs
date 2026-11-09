using System;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal static partial class CriticalThreadGuardian
    {

        private sealed class DirectoryPin : IDisposable
        {
            private IntPtr handle;

            private bool closeFault;

            internal readonly string Identity;

            internal bool Valid
            {
                get
                {
                    FileIdentity info;
                    if (handle != IntPtr.Zero && GetFileInformationByHandle(handle, out info) && FileKey(info) == Identity)
                    {
                        return (info.Attributes & 0x400) == 0;
                    }
                    return false;
                }
            }

            internal DirectoryPin(string path, string sid)
            {
                RequireAcl(path, sid, false);
                handle = CreateFile(path, 128u, 3u, IntPtr.Zero, 3u, 35651584u, IntPtr.Zero);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    handle = IntPtr.Zero;
                    throw new IOException("original session directory pin unavailable");
                }
                try
                {
                    FileIdentity info;
                    if (!GetFileInformationByHandle(handle, out info) || (info.Attributes & 0x400u) != 0 || (info.Attributes & 0x10) == 0)
                    {
                        throw new IOException("original directory identity unavailable");
                    }
                    Identity = FileKey(info);
                }
                catch (Exception cause)
                {
                    HardThreadConstructionCleanup.CloseFailed(this, cause);
                    throw;
                }
            }

            public void Dispose()
            {
                HardThreadRawHandleClosure.Close(ref handle, ref closeFault, CloseHandle);
            }
        }

        private sealed class RootPin : IDisposable
        {
            private IntPtr handle;

            private bool closeFault;

            internal readonly string Identity;

            internal bool Valid
            {
                get
                {
                    FileIdentity info;
                    if (handle != IntPtr.Zero && GetFileInformationByHandle(handle, out info) && (info.Attributes & 0x400) == 0)
                    {
                        return info.Volume.ToString("x8") + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8") == Identity;
                    }
                    return false;
                }
            }

            internal RootPin(string root, string sid)
            {
                RequireAcl(root, sid);
                handle = CreateFile(root, 128u, 3u, IntPtr.Zero, 3u, 35651584u, IntPtr.Zero);
                if (handle == new IntPtr(-1))
                {
                    handle = IntPtr.Zero;
                    throw new IOException("original root pin unavailable");
                }
                try
                {
                    FileIdentity info;
                    if (!GetFileInformationByHandle(handle, out info) || (info.Attributes & 0x400u) != 0 || (info.Attributes & 0x10) == 0)
                    {
                        throw new IOException("original root pin invalid");
                    }
                    Identity = info.Volume.ToString("x8") + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8");
                }
                catch (Exception cause)
                {
                    HardThreadConstructionCleanup.CloseFailed(this, cause);
                    throw;
                }
            }

            public void Dispose()
            {
                HardThreadRawHandleClosure.Close(ref handle, ref closeFault, CloseHandle);
            }
        }

        private sealed class ActorPin : IDisposable
        {
            internal IntPtr Handle;

            internal readonly HardThreadGuardianActor Actor;

            private FileStream image;

            private bool closeFault;

            internal HardThreadRead State
            {
                get
                {
                    switch ((uint)((Handle == IntPtr.Zero) ? (-1) : ((int)WaitForSingleObject(Handle, 0u))))
                    {
                    default:
                        return HardThreadRead.Unknown;
                    case 258u:
                        return HardThreadRead.Known;
                    case 0u:
                        return HardThreadRead.Gone;
                    }
                }
            }

            internal bool ClosureCertain
            {
                get
                {
                    return !closeFault;
                }
            }

            internal ActorPin(int pid, HardThreadGuardianActor expected, IntPtr originalSpawn = default(IntPtr))
            {
                if (originalSpawn == IntPtr.Zero)
                {
                    Handle = OpenProcess(1053696u, false, pid);
                }
                else if (!DuplicateHandle(GetCurrentProcess(), originalSpawn, GetCurrentProcess(), out Handle, 1053696u, false, 0u))
                {
                    throw new IOException("original spawn handle could not be retained");
                }
                if (Handle == IntPtr.Zero)
                {
                    throw new IOException("original actor query denied");
                }
                try
                {
                    StringBuilder stringBuilder = new StringBuilder(32768);
                    int capacity = stringBuilder.Capacity;
                    long born;
                    long exit;
                    long kernel;
                    long user;
                    if (GetProcessId(Handle) != (uint)pid || !GetProcessTimes(Handle, out born, out exit, out kernel, out user) || !QueryFullProcessImageName(Handle, 0u, stringBuilder, ref capacity))
                    {
                        throw new IOException("actor identity unknown");
                    }
                    image = new FileStream(stringBuilder.ToString(), FileMode.Open, FileAccess.Read, FileShare.Read);
                    Actor = new HardThreadGuardianActor(pid, born, stringBuilder.ToString(), Hash(image));
                    if (expected != null && !expected.Same(Actor))
                    {
                        throw new IOException("actor creation or image differs");
                    }
                }
                catch (Exception cause)
                {
                    HardThreadConstructionCleanup.CloseFailed(this, cause);
                    throw;
                }
            }

            internal IntPtr Duplicate()
            {
                IntPtr duplicate;
                if (!DuplicateHandle(GetCurrentProcess(), Handle, GetCurrentProcess(), out duplicate, 0u, false, 2u))
                {
                    throw new IOException("original target handle could not be retained");
                }
                return duplicate;
            }

            internal void CloseImage()
            {
                if (image != null)
                {
                    image.Dispose();
                    image = null;
                }
            }

            internal bool VerifyImage()
            {
                if (image != null)
                {
                    return Hash(image) == Actor.ImageHash;
                }
                return false;
            }

            public void Dispose()
            {
                HardThreadPinClosure.Close(CloseImage, ref Handle, ref closeFault, CloseHandle);
            }
        }

        private sealed class TargetPin : IDisposable
        {
            internal IntPtr Handle;

            private bool closeFault;

            internal TargetPin(HardThreadTarget expected)
            {
                Handle = OpenProcess(1053696u, false, expected.ProcessId);
                if (Handle == IntPtr.Zero)
                {
                    throw new IOException("original target handle unavailable");
                }
                try
                {
                    StringBuilder stringBuilder = new StringBuilder(32768);
                    int capacity = stringBuilder.Capacity;
                    long born;
                    long exit;
                    long kernel;
                    long user;
                    if (!GetProcessTimes(Handle, out born, out exit, out kernel, out user) || born != expected.ProcessCreation || !QueryFullProcessImageName(Handle, 0u, stringBuilder, ref capacity) || !string.Equals(Canonical(stringBuilder.ToString()), expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("original target creation differs");
                    }
                }
                catch (Exception cause)
                {
                    HardThreadConstructionCleanup.CloseFailed(this, cause);
                    throw;
                }
            }

            internal IntPtr Duplicate()
            {
                IntPtr duplicate;
                if (!DuplicateHandle(GetCurrentProcess(), Handle, GetCurrentProcess(), out duplicate, 0u, false, 2u))
                {
                    throw new IOException("retained target could not be duplicated");
                }
                return duplicate;
            }

            public void Dispose()
            {
                HardThreadRawHandleClosure.Close(ref Handle, ref closeFault, CloseHandle);
            }
        }

    }
}
