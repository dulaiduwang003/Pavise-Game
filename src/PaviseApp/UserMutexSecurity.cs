using System;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PaviseApp
{
    internal static class UserMutexSecurity
    {
        internal static MutexSecurity Create(SecurityIdentifier user)
        {
            if (user == null)
            {
                throw new ArgumentNullException("user");
            }
            MutexSecurity mutexSecurity = new MutexSecurity();
            mutexSecurity.SetOwner(user);
            mutexSecurity.SetAccessRuleProtection(true, false);
            mutexSecurity.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
            return mutexSecurity;
        }

        internal static void Verify(MutexSecurity security, SecurityIdentifier user)
        {
            if (security == null || user == null || !user.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected || !security.AreAccessRulesCanonical)
            {
                throw new UnauthorizedAccessException("Mutex owner or DACL protection differs from the admitted user");
            }
            AuthorizationRuleCollection accessRules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            if (accessRules.Count != 1)
            {
                throw new UnauthorizedAccessException("Mutex must have exactly one user access rule");
            }
            MutexAccessRule mutexAccessRule = accessRules[0] as MutexAccessRule;
            if (mutexAccessRule == null || mutexAccessRule.IsInherited || !user.Equals(mutexAccessRule.IdentityReference) || mutexAccessRule.AccessControlType != 0 || mutexAccessRule.MutexRights != MutexRights.FullControl || mutexAccessRule.InheritanceFlags != 0 || mutexAccessRule.PropagationFlags != 0)
            {
                throw new UnauthorizedAccessException("Mutex access rule differs from the admitted user");
            }
        }
    }
}
