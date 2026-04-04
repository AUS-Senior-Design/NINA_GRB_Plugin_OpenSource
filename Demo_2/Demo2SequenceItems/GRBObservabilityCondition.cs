using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.SequenceItem;
using Sd.NINA.Demo2.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Demo2TestCategory {
    /// <summary>
    /// Internal helper — NOT exported to the NINA sequencer UI.
    ///
    /// Observability window management is handled automatically by FirestoreGrbListener:
    ///   - Loop 1 evaluates each GRB against GRBObservabilityService and writes to observable_list.
    ///   - Loop 2 monitors observable_list and only queues a GRB into GRBPendingState when its
    ///     observation window is currently active.
    ///
    /// Because of this, GRBAlertTrigger will naturally never fire outside a valid window,
    /// so no user-facing condition item is needed. This class is kept for internal use only.
    /// </summary>
    // [Export(typeof(ISequenceCondition))]  ← intentionally not exported; window logic is internal
    [JsonObject(MemberSerialization.OptIn)]
    public class GRBObservabilityCondition : SequenceCondition {

        private DateTime? _windowEnd;

        /// <summary>
        /// Optional hard stop for the loop (UTC). When set, the condition becomes false
        /// after this time even if a GRB arrives.  Leave null to run indefinitely.
        /// </summary>
        [JsonProperty]
        public DateTime? WindowEnd {
            get => _windowEnd;
            set {
                _windowEnd = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(WindowEndDisplay));
            }
        }

        public string WindowEndDisplay =>
            _windowEnd.HasValue ? _windowEnd.Value.ToString("HH:mm UTC") : "Not set";

        public GRBObservabilityCondition() { }

        public GRBObservabilityCondition(GRBObservabilityCondition copyMe) : this() {
            WindowEnd = copyMe.WindowEnd;
            CopyMetaData(copyMe);
        }

        public override object Clone() => new GRBObservabilityCondition(this);

        /// <summary>
        /// Returns true (loop keeps going) when:
        ///   1. A GRB is already queued and ready for capture, OR
        ///   2. No hard-stop window end has been set (wait indefinitely), OR
        ///   3. The current time is still before the configured window end.
        /// </summary>
        public override bool Check(ISequenceItem previousItem, ISequenceItem nextItem) {
            bool grbPending = GRBPendingState.PendingGrb != null;
            bool withinWindow = !_windowEnd.HasValue || DateTime.UtcNow < _windowEnd.Value;

            bool result = grbPending || withinWindow;

            if (!result) {
                Logger.Info("[GRB Condition] Observation window expired and no GRB pending — exiting loop.");
            } else if (grbPending) {
                Logger.Info("[GRB Condition] GRB pending — loop continues.");
            } else {
                TimeSpan remaining = _windowEnd.Value - DateTime.UtcNow;
                Logger.Info($"[GRB Condition] Waiting for GRB — window closes in {remaining:hh\\:mm\\:ss}.");
            }

            return result;
        }

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(GRBObservabilityCondition)}, WindowEnd: {WindowEndDisplay}";
    }
}
