using HDRCalibrate.Services;
using HDRCalibrate.Dwm;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace HDRCalibrate.Tests
{
    internal static class ProgramGammaRuntimeTests
    {
        public static void UnsupportedBuildBlockedBeforeNativeFiles()
        {
            // Deliberately bypass the approval factory only to test the runner's
            // independent entry gate. A null target also prevents any process
            // launch if this guard regresses. No valid native request is supplied.
            var approval = (ProgramGammaApproval)FormatterServices.GetUninitializedObject(typeof(ProgramGammaApproval));
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(ProgramGammaApproval).GetField("<DwmCoreVersion>k__BackingField", flags).SetValue(approval,
                "10.0.26100.9278 (WinBuild.160101.0800)");
            typeof(ProgramGammaApproval).GetField("_windowsEnvironment", flags).SetValue(approval, new WindowsEnvironmentIdentity
            {
                DwmCoreSha256 = "40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222"
            });
            var rejected = false;
            try
            {
                new ProgramGammaSessionRunner().RunAsync(null, approval, false, null, null,
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (NotSupportedException ex) { rejected = ex.Message == ProgramGammaBuildSupport.UnsupportedMessage; }
            Require(rejected, "The native runner did not reject an unsupported build before opening its payload or starting a helper.");
        }

        public static void AbandonedUacDoesNotHoldSession()
        {
            var wait = typeof(ProgramGammaSessionRunner).GetMethod("AwaitOwnedLaunchAsync", BindingFlags.NonPublic | BindingFlags.Static);
            Require(wait != null, "Missing bounded launch seam.");
            foreach (var cancelled in new[] { false, true })
            {
                var launch = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var cancellation = new CancellationTokenSource())
                {
                    var pending = (Task<Process>)wait.Invoke(null, new object[] { launch.Task, cancellation.Token, cancelled ? 30000 : 20 });
                    if (cancelled) cancellation.Cancel();
                    Require(Task.WhenAny(pending, Task.Delay(1000)).GetAwaiter().GetResult() == pending,
                        "An abandoned launch still holds the managed session.");
                    var rejected = false;
                    try { pending.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { rejected = cancelled; }
                    catch (TimeoutException) { rejected = !cancelled; }
                    Require(rejected, "Launch cancellation/deadline was not reported.");
                    // A fresh handle to our own process, not a launch or termination.
                    var late = Process.GetCurrentProcess();
                    var pinned = late.Handle;
                    launch.SetResult(late);
                    Require(SpinWait.SpinUntil(() =>
                    {
                        try { var ignored = late.Id; return false; }
                        catch (InvalidOperationException) { return true; }
                    }, 1000), "The late helper handle was not disposed.");
                }
            }
        }

        public static void UnresponsiveDispatcherWithdrawsHeartbeat()
        {
            var ready = new TaskCompletionSource<Tuple<Dispatcher, ProgramGammaSessionRunner>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                ready.SetResult(Tuple.Create(dispatcher, new ProgramGammaSessionRunner()));
                Dispatcher.Run();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Require(ready.Task.Wait(2000), "Owned test dispatcher did not start.");
            var own = ready.Task.Result;
            var check = typeof(ProgramGammaSessionRunner).GetMethod("OwnerUiRespondsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Require(check != null, "Missing dispatcher acknowledgment seam.");
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                try
                {
                    var responsive = (Task<bool>)check.Invoke(own.Item2, new object[] { CancellationToken.None });
                    Require(responsive.Wait(2000) && responsive.Result, "A responsive owned dispatcher did not acknowledge.");
                    own.Item1.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        entered.Set();
                        release.WaitOne(3000);
                    }));
                    Require(entered.WaitOne(2000), "Did not block the owned test dispatcher.");
                    var blocked = (Task<bool>)check.Invoke(own.Item2, new object[] { CancellationToken.None });
                    Require(blocked.Wait(2000) && !blocked.Result, "A blocked dispatcher continued to authorize a heartbeat.");
                }
                finally
                {
                    release.Set();
                    own.Item1.BeginInvokeShutdown(DispatcherPriority.Send);
                    Require(thread.Join(2000), "Owned test dispatcher did not finish.");
                }
            }
        }

        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
