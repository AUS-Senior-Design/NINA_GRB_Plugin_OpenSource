using Newtonsoft.Json;
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
using Sd.NINA.Demo2.Services;
using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Demo2TestCategory {
    /// <summary>
    /// Testing instruction — captures 3 × 30 s LIGHT frames in place (no slew).
    /// Use this during the testing phase when you don't have a mount connected.
    /// Posts to Firestore just like the full GRB Capture instruction.
    /// </summary>
    [ExportMetadata("Name", "GRB Capture (Camera Only)")]
    [ExportMetadata("Description", "Captures 3 × 30 s LIGHT frames in place — no slew. For testing without a mount.")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "GRB")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class GRBCaptureOnlyInstruction : SequenceItem {

        private readonly IImagingMediator  imagingMediator;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IProfileService   profileService;

        [ImportingConstructor]
        public GRBCaptureOnlyInstruction(
                IImagingMediator    imagingMediator,
                IImageSaveMediator  imageSaveMediator,
                IProfileService     profileService) {
            this.imagingMediator   = imagingMediator;
            this.imageSaveMediator = imageSaveMediator;
            this.profileService    = profileService;
        }

        public GRBCaptureOnlyInstruction(GRBCaptureOnlyInstruction copyMe)
            : this(copyMe.imagingMediator, copyMe.imageSaveMediator, copyMe.profileService) {
            CopyMetaData(copyMe);
        }

        public override object Clone() => new GRBCaptureOnlyInstruction(this);

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var grb = GRBPendingState.TakeAndClear();
            if (grb == null) {
                Logger.Info("[GRB Camera Only] No pending GRB — skipping.");
                return;
            }

            try {
                Logger.Info($"[GRB Camera Only] Alert: {grb.Name} — capturing in place (no slew).");
                var seq = new CaptureSequence(30, CaptureSequence.ImageTypes.LIGHT, null, new BinningMode(1, 1), 1);

                for (int i = 1; i <= 3; i++) {
                    token.ThrowIfCancellationRequested();
                    progress.Report(new ApplicationStatus { Status = $"GRB {grb.Name}: exposure {i}/3  (30 s)" });
                    Logger.Info($"[GRB Camera Only] Exposure {i}/3");

                    var exposureData = await imagingMediator.CaptureImage(seq, token, progress);
                    var imageData    = await exposureData.ToImageData(progress, token);
                    var prepareTask  = imagingMediator.PrepareImage(exposureData, new PrepareImageParameters(true, false), token);
                    await imageSaveMediator.Enqueue(imageData, prepareTask, progress, token);

                    Logger.Info($"[GRB Camera Only] Exposure {i}/3 saved.");
                }

                Logger.Info($"[GRB Camera Only] Done: 3 exposures captured for {grb.Name}");
                Notification.ShowSuccess($"GRB {grb.Name}: 3 exposures captured (camera only).");

                // Read the actual NINA image save path from the active profile —
                // same logic as GRBCaptureInstruction so image_analyzer.py finds the files
                string imageRoot = profileService?.ActiveProfile?.ImageFileSettings?.FilePath
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "N.I.N.A");

                await FirestoreCapturePoster.PostCaptureAsync(grb, 3, 30, imageRoot);
                GRBObservabilityService.MarkAsObserved(grb.Name);

            } catch (OperationCanceledException) {
                Logger.Warning($"[GRB Camera Only] Cancelled — {grb?.Name}");
            } catch (Exception ex) {
                Logger.Error($"[GRB Camera Only] Failed for {grb?.Name}: {ex.Message}");
                Notification.ShowError($"GRB capture failed: {ex.Message}");
            }
        }

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(GRBCaptureOnlyInstruction)}";
    }
}
