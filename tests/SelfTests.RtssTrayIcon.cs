#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int rtssTrayAssertions;
        private static int rtssTrayCases;
        private const string RtssTrayPath = @"C:\PaviseTrayFixture\RTSS.exe";
        private const string RtssTrayOtherPath = @"C:\PaviseTrayFixtureOther\RTSS.exe";

        internal static void RunRtssTrayIconTests()
        {
            rtssTrayAssertions = 0;
            rtssTrayCases = 0;
            RtssTrayRejectsInvalidComponents();
            RtssTrayPreservesUserInstallation();
            RtssTrayHidesOnceAndRefreshes();
            RtssTrayRequiresExactTarget();
            RtssTrayRequiresExistingIcon();
            RtssTrayDoesNotOwnFailedHide();
            RtssTrayDiscardsReusedProcess();
            RtssTrayDiscardsReplacedWindow();
            RtssTrayDiscardsDeadProcess();
            RtssTrayRehidesAfterExplorerRestart();
            RtssTrayRetriesFailedRestore();
            RtssTrayRetainsOnlyFailedRestores();
            RtssTrayKeepsOldPathHidden();
            RtssTrayPathChangeDoesNotRequireRestore();
            RtssTrayForgetDropsOwnership();
            RtssTrayValidatesIconRect();
            RtssTrayLifecycleStartsOnlyOnHide();
            RtssTrayLifecycleUsesExistingTick();
            RtssTrayLifecycleRefreshesShellOnce();
            RtssTrayLifecycleCancelsOnRestore();
            RtssTrayLifecycleCancelsOnForget();
            RtssTrayLifecycleSeparatesGenerations();
            RtssTrayLifecycleBoundsFailureRetries();
            RtssTrayLifecycleExplicitHideResetsRetries();
            RtssTrayHidesOnlyOwnedWindows();
            RtssTrayGuardsWindowsWithoutExplorer();
            RtssTrayRehidesWindowsWhileIconIsCached();
            RtssTrayRetriesFailedWindowHide();
            RtssTrayLifecycleRecoversDelayedProcess();
            RtssTrayLifecycleBacksOffRejectedQueue();
            RtssTrayLifecycleRecoversShellFailure();
        }

        private static void RtssTrayRejectsInvalidComponents()
        {
            rtssTrayCases++;
            var backend = new RtssTrayFakeBackend();
            backend.Add(RtssTrayTarget(41, 101, 501, RtssTrayPath));
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(!controller.Hide(null), "Null component is rejected");
            RtssTrayAssert(!controller.Hide(new RtssComponentInfo { Path = RtssTrayPath }), "Uninstalled component is rejected");
            var installing = RtssTrayComponent(RtssTrayPath);
            installing.InstallationInProgress = true;
            RtssTrayAssert(!controller.Hide(installing), "Installation in progress is rejected");
            var uninstalling = RtssTrayComponent(RtssTrayPath);
            uninstalling.UninstallationInProgress = true;
            RtssTrayAssert(!controller.Hide(uninstalling), "Uninstallation in progress is rejected");
            foreach (string path in new[] { "", "RTSS.exe", @"C:\PaviseTrayFixture\other.exe" })
                RtssTrayAssert(!controller.Hide(RtssTrayComponent(path)), "Invalid component path is rejected: " + path);
            RtssTrayAssert(backend.FindCalls == 0 && backend.Mutations.Count == 0,
                "Invalid components never enumerate or mutate tray targets");
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 0, "Empty ownership restores without mutation");
        }

        private static void RtssTrayPreservesUserInstallation()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var controller = new RtssTrayIconController(backend);
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(controller, backend, queue.Add);
            var user = RtssTrayComponent(RtssTrayPath);
            user.InstalledByPavise = false;
            RtssTrayAssert(!controller.Hide(user) && !lifecycle.Hide(user), "Pre-existing/user RTSS is never hidden");
            backend.Time = 61000; lifecycle.Maintain();
            RtssTrayAssert(backend.Operations == 0 && queue.AddCalls == 0,
                "User RTSS never probes tray, hides windows, installs watchers or queues maintenance");
            RtssTrayAssert(lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Pavise-installed RTSS retains managed behavior");
            backend.Time += 30000; lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1, "Managed refresh queued before ownership changes");
            RtssTrayAssert(!lifecycle.Hide(user), "Changed ownership cancels managed hiding");
            RtssTrayAssert(backend.Mutations.Count == 2 && !backend.Mutations[1].Hidden && backend.Watched.Count == 0,
                "Only previously owned icon is restored and window watchers stop");
            int operations = backend.Operations;
            queue.RunNext(); backend.Time += 60000; lifecycle.Maintain();
            RtssTrayAssert(backend.Operations == operations && queue.AddCalls == 1,
                "Stale queued refresh cannot hide a user installation");
        }

        private static void RtssTrayHidesOnceAndRefreshes()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "First hide succeeds");
            RtssTrayAssert(backend.Mutations.Count == 1 && backend.Mutations[0].Hidden, "First hide changes one icon");
            RtssTrayAssert(backend.HasIconCalls == 1, "First hide confirms the icon exists");
            backend.Time = 30999;
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Cached ownership remains successful");
            RtssTrayAssert(backend.Mutations.Count == 1, "Stable icon is not rewritten before 30 seconds");
            backend.Time = 31000;
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Periodic refresh succeeds at 30 seconds");
            RtssTrayAssert(backend.Mutations.Count == 2 && backend.Mutations[1].Hidden, "Expired cache refreshes the icon");
            RtssTrayAssert(backend.FindCalls == 3, "Every hide discovers current process and window targets");
            RtssTrayAssert(controller.Restore(), "Owned icon restores");
            RtssTrayAssert(backend.Mutations.Count == 3 && !backend.Mutations[2].Hidden, "Restore clears hidden state once");
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 3, "Repeated restore is idempotent");
        }

        private static void RtssTrayRequiresExactTarget()
        {
            rtssTrayCases++;
            var backend = new RtssTrayFakeBackend();
            backend.Add(RtssTrayTarget(42, 102, 502, RtssTrayOtherPath));
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(!controller.Hide(RtssTrayComponent(RtssTrayPath)), "Other installation cannot be hidden");
            RtssTrayAssert(backend.Mutations.Count == 0, "Backend discovery cannot authorize a different executable path");
            backend = RtssTrayBackend();
            backend.MatchAllowed = false;
            controller = new RtssTrayIconController(backend);
            RtssTrayAssert(!controller.Hide(RtssTrayComponent(RtssTrayPath)), "Stale target cannot be hidden");
            RtssTrayAssert(backend.Mutations.Count == 0, "Failed process identity check prevents mutation");
            backend = RtssTrayBackend();
            controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(@"C:\PaviseTrayFixture\..\PaviseTrayFixture\RTSS.exe")),
                "Equivalent absolute component path is normalized");
            RtssTrayAssert(String.Equals(backend.FindPaths[0], RtssTrayPath, StringComparison.OrdinalIgnoreCase),
                "Discovery receives the normalized exact executable path");
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath.ToUpperInvariant())),
                "Path case differences keep the same ownership");
            RtssTrayAssert(backend.Mutations.Count == 1, "Equivalent paths do not restore and re-hide the icon");
        }

        private static void RtssTrayRequiresExistingIcon()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.IconExists = false;
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(!controller.Hide(RtssTrayComponent(RtssTrayPath)), "Missing icon cannot acquire ownership");
            RtssTrayAssert(backend.HasIconCalls == 1 && backend.Mutations.Count == 0, "Missing icon is not modified");
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 0, "Missing icon is never restored");
            backend.IconExists = true;
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)) && backend.Mutations.Count == 1,
                "Late icon creation is discovered on a later hide");
        }

        private static void RtssTrayDoesNotOwnFailedHide()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.HideSucceeds = false;
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(!controller.Hide(RtssTrayComponent(RtssTrayPath)), "Failed native hide reports failure");
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 1,
                "Failed hide does not grant restore ownership");
            backend.HideSucceeds = true;
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Failed hide is retried without waiting for cache expiry");
            RtssTrayAssert(backend.Mutations.Count == 2 && backend.HasIconCalls == 2, "Retry confirms icon and attempts hide again");
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 3, "Successful retry acquires ownership");
        }

        private static void RtssTrayDiscardsReusedProcess()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Original process icon hides");
            backend.ClearTargets();
            backend.Add(RtssTrayTarget(41, 202, 501, RtssTrayPath));
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Reused PID with new birth acquires new ownership");
            RtssTrayAssert(backend.Mutations.Count == 2 && backend.Mutations[1].Hidden && backend.Mutations[1].Birth == 202,
                "Reused PID is hidden afresh without restoring the prior process");
            RtssTrayAssert(controller.Restore(), "Replacement process icon restores");
            RtssTrayAssert(backend.Mutations.Count == 3 && backend.Mutations[2].Birth == 202 && !backend.Mutations[2].Hidden,
                "Only the replacement process identity is restored");
        }

        private static void RtssTrayDiscardsReplacedWindow()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Original window icon hides");
            backend.ClearTargets();
            backend.Add(RtssTrayTarget(41, 101, 601, RtssTrayPath));
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "New owner window acquires new ownership");
            RtssTrayAssert(backend.Mutations.Count == 2 && backend.Mutations[1].Window == new IntPtr(601),
                "Changed window handle bypasses the old cache");
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 3 && backend.Mutations[2].Window == new IntPtr(601),
                "Only the replacement window is restored");
        }

        private static void RtssTrayDiscardsDeadProcess()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Live icon acquires ownership");
            backend.ClearTargets();
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 1, "Restore drops dead process ownership without mutation");
            backend.Add(RtssTrayTarget(41, 202, 501, RtssTrayPath));
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 1, "PID reuse cannot revive dropped ownership");
        }

        private static void RtssTrayRehidesAfterExplorerRestart()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Icon hides before shell restart");
            backend.Shell = 2;
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Shell identity change triggers immediate refresh");
            RtssTrayAssert(backend.Mutations.Count == 2 && backend.Mutations[1].Hidden, "Explorer recreation bypasses time cache");
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)) && backend.Mutations.Count == 2,
                "Refreshed shell identity is cached");
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 3, "Shell refresh retains one restore owner");
        }

        private static void RtssTrayRetriesFailedRestore()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Icon hides before restore failure");
            backend.RestoreSucceeds = false;
            RtssTrayAssert(!controller.Restore(), "Failed restore reports failure");
            RtssTrayAssert(backend.Mutations.Count == 2 && !backend.Mutations[1].Hidden, "Failed restore attempts to clear hidden state");
            backend.RestoreSucceeds = true;
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 3, "Failed restore retains ownership for retry");
            RtssTrayAssert(!backend.Mutations[2].Hidden, "Restore retry does not re-hide the icon");
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 3, "Successful restore releases ownership");
        }

        private static void RtssTrayRetainsOnlyFailedRestores()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.Add(RtssTrayTarget(42, 102, 502, RtssTrayPath));
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)) && backend.Mutations.Count == 2,
                "Multiple verified icons can acquire ownership");
            backend.FailedRestorePid = 42;
            RtssTrayAssert(!controller.Restore() && backend.Mutations.Count == 4, "Partial restore attempts every owned target");
            backend.FailedRestorePid = 0;
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 5, "Only failed restore remains queued");
            RtssTrayAssert(backend.Mutations[4].Pid == 42 && !backend.Mutations[4].Hidden,
                "Already restored target is not modified again");
        }

        private static void RtssTrayKeepsOldPathHidden()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.Add(RtssTrayTarget(42, 102, 502, RtssTrayOtherPath));
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)) && backend.Mutations.Count == 1,
                "Initial path owns only its exact icon");
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayOtherPath)), "Changing install path starts background management for the new path");
            RtssTrayAssert(backend.Mutations.Count == 2 && backend.Mutations[1].Hidden && backend.Mutations[1].Pid == 42,
                "Old installation stays hidden when the new installation is adopted");
            RtssTrayAssert(backend.Watched.Count == 1 && backend.Watched[0].Pid == 42,
                "Path switch removes the previous installation's event listener");
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 3 && backend.Mutations[2].Pid == 42,
                "Path switch transfers ownership to the new installation");
        }

        private static void RtssTrayPathChangeDoesNotRequireRestore()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.Add(RtssTrayTarget(42, 102, 502, RtssTrayOtherPath));
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Initial install icon hides");
            backend.RestoreSucceeds = false;
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayOtherPath)), "Path switch never requires showing the old installation's icon");
            RtssTrayAssert(backend.Mutations.Count == 2 && backend.Mutations[1].Pid == 42 && backend.Mutations[1].Hidden,
                "Failed restoration capability cannot block a new background installation");
            backend.ClearTargets();
            RtssTrayAssert(!controller.Hide(RtssTrayComponent(RtssTrayOtherPath)), "An exited RTSS process remains eligible for later retry");
            RtssTrayAssert(backend.Watched.Count == 0, "Maintenance releases event listener threads when RTSS exits without restarting");
        }

        private static void RtssTrayForgetDropsOwnership()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Icon hides before ownership is forgotten");
            int operations = backend.Operations;
            controller.Forget();
            RtssTrayAssert(backend.Operations == operations + 1 && backend.Watched.Count == 0,
                "Forget only stops event listeners without restoring the tray or windows");
            RtssTrayAssert(controller.Restore() && backend.Mutations.Count == 1, "Forgotten target is never restored");
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)) && backend.Mutations.Count == 2,
                "A later hide acquires fresh ownership after Forget");
        }

        private static void RtssTrayValidatesIconRect()
        {
            rtssTrayCases++;
            RtssTrayAssert(RtssTrayIconController.ValidIconRect(0, 1, 2, 17, 18), "S_OK with positive rectangle confirms icon");
            RtssTrayAssert(RtssTrayIconController.ValidIconRect(1, -20, -10, -4, 6), "Nonnegative HRESULT and offscreen rectangle are accepted");
            RtssTrayAssert(!RtssTrayIconController.ValidIconRect(unchecked((int)0x80004005), 1, 2, 17, 18),
                "Failed HRESULT never confirms an icon");
            RtssTrayAssert(!RtssTrayIconController.ValidIconRect(0, 1, 2, 1, 18), "Zero-width rectangle is rejected");
            RtssTrayAssert(!RtssTrayIconController.ValidIconRect(0, 1, 2, 17, 2), "Zero-height rectangle is rejected");
            RtssTrayAssert(!RtssTrayIconController.ValidIconRect(0, 17, 18, 1, 2), "Inverted rectangle is rejected");
        }

        private static void RtssTrayLifecycleStartsOnlyOnHide()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            lifecycle.Maintain();
            backend.Time = 100000;
            backend.Shell = 2;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 0 && backend.FindCalls == 0 && backend.Mutations.Count == 0,
                "Maintenance before explicit Hide never discovers or changes icons");
            RtssTrayAssert(lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Explicit Hide runs immediately");
            RtssTrayAssert(queue.Pending.Count == 0 && backend.FindCalls == 1 && backend.Mutations.Count == 1,
                "Initial hide does not wait for a queued worker");
        }

        private static void RtssTrayLifecycleUsesExistingTick()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            RtssTrayAssert(lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Lifecycle acquires initial icon");
            for (int i = 0; i < 20; i++)
            {
                backend.Time = 1000 + i * 1000;
                lifecycle.Maintain();
            }
            backend.Time = 30999;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 0 && backend.FindCalls == 1 && backend.Mutations.Count == 1,
                "Ticks before 30 seconds perform no enumeration or icon mutation");
            backend.Time = 31000;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1 && backend.FindCalls == 1 && backend.Mutations.Count == 1,
                "Due tick queues work instead of enumerating inline");
            lifecycle.Maintain();
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1, "Multiple due ticks keep a single worker pending");
            queue.RunNext();
            RtssTrayAssert(backend.FindCalls == 2 && backend.Mutations.Count == 2,
                "Queued worker refreshes once when it executes");
            backend.Time = 60999;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 0, "Successful worker starts the next 30 second interval");
            backend.Time = 61000;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1, "Next refresh becomes eligible at its deadline");
        }

        private static void RtssTrayLifecycleRefreshesShellOnce()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            RtssTrayAssert(lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Lifecycle hides before Explorer changes");
            backend.Shell = 2;
            lifecycle.Maintain();
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1 && backend.FindCalls == 1,
                "New shell queues one immediate refresh before the normal deadline");
            queue.RunNext();
            RtssTrayAssert(backend.FindCalls == 2 && backend.Mutations.Count == 2,
                "Shell refresh worker reapplies hidden state");
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 0, "Refreshed shell does not queue repeatedly");
        }

        private static void RtssTrayLifecycleCancelsOnRestore()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            RtssTrayAssert(lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Lifecycle hides before restore cancellation");
            backend.Time = 31000;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1, "Refresh is queued before restore");
            RtssTrayAssert(lifecycle.Restore(), "Restore immediately releases owned icon");
            RtssTrayAssert(backend.Mutations.Count == 2 && !backend.Mutations[1].Hidden,
                "Restore clears hidden state before queued work runs");
            int operations = backend.Operations;
            queue.RunNext();
            RtssTrayAssert(backend.Operations == operations, "Worker queued before Restore is a complete no-op");
            backend.Time = 100000;
            backend.Shell = 2;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 0 && backend.Mutations.Count == 2,
                "Restore disables later deadline and shell-triggered maintenance");
        }

        private static void RtssTrayLifecycleCancelsOnForget()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            RtssTrayAssert(lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Lifecycle hides before forgetting");
            backend.Time = 31000;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1, "Refresh is queued before Forget");
            int operations = backend.Operations;
            lifecycle.Forget();
            RtssTrayAssert(backend.Operations == operations + 1 && backend.Watched.Count == 0,
                "Forget cancels native event listeners without showing windows or icons");
            operations = backend.Operations;
            queue.RunNext();
            RtssTrayAssert(backend.Operations == operations, "Stale worker after Forget performs no backend operations");
            backend.Time = 100000;
            backend.Shell = 2;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 0 && backend.Mutations.Count == 1,
                "Forget disables maintenance without restoring the icon");
            RtssTrayAssert(lifecycle.Restore() && backend.Mutations.Count == 1,
                "Forgotten lifecycle has no icon to restore");
        }

        private static void RtssTrayLifecycleSeparatesGenerations()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.Add(RtssTrayTarget(42, 102, 502, RtssTrayOtherPath));
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            RtssTrayAssert(lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "First generation hides its target");
            backend.Time = 31000;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1, "First generation has a pending refresh");
            lifecycle.Forget();
            RtssTrayAssert(lifecycle.Hide(RtssTrayComponent(RtssTrayOtherPath)), "New explicit hide enables a fresh generation");
            backend.Time = 61000;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 2, "New generation can queue independently from the stale callback");
            int operations = backend.Operations;
            queue.RunNext();
            RtssTrayAssert(backend.Operations == operations, "Old callback cannot inspect or hide the old target");
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1, "Old callback cannot clear the new generation's single-flight guard");
            queue.RunNext();
            RtssTrayAssert(backend.Mutations.Count == 3 && backend.Mutations[2].Hidden && backend.Mutations[2].Pid == 42,
                "Current generation refreshes only its new target");
            RtssTrayAssert(lifecycle.Restore() && backend.Mutations.Count == 4 && backend.Mutations[3].Pid == 42,
                "Current generation retains its restore ownership");
        }

        private static void RtssTrayLifecycleBoundsFailureRetries()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.IconExists = false;
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            RtssTrayAssert(!lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Initial missing icon reports failure");
            for (int retry = 1; retry <= 5; retry++)
            {
                backend.Time = 1000 + retry * 2000 - 1;
                lifecycle.Maintain();
                RtssTrayAssert(queue.Pending.Count == 0, "Failed hide does not retry before its two second deadline");
                backend.Time++;
                lifecycle.Maintain();
                lifecycle.Maintain();
                RtssTrayAssert(queue.Pending.Count == 1, "Failure retry remains single-flight: " + retry);
                queue.RunNext();
                RtssTrayAssert(backend.FindCalls == retry + 1, "Failure retry enumerates exactly once: " + retry);
            }
            backend.Time = 40999;
            lifecycle.Maintain();
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 0 && backend.FindCalls == 6 && backend.Mutations.Count == 0,
                "Five fast failures switch to a 30 second retry interval");
            backend.Time = 41000;
            backend.IconExists = true;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1, "Late icon is retried without requiring an Explorer restart");
            queue.RunNext();
            RtssTrayAssert(backend.FindCalls == 7 && backend.Mutations.Count == 1,
                "Maintenance recovers a late icon after the fast retry budget is exhausted");
        }

        private static void RtssTrayLifecycleExplicitHideResetsRetries()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.IconExists = false;
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            RtssTrayAssert(!lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Initial explicit hide can fail");
            for (int retry = 1; retry <= 5; retry++)
            {
                backend.Time = 1000 + retry * 2000;
                lifecycle.Maintain();
                queue.RunNext();
            }
            backend.Time = 13000;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 0, "Initial retry budget is exhausted");
            RtssTrayAssert(!lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "New explicit hide attempts immediately after exhaustion");
            backend.Time = 15000;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 1, "New explicit hide grants a fresh retry budget");
            backend.IconExists = true;
            queue.RunNext();
            RtssTrayAssert(backend.FindCalls == 8 && backend.Mutations.Count == 1, "Fresh retry can acquire the late icon");
            backend.Time = 17000;
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 0, "Successful recovery stops fast failure retries");
        }

        private static void RtssTrayHidesOnlyOwnedWindows()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.Windows.Add(RtssTrayTarget(41, 101, 501, RtssTrayPath));
            backend.Windows.Add(RtssTrayTarget(41, 101, 601, RtssTrayPath));
            backend.Windows.Add(RtssTrayTarget(42, 101, 602, RtssTrayPath));
            backend.Windows.Add(RtssTrayTarget(41, 202, 603, RtssTrayPath));
            backend.Windows.Add(RtssTrayTarget(41, 101, 604, RtssTrayOtherPath));
            backend.Windows.Add(RtssTrayTarget(41, 101, 605, @"C:\Games\Game.exe"));
            backend.Windows.Add(RtssTrayTarget(41, 101, 0, RtssTrayPath));
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Owned main window and dialog can be hidden");
            RtssTrayAssert(backend.WindowMutations.Count == 2 && backend.WindowMutations[0].Window == new IntPtr(501)
                && backend.WindowMutations[1].Window == new IntPtr(601),
                "Window hiding requires the owner's exact executable path, PID, birth and a valid HWND");
            RtssTrayAssert(backend.Watched.Count == 1 && backend.Watched[0].Pid == 41 && backend.Watched[0].Birth == 101,
                "Only the verified RTSS owner receives a process-scoped window listener");
            backend.MatchAllowed = false;
            int hides = backend.WindowMutations.Count, watches = backend.WatchCalls;
            RtssTrayAssert(!controller.Hide(RtssTrayComponent(RtssTrayPath)), "Stale process identity rejects a later hide");
            RtssTrayAssert(backend.WindowMutations.Count == hides && backend.WatchCalls == watches,
                "A stale owner cannot hide windows or register another listener");
        }

        private static void RtssTrayGuardsWindowsWithoutExplorer()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.Shell = 0;
            backend.IconExists = false;
            backend.Windows.Add(RtssTrayTarget(41, 101, 601, RtssTrayPath));
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(!controller.Hide(RtssTrayComponent(RtssTrayPath)), "Unavailable Explorer keeps tray work pending");
            RtssTrayAssert(backend.WindowMutations.Count == 1 && backend.Watched.Count == 1,
                "Window hiding and event registration do not require a taskbar or an existing icon");
            RtssTrayAssert(backend.Mutations.Count == 0 && backend.HasIconCalls == 0, "Missing shell receives no notification operations");
        }

        private static void RtssTrayRehidesWindowsWhileIconIsCached()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Icon is acquired before a configuration dialog opens");
            backend.Time = 2000;
            backend.Windows.Add(RtssTrayTarget(41, 101, 601, RtssTrayPath));
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Visible dialog hides even inside the icon cache interval");
            RtssTrayAssert(backend.Mutations.Count == 1 && backend.WindowMutations.Count == 1,
                "Tray cache never skips window handling and does not cause a redundant icon write");
            RtssTrayAssert(controller.Restore() && backend.Watched.Count == 0 && backend.WindowMutations.Count == 1,
                "Explicit tray restoration detaches listeners without reopening any configuration window");
        }

        private static void RtssTrayRetriesFailedWindowHide()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.Windows.Add(RtssTrayTarget(41, 101, 601, RtssTrayPath));
            backend.WindowHideSucceeds = false;
            var controller = new RtssTrayIconController(backend);
            RtssTrayAssert(!controller.Hide(RtssTrayComponent(RtssTrayPath)), "Window hide failure is reported despite a successful tray hide");
            backend.WindowHideSucceeds = true;
            RtssTrayAssert(controller.Hide(RtssTrayComponent(RtssTrayPath)), "Window hide failure can recover on the next admitted attempt");
            RtssTrayAssert(backend.WindowMutations.Count == 2 && backend.Mutations.Count == 1,
                "Window retry leaves the already hidden tray icon untouched");
            int hides = backend.WindowMutations.Count;
            controller.Forget();
            RtssTrayAssert(backend.Watched.Count == 0 && backend.WindowMutations.Count == hides && backend.Mutations.Count == 1,
                "Shutdown drops listeners while leaving RTSS in the background");
        }

        private static void RtssTrayLifecycleRecoversDelayedProcess()
        {
            rtssTrayCases++;
            var backend = new RtssTrayFakeBackend();
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            RtssTrayAssert(!lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Missing RTSS process leaves background management pending");
            for (int retry = 1; retry <= 5; retry++)
            {
                backend.Time = 1000 + retry * 2000;
                lifecycle.Maintain(); queue.RunNext();
            }
            backend.Time = 41000;
            lifecycle.Maintain(); queue.RunNext();
            backend.Time = 70999;
            backend.Add(RtssTrayTarget(41, 101, 501, RtssTrayPath));
            backend.Windows.Add(RtssTrayTarget(41, 101, 601, RtssTrayPath));
            lifecycle.Maintain();
            RtssTrayAssert(queue.Pending.Count == 0, "Continued missing-process retries remain at the slow interval");
            backend.Time++;
            lifecycle.Maintain(); queue.RunNext();
            RtssTrayAssert(backend.Mutations.Count == 1 && backend.WindowMutations.Count == 1 && backend.Watched.Count == 1,
                "RTSS starting over a minute later is hidden and receives a listener without toggling the feature");
        }

        private static void RtssTrayLifecycleBacksOffRejectedQueue()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            RtssTrayAssert(lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Lifecycle starts before worker queue rejection");
            queue.Accept = false;
            backend.Time = 31000;
            lifecycle.Maintain();
            for (int i = 0; i < 10; i++) { backend.Time++; lifecycle.Maintain(); }
            RtssTrayAssert(queue.AddCalls == 1 && queue.Pending.Count == 0,
                "Rejected background work does not retry on every application tick");
            queue.Accept = true;
            backend.Time = 61000;
            lifecycle.Maintain(); queue.RunNext();
            RtssTrayAssert(backend.Mutations.Count == 2, "Worker queue recovery resumes background management");
        }

        private static void RtssTrayLifecycleRecoversShellFailure()
        {
            rtssTrayCases++;
            var backend = RtssTrayBackend();
            backend.ThrowShell = true;
            var queue = new RtssTrayFakeQueue();
            var lifecycle = new RtssTrayIconLifecycle(new RtssTrayIconController(backend), backend, queue.Add);
            RtssTrayAssert(!lifecycle.Hide(RtssTrayComponent(RtssTrayPath)), "Shell query failure is contained");
            backend.ThrowShell = false;
            backend.Time = 31000;
            lifecycle.Maintain(); queue.RunNext();
            RtssTrayAssert(backend.Mutations.Count == 1 && backend.Watched.Count == 1,
                "Shell query exception does not permanently stop later window and tray management");
        }

        private static RtssComponentInfo RtssTrayComponent(string path)
        { return new RtssComponentInfo { Installed = true, InstalledByPavise = true, Running = true, Path = path, Version = "7.3.7" }; }

        private static RtssTrayIconTarget RtssTrayTarget(int pid, long birth, int window, string path)
        { return new RtssTrayIconTarget { Pid = pid, Birth = birth, Window = new IntPtr(window), Path = path }; }

        private static RtssTrayFakeBackend RtssTrayBackend()
        {
            var backend = new RtssTrayFakeBackend();
            backend.Add(RtssTrayTarget(41, 101, 501, RtssTrayPath));
            return backend;
        }

        private static void RtssTrayAssert(bool condition, string message)
        {
            rtssTrayAssertions++;
            if (!condition) throw new InvalidOperationException("RTSS tray: " + message);
        }

        private sealed class RtssTrayMutation
        {
            internal int Pid;
            internal long Birth;
            internal IntPtr Window;
            internal bool Hidden;
        }

        private sealed class RtssTrayFakeQueue
        {
            internal readonly List<Action> Pending = new List<Action>();
            internal bool Accept = true;
            internal int AddCalls;
            internal bool Add(Action action) { AddCalls++; if (!Accept) return false; Pending.Add(action); return true; }
            internal void RunNext()
            {
                if (Pending.Count == 0) throw new InvalidOperationException("RTSS tray test has no queued worker");
                Action action = Pending[0];
                Pending.RemoveAt(0);
                action();
            }
        }

        private sealed class RtssTrayFakeBackend : IRtssTrayIconBackend
        {
            internal long Time = 1000;
            internal long Shell = 1;
            internal bool MatchAllowed = true;
            internal bool IconExists = true;
            internal bool HideSucceeds = true;
            internal bool RestoreSucceeds = true;
            internal bool WindowHideSucceeds = true;
            internal bool ThrowShell;
            internal int FailedRestorePid;
            internal int Operations;
            internal int FindCalls;
            internal int HasIconCalls;
            internal int WatchCalls;
            internal readonly List<RtssTrayIconTarget> Targets = new List<RtssTrayIconTarget>();
            internal readonly List<RtssTrayIconTarget> Windows = new List<RtssTrayIconTarget>();
            internal readonly List<RtssTrayIconTarget> Watched = new List<RtssTrayIconTarget>();
            internal readonly List<RtssTrayMutation> Mutations = new List<RtssTrayMutation>();
            internal readonly List<RtssTrayMutation> WindowMutations = new List<RtssTrayMutation>();
            internal readonly List<string> FindPaths = new List<string>();

            public long Clock { get { Operations++; return Time; } }
            public long ShellIdentity { get { Operations++; if (ThrowShell) throw new InvalidOperationException("Fake shell failure"); return Shell; } }
            internal void Add(RtssTrayIconTarget target) { Targets.Add(target); }
            internal void ClearTargets() { Targets.Clear(); }

            public IEnumerable<RtssTrayIconTarget> Find(string path)
            {
                Operations++;
                FindCalls++;
                FindPaths.Add(path);
                return Targets.ToArray();
            }

            public bool Matches(RtssTrayIconTarget target)
            {
                Operations++;
                if (!MatchAllowed || target == null) return false;
                foreach (RtssTrayIconTarget live in Targets)
                    if (live.Pid == target.Pid && live.Birth == target.Birth && live.Window == target.Window
                        && String.Equals(live.Path, target.Path, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (RtssTrayIconTarget live in Windows)
                    if (live.Pid == target.Pid && live.Birth == target.Birth && live.Window == target.Window
                        && String.Equals(live.Path, target.Path, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            public bool HasIcon(RtssTrayIconTarget target)
            {
                Operations++;
                HasIconCalls++;
                return IconExists;
            }

            public bool SetHidden(RtssTrayIconTarget target, bool hidden)
            {
                Operations++;
                Mutations.Add(new RtssTrayMutation { Pid = target.Pid, Birth = target.Birth, Window = target.Window, Hidden = hidden });
                return hidden ? HideSucceeds : RestoreSucceeds && target.Pid != FailedRestorePid;
            }

            public IEnumerable<RtssTrayIconTarget> FindWindows(RtssTrayIconTarget owner)
            {
                Operations++;
                return Windows.ToArray();
            }
            public bool HideWindow(RtssTrayIconTarget target)
            {
                Operations++;
                WindowMutations.Add(new RtssTrayMutation { Pid = target.Pid, Birth = target.Birth, Window = target.Window, Hidden = true });
                return WindowHideSucceeds;
            }
            public void WatchWindows(RtssTrayIconTarget owner)
            {
                Operations++; WatchCalls++;
                foreach (RtssTrayIconTarget existing in Watched)
                    if (existing.Pid == owner.Pid && existing.Birth == owner.Birth && existing.Window == owner.Window) return;
                Watched.Add(owner);
            }
            public void StopWatchingWindows() { Operations++; Watched.Clear(); }
        }
    }
}
#endif
