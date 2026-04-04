using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using Sd.NINA.Demo2.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Tests {
    /// <summary>
    /// Manual test scenarios for GRBObservatoryStateService.
    /// Run these at home without any hardware.
    ///
    /// Call ObservatoryStateTests.RunAll() from anywhere (e.g. a button in the dockable panel).
    /// Each scenario prints PASS or FAIL to the NINA log.
    /// </summary>
    public static class ObservatoryStateTests {

        public static async Task RunAll() {
            Logger.Info("[TEST] ====== Observatory State Tests ======");
            await Test_SafetyUnsafe_Skips();
            await Test_ShutterOpen_Proceeds();
            await Test_ShutterClosed_OpensAndProceeds();
            await Test_ShutterClosing_WaitsAndProceeds();
            await Test_ShutterOpening_WaitsAndProceeds();
            await Test_TelescopeParked_Unparks();
            await Test_CameraBusy_WaitsAndProceeds();
            await Test_CameraBusy_ExceedsThreshold_Interrupts();
            Logger.Info("[TEST] ====== All tests complete ======");
        }

        // ── Individual scenarios ──────────────────────────────────────────────

        static async Task Test_SafetyUnsafe_Skips() {
            var dome   = new MockDomeMediator   { SimulatedShutterStatus = ShutterState.ShutterOpen };
            var safety = new MockSafetyMonitorMediator { SimulatedIsSafe = false };  // UNSAFE
            var scope  = new MockTelescopeMediator();
            var camera = new MockCameraMediator();

            var svc    = new GRBObservatoryStateService(scope, dome, camera, safety);
            var result = await svc.EvaluateAndPrepareAsync("GRB_TEST", null, CancellationToken.None);

            Log("Test_SafetyUnsafe_Skips",
                result.Readiness == GRBReadiness.Skip,
                result.Reason);
        }

        static async Task Test_ShutterOpen_Proceeds() {
            var dome   = new MockDomeMediator   { SimulatedShutterStatus = ShutterState.ShutterOpen };
            var safety = new MockSafetyMonitorMediator { SimulatedIsSafe = true };
            var scope  = new MockTelescopeMediator();
            var camera = new MockCameraMediator();

            var svc    = new GRBObservatoryStateService(scope, dome, camera, safety);
            var result = await svc.EvaluateAndPrepareAsync("GRB_TEST", null, CancellationToken.None);

            Log("Test_ShutterOpen_Proceeds",
                result.Readiness == GRBReadiness.Ready,
                result.Reason);
        }

        static async Task Test_ShutterClosed_OpensAndProceeds() {
            // Shutter starts Closed, MockDomeMediator will flip it to Open after OpenDelaySeconds
            var dome = new MockDomeMediator {
                SimulatedShutterStatus = ShutterState.ShutterClosed,
                OpenDelaySeconds       = 1   // fast for testing
            };
            var safety = new MockSafetyMonitorMediator { SimulatedIsSafe = true };
            var scope  = new MockTelescopeMediator();
            var camera = new MockCameraMediator();

            var svc    = new GRBObservatoryStateService(scope, dome, camera, safety);
            var result = await svc.EvaluateAndPrepareAsync("GRB_TEST", null, CancellationToken.None);

            Log("Test_ShutterClosed_OpensAndProceeds",
                result.Readiness == GRBReadiness.Ready && dome.SimulatedShutterStatus == ShutterState.ShutterOpen,
                result.Reason);
        }

        static async Task Test_ShutterClosing_WaitsAndProceeds() {
            // Shutter starts Closing — service should wait for Closed, then open
            var dome = new MockDomeMediator {
                SimulatedShutterStatus = ShutterState.ShutterClosing,
                CloseDelaySeconds      = 1,
                OpenDelaySeconds       = 1
            };
            var safety = new MockSafetyMonitorMediator { SimulatedIsSafe = true };
            var scope  = new MockTelescopeMediator();
            var camera = new MockCameraMediator();

            var svc    = new GRBObservatoryStateService(scope, dome, camera, safety);
            var result = await svc.EvaluateAndPrepareAsync("GRB_TEST", null, CancellationToken.None);

            Log("Test_ShutterClosing_WaitsAndProceeds",
                result.Readiness == GRBReadiness.Ready && dome.SimulatedShutterStatus == ShutterState.ShutterOpen,
                result.Reason);
        }

        static async Task Test_ShutterOpening_WaitsAndProceeds() {
            var dome = new MockDomeMediator {
                SimulatedShutterStatus = ShutterState.ShutterOpening,
                OpenDelaySeconds       = 1
            };
            var safety = new MockSafetyMonitorMediator { SimulatedIsSafe = true };
            var scope  = new MockTelescopeMediator();
            var camera = new MockCameraMediator();

            var svc    = new GRBObservatoryStateService(scope, dome, camera, safety);
            var result = await svc.EvaluateAndPrepareAsync("GRB_TEST", null, CancellationToken.None);

            Log("Test_ShutterOpening_WaitsAndProceeds",
                result.Readiness == GRBReadiness.Ready,
                result.Reason);
        }

        static async Task Test_TelescopeParked_Unparks() {
            var dome   = new MockDomeMediator   { SimulatedShutterStatus = ShutterState.ShutterOpen };
            var safety = new MockSafetyMonitorMediator { SimulatedIsSafe = true };
            var scope  = new MockTelescopeMediator { SimulatedAtPark = true };  // PARKED
            var camera = new MockCameraMediator();

            var svc    = new GRBObservatoryStateService(scope, dome, camera, safety);
            var result = await svc.EvaluateAndPrepareAsync("GRB_TEST", null, CancellationToken.None);

            Log("Test_TelescopeParked_Unparks",
                result.Readiness == GRBReadiness.Ready && !scope.SimulatedAtPark,
                result.Reason);
        }

        static async Task Test_CameraBusy_WaitsAndProceeds() {
            var dome   = new MockDomeMediator   { SimulatedShutterStatus = ShutterState.ShutterOpen };
            var safety = new MockSafetyMonitorMediator { SimulatedIsSafe = true };
            var scope  = new MockTelescopeMediator();
            // Camera busy for 1s, MaxWaitMinutes = 1 → should wait and proceed
            var camera = new MockCameraMediator { SimulatedIsExposing = true, ExposureFinishesAfterSeconds = 1 };

            Properties.Settings.Default.MaxWaitMinutes = 1;

            var svc    = new GRBObservatoryStateService(scope, dome, camera, safety);
            var result = await svc.EvaluateAndPrepareAsync("GRB_TEST", null, CancellationToken.None);

            Log("Test_CameraBusy_WaitsAndProceeds",
                result.Readiness == GRBReadiness.Ready,
                result.Reason);
        }

        static async Task Test_CameraBusy_ExceedsThreshold_Interrupts() {
            var dome   = new MockDomeMediator   { SimulatedShutterStatus = ShutterState.ShutterOpen };
            var safety = new MockSafetyMonitorMediator { SimulatedIsSafe = true };
            var scope  = new MockTelescopeMediator();
            // Camera never finishes, MaxWaitMinutes = 0 → should interrupt immediately and proceed
            var camera = new MockCameraMediator { SimulatedIsExposing = true, ExposureFinishesAfterSeconds = 9999 };

            Properties.Settings.Default.MaxWaitMinutes = 0;

            var svc    = new GRBObservatoryStateService(scope, dome, camera, safety);
            var result = await svc.EvaluateAndPrepareAsync("GRB_TEST", null, CancellationToken.None);

            Log("Test_CameraBusy_ExceedsThreshold_Interrupts",
                result.Readiness == GRBReadiness.Ready,   // Ready because we interrupt and proceed
                result.Reason);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void Log(string testName, bool passed, string reason) {
            string status = passed ? "PASS ✓" : "FAIL ✗";
            Logger.Info($"[TEST] {status}  {testName}  — {reason}");
            if (!passed)
                Logger.Error($"[TEST] FAILED: {testName}");
        }
    }
}
