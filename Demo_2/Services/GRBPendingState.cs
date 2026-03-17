using Sd.NINA.Demo2.Models;

namespace Sd.NINA.Demo2.Services {
    /// <summary>
    /// Shared singleton state between GRBAlertTrigger and GRBCaptureInstruction.
    /// The trigger sets PendingGrb when a new observable GRB is detected.
    /// The capture instruction reads and clears it when it runs.
    /// </summary>
    public static class GRBPendingState {
        private static readonly object _lock = new object();
        private static GRBEvent _pendingGrb;

        public static GRBEvent PendingGrb {
            get { lock (_lock) { return _pendingGrb; } }
            set { lock (_lock) { _pendingGrb = value; } }
        }

        /// <summary>Takes and clears the pending GRB atomically. Returns null if nothing is pending.</summary>
        public static GRBEvent TakeAndClear() {
            lock (_lock) {
                var grb = _pendingGrb;
                _pendingGrb = null;
                return grb;
            }
        }
    }
}
