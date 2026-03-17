using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Utility;
using Sd.NINA.Demo2.Models;
using System;
using System.Collections.Generic;
using NINA.Core.Model;
using NINA.Core.Utility.Notification;
using NINA.Sequencer.SequenceItem;
using Sd.NINA.Demo2.Properties;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using static NINA.Astrometry.Coordinates;

namespace Sd.NINA.Demo2.Services {
    public class GRBObservabilityService {
        // Observatory location — set via constructor from NINA profile
        private readonly double latitude;
        private readonly double longitude;

        public GRBObservabilityService(double latitude, double longitude) {
            this.latitude = latitude;
            this.longitude = longitude;
        }

        [JsonProperty]
        public double DecMin { get; set; }

        /*

        // Just for the time being in the options
        double minAlt = 25;           // minimum altitude during window (degrees)
        double startAlt = 30;         // minimum altitude at window START (degrees)
        double decMin = -40;
        double decMax = 90;
        double maxLocalUncertainty = 0.5;
        double grbMaxAge = 20;
        double countRateThreshold = 3000;  // cts/s
        double flux1 = 1.0;
        int flux2 = -9;               // flux threshold = 1.0e-9 erg/cm²/s
        double maxMag = 20;
        double snrThreshold = 5;
        double minObsWindowHours = 0.5;  // minimum observable window duration

        */

        // Just for the time being in the options
        double minAlt = Settings.Default.Altitude;           // minimum altitude during window (degrees)
        double startAlt = 30;         // minimum altitude at window START (degrees)
        double decMin = Settings.Default.DecMin;
        double decMax = Settings.Default.DecMax;
        double maxLocalUncertainty = Settings.Default.MaxLocalUncertainty;
        double grbMaxAge = Settings.Default.GRB_age;
        double countRateThreshold = Settings.Default.CountRate;  // cts/s
        double flux1 = Settings.Default.Flux_1;
        int flux2 = Settings.Default.Flux_2;               // flux threshold = 1.0e-9 erg/cm²/s
        double maxMag = Settings.Default.Mag;
        double snrThreshold = Settings.Default.SNR;
        double minObsWindowHours = 0.5;  // minimum observable window duration

        // Accepted space telescopes — only process alerts from these
        readonly HashSet<string> allowedTelescopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Swift", "Fermi", "Fermi/GBM", "Fermi/LAT", "INTEGRAL", "MAXI", "Insight-HXMT"
        };

        // Track GRBs already observed by this observatory to avoid duplicates
        static readonly HashSet<string> alreadyObserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


        // should we add these in options too?
        double astroTwilight = -16.0;   // Sun altitude in degrees
        double maxMoonAlt = 65.0;       // Max moon altitude allowed
        double minMoonSep = 45.0;       // Min angular distance between Moon and GRB
        double maxMoonPhase = 40.0;     // Percent illumination *-* its 40.0
        // changed it to 100 for testing purposes

        public GRBObservabilityResult Evaluate(GRBEvent grb) {

        
            // 1st deterministic evaluation
            GRBObservabilityResult result = Static_Evaluate(grb);
            if (!result.IsObservable) {
                // GRB failed basic criteria → no need to calculate altitude
                return result;
            }


            // 2️nd Compute next 24h observability window
            // IN UTC time
            var obsWindow = GetNext24hObservabilityWindow(grb);

            if (obsWindow.StartTime.HasValue && obsWindow.EndTime.HasValue) {
                result.IsObservable = true;
                result.StartTime = obsWindow.StartTime.Value;
                result.EndTime = obsWindow.EndTime.Value;
            } else {
                // Not observable in the next 24 hours
                result.IsObservable = false;
                result.StartTime = null;
                result.EndTime = null;
            }

            // 3rd Calculate Age if GRB is observable
            // age = obsStart - triggerTime
            if (result.StartTime.HasValue) { //Start time will only have value if observable
                double grbAgeHours = (result.StartTime.Value - grb.TriggerTime).TotalHours;
                bool ageCheck = grbAgeHours <= grbMaxAge;

                Console.WriteLine($"GRB age check (<= {grbMaxAge}h): {ageCheck} ({grbAgeHours:F2}h)");
                result.IsObservable = ageCheck;
            }

            return result;
        }


