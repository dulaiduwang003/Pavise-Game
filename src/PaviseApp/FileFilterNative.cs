using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    // 查询使用 fltUserStructures.h 定义的 fltlib 完整信息布局；修改使用 FilterAttach，不使用仅用于调试的 FilterAttachAtAltitude。

    internal sealed class FileFilterNative : IFileFilterNative
    {
        private const int NoMoreItems = unchecked((int)0x80070103);
        private const int BufferBytes = 65536;
        private sealed class DriverInfo
        {
            internal string Path="", Identity="", Publisher="", Description="", Error="";
            internal Dictionary<string,string> Instances = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        }
        [DllImport("fltlib.dll", CharSet=CharSet.Unicode)] private static extern int FilterVolumeInstanceFindFirst(string volume, int infoClass, IntPtr buffer, uint size, out uint returned, out IntPtr handle);
        [DllImport("fltlib.dll")] private static extern int FilterVolumeInstanceFindNext(IntPtr handle, int infoClass, IntPtr buffer, uint size, out uint returned);
        [DllImport("fltlib.dll")] private static extern int FilterVolumeInstanceFindClose(IntPtr handle);
        [DllImport("fltlib.dll")] private static extern int FilterFindFirst(int infoClass, IntPtr buffer, uint size, out uint returned, out IntPtr handle);
        [DllImport("fltlib.dll")] private static extern int FilterFindNext(IntPtr handle, int infoClass, IntPtr buffer, uint size, out uint returned);
        [DllImport("fltlib.dll")] private static extern int FilterFindClose(IntPtr handle);
        [DllImport("fltlib.dll", CharSet=CharSet.Unicode)] private static extern int FilterDetach(string filter, string volume, string instance);
        [DllImport("fltlib.dll", CharSet=CharSet.Unicode)] private static extern int FilterAttach(string filter, string volume, string instance, uint nameBytes, StringBuilder actualName);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder name, uint size);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool GetVolumePathNamesForVolumeName(string volume, [Out]char[] names, uint length, out uint required);
        [DllImport("advapi32.dll", SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError=true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool all, ref TokenPrivileges requested, uint length, out TokenPrivileges previous, out uint returned);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint Low; internal int High; }
        [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { internal uint Count; internal Luid Luid; internal uint Attributes; }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct TrustFile { internal uint Size; [MarshalAs(UnmanagedType.LPWStr)] internal string Path; internal IntPtr Handle, Subject; }
        [StructLayout(LayoutKind.Sequential)] private struct TrustData
        {
            internal uint Size; internal IntPtr Callback, Sip; internal uint UI, Revocation, Choice; internal IntPtr File; internal uint StateAction; internal IntPtr State, Url; internal uint Flags, Context;
        }
        [DllImport("wintrust.dll", ExactSpelling=true, CharSet=CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr window, [In]ref Guid action, [In,Out]ref TrustData data);

        private static string HResult(int value) { return "0x" + value.ToString("X8"); }
        private static string NativeVolume(string guid) { return guid.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\??\" + guid.Substring(4) : guid; }
        private static string ReadString(IntPtr block, uint used, int lengthOffset, int bufferOffset)
        {
            int bytes=(ushort)Marshal.ReadInt16(block,lengthOffset), offset=(ushort)Marshal.ReadInt16(block,bufferOffset);
            if ((bytes & 1)!=0 || offset<20 || (uint)offset>used || (uint)bytes>used-(uint)offset) throw new InvalidDataException("Invalid filter information string bounds");
            return Marshal.PtrToStringUni(IntPtr.Add(block,offset),bytes/2) ?? "";
        }
        private static List<FileFilterCandidate> VolumeInstances(string guid, string display)
        {
            var rows=new List<FileFilterCandidate>(); IntPtr memory=Marshal.AllocHGlobal(BufferBytes), handle=new IntPtr(-1);
            try
            {
                uint used; int hr=FilterVolumeInstanceFindFirst(NativeVolume(guid),2,memory,BufferBytes,out used,out handle);
                if(hr==NoMoreItems)return rows;
                if(hr<0)throw new InvalidOperationException("Filter instance query failed: "+HResult(hr));
                for(int count=0;count<4096;count++)
                {
                    if(used<20||used>BufferBytes)throw new InvalidDataException("Invalid filter instance information size");
                    rows.Add(new FileFilterCandidate { VolumeGuid=guid,VolumeDisplay=display,
                        InstanceName=ReadString(memory,used,4,6),Altitude=ReadString(memory,used,8,10),FilterName=ReadString(memory,used,16,18) });
                    hr=FilterVolumeInstanceFindNext(handle,2,memory,BufferBytes,out used);
                    if(hr==NoMoreItems)return rows;
                    if(hr<0)throw new InvalidOperationException("Filter instance query failed: "+HResult(hr));
                }
                throw new InvalidDataException("Filter instance enumeration limit exceeded");
            }
            finally { if(handle!=IntPtr.Zero&&handle!=new IntPtr(-1))FilterVolumeInstanceFindClose(handle); Marshal.FreeHGlobal(memory); }
        }
        private static bool Loaded(string filter)
        {
            IntPtr memory=Marshal.AllocHGlobal(BufferBytes),handle=new IntPtr(-1);
            try
            {
                uint used; int hr=FilterFindFirst(0,memory,BufferBytes,out used,out handle);
                if(hr==NoMoreItems)return false;
                if(hr<0)throw new InvalidOperationException("Filter query failed: "+HResult(hr));
                for(int count=0;count<4096;count++)
                {
                    if(used<14||used>BufferBytes)throw new InvalidDataException("Invalid filter information size");
                    int length=(ushort)Marshal.ReadInt16(memory,12);
                    if((length&1)!=0||length>used-14)throw new InvalidDataException("Invalid filter name size");
                    if(FileFilterCandidate.Eq(Marshal.PtrToStringUni(IntPtr.Add(memory,14),length/2),filter))return true;
                    hr=FilterFindNext(handle,0,memory,BufferBytes,out used); if(hr==NoMoreItems)return false;
                    if(hr<0)throw new InvalidOperationException("Filter query failed: "+HResult(hr));
                }
                throw new InvalidDataException("Filter enumeration limit exceeded");
            }
            finally {if(handle!=IntPtr.Zero&&handle!=new IntPtr(-1))FilterFindClose(handle);Marshal.FreeHGlobal(memory);}
        }
        private static bool Signature(string path, out string publisher, out string thumbprint)
        {
            publisher=thumbprint=""; IntPtr file=IntPtr.Zero;
            var info=new TrustFile { Size=(uint)Marshal.SizeOf(typeof(TrustFile)),Path=path };
            var data=new TrustData { Size=(uint)Marshal.SizeOf(typeof(TrustData)),UI=2,Choice=1,StateAction=1,Flags=0x1000 };
            Guid action=new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            try
            {
                file=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));Marshal.StructureToPtr(info,file,false);data.File=file;
                if(WinVerifyTrust(new IntPtr(-1),ref action,ref data)!=0)return false;
                using(var source=X509Certificate.CreateFromSignedFile(path))
                using(var cert=new X509Certificate2(source)) {publisher=cert.Subject;thumbprint=cert.Thumbprint;}
                return !string.IsNullOrEmpty(publisher)&&!string.IsNullOrEmpty(thumbprint);
            }
            catch{return false;}
            finally
            {
                data.StateAction=2; if(data.State!=IntPtr.Zero)WinVerifyTrust(new IntPtr(-1),ref action,ref data);
                if(file!=IntPtr.Zero){Marshal.DestroyStructure(file,typeof(TrustFile));Marshal.FreeHGlobal(file);}
            }
        }
        private static string ResolveImage(string image)
        {
            if(string.IsNullOrWhiteSpace(image))return ""; image=Environment.ExpandEnvironmentVariables(image.Trim().Trim('"'));
            if(image.StartsWith(@"\SystemRoot\",StringComparison.OrdinalIgnoreCase))image=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),image.Substring(12));
            else if(image.StartsWith(@"\??\",StringComparison.Ordinal))image=image.Substring(4);
            else if(image.StartsWith(@"System32\",StringComparison.OrdinalIgnoreCase))image=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),image);
            if(!Path.IsPathRooted(image)||!image.EndsWith(".sys",StringComparison.OrdinalIgnoreCase))return "";
            return Path.GetFullPath(image);
        }
        private static DriverInfo Driver(string filter, string instance, string altitude)
        {
            var found=new List<DriverInfo>();
            using(var services=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services",false))
            {
                if(services==null)return new DriverInfo {Error="Driver service registry unavailable"};
                foreach(string name in services.GetSubKeyNames())
                {
                    using(var service=services.OpenSubKey(name,false))
                    {
                        if(service==null)continue;
                        bool matched=false;
                        foreach(string location in new[]{@"Parameters\Instances","Instances"})
                        {
                            using(var instances=service.OpenSubKey(location,false))
                            {
                                if(instances==null)continue;
                                using(var entry=instances.OpenSubKey(instance,false))
                                {
                                    if(entry==null || !string.Equals(Convert.ToString(entry.GetValue("Altitude")),altitude,StringComparison.Ordinal))continue;
                                    matched=true;
                                }
                            }
                        }
                        if(!matched)continue;
                        // 恢复时需向 FilterAttach 提供准确的注册名和高度；同时检查两种注册表布局时，同一服务只计一次。

                        string image=ResolveImage(Convert.ToString(service.GetValue("ImagePath")));
                        // Wof 通常没有 ImagePath，此备用路径仅供诊断；Wof 始终是受保护系统目标，不能在此分离。

                        if(image.Length==0&&FileFilterCandidate.Eq(filter,"Wof")&&FileFilterCandidate.Eq(name,"Wof"))
                            image=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\drivers\Wof.sys");
                        var info=new DriverInfo {Path=image};
                        info.Instances[instance]=altitude;found.Add(info);
                    }
                }
            }
            if(found.Count!=1)return new DriverInfo {Error="No unique registered driver instance owner"};
            DriverInfo value=found[0];
            if(value.Path.Length==0||!File.Exists(value.Path)) {value.Error="Driver image unavailable";return value;}
            // 信任验证失败时，文件元数据仍可供只读诊断；没有下方签名映像身份，不授予操作资格。

            var version=FileVersionInfo.GetVersionInfo(value.Path);
            value.Publisher=version.CompanyName??"";
            value.Description=version.FileDescription??"";
            string publisher,thumb;
            if(!Signature(value.Path,out publisher,out thumb)){value.Error="Driver signature or owner could not be verified";return value;}
            // 现代第三方内核映像可能使用微软硬件发布者签名。组件归属由签名映像的厂商元数据确认，签名证书仍保存在 Identity 中。


            value.Publisher=string.IsNullOrWhiteSpace(version.CompanyName)?publisher:version.CompanyName;
            value.Description=version.FileDescription??"";
            using(var stream=new FileStream(value.Path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
            using(var sha=SHA256.Create())value.Identity=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")+":"+thumb;
            return value;
        }
        private static void Identify(FileFilterCandidate target, DriverInfo info)
        {
            target.DriverPath=info.Path;target.DriverIdentity=info.Identity;target.Publisher=info.Publisher;target.Description=info.Description;
            string protection=FileFilterAdmission.ProtectedNameReason(target.FilterName);
            string reason=protection.Length!=0?protection:string.IsNullOrEmpty(info.Error)?FileFilterAdmission.Reason(target):info.Error;
            target.Eligible=reason.Length==0;target.BlockReason=FileFilterAdmission.DisplayReason(reason);
            if(protection=="Protected filter with unverified ownership"&&!string.IsNullOrEmpty(info.Error))
                target.BlockReason+=" · "+FileFilterAdmission.DisplayReason(info.Error);
        }
        public FileFilterCandidate[] Enumerate(out string error)
        {
            error="";var result=new List<FileFilterCandidate>();var drivers=new Dictionary<string,DriverInfo>(StringComparer.OrdinalIgnoreCase);var volumes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach(DriveInfo drive in DriveInfo.GetDrives())
                {
                    if(drive.DriveType!=DriveType.Fixed||!drive.IsReady||!string.Equals(drive.DriveFormat,"NTFS",StringComparison.OrdinalIgnoreCase))continue;
                    var guid=new StringBuilder(128);if(!GetVolumeNameForVolumeMountPoint(drive.RootDirectory.FullName,guid,128))continue;
                    if(!volumes.Add(guid.ToString()))continue;
                    List<FileFilterCandidate> rows=VolumeInstances(guid.ToString(),drive.Name.TrimEnd((char)92));
                    foreach(FileFilterCandidate row in rows)
                    {
                        string key=row.FilterName+"|"+row.InstanceName+"|"+row.Altitude;DriverInfo info;
                        if(!drivers.TryGetValue(key,out info)){try {info=Driver(row.FilterName,row.InstanceName,row.Altitude);}catch(Exception e){info=new DriverInfo {Error=e.GetType().Name};}drivers[key]=info;}
                        Identify(row,info); int same=0;foreach(FileFilterCandidate other in rows)if(FileFilterCandidate.Eq(row.FilterName,other.FilterName))same++;
                        if(same!=1){row.Eligible=false;row.BlockReason=FileFilterAdmission.DisplayReason("Multiple instances of this filter are attached to this volume");}
                        result.Add(row);
                    }
                }
            }
            catch(Exception e){error=e.Message;return new FileFilterCandidate[0];}
            return result.ToArray();
        }
        public bool Observe(FileFilterCandidate expected, out FileFilterObservation observation, out string error)
        {
            observation=new FileFilterObservation();error="";
            try
            {
                var paths=new char[32768];uint required;
                observation.VolumePresent=GetVolumePathNamesForVolumeName(expected.VolumeGuid,paths,(uint)paths.Length,out required);
                if(!observation.VolumePresent){error="Original volume is unavailable";return true;}
                observation.FilterLoaded=Loaded(expected.FilterName); if(!observation.FilterLoaded){error="Original filter is not loaded";return true;}
                DriverInfo owner=Driver(expected.FilterName,expected.InstanceName,expected.Altitude);
                var actual=expected.Copy();Identify(actual,owner);
                observation.DriverMatches=actual.Eligible&&actual.SameDriver(expected);
                var matches=new List<FileFilterCandidate>();
                foreach(FileFilterCandidate row in VolumeInstances(expected.VolumeGuid,expected.VolumeDisplay))
                    if(FileFilterCandidate.Eq(row.FilterName,expected.FilterName)){Identify(row,owner);matches.Add(row);}
                observation.Instances=matches.ToArray();return true;
            }
            catch(Exception e){error=e.Message;return false;}
        }
        private sealed class DriverPrivilege : IDisposable
        {
            private IntPtr token;private TokenPrivileges previous;private bool changed;
            internal DriverPrivilege()
            {
                if(!OpenProcessToken(Process.GetCurrentProcess().Handle,0x28,out token))throw new UnauthorizedAccessException("Cannot open driver privilege token");
                try
                {
                    Luid id;if(!LookupPrivilegeValue(null,"SeLoadDriverPrivilege",out id))throw new UnauthorizedAccessException("Driver privilege unavailable");
                    var requested=new TokenPrivileges {Count=1,Luid=id,Attributes=2};uint returned;
                    if(!AdjustTokenPrivileges(token,false,ref requested,(uint)Marshal.SizeOf(typeof(TokenPrivileges)),out previous,out returned)||Marshal.GetLastWin32Error()!=0)throw new UnauthorizedAccessException("Driver privilege was not granted");
                    changed=true;
                }
                catch{CloseHandle(token);token=IntPtr.Zero;throw;}
            }
            public void Dispose(){if(token!=IntPtr.Zero){if(changed){TokenPrivileges unused;uint size;AdjustTokenPrivileges(token,false,ref previous,(uint)Marshal.SizeOf(typeof(TokenPrivileges)),out unused,out size);}CloseHandle(token);token=IntPtr.Zero;}}
        }
        public bool Detach(FileFilterCandidate target,out string error)
        {
            if(target==null||!target.Eligible||FileFilterAdmission.Reason(target).Length!=0){error="Filter target is not eligible";return false;}
            error="";try{using(new DriverPrivilege()){int hr=FilterDetach(target.FilterName,NativeVolume(target.VolumeGuid),target.InstanceName);if(hr>=0)return true;error="FilterDetach: "+HResult(hr);return false;}}catch(Exception e){error=e.Message;return false;}
        }
        public bool Attach(FileFilterCandidate target,out string error)
        {
            if(target==null||!target.Eligible||FileFilterAdmission.Reason(target).Length!=0){error="Filter target is not eligible";return false;}
            error="";try{using(new DriverPrivilege()){var name=new StringBuilder(1024);int hr=FilterAttach(target.FilterName,NativeVolume(target.VolumeGuid),target.InstanceName,2048,name);if(hr>=0)return true;error="FilterAttach: "+HResult(hr);return false;}}catch(Exception e){error=e.Message;return false;}
        }
    }
}
