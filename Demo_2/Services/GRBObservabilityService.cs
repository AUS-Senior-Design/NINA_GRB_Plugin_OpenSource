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

        double minAlt      => Settings.Default.Altitude;
        double startAlt    => Settings.Default.Altitude;   // start threshold matches min altitude
        double decMin      => Settings.Default.DecMin;
        double decMax      => Settings.Default.DecMax;
        double maxLocalUncertainty => Settings.Default.MaxLocalUncertainty;
        double grbMaxAge   => Settings.Default.GRB_age;
        double countRateThreshold  => Settings.Default.CountRate;
        double flux1       => Settings.Default.Flux_1;
        int    flux2       => Settings.Default.Flux_2;
        double maxMag      => Settings.Default.Mag;
        double snrThreshold => Settings.Default.SNR;
        double minObsWindowHours = 0.1;

        // Accepted space telescopes — built dynamically from the Options checkboxes.
        // "OTHERS" is the catch-all value the Python parser writes for any unlisted telescope.
        HashSet<string> allowedTelescopes {
            get {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Settings.Default.isSwift) { set.Add("Swift"); set.Add("SWIFT"); }
                if (Settings.Default.isEP)    { set.Add("EP"); set.Add("Einstein Probe"); }
                if (Settings.Default.isFermi) { set.Add("Fermi"); set.Add("FERMI"); set.Add("Fermi/GBM"); set.Add("Fermi/LAT"); }
                if (Settings.Default.isSVOM)  { set.Add("SVOM"); }
                if (Settings.Default.isOther) { set.Add("OTHERS"); set.Add("INTEGRAL"); set.Add("MAXI"); set.Add("Insight-HXMT"); set.Add("IceCube"); set.Add("GECAM"); }
                return set;
            }
        }

        // Track GRBs already observed by this observatory to avoid duplicates
        static readonly HashSet<string> alreadyObserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


        // Moon / twilight thresholds — now read from Options (C)
        double astroTwilight => Settings.Default.AstroTwilight;   // Sun altitude for darkness (degrees)
        double maxMoonAlt    => Settings.Default.MaxMoonAlt;      // Maximum moon altitude (degrees)
        double minMoonSep    => Settings.Default.MinMoonSep;      // Minimum moon–GRB separation (degrees)
        double maxMoonPhase  => Settings.Default.MaxMoonPhase;    // Maximum moon illumination (%)

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
            if (result.StartTime.HasValue) {
                double grbAgeHours = (result.StartTime.Value - grb.TriggerTime).TotalHours;
                bool ageCheck = grbAgeHours <= grbMaxAge;
                Logger.Info($"[GRB Obs]   TriggerTime={grb.TriggerTime:HH:mm} UTC  WindowStart={result.StartTime.Value:HH:mm} UTC  Age={grbAgeHours:F1}h  Limit={grbMaxAge}h  AgeCheck={ageCheck}");
                result.IsObservable = ageCheck;
            } else {
                Logger.Info($"[GRB Obs]   No observable window found in next 24h (lat={latitude:F2}, lon={longitude:F2})");
            }

            return result;
        }


        private GRBObservabilityResult Static_Evaluate(GRBEvent grb) {
            var result = new GRBObservabilityResult();

            Logger.Info($"[GRB Obs] Static check for {grb.Name}:");

            // Space telescope filter — if the user checked "Other", accept any telescope not matched elsewhere
            bool telescopeCheck = string.IsNullOrEmpty(grb.SpaceTelescope)
                || allowedTelescopes.Contains(grb.SpaceTelescope)
                || (Settings.Default.isOther && !new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "Swift","SWIFT","EP","Einstein Probe","Fermi","FERMI","Fermi/GBM","Fermi/LAT","SVOM" }
                        .Contains(grb.SpaceTelescope));
            Logger.Info($"[GRB Obs]   Telescope ({grb.SpaceTelescope}): {telescopeCheck}");

            bool notAlreadyObserved = !alreadyObserved.Contains(grb.Name ?? "");
            Logger.Info($"[GRB Obs]   NotAlreadyObserved: {notAlreadyObserved}");

            bool decCheck = grb.Dec >= decMin && grb.Dec <= decMax;
            Logger.Info($"[GRB Obs]   Dec {grb.Dec:F2} in [{decMin},{decMax}]: {decCheck}");

            bool errorCheck = grb.Error <= maxLocalUncertainty;
            Logger.Info($"[GRB Obs]   Error {grb.Error:F1} <= {maxLocalUncertainty}: {errorCheck}");

            bool countRateCheck = !grb.CountRate.HasValue || grb.CountRate >= countRateThreshold;
            Logger.Info($"[GRB Obs]   CountRate {grb.CountRate} >= {countRateThreshold}: {countRateCheck}");

            double fluxThreshold = flux1 * Math.Pow(10, flux2);
            bool fluxCheck = !grb.Flux.HasValue || grb.Flux >= fluxThreshold;
            Logger.Info($"[GRB Obs]   Flux {grb.Flux} >= {fluxThreshold:E2}: {fluxCheck}");

            bool magCheck = !grb.Magnitude.HasValue || grb.Magnitude <= maxMag;
            Logger.Info($"[GRB Obs]   Mag {grb.Magnitude} <= {maxMag}: {magCheck}");

            bool snrCheck = !grb.SNR.HasValue || grb.SNR >= snrThreshold;
            Logger.Info($"[GRB Obs]   SNR {grb.SNR} >= {snrThreshold}: {snrCheck}");

            result.IsObservable = telescopeCheck && notAlreadyObserved && decCheck && errorCheck && countRateCheck && fluxCheck && magCheck && snrCheck;
            Logger.Info($"[GRB Obs]   Static result: {result.IsObservable}");

            return result;
        }

        /// <summary>Call this after a GRB has been successfully captured to prevent duplicate observations.</summary>
        public static void MarkAsObserved(string grbName) {
            if (!string.IsNullOrEmpty(grbName))
                alreadyObserved.Add(grbName);
        }

        /// <summary>Returns true if this GRB has already been captured this session.</summary>
        public static bool IsAlreadyObserved(string grbName) =>
            !string.IsNullOrEmpty(grbName) && alreadyObserved.Contains(grbName);

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

                // Log every step where GRB is up and sky is dark, to diagnose moon failures
                if (darkTime && sourceUp)
                    Logger.Info($"[GRB Scan] {currentTime:HH:mm} UTC | Alt={alt:F1}° | MoonAlt={moonAlt:F1}° (max={maxMoonAlt}) | Sep={separation:F1}° (min={minMoonSep}) | Phase={moonPhase:F1}% (max={maxMoonPhase}) | MoonOK={moonConstraints}");

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