        private GRBObservabilityResult Static_Evaluate(GRBEvent grb) {
            var result = new GRBObservabilityResult();

            Console.WriteLine("Static Evaluation Debug:");

            // Space telescope filter
            bool telescopeCheck = string.IsNullOrEmpty(grb.SpaceTelescope) ||
                                   allowedTelescopes.Contains(grb.SpaceTelescope);
            Console.WriteLine($"Telescope check ({grb.SpaceTelescope}): {telescopeCheck}");

            // Already observed by this observatory?
            bool notAlreadyObserved = !alreadyObserved.Contains(grb.Name ?? "");
            Console.WriteLine($"Not already observed ({grb.Name}): {notAlreadyObserved}");


            // Declination
            bool decCheck = grb.Dec >= decMin && grb.Dec <= decMax;
            Console.WriteLine($"Dec check ({decMin}-{decMax}): {decCheck}");


            // Local Uncertainty
            bool errorCheck = grb.Error <= maxLocalUncertainty;
            Console.WriteLine($"Error check (<= {maxLocalUncertainty}): {errorCheck}");

            // Count Rate
            bool countRateCheck = !grb.CountRate.HasValue || grb.CountRate >= countRateThreshold;
            Console.WriteLine($"CountRate check (>= {countRateThreshold}): {countRateCheck} ({grb.CountRate})");

            // Flux
            double fluxThreshold = flux1 * Math.Pow(10, flux2);
            bool fluxCheck = !grb.Flux.HasValue || grb.Flux >= fluxThreshold;
            Console.WriteLine($"Flux check (>= {fluxThreshold:E2}): {fluxCheck} ({grb.Flux})");

            // Magnitude
            bool magCheck = !grb.Magnitude.HasValue || grb.Magnitude <= maxMag;
            Console.WriteLine($"Magnitude check (>= {maxMag}): {magCheck} ({grb.Magnitude})");

            // SNR
            bool snrCheck = !grb.SNR.HasValue || grb.SNR >= snrThreshold;
            Console.WriteLine($"SNR check (>= {snrThreshold}): {snrCheck} ({grb.SNR})");

            result.IsObservable = decCheck && errorCheck && countRateCheck && fluxCheck && magCheck && snrCheck;

            return result;
        }

        /// <summary>Call this after a GRB has been successfully captured to prevent duplicate observations.</summary>
        public static void MarkAsObserved(string grbName) {
            if (!string.IsNullOrEmpty(grbName))
                alreadyObserved.Add(grbName);
        }

