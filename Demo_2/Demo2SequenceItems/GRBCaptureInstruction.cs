using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces.Mediator;
using Sd.NINA.Demo2.Models;
using Sd.NINA.Demo2.Properties;
using Sd.NINA.Demo2.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Demo2TestCategory {
    /// <summary>
    /// Sequence instruction that:
    ///   1. Slews the mount to the GRB coordinates (up to 3 attempts if slew fails)
    ///   2. Captures 3 × 30-second LIGHT frames
    ///   3. Posts capture data to Firestore
    ///
    /// Place this in the same sequence loop as GRBAlertTrigger.
    /// No-op if no GRB is pending.
    ///
    /// Note: Plate-solve centering can be added in SlewAndCenter() once
    /// NINA.PlateSolving is referenced in the project.
    /// </summary>
    [ExportMetadata("Name", "GRB Capture")]
    [ExportMetadata("Description", "Checks observatory state (shutter, safety, telescope, camera), then slews and captures when a GRB alert fires.")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "GRB")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class GRBCaptureInstruction : SequenceItem {

        private readonly IImagingMediator        imagingMediator;
        private readonly IImageSaveMediator      imageSaveMediator;
        private readonly ITelescopeMediator      telescopeMediator;
        private readonly IDomeMediator           domeMediator;
        private readonly ICameraMediator         cameraMediator;
        private readonly ISafetyMonitorMediator  safetyMediator;
        private readonly IProfileService         profileService;
        private readonly GRBObservatoryStateService _observatoryService;

        // Retry up to 3 times if the mount reports a slew failure
        private const int MaxSlewAttempts = 3;

        [ImportingConstructor]
        public GRBCaptureInstruction(
                IImagingMediator        imagingMediator,
                IImageSaveMediator      imageSaveMediator,
                ITelescopeMediator      telescopeMediator,
                IDomeMediator           domeMediator,
                ICameraMediator         cameraMediator,
                ISafetyMonitorMediator  safetyMediator,
                IProfileService         profileService) {
            this.imagingMediator   = imagingMediator;
            this.imageSaveMediator = imageSaveMediator;
            this.telescopeMediator = telescopeMediator;
            this.domeMediator      = domeMediator;
            this.cameraMediator    = cameraMediator;
            this.safetyMediator    = safetyMediator;
            this.profileService    = profileService;
            _observatoryService    = new GRBObservatoryStateService(
                telescopeMediator, domeMediator, cameraMediator, safetyMediator);
        }

        public GRBCaptureInstruction(GRBCaptureInstruction copyMe)
            : this(copyMe.imagingMediator, copyMe.imageSaveMediator, copyMe.telescopeMediator,
                   copyMe.domeMediator, copyMe.cameraMediator, copyMe.safetyMediator, copyMe.profileService) {
            CopyMetaData(copyMe);
        }

        public override object Clone() => new GRBCaptureInstruction(this);

        // ── Main execution ─────────────────────────────────────────────────────────
        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var grb = GRBPendingState.TakeAndClear();
            if (grb == null) {
                Logger.Info("[GRB Capture] No pending GRB — skipping.");
                return;
            }

            if (GRBObservabilityService.IsAlreadyObserved(grb.Name)) {
                Logger.Info($"[GRB Capture] {grb.Name} already captured this session — skipping duplicate.");
                return;
            }

            try {
                Logger.Info($"[GRB Capture] Alert: {grb.Name}  RA={grb.RA:F4}°  Dec={grb.Dec:F4}°");

                // ── Step 1: Pre-flight observatory state check ─────────────────
                var obsCheck = await _observatoryService.EvaluateAndPrepareAsync(grb.Name, progress, token);
                if (obsCheck.Readiness == GRBReadiness.Skip) {
                    Logger.Warning($"[GRB Capture] Skipping {grb.Name} — {obsCheck.Reason}");
                    Notification.ShowWarning($"GRB {grb.Name} skipped: {obsCheck.Reason}");
                    // Post skip reason to Firestore so Python side can see why it was missed
                    await FirestoreCapturePoster.PostSkippedAsync(grb, obsCheck.Reason);
                    return;
                }

                // ── Step 2: Slew to GRB coordinates ───────────────────────────────
                // Save current position so we can return after capture
                var preSlewCoords = telescopeMediator.GetInfo().Connected
                    ? telescopeMediator.GetInfo().Coordinates
                    : null;

                await SlewAndCenter(grb, progress, token);

                // ── Step 3: Science exposures (count/time/binning from Options) ────
                int    expCount   = Math.Max(1, Settings.Default.ExposureCount);
                double expSeconds = Math.Max(1.0, Settings.Default.ExposureTime);
                int    binning    = Math.Max(1, Settings.Default.Binning);

                Logger.Info($"[GRB Capture] Starting {expCount} × {expSeconds} s exposures (bin {binning}×{binning}) for {grb.Name}");
                var seq = new CaptureSequence(expSeconds, CaptureSequence.ImageTypes.LIGHT,
                                              null, new BinningMode((short)binning, (short)binning), 1);

                for (int i = 1; i <= expCount; i++) {
                    token.ThrowIfCancellationRequested();
                    progress.Report(new ApplicationStatus {
                        Status = $"GRB {grb.Name}: exposure {i}/{expCount}  ({expSeconds} s, bin {binning}×{binning})"
                    });
                    Logger.Info($"[GRB Capture] Exposure {i}/{expCount}");

                    var exposureData = await imagingMediator.CaptureImage(seq, token, progress);
                    var imageData    = await exposureData.ToImageData(progress, token);
                    var prepareTask  = imagingMediator.PrepareImage(exposureData, new PrepareImageParameters(true, false), token);
                    await imageSaveMediator.Enqueue(imageData, prepareTask, progress, token);

                    Logger.Info($"[GRB Capture] Exposure {i}/{expCount} saved.");
                }

                // ── Step 4: Notify + Firestore ────────────────────────────────────
                Logger.Info($"[GRB Capture] Done: {expCount} exposures captured for {grb.Name}");
                Notification.ShowSuccess($"GRB {grb.Name}: {expCount} exposures captured.");

                // Insiyah: Update status in observable_list 
                await FirestoreGrbListener.UpdateObservableListStatusAsync(
                    FirestoreGrbListener.SharedProjectId,
                    FirestoreGrbListener.SharedCredential,
                    grb.Name,
                    "observed");

                // Pass the real NINA image save root so image_analyzer.py finds the FITS files
                string imageRoot = profileService?.ActiveProfile?.ImageFileSettings?.FilePath
                    ?? System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A");
                await FirestoreCapturePoster.PostCaptureAsync(grb, expCount, (int)expSeconds, imageRoot);
                GRBObservabilityService.MarkAsObserved(grb.Name);

                // ── Step 5: Return to original position ───────────────────────────
                if (preSlewCoords != null && telescopeMediator.GetInfo().Connected) {
                    progress.Report(new ApplicationStatus { Status = "GRB capture done — returning to original position." });
                    Logger.Info($"[GRB Capture] Returning to original position RA={preSlewCoords.RADegrees:F4}°  Dec={preSlewCoords.Dec:F4}°");
                    await telescopeMediator.SlewToCoordinatesAsync(preSlewCoords, token);
                    Logger.Info("[GRB Capture] Returned to original position.");
                }

            } catch (OperationCanceledException) {
                Logger.Warning($"[GRB Capture] Cancelled — {grb?.Name}");
            } catch (Exception ex) {
                Logger.Error($"[GRB Capture] Failed for {grb?.Name}: {ex.Message}");
                Notification.ShowError($"GRB capture failed: {ex.Message}");
            }
        }

        // ── Slew with retry ────────────────────────────────────────────────────────
        /// <summary>
        /// Slews the mount to the GRB target, then runs plate-solve centering.
        /// Centering loop (up to 3 iterations):
        ///   1. Capture a 5 s, 2×2 binned SNAPSHOT
        ///   2. Prepare image — if NINA's plate solver is configured it embeds WCS into metadata
        ///   3. If WCS found: compute offset, sync + re-slew if offset > 30 arcsec
        ///   4. If no WCS after the first snap: log and skip remaining iterations
        /// Falls back to a plain slew when telescope is not connected.
        /// </summary>
        private async Task SlewAndCenter(GRBEvent grb, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (!telescopeMediator.GetInfo().Connected) {
                Logger.Warning("[GRB Capture] Telescope not connected — skipping slew, capturing in place.");
                Notification.ShowWarning($"GRB {grb.Name}: telescope not connected.");
                return;
            }

            // RA stored in degrees → convert to hours for NINA Coordinates
            var target = new Coordinates(grb.RA / 15.0, grb.Dec, Epoch.J2000, Coordinates.RAType.Hours);

            // Pre-slew altitude check — abort if target is below the horizon
            {
                double lat = profileService.ActiveProfile.AstrometrySettings.Latitude;
                double lon = profileService.ActiveProfile.AstrometrySettings.Longitude;
                double lstHours = AstroUtil.GetLocalSiderealTime(DateTime.UtcNow, lon);
                Angle  lst = Angle.ByHours(lstHours);
                Angle  ha  = AstroUtil.GetHourAngle(lst, Angle.ByDegree(grb.RA));
                double alt = AstroUtil.GetAltitude(ha.Degree, lat, grb.Dec);
                Logger.Info($"[GRB Capture] Pre-slew altitude check: Alt={alt:F1}°");
                if (alt < 0) {
                    Logger.Error($"[GRB Capture] {grb.Name} is below the horizon (Alt={alt:F1}°) — aborting slew.");
                    Notification.ShowError($"GRB {grb.Name}: target is below the horizon (Alt={alt:F1}°). Capture aborted.");
                    throw new InvalidOperationException($"GRB {grb.Name} is below the horizon (Alt={alt:F1}°).");
                }
            }

            // ── Step 1: Initial slew with retries ─────────────────────────────────
            bool slewOk = false;
            for (int attempt = 1; attempt <= MaxSlewAttempts; attempt++) {
                token.ThrowIfCancellationRequested();
                progress.Report(new ApplicationStatus {
                    Status = $"GRB {grb.Name}: slewing… (attempt {attempt}/{MaxSlewAttempts})"
                });
                Logger.Info($"[GRB Capture] Slew attempt {attempt}/{MaxSlewAttempts} → RA={grb.RA:F4}°  Dec={grb.Dec:F4}°");

                slewOk = await telescopeMediator.SlewToCoordinatesAsync(target, token);
                if (slewOk) {
                    Logger.Info($"[GRB Capture] Slew succeeded on attempt {attempt}.");
                    break;
                }
                Logger.Warning($"[GRB Capture] Slew failed on attempt {attempt}.");
            }

            if (!slewOk) {
                Logger.Error("[GRB Capture] All slew attempts failed — capturing at current position.");
                Notification.ShowWarning($"GRB {grb.Name}: slew failed after {MaxSlewAttempts} attempts, capturing in place.");
                return;
            }

            progress.Report(new ApplicationStatus { Status = $"GRB {grb.Name}: on target — running plate-solve centering." });

            // ── Step 2: Plate-solve centering loop ────────────────────────────────
            // Requires NINA's plate solver to be configured in Tools → Options → Plate Solving.
            // If no WCS is returned on the first snap, centering is skipped gracefully.
            const double CenteringThresholdArcsec = 30.0;
            const int    MaxCenteringIterations   = 3;

            for (int iter = 1; iter <= MaxCenteringIterations; iter++) {
                token.ThrowIfCancellationRequested();
                progress.Report(new ApplicationStatus {
                    Status = $"GRB {grb.Name}: centering snap {iter}/{MaxCenteringIterations}…"
                });

                // Capture a short, binned snap purely for centering
                var snapSeq = new CaptureSequence(5, CaptureSequence.ImageTypes.SNAPSHOT,
                                                  null, new BinningMode(2, 2), 1);
                IExposureData snapExposure;
                try {
                    snapExposure = await imagingMediator.CaptureImage(snapSeq, token, progress);
                } catch (Exception ex) {
                    Logger.Warning($"[GRB Capture] Centering snap {iter} failed: {ex.Message} — stopping centering.");
                    break;
                }

                // PrepareImage with autoStretch=false, detectStars=false (fast).
                // If plate-solve is enabled in NINA settings, solved WCS will be in RawImageData.MetaData.
                var prepared = await imagingMediator.PrepareImage(
                    snapExposure, new PrepareImageParameters(false, false), token);

                var wcs = prepared?.RawImageData?.MetaData?.WorldCoordinateSystem;
                if (wcs == null) {
                    if (iter == 1)
                        Logger.Info("[GRB Capture] No WCS in centering snap — plate solver not configured or solve failed. Skipping centering.");
                    break;
                }

                // Get the sky coordinate at the image centre pixel
                double cx = prepared.RawImageData.Properties.Width  / 2.0;
                double cy = prepared.RawImageData.Properties.Height / 2.0;
                var solvedCenter = wcs.GetCoordinates(cx, cy);

                // Angular separation between solved centre and GRB target
                double sepArcsec = (solvedCenter - target).Distance.ArcSeconds;
                Logger.Info($"[GRB Capture] Centering iter {iter}: offset = {sepArcsec:F1}\"");

                if (sepArcsec <= CenteringThresholdArcsec) {
                    Logger.Info($"[GRB Capture] Centering complete — offset {sepArcsec:F1}\" ≤ {CenteringThresholdArcsec}\".");
                    progress.Report(new ApplicationStatus { Status = $"GRB {grb.Name}: centred ({sepArcsec:F1}\")." });
                    break;
                }

                // Sync telescope to the solved position, then re-slew to target
                Logger.Info($"[GRB Capture] Offset {sepArcsec:F1}\" > {CenteringThresholdArcsec}\" — syncing and re-slewing.");
                await telescopeMediator.Sync(solvedCenter);
                await telescopeMediator.SlewToCoordinatesAsync(target, token);

                if (iter == MaxCenteringIterations)
                    Logger.Warning($"[GRB Capture] Reached max centering iterations — residual offset {sepArcsec:F1}\".");
            }
        }

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(GRBCaptureInstruction)}";
    }
}
