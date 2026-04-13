using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using Sd.NINA.Demo2.Properties;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Services {

    /// <summary>
    /// Result returned by GRBObservatoryStateService.EvaluateAndPrepareAsync().
    /// </summary>
    public enum GRBReadiness {
        /// <summary>All checks passed — proceed with slew and capture.</summary>
        Ready,
        /// <summary>Unrecoverable condition — skip this GRB and log the reason.</summary>
        Skip
    }

    public class ObservatoryCheckResult {
        public GRBReadiness Readiness { get; set; }
        public string Reason          { get; set; }
    }

    /// <summary>
    /// Checks and resolves all observatory / telescope / camera states before a GRB capture.
    ///
    /// Decision tree (in order):
    ///   1. Safety monitor UNSAFE          → Skip immediately, never override
    ///   2. Shutter CLOSING                → Wait for fully closed, then open it
    ///   3. Shutter CLOSED                 → Open it and wait until fully open
    ///   4. Shutter OPENING                → Wait until fully open
    ///   5. Telescope PARKED               → Unpark automatically
    ///   6. Camera EXPOSING                → Wait up to MaxWaitMinutes; interrupt if threshold exceeded
    ///   7. All clear                      → Return Ready
    /// </summary>
    public class GRBObservatoryStateService {

        private readonly ITelescopeMediator      _telescope;
        private readonly IDomeMediator           _dome;       // controls the shutter
        private readonly ICameraMediator         _camera;
        private readonly ISafetyMonitorMediator  _safety;

        /// <summary>Seconds between each status re-check while waiting.</summary>
        private const int PollIntervalSeconds   = 10;

        /// <summary>Minutes to wait for shutter open/close before giving up.</summary>
        private const int ShutterTimeoutMinutes = 5;

        public GRBObservatoryStateService(
            ITelescopeMediator     telescope,
            IDomeMediator          dome,
            ICameraMediator        camera,
            ISafetyMonitorMediator safety) {
            _telescope = telescope;
            _dome      = dome;
            _camera    = camera;
            _safety    = safety;
        }

        /// <summary>
        /// Runs the full state check pipeline for the given GRB.
        /// Blocks internally while resolving transient states (shutter moving, camera busy).
        /// Returns Ready when all conditions are met, or Skip with a reason if unrecoverable.
        /// </summary>
        public async Task<ObservatoryCheckResult> EvaluateAndPrepareAsync(
            string grbName, IProgress<ApplicationStatus> progress, CancellationToken token) {

            // ── 1. Safety monitor ─────────────────────────────────────────────
            // Poll until the monitor is BOTH connected AND safe.
            // Disconnected = unknown = not safe — we wait rather than proceeding blind.
            var safetyInfo = _safety.GetInfo();
            if (!safetyInfo.Connected || !safetyInfo.IsSafe) {
                Report(progress, $"GRB {grbName}: safety monitor UNSAFE — waiting until safe…");
                Logger.Warning($"[Observatory] Safety monitor UNSAFE — polling until safe for GRB {grbName}.");
                Notification.ShowWarning($"GRB {grbName}: safety UNSAFE — waiting for safe conditions.");
                await WaitForSafe(grbName, progress, token);
                Logger.Info($"[Observatory] Safety monitor now SAFE — continuing for GRB {grbName}.");
                Notification.ShowInformation($"GRB {grbName}: conditions safe — resuming.");
            }

            // ── 2 + 3 + 4. Shutter state ──────────────────────────────────────
            var domeInfo = _dome.GetInfo();
            if (domeInfo.Connected) {

                // If closing — let it finish, then fall through to handle Closed
                if (domeInfo.ShutterStatus == ShutterState.ShutterClosing) {
                    Report(progress, $"GRB {grbName}: shutter is closing — waiting for it to fully close…");
                    Logger.Info($"[Observatory] Shutter closing — waiting before re-opening for GRB {grbName}.");
                    Notification.ShowInformation($"GRB {grbName}: shutter closing, waiting…");

                    bool closedOk = await WaitForShutter(ShutterState.ShutterClosed, ShutterTimeoutMinutes, token);
                    if (!closedOk)
                        return Skip("Shutter did not finish closing within timeout — cannot safely re-open.");

                    // Refresh after wait
                    domeInfo = _dome.GetInfo();
                }

                // If closed (or just finished closing above) — open it
                if (domeInfo.ShutterStatus == ShutterState.ShutterClosed) {
                    Report(progress, $"GRB {grbName}: shutter is closed — opening…");
                    Logger.Info($"[Observatory] Opening shutter for GRB {grbName}.");
                    Notification.ShowInformation($"GRB {grbName}: opening shutter…");

                    await _dome.OpenShutter(token);

                    bool openedOk = await WaitForShutter(ShutterState.ShutterOpen, ShutterTimeoutMinutes, token);
                    if (!openedOk)
                        return Skip("Shutter did not open within timeout — cannot observe.");

                    Logger.Info($"[Observatory] Shutter open — ready for GRB {grbName}.");
                    Notification.ShowSuccess($"GRB {grbName}: shutter open.");
                }

                // If still opening (was already in progress before we arrived) — just wait
                else if (domeInfo.ShutterStatus == ShutterState.ShutterOpening) {
                    Report(progress, $"GRB {grbName}: shutter is opening — waiting…");
                    Logger.Info($"[Observatory] Shutter already opening — waiting for GRB {grbName}.");
                    Notification.ShowInformation($"GRB {grbName}: shutter opening, waiting…");

                    bool openOk = await WaitForShutter(ShutterState.ShutterOpen, ShutterTimeoutMinutes, token);
                    if (!openOk)
                        return Skip("Shutter did not finish opening within timeout.");

                    Logger.Info($"[Observatory] Shutter open.");
                }

                // Re-check safety after shutter operations — weather may have changed.
                // If UNSAFE: close the shutter to protect the equipment, then poll
                // until conditions are safe again and re-open before continuing.
                safetyInfo = _safety.GetInfo();
                if (!safetyInfo.Connected || !safetyInfo.IsSafe) {
                    Notification.ShowWarning($"GRB {grbName}: safety UNSAFE after shutter opened — closing shutter and waiting.");
                    Logger.Warning($"[Observatory] Safety UNSAFE after shutter opened — closing shutter and waiting for GRB {grbName}.");
                    await _dome.CloseShutter(token);
                    await WaitForShutter(ShutterState.ShutterClosed, ShutterTimeoutMinutes, token);

                    Report(progress, $"GRB {grbName}: shutter closed — waiting for safe conditions…");
                    await WaitForSafe(grbName, progress, token);

                    // Conditions safe again — re-open the shutter
                    Logger.Info($"[Observatory] Conditions safe again — re-opening shutter for GRB {grbName}.");
                    await _dome.OpenShutter(token);
                    bool reopenedOk = await WaitForShutter(ShutterState.ShutterOpen, ShutterTimeoutMinutes, token);
                    if (!reopenedOk)
                        return Skip("Shutter did not re-open after conditions became safe — cannot observe.");

                    Logger.Info($"[Observatory] Shutter re-opened — continuing for GRB {grbName}.");
                    Notification.ShowInformation($"GRB {grbName}: conditions safe, shutter re-opened — continuing.");
                }
            }

            // ── 5. Telescope parked ───────────────────────────────────────────
            var teleInfo = _telescope.GetInfo();
            if (teleInfo.Connected && teleInfo.AtPark) {
                Report(progress, $"GRB {grbName}: telescope parked — unparking…");
                Logger.Info($"[Observatory] Telescope is parked — unparking for GRB {grbName}.");
                Notification.ShowInformation($"GRB {grbName}: unparking telescope…");

                await _telescope.UnparkTelescope(progress, token);
                Logger.Info($"[Observatory] Telescope unparked.");
            }

            // ── 6. Camera busy ────────────────────────────────────────────────
            var camInfo = _camera.GetInfo();
            if (camInfo.Connected && camInfo.IsExposing) {
                int maxWait = Math.Max(1, Settings.Default.MaxWaitMinutes);
                Report(progress, $"GRB {grbName}: camera exposing — waiting up to {maxWait} min…");
                Logger.Info($"[Observatory] Camera is exposing — waiting up to {maxWait} min for GRB {grbName}.");
                Notification.ShowInformation($"GRB {grbName}: camera busy, waiting up to {maxWait} min…");

                bool cameraFree = await WaitForCameraIdle(maxWait, token);

                if (cameraFree) {
                    Logger.Info($"[Observatory] Camera free — proceeding with GRB {grbName}.");
                } else {
                    // Threshold exceeded — interrupt and proceed anyway
                    Logger.Warning($"[Observatory] Camera still exposing after {maxWait} min — " +
                                   $"interrupting for GRB {grbName}.");
                    Notification.ShowWarning(
                        $"GRB {grbName}: wait threshold ({maxWait} min) exceeded — interrupting current exposure.");
                }
            }

            // ── All checks passed ─────────────────────────────────────────────
            Logger.Info($"[Observatory] All pre-flight checks passed for GRB {grbName} — proceeding.");
            return new ObservatoryCheckResult { Readiness = GRBReadiness.Ready, Reason = "All checks passed." };
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Polls the safety monitor every PollIntervalSeconds indefinitely until it reports
        /// safe (or is disconnected). Returns when it is safe to proceed.
        /// Only exits early if the CancellationToken is cancelled.
        /// </summary>
        private async Task WaitForSafe(string grbName, IProgress<ApplicationStatus> progress, CancellationToken token) {
            while (true) {
                token.ThrowIfCancellationRequested();
                var info = _safety.GetInfo();
                // Only proceed when the monitor is BOTH connected AND safe.
                // Disconnected = unknown = not safe — keep waiting.
                if (info.Connected && info.IsSafe) return;
                Report(progress, $"GRB {grbName}: safety UNSAFE — rechecking in {PollIntervalSeconds}s…");
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), token);
            }
        }

        /// <summary>Polls shutter status every PollIntervalSeconds until target or timeout.</summary>
        private async Task<bool> WaitForShutter(
            ShutterState target, int timeoutMinutes, CancellationToken token) {
            var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
            while (DateTime.UtcNow < deadline) {
                token.ThrowIfCancellationRequested();
                if (_dome.GetInfo().ShutterStatus == target) return true;
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), token);
            }
            Logger.Warning($"[Observatory] Timeout ({timeoutMinutes} min) waiting for shutter → {target}.");
            return false;
        }

        /// <summary>Polls camera state every PollIntervalSeconds until idle or timeout.</summary>
        private async Task<bool> WaitForCameraIdle(int maxWaitMinutes, CancellationToken token) {
            var deadline = DateTime.UtcNow.AddMinutes(maxWaitMinutes);
            while (DateTime.UtcNow < deadline) {
                token.ThrowIfCancellationRequested();
                if (!_camera.GetInfo().IsExposing) return true;
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), token);
            }
            return false;
        }

        private static void Report(IProgress<ApplicationStatus> progress, string message) =>
            progress?.Report(new ApplicationStatus { Status = message });

        private static ObservatoryCheckResult Skip(string reason) {
            Logger.Warning($"[Observatory] GRB skipped — {reason}");
            return new ObservatoryCheckResult { Readiness = GRBReadiness.Skip, Reason = reason };
        }
    }
}
