using System;
using System.Threading;

namespace PaviseApp
{
    internal static class MainInstanceBootstrapChecks
    {
        private static int assertions;
        private static void Check(bool result, string description)
        { assertions++; if (!result) throw new Exception("FAIL: " + description); }
        private static void Refuses(Action action, string description)
        {
            bool refused = false;
            try { action(); }
            catch (UnauthorizedAccessException) { refused = true; }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, description);
        }
        private static int Main()
        {
            try
            {
                Check(Program.CurrentMainInstanceBootstrap == null, "No proof is minted by reading the entrypoint");
                using (var mutex = new Mutex(true))
                {
                    Refuses(delegate { new Program.MainInstanceBootstrapLease(mutex, new object()); }, "An arbitrary mutex and caller-supplied witness cannot mint a proof");
                    using (var lease = Program.AcquireMainInstanceBootstrapForTest(mutex))
                    {
                        Check(object.ReferenceEquals(lease, Program.CurrentMainInstanceBootstrap), "The original successful acquisition publishes its exact capability");
                        lease.RequireCurrent(); Check(true, "The owning Main thread can attest the retained original handle");
                        lease.ClaimBootstrap(); lease.RequireCurrent();
                        Check(true, "A claimed proof remains verifiable throughout the single bootstrap attempt");
                        Refuses(lease.ClaimBootstrap, "A second bootstrap attempt cannot consume the original capability");
                        Exception threadFailure = null;
                        var thread = new Thread(delegate() { try { lease.RequireCurrent(); } catch (Exception error) { threadFailure = error; } });
                        thread.Start(); Check(thread.Join(3000), "Foreign-thread refusal completes without reacquiring a mutex");
                        Check(threadFailure is UnauthorizedAccessException, "A worker cannot borrow Main's mutex ownership");
                        Refuses(delegate { using (var ignored = Program.AcquireMainInstanceBootstrapForTest(mutex)) { } }, "A second mint cannot replace the original published capability");
                        Check(object.ReferenceEquals(lease, Program.CurrentMainInstanceBootstrap), "A rejected second mint cannot revoke the real owner");
                        lease.Dispose();
                        Check(Program.CurrentMainInstanceBootstrap == null, "Revocation clears the public capability before native release");
                        Refuses(lease.RequireCurrent, "A saved reference cannot outlive explicit revocation");
                    }
                    mutex.ReleaseMutex();
                }
                using (var mutex = new Mutex(true))
                using (var lease = Program.AcquireMainInstanceBootstrapForTest(mutex))
                {
                    mutex.Close();
                    Refuses(lease.RequireCurrent, "Keeping a SafeHandle reference cannot hide that Main closed its original mutex");
                }
                using (var mutex = new Mutex(true))
                using (var lease = Program.AcquireMainInstanceBootstrapForTest(mutex))
                {
                    var original = mutex.SafeWaitHandle;
                    var substitute = new Microsoft.Win32.SafeHandles.SafeWaitHandle(original.DangerousGetHandle(), false);
                    try
                    {
                        mutex.SafeWaitHandle = substitute;
                        Refuses(lease.RequireCurrent, "Even the same numeric handle in a substituted wrapper is not the original capability");
                    }
                    finally { mutex.SafeWaitHandle = original; substitute.Dispose(); }
                    lease.RequireCurrent();
                    Check(true, "The exact retained wrapper remains usable after the isolated replacement check");
                    lease.Dispose(); mutex.ReleaseMutex();
                }
                Check(Program.CurrentMainInstanceBootstrap == null, "All test-owned capabilities are revoked without touching a product mutex");
                Console.WriteLine("PASS main-instance bootstrap: " + assertions + " assertions; product_mutex=false; main=false; native_settings=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
