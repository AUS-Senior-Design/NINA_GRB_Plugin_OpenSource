// Insiyah : This class store the result of whether a GRB is observable or not
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Models {
    public class GRBObservabilityResult {

        public bool IsObservable { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }
}
