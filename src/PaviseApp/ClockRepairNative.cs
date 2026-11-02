using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;

namespace PaviseApp
{
    internal sealed class ClockRepairSnapshot
    {
        internal string EntryId, BootIdentity;
        // Only explicit values on the current OS loader are included. Missing is not false.
        internal readonly Dictionary<uint, ulong> Values = new Dictionary<uint, ulong>();
    }

    internal interface IClockRepairNative
    {
        bool Read(out ClockRepairSnapshot value, out string error);
        bool Delete(string entryId, uint type, out string error);
        bool Set(string entryId, uint type, ulong value, out string error);
    }

    internal sealed class ClockRepairNative : IClockRepairNative
    {
        // Microsoft BCD element table: A2=useplatformclock, A4=useplatformtick,
        // A6 (integer)=tscsyncpolicy. A5=disabledynamictick is deliberately excluded.
        internal const uint UsePlatformClock = 0x260000A2;
        internal const uint UsePlatformTick = 0x260000A4;
        internal const uint TscSyncPolicy = 0x250000A6;
        private const string CurrentAlias = "{fa926493-6f1c-4193-a414-58f0b2456d1e}";
        internal static readonly uint[] Types = { UsePlatformClock, UsePlatformTick, TscSyncPolicy };

        internal static bool Allowed(uint type)
        { return type == UsePlatformClock || type == UsePlatformTick || type == TscSyncPolicy; }
        internal static bool ValidNumber(uint type, ulong value)
        { return Allowed(type) && value <= (type == TscSyncPolicy ? 2UL : 1UL); }
        internal static bool Forced(uint type, ulong value)
        { return ValidNumber(type, value) && value != 0; }

        private static ManagementScope Scope()
        {
            var options = new ConnectionOptions { EnablePrivileges = true, Impersonation = ImpersonationLevel.Impersonate,
                Timeout = TimeSpan.FromSeconds(10) };
            var scope = new ManagementScope(@"\\.\root\WMI", options);
            scope.Connect();
            return scope;
        }
        private static ManagementBaseObject Call(ManagementObject target, string method, Action<ManagementBaseObject> fill)
        {
            using (ManagementBaseObject input = target.GetMethodParameters(method))
            {
                if (fill != null) fill(input);
                ManagementBaseObject output = target.InvokeMethod(method, input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) });
                if (output == null || !(output["ReturnValue"] is bool) || !(bool)output["ReturnValue"])
                {
                    if (output != null) output.Dispose();
                    throw new InvalidOperationException("BCD WMI " + method + " did not succeed");
                }
                return output;
            }
        }
        private static ManagementObject OpenCurrent(ManagementScope scope, out string actualId)
        {
            using (var definition = new ManagementClass(scope, new ManagementPath("BcdStore"), null))
            using (ManagementBaseObject input = definition.GetMethodParameters("OpenStore"))
            {
                input["File"] = "";
                using (ManagementBaseObject output = definition.InvokeMethod("OpenStore", input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) }))
                    if (output == null || !(output["ReturnValue"] is bool) || !(bool)output["ReturnValue"])
                        throw new InvalidOperationException("BCD system store could not be opened");
            }
            using (var store = new ManagementObject(scope, new ManagementPath("BcdStore.FilePath=\"\""), null))
            using (ManagementBaseObject opened = Call(store, "OpenObject", delegate(ManagementBaseObject input) { input["Id"] = CurrentAlias; }))
            {
                var embedded = opened["Object"] as ManagementBaseObject;
                if (embedded == null) throw new InvalidOperationException("BCD current loader was not returned");
                using (embedded)
                {
                    Guid id;
                    string raw = Convert.ToString(embedded["Id"], CultureInfo.InvariantCulture);
                    if (!Guid.TryParse(raw, out id) || id == Guid.Empty || id == new Guid(CurrentAlias)
                        || Convert.ToUInt32(embedded["Type"], CultureInfo.InvariantCulture) != 0x10200003
                        || !string.IsNullOrEmpty(Convert.ToString(embedded["StoreFilePath"], CultureInfo.InvariantCulture)))
                        throw new InvalidOperationException("BCD current OS loader identity could not be resolved");
                    actualId = id.ToString("B");
                    return new ManagementObject(scope, new ManagementPath("BcdObject.Id=\"" + actualId + "\",StoreFilePath=\"\""), null);
                }
            }
        }
        public bool Read(out ClockRepairSnapshot value, out string error)
        {
            value = null; error = "";
            try
            {
                ManagementScope scope = Scope(); string id;
                using (ManagementObject current = OpenCurrent(scope, out id))
                // EnumerateElements has no input parameters. Do not treat provider failure as absence.
                using (ManagementBaseObject output = Call(current, "EnumerateElements", null))
                {
                    var elements = output["Elements"] as ManagementBaseObject[];
                    if (elements == null) throw new InvalidOperationException("BCD explicit element enumeration was not returned");
                    var snapshot = new ClockRepairSnapshot { EntryId = id, BootIdentity = BootSessionIdentity.Read() };
                    foreach (ManagementBaseObject element in elements)
                    {
                        using (element)
                        {
                            uint type = Convert.ToUInt32(element["Type"], CultureInfo.InvariantCulture);
                            if (!Allowed(type)) continue;
                            ulong number;
                            if (type == TscSyncPolicy)
                            {
                                object integer = element["Integer"];
                                if (integer == null) throw new InvalidOperationException("BCD integer has no value");
                                number = Convert.ToUInt64(integer, CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                object boolean = element["Boolean"];
                                if (!(boolean is bool)) throw new InvalidOperationException("BCD Boolean has an unexpected type");
                                number = (bool)boolean ? 1UL : 0UL;
                            }
                            if (!ValidNumber(type, number) || snapshot.Values.ContainsKey(type))
                                throw new InvalidOperationException("BCD clock value is unsupported or duplicated");
                            snapshot.Values.Add(type, number);
                        }
                    }
                    value = snapshot; return true;
                }
            }
            catch (Exception ex) { error = "BCD WMI: " + ex.GetType().Name + " (" + ex.Message + ")"; return false; }
        }
        private bool Write(string entryId, uint type, ulong number, bool remove, out string error)
        {
            error = "";
            try
            {
                Guid wanted;
                if (!Guid.TryParse(entryId, out wanted) || wanted == Guid.Empty || !Allowed(type) || (!remove && !ValidNumber(type, number)))
                    throw new InvalidOperationException("Unapproved BCD target");
                ManagementScope scope = Scope(); string actualId;
                using (ManagementObject current = OpenCurrent(scope, out actualId))
                {
                    if (new Guid(actualId) != wanted) throw new InvalidOperationException("The current OS loader changed");
                    string method = remove ? "DeleteElement" : type == TscSyncPolicy ? "SetIntegerElement" : "SetBooleanElement";
                    using (ManagementBaseObject output = Call(current, method, delegate(ManagementBaseObject input) {
                        input["Type"] = type;
                        if (!remove) input[type == TscSyncPolicy ? "Integer" : "Boolean"] = type == TscSyncPolicy ? (object)number : number != 0;
                    })) { }
                }
                return true;
            }
            catch (Exception ex) { error = "BCD WMI: " + ex.GetType().Name + " (" + ex.Message + ")"; return false; }
        }
        public bool Delete(string entryId, uint type, out string error) { return Write(entryId, type, 0, true, out error); }
        public bool Set(string entryId, uint type, ulong value, out string error) { return Write(entryId, type, value, false, out error); }
    }
}
