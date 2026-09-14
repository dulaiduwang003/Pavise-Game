// @author bdth 2074055628@qq.com
// File purpose Decide per CPU category which items differ in the esports power plan, pure decision, touches no hardware
using System;

namespace PaviseApp
{
    internal sealed class PowerPlanProfile
    {
        public readonly string Tag;
        public readonly bool WriteHetero;
        public readonly uint HeteroSched;
        public readonly bool PreserveCoreParking;

        private PowerPlanProfile(string tag, bool hetero, uint sched, bool preserveCoreParking)
        {
            Tag = tag; WriteHetero = hetero; HeteroSched = sched;
            PreserveCoreParking = preserveCoreParking;
        }

        public bool UseArenaCoreParking(bool aggressive)
        {
            return !PreserveCoreParking;
        }

        public static PowerPlanProfile Resolve(bool amdCpu, bool hybrid, bool asymCache,
            string partitionTag)
        {
            // Intel hybrid short/long thread scheduling policy writes 5 automatic, leaving it to Thread Director
            //   Once wrote 2 prefer performance cores, with no measured basis; it crowds system short threads and game helper threads onto P-cores together
            //   Background is already pushed to E-cores by EcoQoS, no need to fight for the wheel with a global policy
            if (hybrid && !amdCpu) return new PowerPlanProfile(Lang.T("t.powerplanprofile.1"), true, 5, false);
            if (hybrid) return new PowerPlanProfile(Lang.T("t.powerplanprofile.2"), true, 0, false);
            if (asymCache) return new PowerPlanProfile(Lang.T("t.powerplanprofile.3"), false, 0, true);
            if (partitionTag == "symmetric-ccd") return new PowerPlanProfile(Lang.T("t.powerplanprofile.4"), false, 0, false);
            return new PowerPlanProfile(Lang.T("gfx.tab.common"), false, 0, false);
        }
    }
}
