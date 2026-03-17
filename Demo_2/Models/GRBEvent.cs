// - Insiyah: File file creates an class specifing the parameter in each GRB Event / Object
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Models {
    public class GRBEvent {

        public string Name { get; set; }
        public DateTime TriggerTime { get; set; }

        // In degrees 
        public double RA { get; set; }
        public double Dec { get; set; }

        // In arcmins
        public double Error { get; set; }

        // Telescope that detected the GRB (e.g. "Swift", "Fermi/GBM", "INTEGRAL")
        public string SpaceTelescope { get; set; }

        // Optional - For now

        public double? Magnitude { get; set; }
        public double? Flux { get; set; }
        public double? CountRate { get; set; }
        public double? SNR { get; set; }
    }
}
