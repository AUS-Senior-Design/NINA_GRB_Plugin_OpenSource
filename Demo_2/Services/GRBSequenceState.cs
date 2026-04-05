using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Services {
    
    /// <summary>
    /// halla: simple static flag that tracks whether the NINA advanced sequence
    /// containing GRBAlertTrigger is currently running.
    /// GRBAlertTrigger sets IsSequenceRunning = true on Initialize() and false on Teardown().
    /// FirestoreGrbListener's Loop 2 checks this before queuing any GRB.
    /// </summary>

    internal class GRBSequenceState {
        public static bool IsSequenceRunning { get; set; } = false;
    }
}
