using System;

namespace PaviseApp
{
    internal sealed class HardThreadGuardianHandshake
    {
        private readonly HardThreadGuardianActor parent;

        private readonly HardThreadGuardianActor child;

        private readonly string nonce;

        private readonly string source;

        private bool accepted;

        private bool parentGone;

        private bool guardLost;

        internal bool ForwardReady
        {
            get
            {
                if (accepted && !parentGone)
                {
                    return !guardLost;
                }
                return false;
            }
        }

        internal bool RecoveryEligible
        {
            get
            {
                if (accepted)
                {
                    return parentGone;
                }
                return false;
            }
        }

        internal HardThreadGuardianHandshake(HardThreadGuardianActor parent, HardThreadGuardianActor child, string nonce, string source)
        {
            if (parent == null || child == null || nonce == null || nonce.Length != 64 || source == null || source.Length != 64)
            {
                throw new ArgumentException("original guardian handshake facts");
            }
            this.parent = parent;
            this.child = child;
            this.nonce = nonce;
            this.source = source;
        }

        internal void Accept(int observedServerPid, int observedClientPid, HardThreadGuardianActor originalParent, HardThreadGuardianActor originalChild, string originalNonce, string scopeHash)
        {
            if (accepted || observedServerPid != parent.Pid || observedClientPid != child.Pid || !parent.Same(originalParent) || !child.Same(originalChild) || nonce != originalNonce || source != scopeHash)
            {
                throw new InvalidOperationException("live guardian peer or original scope mismatch");
            }
            accepted = true;
        }

        internal void ObserveParent(HardThreadRead state)
        {
            if (state == HardThreadRead.Gone)
            {
                parentGone = true;
            }
        }

        internal void ObserveGuard(HardThreadRead state)
        {
            if (state != 0)
            {
                guardLost = true;
            }
        }
    }
}
