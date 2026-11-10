using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
    internal static partial class CriticalThreadGuardian
    {

        private sealed class Scope
        {
            internal HardThreadTarget Target;

            internal HardThreadGuardianActor Parent;

            internal HardThreadGuardianActor Child;

            internal string Root;

            internal string RootIdentity;

            internal string Sid;

            internal string Nonce;

            internal byte[] Bytes;

            internal HardThreadOriginContext Origin;

            internal byte[] OriginSeal;

            internal string Digest
            {
                get
                {
                    return Hash(Bytes);
                }
            }

            private byte[] Binding()
            {
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    using (BinaryWriter binaryWriter = new BinaryWriter(memoryStream, new UTF8Encoding(false), true))
                    {
                        binaryWriter.Write("Pavise.HardThread.Scope.V2");
                        binaryWriter.Write(Target.SessionId.ToByteArray());
                        binaryWriter.Write(Target.ProcessId);
                        binaryWriter.Write(Target.ProcessCreation);
                        binaryWriter.Write(Target.ExecutablePath);
                        binaryWriter.Write(Target.AllowedMask);
                        binaryWriter.Write(Root);
                        binaryWriter.Write(RootIdentity);
                        binaryWriter.Write(Sid);
                        binaryWriter.Write(Nonce);
                        foreach (HardThreadGuardianActor hardThreadGuardianActor in new HardThreadGuardianActor[2] { Parent, Child })
                        {
                            binaryWriter.Write(hardThreadGuardianActor.Pid);
                            binaryWriter.Write(hardThreadGuardianActor.Creation);
                            binaryWriter.Write(hardThreadGuardianActor.Path);
                            binaryWriter.Write(hardThreadGuardianActor.ImageHash);
                        }
                        binaryWriter.Write(Origin.Machine);
                        binaryWriter.Write(Origin.Name);
                        binaryWriter.Write(Origin.Boot);
                    }
                    return memoryStream.ToArray();
                }
            }

            internal byte[] Encode()
            {
                return Encode(new NativeOrigin());
            }

            internal byte[] Encode(IHardThreadOriginPlatform platform)
            {
                Origin = platform.Current();
                OriginSeal = platform.Seal(Binding());
                if (OriginSeal == null || OriginSeal.Length < 1 || OriginSeal.Length > 16384)
                {
                    throw new InvalidDataException("bounded machine origin seal required");
                }
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    using (XmlWriter xmlWriter = XmlWriter.Create(memoryStream, new XmlWriterSettings
                    {
                        Encoding = new UTF8Encoding(false),
                        CloseOutput = false
                    }))
                    {
                        xmlWriter.WriteStartElement("originalHardThreadScope");
                        Write(xmlWriter, "version", "2");
                        Write(xmlWriter, "session", Target.SessionId);
                        Write(xmlWriter, "pid", Target.ProcessId);
                        Write(xmlWriter, "birth", Target.ProcessCreation);
                        Write(xmlWriter, "path", Target.ExecutablePath);
                        Write(xmlWriter, "allowed", Target.AllowedMask);
                        Write(xmlWriter, "root", Root);
                        Write(xmlWriter, "rootId", RootIdentity);
                        Write(xmlWriter, "sid", Sid);
                        Write(xmlWriter, "nonce", Nonce);
                        WriteActor(xmlWriter, "parent", Parent);
                        WriteActor(xmlWriter, "child", Child);
                        Write(xmlWriter, "machine", Origin.Machine);
                        Write(xmlWriter, "machineName", Origin.Name);
                        Write(xmlWriter, "kernel", Origin.Boot);
                        Write(xmlWriter, "originSeal", Convert.ToBase64String(OriginSeal));
                        xmlWriter.WriteEndElement();
                    }
                    return Bytes = memoryStream.ToArray();
                }
            }

            private static void Write(XmlWriter writer, string key, object value)
            {
                writer.WriteAttributeString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
            }

            private static void WriteActor(XmlWriter writer, string prefix, HardThreadGuardianActor actor)
            {
                Write(writer, prefix + "Pid", actor.Pid);
                Write(writer, prefix + "Birth", actor.Creation);
                Write(writer, prefix + "Path", actor.Path);
                Write(writer, prefix + "Hash", actor.ImageHash);
            }

            private static HardThreadGuardianActor ReadActor(XmlElement xml, string prefix)
            {
                return new HardThreadGuardianActor(int.Parse(xml.GetAttribute(prefix + "Pid")), long.Parse(xml.GetAttribute(prefix + "Birth")), xml.GetAttribute(prefix + "Path"), xml.GetAttribute(prefix + "Hash"));
            }

            internal static Scope Parse(byte[] bytes, string root, Guid session, string nonce, string actualSid, IHardThreadOriginPlatform platform, bool currentKernel)
            {
                if (bytes.Length < 32 || bytes.Length > 65536)
                {
                    throw new InvalidDataException("guardian scope size");
                }
                XmlDocument currentXmlDocument = new XmlDocument();
                currentXmlDocument.XmlResolver = null;
                using (XmlReader reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
                {
                    XmlResolver = null,
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = 65536L
                }))
                {
                    currentXmlDocument.Load(reader);
                }
                XmlElement documentElement = currentXmlDocument.DocumentElement;
                if (documentElement == null || documentElement.Name != "originalHardThreadScope" || documentElement.GetAttribute("version") != "2" || documentElement.HasChildNodes)
                {
                    throw new InvalidDataException("guardian scope format or original machine domain is unavailable");
                }
                Scope currentScope = new Scope();
                currentScope.Target = new HardThreadTarget(Guid.Parse(documentElement.GetAttribute("session")), int.Parse(documentElement.GetAttribute("pid")), long.Parse(documentElement.GetAttribute("birth")), documentElement.GetAttribute("path"), ulong.Parse(documentElement.GetAttribute("allowed")));
                currentScope.Parent = ReadActor(documentElement, "parent");
                currentScope.Child = ReadActor(documentElement, "child");
                currentScope.Root = Canonical(documentElement.GetAttribute("root"));
                currentScope.RootIdentity = documentElement.GetAttribute("rootId");
                currentScope.Sid = documentElement.GetAttribute("sid");
                currentScope.Nonce = documentElement.GetAttribute("nonce");
                currentScope.Bytes = (byte[])bytes.Clone();
                currentScope.Origin = new HardThreadOriginContext(documentElement.GetAttribute("machine"), documentElement.GetAttribute("machineName"), documentElement.GetAttribute("kernel"));
                currentScope.OriginSeal = Convert.FromBase64String(documentElement.GetAttribute("originSeal"));
                if (currentScope.Target.SessionId != session || currentScope.Nonce != nonce || !IsHash(nonce) || !string.Equals(currentScope.Root, Canonical(root), StringComparison.OrdinalIgnoreCase) || currentScope.Sid != actualSid || currentScope.Parent.ImageHash != currentScope.Child.ImageHash || !IsHash(currentScope.Parent.ImageHash))
                {
                    throw new InvalidDataException("original guardian scope mismatch");
                }
                HardThreadOriginContext hardThreadOriginContext = platform.Current();
                if (!currentScope.Origin.SameMachine(hardThreadOriginContext) || (currentKernel && currentScope.Origin.Boot != hardThreadOriginContext.Boot) || currentScope.OriginSeal.Length < 1 || currentScope.OriginSeal.Length > 16384 || Hash(platform.Unseal(currentScope.OriginSeal)) != Hash(currentScope.Binding()))
                {
                    throw new InvalidDataException("original Windows protection or kernel domain differs");
                }
                string path = Path.Combine(currentScope.Root, "guards", session.ToString("N"), "Pavise.HardThreadGuardian.exe");
                if (!string.Equals(Canonical(path), currentScope.Child.Path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("guardian is outside the original runtime");
                }
                return currentScope;
            }
        }

        private sealed class ActorWait : WaitHandle
        {
            internal ActorWait(IntPtr handle)
            {
                base.SafeWaitHandle = new SafeWaitHandle(handle, false);
            }
        }

        private sealed class NativeRegistration : IDisposable
        {
            internal Scope Scope;

            internal ActorPin Parent;

            internal TargetPin Target;

            internal RootPin Root;

            internal FileStream Source;

            internal NamedPipeClientStream Pipe;

            internal HardThreadGuardianHandshake Handshake;

            internal readonly object Coordination = new object();

            private HardThreadResourceClosure closure;

            private Exception unassignedCloseFailure;

            private volatile bool closingStarted;

            internal bool ResourcesClosed
            {
                get
                {
                    lock (Coordination)
                    {
                        return unassignedCloseFailure == null && closure != null && closure.ResourcesClosed;
                    }
                }
            }

            internal void RememberClosureFailure(Exception error)
            {
                if (HardThreadClosureFailure.Contains(error))
                {
                    lock (Coordination)
                    {
                        unassignedCloseFailure = error;
                    }
                }
            }

            internal void RequireStopped()
            {
                if (closingStarted || Parent == null || Root == null || Source == null || Handshake == null || !Handshake.RecoveryEligible || Parent.State != HardThreadRead.Gone)
                {
                    throw new InvalidOperationException("original live registration has no stopped-parent proof");
                }
            }

            public void Dispose()
            {
                lock (Coordination)
                {
                    closingStarted = true;
                    if (closure == null)
                    {
                        closure = new HardThreadResourceClosure(Pipe, Source, Target, Parent, Root);
                    }
                    if (unassignedCloseFailure == null)
                    {
                        closure.Dispose();
                        return;
                    }
                    try
                    {
                        closure.Dispose();
                    }
                    catch (Exception ex)
                    {
                        throw new AggregateException("Original registration construction and cleanup remain unconfirmed", unassignedCloseFailure, ex);
                    }
                    throw new HardThreadClosureFailure("Unassigned registration source closure is unconfirmed", unassignedCloseFailure);
                }
            }
        }

        internal sealed class StopProof : IDisposable
        {
            private NativeRegistration original;

            internal HardThreadTarget Target
            {
                get
                {
                    RequireStopped();
                    return original.Scope.Target;
                }
            }

            internal string CanonicalStore
            {
                get
                {
                    RequireStopped();
                    return original.Scope.Root;
                }
            }

            internal string SourceNonce
            {
                get
                {
                    RequireStopped();
                    return original.Scope.Nonce;
                }
            }

            internal string SourceScopeHash
            {
                get
                {
                    RequireStopped();
                    return original.Scope.Digest;
                }
            }

            private StopProof(NativeRegistration original)
            {
                this.original = original;
            }

            internal static StopProof Issue(object original)
            {
                NativeRegistration nativeRegistration = original as NativeRegistration;
                if (nativeRegistration == null || !object.ReferenceEquals(issuingProof, nativeRegistration))
                {
                    throw new InvalidOperationException("only the original live guardian can issue recovery authority");
                }
                nativeRegistration.RequireStopped();
                return new StopProof(nativeRegistration);
            }

            internal IDisposable EnterUse()
            {
                RequireStopped();
                return new RecoveryUse(original.Coordination);
            }

            internal bool WaitForTargetExit()
            {
                RequireStopped();
                return WaitForSingleObject(original.Target.Handle, uint.MaxValue) == 0;
            }

            internal bool TargetExited()
            {
                RequireStopped();
                return WaitForSingleObject(original.Target.Handle, 0) == 0;
            }

            internal void RequireStopped()
            {
                if (original == null)
                {
                    throw new ObjectDisposedException("guardian proof");
                }
                original.RequireStopped();
            }

            internal IntPtr DuplicateTarget()
            {
                RequireStopped();
                return original.Target.Duplicate();
            }

            public void Dispose()
            {
                if (original != null)
                {
                    original.Dispose();
                    original = null;
                }
            }
        }

        private sealed class RecoveryUse : IDisposable
        {
            private object held;

            internal RecoveryUse(object value)
            {
                held = value;
                Monitor.Enter(held);
            }

            public void Dispose()
            {
                object heldSnapshot = held;
                held = null;
                if (heldSnapshot != null)
                {
                    Monitor.Exit(heldSnapshot);
                }
            }
        }

        private sealed class NativeOrigin : IHardThreadOriginPlatform
        {
            private static readonly byte[] entropy = Text("Pavise.HardThread.Origin.V2.LocalMachine");

            public HardThreadOriginContext Current()
            {
                string input;
                using (RegistryKey registryKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (RegistryKey currentRegistryKey = registryKey.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography", false))
                    {
                        input = ((currentRegistryKey == null) ? null : (currentRegistryKey.GetValue("MachineGuid") as string));
                    }
                }
                Guid result;
                if (!Guid.TryParse(input, out result) || result == Guid.Empty)
                {
                    throw new IOException("Windows machine protection domain unavailable");
                }
                return new HardThreadOriginContext(result.ToString("N"), Environment.MachineName, BootSessionIdentity.Read());
            }

            public byte[] Seal(byte[] original)
            {
                return ProtectedData.Protect(original, entropy, DataProtectionScope.LocalMachine);
            }

            public byte[] Unseal(byte[] original)
            {
                return ProtectedData.Unprotect(original, entropy, DataProtectionScope.LocalMachine);
            }
        }

    }
}
