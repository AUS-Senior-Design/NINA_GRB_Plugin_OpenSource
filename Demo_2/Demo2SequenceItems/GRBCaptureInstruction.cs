using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces.Mediator;
using Sd.NINA.Demo2.Models;
using Sd.NINA.Demo2.Services;
using System;
using System.ComponentModel.Composition;
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
    [ExportMetadata("Description", "Slews to GRB coordinates (up to 3 attempts), then captures 3 × 30 s LIGHT frames when a GRB alert fires.")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "GRB")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class GRBCaptureInstruction : SequenceItem {

        private readonly IImagingMediator imagingMediator;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly ITelescopeMediator telescopeMediator;

        // Retry up to 3 times if the mount reports a slew failure
        private const int MaxSlewAttempts = 3;

        [ImportingConstructor]
        public GRBCaptureInstruction(
                IImagingMediator   imagingMediator,
                IImageSaveMediator imageSaveMediator,
                ITelescopeMediator telescopeMediator) {
            this.imagingMediator   = imagingMediator;
            this.imageSaveMediator = imageSaveMediator;
            this.telescopeMediator = telescopeMediator;
        }

        public GRBCaptureInstruction(GRBCaptureInstruction copyMe)
            : this(copyMe.imagingMediator, copyMe.imageSaveMediator, copyMe.telescopeMediator) {
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

            try {
                Logger.Info($"[GRB Capture] Alert: {grb.Name}  RA={grb.RA:F4}°  Dec={grb.Dec:F4}°");

                // ── Step 1: Slew to GRB coordinates ───────────────────────────────
                await SlewAndCenter(grb, progress, token);

                // ── Step 2: Three 30-second science exposures ─────────────────────
                Logger.Info($"[GRB Capture] Starting 3 × 30 s exposures for {grb.Name}");
                var seq = new CaptureSequence(30, CaptureSequence.ImageTypes.LIGHT, null, new BinningMode(1, 1), 1);

                for (int i = 1; i <= 3; i++) {
                    token.ThrowIfCancellationRequested();
                    progress.Report(new ApplicationStatus { Status = $"GRB {grb.Name}: exposure {i}/3  (30 s)" });
                    Logger.Info($"[GRB Capture] Exposure {i}/3");

                    var exposureData = await imagingMediator.CaptureImage(seq, token, progress);
                    var imageData    = await exposureData.ToImageData(progress, token);
                    var prepareTask  = imagingMediator.PrepareImage(exposureData, new PrepareImageParameters(true, false), token);
                    await imageSaveMediator.Enqueue(imageData, prepareTask, progress, token);

                    Logger.Info($"[GRB Capture] Exposure {i}/3 saved.");
                }

                // ── Step 3: Notify + Firestore ────────────────────────────────────
                Logger.Info($"[GRB Capture] Done: 3 exposures captured for {grb.Name}");
                Notification.ShowSuccess($"GRB {grb.Name}: 3 exposures captured.");
                await FirestoreCapturePoster.PostCaptureAsync(grb, 3, 30);
                GRBObservabilityService.MarkAsObserved(grb.Name);

            } catch (OperationCanceledException) {
                Logger.Warning($"[GRB Capture] Cancelled — {grb?.Name}");
            } catch (Exception ex) {
                Logger.Error($"[GRB Capture] Failed for {grb?.Name}: {ex.Message}");
                Notification.ShowError($"GRB capture failed: {ex.Message}");
            }
        }

        // ── Slew with retry ────────────────────────────────────────────────────────
        /// <summary>
        /// Slews the mount to the GRB target coordinates.
        /// Retries up to MaxSlewAttempts times if the mount reports a failure.
        /// If the telescope is not connected, captures in place (no slew).
        ///
        /// TODO: Add plate-solve centering here once NINA.PlateSolving is referenced.
        ///       After each successful slew, capture a 5-second snap, plate solve it,
        ///       sync the mount if offset > 30", and re-slew. Repeat up to 3 times.
        /// </summary>
        private async Task SlewAndCenter(GRBEvent grb, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (!telescopeMediator.GetInfo().Connected) {
                Logger.Warning("[GRB Capture] Telescope not connected — skipping slew, capturing in place.");
                Notification.ShowWarning($"GRB {grb.Name}: telescope not connected.");
                return;
            }

            // RA stored in degrees in GRBEvent — convert to hours for NINA Coordinates
            var target = new Coordinates(grb.RA / 15.0, grb.Dec, Epoch.J2000, Coordinates.RAType.Hours);

            for (int attempt = 1; attempt <= MaxSlewAttempts; attempt++) {
                token.ThrowIfCancellationRequested();

                progress.Report(new ApplicationStatus {
                    Status = $"GRB {grb.Name}: slewing… (attempt {attempt}/{MaxSlewAttempts})"
                });
                Logger.Info($"[GRB Capture] Slew attempt {attempt}/{MaxSlewAttempts} " +
                            $"→ RA={grb.RA:F4}°  Dec={grb.Dec:F4}°");

                bool slewOk = await telescopeMediator.SlewToCoordinatesAsync(target, token);

                if (slewOk) {
                    Logger.Info($"[GRB Capture] Slew succeeded on attempt {attempt}.");
                    progress.Report(new ApplicationStatus { Status = $"GRB {grb.Name}: on target." });
                    break;
                }

                Logger.Warning($"[GRB Capture] Slew failed on attempt {attempt}.");

                if (attempt == MaxSlewAttempts) {
                    Logger.Error("[GRB Capture] All slew attempts failed — capturing at current position.");
                    Notification.ShowWarning($"GRB {grb.Name}: slew failed after {MaxSlewAttempts} attempts, capturing in place.");
                }
            }
        }

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(GRBCaptureInstruction)}";
    }
}
