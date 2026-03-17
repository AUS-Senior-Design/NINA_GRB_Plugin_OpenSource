/*
Created by : Insiyah Zujar
Date : 14th March 2026
Last Modified: 14 March 2026
Modification: Added class to create Deep Sky Object Container*/

using NINA.Astrometry;
using NINA.Core.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// Creating DSO
namespace Sd.NINA.Demo2.Models {

    internal class GRBDeepSkyObject : DeepSkyObject {
        public GRBDeepSkyObject(string id, Coordinates coords, string imageRepository, CustomHorizon customHorizon) : base(id, coords, imageRepository, customHorizon) {
        }
    }

}