        public GRBObservabilityResult GetNext24hObservabilityWindow(GRBEvent grb) {

            // Defining Result
            var result = new GRBObservabilityResult {
                IsObservable = false,
                StartTime = null,
                EndTime = null
            };


            // Start and End Time 
            // *_* Should we change this to also be similar to python code
            DateTime startTime = DateTime.UtcNow;
            DateTime endTime = startTime.AddHours(24);


            bool isCurrentlyObservable = false;
            DateTime? obsStart = null;

            // checks every 6 minutes from start time to end time
            double stepHours = 0.1; // 6-minute steps

            var observerInfo = new ObserverInfo {
                Latitude = latitude,
                Longitude = longitude,
            };

            for (double t = 0; t <= 24; t += stepHours) {
                DateTime currentTime = startTime.AddHours(t);
                double jd = AstroUtil.GetJulianDate(currentTime);

                //--- Sun & Moon positions---
                var (moonPos, sunPos) = AstroUtil.GetMoonAndSunPosition(currentTime, jd, observerInfo);

                // Compute current altitude of GRB
                double lstHours = AstroUtil.GetLocalSiderealTime(currentTime, longitude);
                Angle lst = Angle.ByHours(lstHours);
                Angle ha = AstroUtil.GetHourAngle(lst, Angle.ByDegree(grb.RA));
                double alt = AstroUtil.GetAltitude(ha.Degree, latitude, grb.Dec);

                // Altitudes of Sun & Moon
                double sunAlt = AstroUtil.GetAltitude(
                    AstroUtil.GetHourAngle(lst, Angle.ByHours(sunPos.RA)).Degree,
                    latitude,
                    sunPos.Dec
                );

                double moonAlt = AstroUtil.GetAltitude(
                    AstroUtil.GetHourAngle(lst, Angle.ByHours(moonPos.RA)).Degree,
                    latitude,
                    moonPos.Dec
                );



                // Angular separation between Moon and GRB
                double moonRaDeg = Angle.ByHours(moonPos.RA).Degree;           
                double separation = AngularSeparation(grb.RA, grb.Dec, moonRaDeg, moonPos.Dec);

                // Moon illumination fraction (0-1)
                double moonPhase =AstroUtil.GetMoonIllumination(currentTime) * 100.0; // convert to percent

                // ---- Observability constraints ----
                bool darkTime = sunAlt < astroTwilight;
                bool sourceUp = alt >= minAlt;
                bool moonConstraints = moonAlt <= maxMoonAlt &&
                                       separation >= minMoonSep &&
                                       moonPhase <= maxMoonPhase;

                // For Debugging Purposes
                //Console.WriteLine(
                //    $"Time: {currentTime:HH:mm} | " +
                //    $"Alt: {alt:F1} | " +
                //    $"SunAlt: {sunAlt:F1} | " +
                //    $"MoonAlt: {moonAlt:F1} | " +
                //    $"Sep: {separation:F1} | " +
                //    $"Phase: {moonPhase:F1}"
                //);

                bool observable = darkTime && sourceUp && moonConstraints;


                // Start of window: altitude must also be >= startAlt (30°)
                bool validWindowStart = alt >= startAlt;

                if (observable && validWindowStart && !isCurrentlyObservable) {
                    // GRB just became observable at sufficient start altitude
                    obsStart = currentTime;
                    isCurrentlyObservable = true;
                } else if (!observable && isCurrentlyObservable) {
                    // GRB just stopped being observable — check minimum duration
                    double windowHours = (currentTime - obsStart.Value).TotalHours;
                    if (windowHours >= minObsWindowHours) {
                        result.StartTime = obsStart.Value;
                        result.EndTime = currentTime;
                        result.IsObservable = true;
                        break; // return first valid window
                    }
                    // Window too short — reset and keep looking
                    isCurrentlyObservable = false;
                    obsStart = null;
                }
            }

            // GRB still observable at end of 24h — check minimum duration
            if (isCurrentlyObservable && obsStart.HasValue && result.StartTime == null) {
                double windowHours = (endTime - obsStart.Value).TotalHours;
                if (windowHours >= minObsWindowHours) {
                    result.StartTime = obsStart.Value;
                    result.EndTime = endTime;
                    result.IsObservable = true;
                }
            }


            return result;
        }

        private double AngularSeparation(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg) {
            double ra1Rad = DegreesToRadians(ra1Deg);
            double dec1Rad = DegreesToRadians(dec1Deg);
            double ra2Rad = DegreesToRadians(ra2Deg);
            double dec2Rad = DegreesToRadians(dec2Deg);

            double cosAngle =
                Math.Sin(dec1Rad) * Math.Sin(dec2Rad) +
                Math.Cos(dec1Rad) * Math.Cos(dec2Rad) *
                Math.Cos(ra1Rad - ra2Rad);

            // Clamp to avoid NaN from floating point rounding
            cosAngle = Math.Max(-1.0, Math.Min(1.0, cosAngle));

            return RadiansToDegrees(Math.Acos(cosAngle));
        }

        private double DegreesToRadians(double deg) => deg * Math.PI / 180.0;
        private double RadiansToDegrees(double rad) => rad * 180.0 / Math.PI;
    }
}

// Insiyah - Notes
/*IProfileService is an interface provided by NINA.
    It gives you access to:
    The active profile
    Equipment settings
    Observatory settings
    Astrometry settings (this is what we care about) 


 Once you have profileService, you can do something like:

    var lat = profileService.ActiveProfile.AstrometrySettings.Latitude;
    var lon = profileService.ActiveProfile.AstrometrySettings.Longitude;*/

/*
 We compute altiude like:
sin(alt) = sin(dec) * sin(lat) + cos(dec) * cos(lat) * cos(H)

Where:
lat = observer latitude
dec = GRB declination
H = hour angle
H = LST - RA
All in radians.*/



