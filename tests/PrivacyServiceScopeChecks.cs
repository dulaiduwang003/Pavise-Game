#if PAVISE_SELFTEST && PAVISE_PRIVACY_NATIVE_RUNNER
using System;
using System.Collections.Generic;
using System.Reflection;

namespace PaviseApp
{
    internal static class PrivacyServiceScopeChecks
    {
        private static int assertions, reads;
        private static readonly Dictionary<string, string> values = new Dictionary<string, string>();
        private static readonly List<string> writes = new List<string>();
        private static void Check(bool condition, string detail)
        { assertions++; if (!condition) throw new InvalidOperationException(detail); }
        private static object Invoke(string name, params object[] arguments)
        {
            try { return typeof(PrivacyNativeBackend).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private static string Configuration(string binary, string startup)
        {
            string result = "svc1|";
            foreach (string part in new[] { "32", startup, binary, "LocalSystem" })
                result += part.Length + ":" + part + "|";
            return result;
        }
        private static void CheckService(string name)
        {
            string identity = PrivacyNativeBackend.B64(name + "-fixture");
            string on = "v:4:" + identity, off = "v:1:" + identity;
            string manual = "c:3:" + identity, disabled = "c:4:" + identity;
            values.Clear(); values[name] = on; values[name + ".Start"] = manual;
            writes.Clear();
            string action = name == "DiagTrack" ? PrivacyActionCatalog.Service : PrivacyActionCatalog.Compatibility;
            var backend = new PrivacyNativeBackend();
            int beforeInspection = reads;
            PrivacyPlan retired = backend.Inspect(action, "");
            Check(!retired.Supported && retired.UnavailableReason == "retired" && retired.Targets.Length == 0
                && reads == beforeInspection, "Retired service inspection reached native discovery: " + name);
            Check(!backend.Write(action, "ctx|11111111111111111111111111111111|system|svc|" + name, off)
                && reads == beforeInspection && writes.Count == 0, "Retired public service write reached restoration helpers: " + name);
            // Private primitives remain exercised because receipt restoration still uses them.
            Invoke("ValidateServiceName", name);
            Check((string)Invoke("ServiceRead", name) == on && (string)Invoke("ServiceStartRead", name) == manual,
                "Allowed service does not reach its isolated reader: " + name);
            Check((bool)Invoke("ServiceWrite", name, on) && (bool)Invoke("ServiceStartWrite", name, manual)
                && writes.Count == 0, "Settled service state was written again: " + name);
            Check((bool)Invoke("ServiceWrite", name, off) && (bool)Invoke("ServiceStartWrite", name, disabled),
                "Service stop and startup disable failed: " + name);
            Check((bool)Invoke("ServiceStartWrite", name, manual) && (bool)Invoke("ServiceWrite", name, on),
                "Service original state did not restore: " + name);
            Check(values[name] == on && values[name + ".Start"] == manual
                && string.Join(",", writes.ToArray()) == name + "," + name + ".Start," + name + ".Start," + name,
                "Service writes escaped their exact target: " + name);
            writes.Clear();
            string replaced = PrivacyNativeBackend.B64(name + "-replacement");
            values[name] = "v:4:" + replaced; values[name + ".Start"] = "c:3:" + replaced;
            Check(!(bool)Invoke("ServiceWrite", name, off) && !(bool)Invoke("ServiceStartWrite", name, disabled)
                && writes.Count == 0, "Service replacement admitted an old identity: " + name);
            values[name] = "missing"; values[name + ".Start"] = "missing";
            Check((string)Invoke("ServiceRead", name) == "missing" && (string)Invoke("ServiceStartRead", name) == "missing",
                "Missing service was represented as configured: " + name);
            Check(!(bool)Invoke("ServiceWrite", name, off) && !(bool)Invoke("ServiceStartWrite", name, disabled)
                && writes.Count == 0, "Missing service was written or created: " + name);
            values[name] = on; values[name + ".Start"] = manual;
            foreach (string bad in new[] { "", "v:0:" + identity, "v:7:" + identity, "c:4:" + identity })
                Check(!(bool)Invoke("ServiceWrite", name, bad) && writes.Count == 0, "Invalid runtime state admitted: " + name);
            foreach (string bad in new[] { "", "c:5:" + identity, "c:-1:" + identity, "v:1:" + identity })
                Check(!(bool)Invoke("ServiceStartWrite", name, bad) && writes.Count == 0, "Invalid startup state admitted: " + name);
        }
        internal static int Run()
        {
            assertions = reads = 0; values.Clear(); writes.Clear();
            try
            {
                PrivacyNativeBackend.ServiceReadForTest = delegate(string name) {
                    reads++; string value; return values.TryGetValue(name, out value) ? value : "missing";
                };
                PrivacyNativeBackend.ServiceWriteForTest = delegate(string name, string value) {
                    writes.Add(name); values[name] = value; return true;
                };
                CheckService("DiagTrack"); CheckService("PcaSvc");
                foreach (string invalid in new[] { null, "", "DPS", "Spooler", "PcaSvc.Start", "PcaSvc ", "PcaSvc\\Other" })
                foreach (string method in new[] { "ValidateServiceName", "ServiceRead", "ServiceStartRead", "ServiceWrite", "ServiceStartWrite" })
                {
                    int before = reads, priorWrites = writes.Count; bool rejected = false;
                    try
                    {
                        if (method == "ServiceWrite") Invoke(method, invalid, "v:1:" + PrivacyNativeBackend.B64("fixture"));
                        else if (method == "ServiceStartWrite") Invoke(method, invalid, "c:4:" + PrivacyNativeBackend.B64("fixture"));
                        else Invoke(method, new object[] { invalid });
                    }
                    catch (ArgumentException) { rejected = true; }
                    Check(rejected && reads == before && writes.Count == priorWrites, "Unknown service reached the backend: " + method);
                }
                string original = (string)Invoke("ServiceIdentity", Configuration("trusted.exe", "3"));
                Check(original == (string)Invoke("ServiceIdentity", Configuration("trusted.exe", "4")), "Startup change invalidated separately owned identity");
                Check(original != (string)Invoke("ServiceIdentity", Configuration("replaced.exe", "3")), "Service binary replacement preserved identity");
                return assertions;
            }
            finally { PrivacyNativeBackend.ServiceReadForTest = null; PrivacyNativeBackend.ServiceWriteForTest = null; }
        }
    }
}
#endif
