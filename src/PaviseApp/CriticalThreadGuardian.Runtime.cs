using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaviseApp
{
    internal static partial class CriticalThreadGuardian
    {

        internal static IHardThreadGuardian Arm(GameMode.CriticalThreadTargetLease original, out HardThreadFileStore originalStore)
        {
            originalStore = null;
            original.RequireActive();
            string canonicalResult = Canonical(original.CanonicalStore);
            HardThreadStorageDomain.RequireLocal(canonicalResult, (string drive) => new DriveInfo(drive).DriveType);
            string userSidResult = UserSid();
            if (!string.Equals(Path.GetFileName(canonicalResult), "CriticalThreadHardAffinityV1", StringComparison.Ordinal))
            {
                throw new InvalidDataException("original startup store required");
            }
            ProvisionRoot(canonicalResult, userSidResult);
            ParentGuardian result = new ParentGuardian(canonicalResult);
            RegisterController(result);
            try
            {
                result.Root = new RootPin(canonicalResult, userSidResult);
                try
                {
                    originalStore = new HardThreadFileStore(canonicalResult);
                    originalStore.RequireForwardAdmission(new HardThreadTarget(original.SessionId, original.ProcessId, original.ProcessCreation, original.ExecutablePath, original.AllowedMask));
                }
                catch (HardThreadOperationException)
                {
                    throw;
                }
                catch (Exception ex2)
                {
                    throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original root writer or historical ledger is unavailable: " + ex2.Message);
                }
                result.Parent = new ActorPin(Process.GetCurrentProcess().Id, null);
                ActorPin parent = result.Parent;
                if (parent.State != 0)
                {
                    throw new IOException("original parent is not alive");
                }
                string nonceResult = Nonce();
                result.Setup = new Scope
                {
                    Target = new HardThreadTarget(original.SessionId, original.ProcessId, original.ProcessCreation, original.ExecutablePath, original.AllowedMask),
                    Parent = parent.Actor,
                    Root = canonicalResult,
                    RootIdentity = result.Root.Identity,
                    Sid = userSidResult,
                    Nonce = nonceResult
                };
                string guardsPath = Path.Combine(canonicalResult, "guards", original.SessionId.ToString("N"));
                if (Directory.Exists(guardsPath))
                {
                    throw new IOException("original guardian session already registered");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(guardsPath));
                if (!CreateDirectory(guardsPath, IntPtr.Zero))
                {
                    throw new IOException("original session directory could not be created exclusively: " + Marshal.GetLastWin32Error());
                }
                result.DirectoryCreated = true;
                NoReparse(guardsPath);
                result.GuardsDirectory = new DirectoryPin(Path.GetDirectoryName(guardsPath), userSidResult);
                result.SessionDirectory = new DirectoryPin(guardsPath, userSidResult);
                string executable = Path.Combine(guardsPath, "Pavise.HardThreadGuardian.exe");
                FileStream source = null;
                FileStream copy = null;
                HardThreadConstructionCleanup.AcquireAndRelease(delegate
                {
                    source = new FileStream(parent.Actor.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    copy = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough);
                    source.CopyTo(copy);
                    copy.Flush(true);
                }, delegate
                {
                    HardThreadConstructionCleanup.CloseOwned(new HardThreadResourceClosure(copy, source));
                });
                result.ExecutableCreated = true;
                WriteNew(executable + ".config", Text("<configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.7.2\"/></startup></configuration>"));
                result.ConfigurationCreated = true;
                PipeSecurity pipeSecurity = new PipeSecurity();
                pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-5-2"), PipeAccessRights.FullControl, AccessControlType.Deny));
                foreach (string sddlForm in new string[3] { userSidResult, "S-1-5-18", "S-1-5-32-544" })
                {
                    pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sddlForm), PipeAccessRights.FullControl, AccessControlType.Allow));
                }
                result.Pipe = new NamedPipeServerStream(PipeName(original.SessionId, nonceResult), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, pipeSecurity);
                StartupInfo startup = default(StartupInfo);
                startup.Size = Marshal.SizeOf(typeof(StartupInfo));
                startup.Flags = 1u;
                startup.Show = 0;
                string directoryName = Path.GetDirectoryName(canonicalResult);
                string text = "\"" + executable + "\" --hard-thread-guardian " + original.SessionId.ToString("N") + " " + nonceResult + " --autostart-data \"" + directoryName + "\"";
                ProcessInfo child;
                if (!CreateProcess(executable, new StringBuilder(text), IntPtr.Zero, IntPtr.Zero, false, 150994944u, IntPtr.Zero, guardsPath, ref startup, out child))
                {
                    int lastWin32Error = Marshal.GetLastWin32Error();
                    bool inJob = false;
                    bool membershipKnown = lastWin32Error == 5 && IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob);
                    throw HardThreadGuardianLaunchPolicy.Failed(lastWin32Error, membershipKnown, inJob);
                }
                result.SpawnSucceeded = true;
                result.SpawnThread = child.Thread;
                result.UnverifiedSpawn = child.Process;
                HardThreadConstructionCleanup.AcquireAndRelease(delegate
                {
                    result.Child = new ActorPin((int)child.Pid, null, child.Process);
                }, delegate
                {
                    result.ReleaseSpawnHandles(result.Child != null);
                });
                if (result.Child.Actor.ImageHash != parent.Actor.ImageHash || !string.Equals(result.Child.Actor.Path, executable, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("spawned guardian bytes differ");
                }
                bool inJob2;
                bool membershipKnown2 = IsProcessInJob(result.Child.Handle, IntPtr.Zero, out inJob2);
                HardThreadGuardianLaunchPolicy.RequireIndependent(membershipKnown2, inJob2);
                result.Scope = new Scope
                {
                    Target = new HardThreadTarget(original.SessionId, original.ProcessId, original.ProcessCreation, original.ExecutablePath, original.AllowedMask),
                    Parent = parent.Actor,
                    Child = result.Child.Actor,
                    Root = canonicalResult,
                    RootIdentity = result.Root.Identity,
                    Sid = userSidResult,
                    Nonce = nonceResult
                };
                byte[] bytes = result.Scope.Encode();
                originalStore.BindOriginalScope(result.Scope.Target, nonceResult, result.Scope.Digest);
                result.Unregister = original.UnregisterGuardianActor;
                if (!original.RegisterGuardianActor(result.Child.Actor))
                {
                    throw new IOException("original guardian background restoration is unconfirmed");
                }
                string path = Path.Combine(guardsPath, "scope.xml");
                WriteNew(path, bytes);
                result.RegistrationCreated = true;
                result.Source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                IAsyncResult asyncResult = result.Pipe.BeginWaitForConnection(null, null);
                if (!asyncResult.AsyncWaitHandle.WaitOne(5000))
                {
                    throw new TimeoutException("original guardian did not connect");
                }
                result.Pipe.EndWaitForConnection(asyncResult);
                asyncResult.AsyncWaitHandle.Close();
                uint pid;
                if (!GetNamedPipeClientProcessId(result.Pipe.SafePipeHandle.DangerousGetHandle(), out pid) || pid != result.Child.Actor.Pid || result.Child.State != 0)
                {
                    throw new IOException("pipe client is not the original spawned guardian");
                }
                WritePacket(result.Pipe, bytes);
                if (Text(ReadPacket(result.Pipe)) != "ready:" + result.Scope.Digest)
                {
                    throw new IOException("guardian did not retain the original scope");
                }
                original.RequireActive();
                WritePacket(result.Pipe, Text("armed:" + result.Scope.Digest));
                result.Watch();
                return result;
            }
            catch (Exception ex3)
            {
                result.RememberClosureFailure(ex3);
                try
                {
                    if (originalStore != null && result.OriginalScope != null)
                    {
                        try
                        {
                            HardThreadJournal hardThreadJournal = originalStore.Load(result.OriginalScope.Target);
                            result.NoteSettled(originalStore.CaptureSettled(result.OriginalScope.Target, hardThreadJournal.Revision));
                        }
                        catch
                        {
                        }
                    }
                    if (originalStore != null)
                    {
                        try
                        {
                            HardThreadConstructionCleanup.CloseOwned(originalStore);
                            originalStore = null;
                        }
                        catch (Exception ex4)
                        {
                            result.RememberClosureFailure(ex4);
                            throw new AggregateException("Original preparation and store cleanup both failed", ex3, ex4);
                        }
                    }
                }
                finally
                {
                    result.Dispose();
                }
                throw;
            }
        }

        private static StopProof OriginalStopProof(NativeRegistration original)
        {
            lock (proofGate)
            {
                original.RequireStopped();
                issuingProof = original;
                try
                {
                    return StopProof.Issue(original);
                }
                finally
                {
                    issuingProof = null;
                }
            }
        }

        private static bool RunNative(string[] args, string originalStartupData, out int exitCode)
        {
            exitCode = 13;
            NativeRegistration nativeRegistration = null;
            try
            {
                Guid result;
                if (args == null || args.Length < 3 || args[0] != "--hard-thread-guardian" || !Guid.TryParseExact(args[1], "N", out result) || result == Guid.Empty || !IsHash(args[2]) || string.IsNullOrEmpty(originalStartupData) || !Path.IsPathRooted(originalStartupData))
                {
                    return true;
                }
                string canonicalResult = Canonical(Path.Combine(originalStartupData, "CriticalThreadHardAffinityV1"));
                RequireAcl(canonicalResult, UserSid());
                NamedPipeClientStream namedPipeClientStream = new NamedPipeClientStream(".", PipeName(result, args[2]), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                NativeRegistration currentNativeRegistration = new NativeRegistration();
                currentNativeRegistration.Pipe = namedPipeClientStream;
                nativeRegistration = currentNativeRegistration;
                namedPipeClientStream.Connect(5000);
                uint pid;
                if (!GetNamedPipeServerProcessId(namedPipeClientStream.SafePipeHandle.DangerousGetHandle(), out pid))
                {
                    throw new IOException("original server PID unavailable");
                }
                byte[] bytes = ReadPacket(namedPipeClientStream);
                nativeRegistration.Scope = Scope.Parse(bytes, canonicalResult, result, args[2], UserSid(), new NativeOrigin(), true);
                if (pid != nativeRegistration.Scope.Parent.Pid || nativeRegistration.Scope.Child.Pid != Process.GetCurrentProcess().Id)
                {
                    throw new IOException("actual guardian pipe peers differ");
                }
                nativeRegistration.Root = new RootPin(canonicalResult, nativeRegistration.Scope.Sid);
                if (nativeRegistration.Root.Identity != nativeRegistration.Scope.RootIdentity)
                {
                    throw new IOException("original source root was replaced");
                }
                nativeRegistration.Parent = new ActorPin((int)pid, nativeRegistration.Scope.Parent);
                using (ActorPin actorPin = new ActorPin(Process.GetCurrentProcess().Id, nativeRegistration.Scope.Child))
                {
                    if (nativeRegistration.Parent.State != 0 || actorPin.State != 0)
                    {
                        throw new IOException("live original peers required");
                    }
                    nativeRegistration.Target = new TargetPin(nativeRegistration.Scope.Target);
                    string path = Path.Combine(canonicalResult, "guards", result.ToString("N"), "scope.xml");
                    nativeRegistration.Source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (Hash(nativeRegistration.Source) != nativeRegistration.Scope.Digest)
                    {
                        throw new IOException("original persisted scope differs from live scope");
                    }
                    nativeRegistration.Handshake = new HardThreadGuardianHandshake(nativeRegistration.Parent.Actor, actorPin.Actor, args[2], nativeRegistration.Scope.Digest);
                    nativeRegistration.Handshake.Accept((int)pid, actorPin.Actor.Pid, nativeRegistration.Parent.Actor, actorPin.Actor, args[2], nativeRegistration.Scope.Digest);
                    WritePacket(namedPipeClientStream, Text("ready:" + nativeRegistration.Scope.Digest));
                    if (Text(ReadPacket(namedPipeClientStream)) != "armed:" + nativeRegistration.Scope.Digest)
                    {
                        throw new IOException("original parent did not acknowledge registration");
                    }
                }
                Task<byte[]> task = ReadStop(namedPipeClientStream);
                bool stopRequested = false;
                long nextTerminalReadTicks = 0L;
                int terminalReadFailures = 0;
                while (true)
                {
                    switch (nativeRegistration.Parent.State)
                    {
                    case HardThreadRead.Unknown:
                        exitCode = 14;
                        return true;
                    default:
                    {
                        if (task != null && task.IsCompleted)
                        {
                            byte[] result2 = task.Result;
                            task = null;
                            if (result2 == null || Text(result2) == "stop:" + nativeRegistration.Scope.Digest)
                            {
                                stopRequested = true;
                            }
                        }
                        if (!stopRequested || terminalReadFailures >= 3 || DateTime.UtcNow.Ticks < nextTerminalReadTicks)
                        {
                            break;
                        }
                        nextTerminalReadTicks = DateTime.UtcNow.AddSeconds(1.0).Ticks;
                        bool allReceiptsTerminal = false;
                        try
                        {
                            using (HardThreadFileStore hardThreadFileStore = new HardThreadFileStore(canonicalResult))
                            {
                                allReceiptsTerminal = true;
                                foreach (HardThreadReceipt row in hardThreadFileStore.Load(nativeRegistration.Scope.Target).Rows)
                                {
                                    if (!row.Terminal)
                                    {
                                        allReceiptsTerminal = false;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            terminalReadFailures++;
                            Logger.Warn("关键线程守护终结日志读取：尝试=" + terminalReadFailures + "/3；保留到父进程退出");
                        }
                        if (allReceiptsTerminal)
                        {
                            exitCode = 0;
                            return true;
                        }
                        break;
                    }
                    case HardThreadRead.Gone:
                    {
                        nativeRegistration.Handshake.ObserveParent(HardThreadRead.Gone);
                        StopProof proof = OriginalStopProof(nativeRegistration);
                        try
                        {
                            nativeRegistration = null;
                            Func<bool> restore = delegate
                            {
                                using (CriticalThreadHardAffinitySession criticalThreadHardAffinitySession = CriticalThreadHardAffinitySession.OpenRecovery(proof))
                                {
                                    return criticalThreadHardAffinitySession.Restore();
                                }
                            };
                            if (!RetainRecoveryAcrossTargetExit(restore, Thread.Sleep, proof.TargetExited, proof.WaitForTargetExit, 250))
                            {
                                Logger.Warn("关键线程守护恢复预算已耗尽，保留权限并停止主动重试");
                                Thread.Sleep(Timeout.Infinite);
                            }
                            exitCode = 0;
                        }
                        finally
                        {
                            if (proof != null)
                            {
                                ((IDisposable)proof).Dispose();
                            }
                        }
                        return true;
                    }
                    }
                    WaitForSingleObject(nativeRegistration.Parent.Handle, 250u);
                }
            }
            catch (Exception error)
            {
                if (nativeRegistration != null)
                {
                    nativeRegistration.RememberClosureFailure(error);
                }
                exitCode = 14;
                return true;
            }
            finally
            {
                HardThreadGuardianExit.CloseForExit(nativeRegistration, ref exitCode);
            }
        }

    }
}
