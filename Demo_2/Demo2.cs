using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using Sd.NINA.Demo2.Properties;
using Sd.NINA.Demo2.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Settings = Sd.NINA.Demo2.Properties.Settings;

namespace Sd.NINA.Demo2 {
    /// <summary>
    /// This class exports the IPluginManifest interface and will be used for the general plugin information and options
    /// The base class "PluginBase" will populate all the necessary Manifest Meta Data out of the AssemblyInfo attributes. Please fill these accoringly
    /// 
    /// An instance of this class will be created and set as datacontext on the plugin options tab in N.I.N.A. to be able to configure global plugin settings
    /// The user interface for the settings will be defined by a DataTemplate with the key having the naming convention "Demo2_Options" where Demo2 corresponds to the AssemblyTitle - In this template example it is found in the Options.xaml
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class Demo2 : PluginBase, INotifyPropertyChanged {
        private readonly IPluginOptionsAccessor pluginSettings;
        private readonly IProfileService profileService;
        private readonly IImageSaveMediator imageSaveMediator;
        private FirestoreGrbListener grbListener;

        // Implementing a file pattern
        private readonly ImagePattern exampleImagePattern = new ImagePattern("$$EXAMPLEPATTERN$$", "An example of an image pattern implementation", "Demo_2");

        [ImportingConstructor]
        public Demo2(IProfileService profileService, IOptionsVM options, IImageSaveMediator imageSaveMediator) {
            if (Settings.Default.UpdateSettings) {
                Settings.Default.Upgrade();
                Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Settings.Default);
            }

            //This is the constructor of your Demo2 plugin class — it runs once when NINA loads your plugin.

            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(this.Identifier));
            this.profileService = profileService;

            // Hook into image saving for adding FITS keywords or image file patterns
            this.imageSaveMediator = imageSaveMediator;
            this.imageSaveMediator.BeforeImageSaved += ImageSaveMediator_BeforeImageSaved;
            this.imageSaveMediator.BeforeFinalizeImageSaved += ImageSaveMediator_BeforeFinalizeImageSaved;

            // Register a new image file pattern for the Options > Imaging > File Patterns area
            options.AddImagePattern(exampleImagePattern);

            // Start the GRB background listener — polls Firestore and stores alerts in GRBPendingState
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string serviceAccountPath = Path.Combine(pluginDir, "firebase_service_account.json");

            // Build the observability service using the observatory location from the active NINA profile
            double lat = profileService.ActiveProfile.AstrometrySettings.Latitude;
            double lon = profileService.ActiveProfile.AstrometrySettings.Longitude;
            var observabilityService = new GRBObservabilityService(lat, lon);

