using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Management;
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
        private sealed class NativeGuardianLaunchAttempt : IHardThreadGuardianLaunchAttempt
        {
            private readonly ParentGuardian owner;
            private readonly string executable, command, directory;
            internal long BrokerStartedFileTime;
            internal bool BrokerUsed;
            private int brokerReceiptPid;
            internal int BrokerReceiptPid { get { return brokerReceiptPid; } }

            internal NativeGuardianLaunchAttempt(ParentGuardian owner, string executable, string command, string directory)
            { this.owner = owner; this.executable = executable; this.command = command; this.directory = directory; }

            public bool StartNative(out int error)
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Flags = 1, Show = 0 };
                ProcessInfo child;
                // 先挂起以核验归属；此时尚无 Scope、恢复授权或游戏线程写入。
                bool created = CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false,
                    0x09000004u, IntPtr.Zero, directory, ref startup, out child);
                error = created ? 0 : Marshal.GetLastWin32Error();
                if (!created) return false;
                owner.SpawnSucceeded = true;
                owner.SpawnThread = child.Thread;
                owner.UnverifiedSpawn = child.Process;
                return true;
            }

            public bool CallerInJob()
            {
                bool inJob;
                if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob))
                    throw new IOException("独立守护调用方作业归属不可读，win32=" + Marshal.GetLastWin32Error());
                return inJob;
            }

            public bool ChildInJob()
            {
                bool inJob;
                if (!IsProcessInJob(owner.UnverifiedSpawn, IntPtr.Zero, out inJob))
                    throw new IOException("独立守护挂起子进程作业归属不可读，win32=" + Marshal.GetLastWin32Error());
                return inJob;
            }

            public bool CallerJobTrusted()
            {
                string reason;
                HardThreadGuardianJobFacts facts = HardThreadGuardianJobProbe.ReadOwn();
                return HardThreadGuardianLaunchPolicy.SystemSessionJob(facts, out reason)
                    && HardThreadGuardianJobAnchors.VerifyReported(facts, HardThreadGuardianJobAnchors.CurrentCreation()) == null;
            }

            public void StopNativeAndConfirm()
            {
                // 只能终止本次 CreateProcess 返回的自有挂起子进程，不按 PID 重开其他进程。
                if (owner.UnverifiedSpawn == IntPtr.Zero) throw new IOException("独立守护挂起子进程句柄已丢失");
                if (WaitForSingleObject(owner.UnverifiedSpawn, 0) != 0)
                {
                    if (!TerminateProcess(owner.UnverifiedSpawn, 13))
                        throw new IOException("独立守护挂起子进程终止未完成，win32=" + Marshal.GetLastWin32Error());
                    if (WaitForSingleObject(owner.UnverifiedSpawn, 5000) != 0)
                        throw new IOException("独立守护挂起子进程退出未核验");
                }
            }

            public void CloseNative() { owner.ReleaseSpawnHandles(true); }

            public void ResumeNative()
            {
                if (ResumeThread(owner.SpawnThread) != 1)
                    throw new IOException("独立守护主线程恢复未完成，win32=" + Marshal.GetLastWin32Error());
            }

            public void StartBroker()
            {
                BrokerUsed = true;
                BrokerStartedFileTime = DateTime.UtcNow.ToFileTimeUtc();
                Logger.Info("独立恢复守护 原生路径未取得独立性，改用一次 WMI 显式脱离作业创建；最终仍核验作业归属与握手");
                owner.BeginBrokerSpawn();
                Task pending;
                try
                {
                    pending = Task.Factory.StartNew(CreateBrokerProcess, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch
                {
                    owner.CompleteBrokerSpawn(IntPtr.Zero, true, "代理创建工作线程未启动");
                    throw;
                }
                if (!pending.Wait(8000))
                    throw new IOException("独立守护 WMI 创建等待超时，是否已创建仍未知；保留同一所有者，结果结清前不再派生");
                if (!string.IsNullOrEmpty(owner.BrokerSpawnFailure) || owner.UnverifiedSpawn == IntPtr.Zero)
                    throw new IOException("独立守护 WMI 创建未完成：" + (owner.BrokerSpawnFailure ?? "创建结果未知"));
            }

            private void CreateBrokerProcess()
            {
                bool enteredCreate = false, absenceConfirmed = false;
                IntPtr process = IntPtr.Zero;
                string failure = null;
                try
                {
                    using (var processClass = new ManagementClass(@"root\cimv2", "Win32_Process", null))
                    using (var startupClass = new ManagementClass(@"root\cimv2", "Win32_ProcessStartup", null))
                    using (ManagementObject startup = startupClass.CreateInstance())
                    using (ManagementBaseObject input = processClass.GetMethodParameters("Create"))
                    {
                        startup["CreateFlags"] = (uint)0x09000000;
                        startup["ShowWindow"] = (ushort)0;
                        input["CommandLine"] = command;
                        input["CurrentDirectory"] = directory;
                        input["ProcessStartupInformation"] = startup;
                        enteredCreate = true;
                        using (ManagementBaseObject output = processClass.InvokeMethod("Create", input,
                            new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(8) }))
                        {
                            uint code = Convert.ToUInt32(output["ReturnValue"]);
                            if (code != 0) { absenceConfirmed = true; failure = "WMI ReturnValue=" + code; }
                            else
                            {
                                int pid = Convert.ToInt32(output["ProcessId"]);
                                if (pid <= 0) failure = "WMI 返回成功但进程编号未知";
                                else
                                {
                                    // PID 只用于取得一次固定句柄；后续沿原 ActorPin 再核验创建时间、路径、镜像和管道身份。
                                    process = OpenProcess(1053696u, false, pid);
                                    if (process == IntPtr.Zero) failure = "WMI 已返回进程编号但句柄未取得，win32=" + Marshal.GetLastWin32Error();
                                    else brokerReceiptPid = pid;
                                }
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    failure = error.GetType().Name + " " + error.Message;
                    // 进入 Create 后的异常不能证明没有创建进程，尤其不能将超时重新包装成可立即重试。
                    absenceConfirmed = !enteredCreate;
                }
                owner.CompleteBrokerSpawn(process, absenceConfirmed, failure);
            }
        }

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
                string[] textEntries = new string[3] { userSidResult, "S-1-5-18", "S-1-5-32-544" };
                foreach (string sddlForm in textEntries)
                {
                    pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sddlForm), PipeAccessRights.FullControl, AccessControlType.Allow));
                }
                result.Pipe = new NamedPipeServerStream(PipeName(original.SessionId, nonceResult), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, pipeSecurity);
                string directoryName = Path.GetDirectoryName(canonicalResult);
                string value = "\"" + executable + "\" --hard-thread-guardian " + original.SessionId.ToString("N") + " " + nonceResult + " --autostart-data \"" + directoryName + "\"";
                var launch = new NativeGuardianLaunchAttempt(result, executable, value, guardsPath);
                HardThreadGuardianLaunchRoute.Start(launch);
                HardThreadConstructionCleanup.AcquireAndRelease(delegate
                {
                    result.Child = new ActorPin((int)GetProcessId(result.UnverifiedSpawn), null, result.UnverifiedSpawn);
                }, delegate
                {
                    result.ReleaseSpawnHandles(result.Child != null);
                });
                if (result.Child.Actor.ImageHash != parent.Actor.ImageHash || !string.Equals(result.Child.Actor.Path, executable, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("spawned guardian bytes differ");
                }
                if (launch.BrokerUsed && result.Child.Actor.Creation < launch.BrokerStartedFileTime)
                    throw new IOException("独立守护代理返回的进程创建时间早于本次请求");
                bool inJob2;
                bool membershipKnown2 = IsProcessInJob(result.Child.Handle, IntPtr.Zero, out inJob2);
                // 作业限制只能由守护自身读取，须随握手应答核验。
                HardThreadGuardianLaunchPolicy.RequireMembershipKnown(membershipKnown2);
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
                // WMI 子进程可能看不到交互桌面；提示只给候选身份，双方仍分别核验。
                WritePacket(result.Pipe, Text(HardThreadGuardianJobAnchors.CaptureDesktop(result.Scope.Parent.Creation).EncodeHint()));
                Func<string> verifyBroker = null;
                if (launch.BrokerUsed && launch.BrokerReceiptPid > 0)
                    verifyBroker = delegate
                    {
                        var proof = new HardThreadGuardianBrokerPin(parent.Handle, result.Child.Handle, parent.Actor,
                            result.Child.Actor, launch.BrokerReceiptPid, launch.BrokerStartedFileTime);
                        string failure = result.VerifyBrokerProof(proof);
                        return failure == null ? null : failure + "；" + proof.Describe();
                    };
                HardThreadGuardianJobFacts jobFacts = HardThreadGuardianLaunchPolicy.RequireIndependent(inJob2, Text(ReadPacket(result.Pipe)), result.Scope.Digest,
                    delegate(HardThreadGuardianJobFacts facts) { return HardThreadGuardianJobAnchors.VerifyReported(facts, result.Scope.Parent.Creation); }, verifyBroker);
                HardThreadGuardianBrokerPin usedBrokerProof = result.BrokerProof;
                original.RequireActive();
                string brokerFailure = result.RecheckBrokerProof();
                if (brokerFailure != null) throw new IOException("独立守护 armed 前 WMI 证明复核失败：" + brokerFailure + "；" + usedBrokerProof.Describe());
                WritePacket(result.Pipe, Text("armed:" + result.Scope.Digest));
                result.Watch();
                if (usedBrokerProof != null)
                    Logger.Info("独立恢复守护已通过本地 WMI 跨会话创建证明，排除调用方及提供程序的隐式作业继承；" + usedBrokerProof.Describe()
                        + "；" + jobFacts.Describe());
                else if (jobFacts != null)
                    Logger.Info("独立恢复守护已核验与可信会话进程共享作业，限制满足独立恢复条件；" + jobFacts.Describe());
                else if (launch.BrokerUsed)
                    Logger.Info("独立恢复守护 WMI 备用创建已通过身份、管道握手与无作业归属核验");
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
                NativeRegistration nativeRegistration2 = new NativeRegistration();
                nativeRegistration2.Pipe = namedPipeClientStream;
                nativeRegistration = nativeRegistration2;
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
                    // 与历史 Scope 分开传递，不改变旧恢复回执；候选身份不能替代本进程的作业成员读取。
                    HardThreadGuardianJobAnchor hint = HardThreadGuardianJobAnchor.ParseHint(Text(ReadPacket(namedPipeClientStream)));
                    WritePacket(namedPipeClientStream, Text(HardThreadGuardianLaunchPolicy.ReadyReply(nativeRegistration.Scope.Digest,
                        HardThreadGuardianJobProbe.ReadOwn(nativeRegistration.Scope.Parent.Creation, hint))));
                    if (Text(ReadPacket(namedPipeClientStream)) != "armed:" + nativeRegistration.Scope.Digest)
                    {
                        throw new IOException("original parent did not acknowledge registration");
                    }
                }
                Task<byte[]> task = ReadStop(namedPipeClientStream);
                bool stopRequested = false;
                long numericValue = 0L;
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
                        if (!stopRequested || terminalReadFailures >= 3 || DateTime.UtcNow.Ticks < numericValue)
                        {
                            break;
                        }
                        numericValue = DateTime.UtcNow.AddSeconds(1.0).Ticks;
                        bool allReceiptsTerminal = false;
                        try
                        {
                            using (HardThreadFileStore hardThreadFileStore = new HardThreadFileStore(canonicalResult))
                            {
                                allReceiptsTerminal = true;
                                HardThreadJournal guardianJournal = hardThreadFileStore.Load(nativeRegistration.Scope.Target);
                                foreach (HardThreadReceipt row in guardianJournal.Rows)
                                {
                                    if (!row.Terminal)
                                    {
                                        allReceiptsTerminal = false;
                                    }
                                }
                                if (GameThreadControlStore.HasDebt(canonicalResult, nativeRegistration.Scope.Target,
                                    nativeRegistration.Scope.Nonce, nativeRegistration.Scope.Digest, guardianJournal.ControlDeclared)) allReceiptsTerminal = false;
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
                                    bool controlRestored = RestoreGameThreadControl(proof, criticalThreadHardAffinitySession.HasDeclaredGameThreadControl);
                                    bool affinityRestored = criticalThreadHardAffinitySession.Restore();
                                    return controlRestored && affinityRestored;
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
