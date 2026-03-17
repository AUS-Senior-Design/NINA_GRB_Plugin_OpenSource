using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using Sd.NINA.Demo2.Services;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Demo2TestCategory {
    /// <summary>
    /// Watches Firestore for new observable GRB alerts.
    /// When one arrives, ShouldTrigger() returns true and Execute() stores it in GRBPendingState
    /// so the GRBCaptureInstruction (in the same sequence) can pick it up and capture images.
    /// </summary>
    [ExportMetadata("Name", "GRB Alert Trigger")]
    [ExportMetadata("Description", "Fires when a new observable GRB is detected via Firestore. Pair with GRB Capture instruction to take images.")]
    [ExportMetadata("Icon", "BulbSVG")]
    [ExportMetadata("Category", "GRB")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class GRBAlertTrigger : SequenceTrigger {
        private readonly IProfileService profileService;

        [ImportingConstructor]
        public GRBAlertTrigger(IProfileService profileService) {
            this.profileService = profileService;
        }

        public override object Clone() {
            return new GRBAlertTrigger(profileService) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description
            };
        }

        /// <summary>Returns true when FirestoreGrbListener has queued a new GRB alert.</summary>
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            return GRBPendingState.PendingGrb != null;
        }

        /// <summary>
        /// The GRB is already in GRBPendingState.
        /// GRBCaptureInstruction will call TakeAndClear() and do the imaging.
        /// </summary>
        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            Logger.Info("[GRB Trigger] Fired — GRBCaptureInstruction will handle imaging.");
            return Task.CompletedTask;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(GRBAlertTrigger)}";
        }

        public override void Teardown() {
            base.Teardown();
        }
    }
}