            grbListener = new FirestoreGrbListener();
            grbListener.SetObservabilityService(observabilityService);   // attach before Start()
            //Insiyah: To get lat and long which is used in PostObservableAlertAsync
            grbListener.SetProfileService(profileService);
            //----------------------------------------------------------------------------
            grbListener.Start(serviceAccountPath);
            FirestoreCapturePoster.Initialize(serviceAccountPath);
        }

        public override Task Teardown() {
            imageSaveMediator.BeforeImageSaved -= ImageSaveMediator_BeforeImageSaved;
            imageSaveMediator.BeforeFinalizeImageSaved -= ImageSaveMediator_BeforeFinalizeImageSaved;
            grbListener?.Stop();
            grbListener?.Dispose();
            return base.Teardown();
        }


        private Task ImageSaveMediator_BeforeImageSaved(object sender, BeforeImageSavedEventArgs e) {
            // Inject GRB-specific FITS keywords into every image saved during a GRB observation.
            // These keywords follow the IAU FITS standard (max 8 chars, no lowercase).
            var grb = Services.GRBPendingState.LastCapturedGrb;
            if (grb != null) {
                e.Image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(
                    "GRBNAME", grb.Name ?? "UNKNOWN", "GRB name from GCN Circular"));
                e.Image.MetaData.GenericHeaders.Add(new DoubleMetaDataHeader(
                    "GRB_RA", grb.RA, "GRB right ascension (degrees, J2000)"));
                e.Image.MetaData.GenericHeaders.Add(new DoubleMetaDataHeader(
                    "GRB_DEC", grb.Dec, "GRB declination (degrees, J2000)"));
                e.Image.MetaData.GenericHeaders.Add(new DoubleMetaDataHeader(
                    "GRB_ERR", grb.Error, "GRB position error (arcmin)"));
                e.Image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(
                    "GRBTEL", grb.SpaceTelescope ?? "UNKNOWN", "Space telescope that detected the GRB"));
                e.Image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(
                    "GRBTRIG", grb.TriggerTime.ToString("yyyy-MM-ddTHH:mm:ss"), "GRB trigger time (UTC)"));
                if (grb.Magnitude.HasValue)
                    e.Image.MetaData.GenericHeaders.Add(new DoubleMetaDataHeader(
                        "GRBMAG", grb.Magnitude.Value, "GRB optical magnitude from alert"));
                if (grb.Flux.HasValue)
                    e.Image.MetaData.GenericHeaders.Add(new DoubleMetaDataHeader(
                        "GRBFLUX", grb.Flux.Value, "GRB X-ray flux (erg/cm2/s)"));
            }
            return Task.CompletedTask;
        }

        private Task ImageSaveMediator_BeforeFinalizeImageSaved(object sender, BeforeFinalizeImageSavedEventArgs e) {
            // Populate the example image pattern with data. This can provide data that may not be immediately available
            e.AddImagePattern(new ImagePattern(exampleImagePattern.Key, exampleImagePattern.Description, exampleImagePattern.Category) {
                Value = $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.ffffffK}"
            });

            return Task.CompletedTask;
        }

        
        // Options ↓↓


        public double DecMin {
            get => Settings.Default.DecMin;
            set { Settings.Default.DecMin = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double DecMax {
            get => Settings.Default.DecMax;
            set { Settings.Default.DecMax = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double MaxLocalUncertainty {
            get => Settings.Default.MaxLocalUncertainty;
            set { Settings.Default.MaxLocalUncertainty = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public string ObservatoryCode {
            get => Settings.Default.ObservatoryCode;
            set { Settings.Default.ObservatoryCode = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double GRB_age {
            get => Settings.Default.GRB_age;
            set { Settings.Default.GRB_age = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double CountRate {
            get => Settings.Default.CountRate;
            set { Settings.Default.CountRate = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double Flux_1 {
            get => Settings.Default.Flux_1;
            set { Settings.Default.Flux_1 = (float)value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double Flux_2 {
            get => Settings.Default.Flux_2;
            set { Settings.Default.Flux_2 = (int)value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double SNR {
            get => Settings.Default.SNR;
            set { Settings.Default.SNR = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public List<string> AvailableProfiles {
            get {
                return profileService.Profiles.Select(p => p.Name).ToList();
            }

        }

        public double Mag {
            get => Settings.Default.Mag;
            set { Settings.Default.Mag = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }

        }

        public double Altitude {
            get => Settings.Default.Altitude;
            set { Settings.Default.Altitude = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }

        }


        // Options for Telescope
        public bool isSwift {
            get => Settings.Default.isSwift;
            set {
                Settings.Default.isSwift = value;CoreUtil.SaveSettings(Settings.Default);RaisePropertyChanged();
            }
        }
        public bool isEP {
            get => Settings.Default.isEP;
            set {
                Settings.Default.isEP = value; CoreUtil.SaveSettings(Settings.Default);RaisePropertyChanged();
            }
        }
        public bool isFermi {
            get => Settings.Default.isFermi;
            set {
                Settings.Default.isFermi = value;CoreUtil.SaveSettings(Settings.Default);RaisePropertyChanged();
            }
        }
        public bool isSVOM {
            get => Settings.Default.isSVOM;
            set {
                Settings.Default.isSVOM = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged();
            }
        }
        public bool isOther {
            get => Settings.Default.isOther;
            set {
                Settings.Default.isOther = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged();
            }
        }


        // ── Exposure settings (B) ─────────────────────────────────────────────────

        public int ExposureCount {
            get => Settings.Default.ExposureCount;
            set { Settings.Default.ExposureCount = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double ExposureTime {
            get => Settings.Default.ExposureTime;
            set { Settings.Default.ExposureTime = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public int Binning {
            get => Settings.Default.Binning;
            set { Settings.Default.Binning = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        // ── Moon / twilight settings (C) ──────────────────────────────────────────

        public double AstroTwilight {
            get => Settings.Default.AstroTwilight;
            set { Settings.Default.AstroTwilight = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double MaxMoonAlt {
            get => Settings.Default.MaxMoonAlt;
            set { Settings.Default.MaxMoonAlt = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double MinMoonSep {
            get => Settings.Default.MinMoonSep;
            set { Settings.Default.MinMoonSep = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        public double MaxMoonPhase {
            get => Settings.Default.MaxMoonPhase;
            set { Settings.Default.MaxMoonPhase = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        // ── Observatory state settings ────────────────────────────────────────────

        public int MaxWaitMinutes {
            get => Settings.Default.MaxWaitMinutes;
            set { Settings.Default.MaxWaitMinutes = value; CoreUtil.SaveSettings(Settings.Default); RaisePropertyChanged(); }
        }

        // Options ^^

        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
