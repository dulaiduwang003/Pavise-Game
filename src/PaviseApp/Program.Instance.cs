using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
    internal static partial class Program
    {
        internal sealed class MainInstanceBootstrapLease : IDisposable
        {
            private readonly Mutex originalMutex;

            private readonly SafeWaitHandle originalHandle;

            private readonly IntPtr handleValue;

            private readonly Thread ownerThread;

            private int active = 1;

            private int claimed;

            internal MainInstanceBootstrapLease(Mutex ownedMutex, object witness)
            {
                if (!object.ReferenceEquals(witness, mainInstanceBootstrapWitness))
                {
                    throw new UnauthorizedAccessException("Only the original Main acquisition can mint this capability");
                }
                if (ownedMutex == null)
                {
                    throw new ArgumentNullException("ownedMutex");
                }
                originalMutex = ownedMutex;
                ownerThread = Thread.CurrentThread;
                originalHandle = ownedMutex.SafeWaitHandle;
                if (originalHandle.IsClosed || originalHandle.IsInvalid)
                {
                    throw new InvalidOperationException("The original single-instance handle is unavailable");
                }
                handleValue = originalHandle.DangerousGetHandle();
            }

            internal void RequireCurrent()
            {
                if (Volatile.Read(ref active) != 1 || !object.ReferenceEquals(ownerThread, Thread.CurrentThread) || originalHandle.IsClosed || originalHandle.IsInvalid || !object.ReferenceEquals(originalMutex.SafeWaitHandle, originalHandle) || originalHandle.DangerousGetHandle() != handleValue)
                {
                    throw new UnauthorizedAccessException("The original main-thread single-instance capability is no longer current");
                }
            }

            internal void ClaimBootstrap()
            {
                RequireCurrent();
                if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
                {
                    throw new InvalidOperationException("The main-instance bootstrap capability has already been claimed");
                }
                RequireCurrent();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref active, 0) != 0)
                {
                    Interlocked.CompareExchange(ref mainInstanceBootstrap, null, this);
                }
            }
        }

        private static MainInstanceBootstrapLease AcquireMainInstanceBootstrap(Mutex ownedMutex)
        {
            MainInstanceBootstrapLease mainInstanceBootstrapLease = new MainInstanceBootstrapLease(ownedMutex, mainInstanceBootstrapWitness);
            if (Interlocked.CompareExchange(ref mainInstanceBootstrap, mainInstanceBootstrapLease, null) != null)
            {
                mainInstanceBootstrapLease.Dispose();
                throw new InvalidOperationException("The original main-instance capability already exists");
            }
            return mainInstanceBootstrapLease;
        }

        private static void InvalidateMainInstanceBootstrap()
        {
            MainInstanceBootstrapLease mainInstanceBootstrapLease = Volatile.Read(ref mainInstanceBootstrap);
            if (mainInstanceBootstrapLease != null)
            {
                mainInstanceBootstrapLease.Dispose();
            }
        }

        internal static bool RecreateSingleInstanceMutex(ref Mutex mutex, string name)
        {
            if (mutex != null)
            {
                mutex.Dispose();
                mutex = null;
            }
            bool createdNew = false;
            try
            {
                mutex = new Mutex(true, name, out createdNew);
            }
            catch
            {
                createdNew = false;
            }
            return createdNew;
        }

        private static EventWaitHandle CreateSignalEvent(string name)
        {
            try
            {
                EventWaitHandleSecurity eventWaitHandleSecurity = new EventWaitHandleSecurity();
                eventWaitHandleSecurity.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User, EventWaitHandleRights.FullControl, AccessControlType.Allow));
                bool createdNew;
                return new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew, eventWaitHandleSecurity);
            }
            catch
            {
            }
            try
            {
                return new EventWaitHandle(false, EventResetMode.AutoReset, name);
            }
            catch
            {
                return null;
            }
        }

        internal static int CompareVersions(string left, string right)
        {
            Version result;
            if (!Version.TryParse(NormalizeVersion(left), out result))
            {
                result = new Version(0, 0);
            }
            Version result2;
            if (!Version.TryParse(NormalizeVersion(right), out result2))
            {
                result2 = new Version(0, 0);
            }
            return result.CompareTo(result2);
        }

        private static string NormalizeVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "0.0.0.0";
            }
            string rawTrimmed = raw.Trim();
            if (rawTrimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                rawTrimmed = rawTrimmed.Substring(1);
            }
            int splitCount = rawTrimmed.Split('.').Length;
            for (int i = splitCount; i < 4; i++)
            {
                rawTrimmed += ".0";
            }
            return rawTrimmed;
        }

        private static bool TryReplaceOlderInstance()
        {
            Process process = null;
            try
            {
                int id;
                int sessionId;
                using (Process currentProcess = Process.GetCurrentProcess())
                {
                    id = currentProcess.Id;
                    sessionId = currentProcess.SessionId;
                }
                foreach (Process processe in Process.GetProcesses())
                {
                    if (processe.Id == id)
                    {
                        processe.Dispose();
                        continue;
                    }
                    string fileVersion = null;
                    try
                    {
                        if (processe.SessionId == sessionId)
                        {
                            FileVersionInfo fileVersionInfo = processe.MainModule.FileVersionInfo;
                            if (IsOlderPaviseInstance(fileVersionInfo.ProductName, fileVersionInfo.CompanyName, fileVersionInfo.FileVersion))
                            {
                                fileVersion = fileVersionInfo.FileVersion;
                            }
                        }
                    }
                    catch
                    {
                    }
                    if (fileVersion != null && CompareVersions(App.Version, fileVersion) > 0 && process == null)
                    {
                        process = processe;
                    }
                    else
                    {
                        processe.Dispose();
                    }
                }
                if (process == null)
                {
                    return false;
                }
                try
                {
                    using (EventWaitHandle eventWaitHandle = EventWaitHandle.OpenExisting("Global\\Pavise_Exit"))
                    {
                        eventWaitHandle.Set();
                    }
                }
                catch
                {
                    return false;
                }
                if (!process.WaitForExit(20000))
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (process != null)
                {
                    process.Dispose();
                }
            }
        }

        internal static bool IsOlderPaviseInstance(string product, string company, string version)
        {
            Version result;
            if (string.Equals(product, "Pavise Auto", StringComparison.OrdinalIgnoreCase) && string.Equals(company, "bdth", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(version) && Version.TryParse(NormalizeVersion(version), out result))
            {
                return CompareVersions(App.Version, version) > 0;
            }
            return false;
        }

    }
}
