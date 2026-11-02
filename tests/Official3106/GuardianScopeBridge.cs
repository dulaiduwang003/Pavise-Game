using System;
using System.Reflection;
namespace PaviseApp
{
    internal static class OfficialGuardianScopeBridge
    {
        internal static CriticalThreadHardAffinitySession OpenSession(IHardThreadGate gate,IHardThreadNative native,IHardThreadStore store,IHardThreadGuardian guardian)
        {
            var constructor=typeof(CriticalThreadHardAffinitySession).GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(IHardThreadGate),typeof(IHardThreadNative),typeof(IHardThreadStore),typeof(IHardThreadGuardian)},null);
            try { return (CriticalThreadHardAffinitySession)constructor.Invoke(new object[]{gate,native,store,guardian}); }
            catch(TargetInvocationException error) { throw error.InnerException; }
        }
        private static readonly Type Scope=typeof(CriticalThreadGuardian).GetNestedType("Scope",BindingFlags.NonPublic);
        private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static object Invoke(MethodInfo method,object target,object[] args) { try { return method.Invoke(target,args); } catch(TargetInvocationException error) { throw error.InnerException; } }
        internal static byte[] Encode(HardThreadTarget target,HardThreadGuardianActor parent,HardThreadGuardianActor child,string root,string rootIdentity,string sid,string nonce,IHardThreadOriginPlatform platform)
        {
            object scope=Activator.CreateInstance(Scope,true);
            string[] fields={"Target","Parent","Child","Root","RootIdentity","Sid","Nonce"};
            object[] values={target,parent,child,root,rootIdentity,sid,nonce};
            for(int i=0;i<fields.Length;i++)Scope.GetField(fields[i],Fields).SetValue(scope,values[i]);
            return (byte[])Invoke(Scope.GetMethod("Encode",Fields,null,new[]{typeof(IHardThreadOriginPlatform)},null),scope,new object[]{platform});
        }
        internal static void Parse(byte[] bytes,string root,Guid session,string nonce,string sid,IHardThreadOriginPlatform platform,bool currentKernel)
        { Invoke(Scope.GetMethod("Parse",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic),null,new object[]{bytes,root,session,nonce,sid,platform,currentKernel}); }
    }
}