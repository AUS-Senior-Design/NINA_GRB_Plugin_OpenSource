using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using Sd.NINA.Demo2.Services;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;

namespace Sd.NINA.Demo2.Demo2Dockables {
    /// <summary>
    /// Dockable panel for the N.I.N.A. Imaging tab.
    /// Shows an altitude chart for the current telescope position AND a live
    /// GRB status dashboard: last alert received, pending GRB, next window.
    /// The status section refreshes every 5 seconds via a background timer.
    /// </summary>
    [Export(typeof(IDockableVM))]
    public class Demo2Dockable : DockableVM, ITelescopeConsumer {
        private INighttimeCalculator nighttimeCalculator;
        private ITelescopeMediator  telescopeMediator;
        private Timer               _refreshTimer;

        [ImportingConstructor]
        public Demo2Dockable(
            IProfileService profileService,
            ITelescopeMediator telescopeMediator,
            INighttimeCalculator nighttimeCalculator) : base(profileService) {

            var dict = new ResourceDictionary();
            dict.Source = new Uri("Sd.NINA.Demo2;component/Demo2Dockables/Demo2DockableTemplates.xaml", UriKind.RelativeOrAbsolute);
            ImageGeometry = (System.Windows.Media.GeometryGroup)dict["Sd.NINA.Demo2_AltitudeSVG"];
            ImageGeometry.Freeze();

            this.nighttimeCalculator = nighttimeCalculator;
            this.telescopeMediator   = telescopeMediator;
            telescopeMediator.RegisterConsumer(this);
            Title = "GRB Monitor";

            System.Threading.Tasks.Task.Run(() => {
                NighttimeData = nighttimeCalculator.Calculate();
                nighttimeCalculator.OnReferenceDayChanged += NighttimeCalculator_OnReferenceDayChanged;
            });

            profileService.LocationChanged += (s, e) =>
                Target?.SetDateAndPosition(NighttimeCalculator.GetReferenceDate(DateTime.Now),
                    profileService.ActiveProfile.AstrometrySettings.Latitude,
                    profileService.ActiveProfile.AstrometrySettings.Longitude);

            profileService.HorizonChanged += (s, e) =>
                Target?.SetCustomHorizon(profileService.ActiveProfile.AstrometrySettings.Horizon);

            // Refresh the GRB status section every 5 seconds
            _refreshTimer = new Timer(_ => RefreshGrbStatus(), null,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        }

        // ── Altitude chart ────────────────────────────────────────────────────────

        private void NighttimeCalculator_OnReferenceDayChanged(object sender, EventArgs e) {
            NighttimeData = nighttimeCalculator.Calculate();
            RaisePropertyChanged(nameof(NighttimeData));
        }

        public void Dispose() {
            _refreshTimer?.Dispose();
            telescopeMediator.RemoveConsumer(this);
        }

        public NighttimeData NighttimeData { get; private set; }
        public TelescopeInfo TelescopeInfo { get; private set; }
        public DeepSkyObject Target        { get; private set; }

        public void UpdateDeviceInfo(TelescopeInfo deviceInfo) {
            if (!IsVisible) return;
            TelescopeInfo = deviceInfo;
            if (TelescopeInfo.Connected && TelescopeInfo.TrackingEnabled && NighttimeData != null) {
                bool showMoon = Target?.Moon.DisplayMoon ?? false;
                if (Target == null || (Target?.Coordinates - deviceInfo.Coordinates)?.Distance.Degree > 1) {
                    Target = new DeepSkyObject("", deviceInfo.Coordinates, "",
                        profileService.ActiveProfile.AstrometrySettings.Horizon);
                    Target.SetDateAndPosition(NighttimeCalculator.GetReferenceDate(DateTime.Now),
                        profileService.ActiveProfile.AstrometrySettings.Latitude,
                        profileService.ActiveProfile.AstrometrySettings.Longitude);
                    if (showMoon) { Target.Refresh(); Target.Moon.DisplayMoon = true; }
                    RaisePropertyChanged(nameof(Target));
                }
            } else {
                Target = null;
                RaisePropertyChanged(nameof(Target));
            }
            RaisePropertyChanged(nameof(TelescopeInfo));
        }

        // ── Live GRB status ───────────────────────────────────────────────────────

        private void RefreshGrbStatus() {
            try {
                var pending = GRBPendingState.PendingGrb;
                var last    = GRBPendingState.LastCapturedGrb;

                PendingGrbName    = pending != null ? pending.Name : "None";
                PendingGrbCoords  = pending != null
                    ? $"RA {pending.RA:F4}°   Dec {pending.Dec:F4}°"
                    : "—";
                PendingGrbTel     = pending?.SpaceTelescope ?? "—";

                LastCapturedName  = last != null ? last.Name : "None yet";
                LastCapturedTime  = last != null
                    ? $"{last.TriggerTime:yyyy-MM-dd HH:mm} UTC"
                    : "—";

                StatusUpdatedAt   = $"Updated {DateTime.UtcNow:HH:mm:ss} UTC";

                RaisePropertyChanged(nameof(PendingGrbName));
                RaisePropertyChanged(nameof(PendingGrbCoords));
                RaisePropertyChanged(nameof(PendingGrbTel));
                RaisePropertyChanged(nameof(LastCapturedName));
                RaisePropertyChanged(nameof(LastCapturedTime));
                RaisePropertyChanged(nameof(StatusUpdatedAt));
                RaisePropertyChanged(nameof(HasPendingGrb));
            } catch (Exception ex) {
                Logger.Warning("[GRB Monitor] Refresh error: " + ex.Message);
            }
        }

        // Bound properties for the dashboard XAML
        public bool   HasPendingGrb     => GRBPendingState.PendingGrb != null;
        public string PendingGrbName    { get; private set; } = "None";
        public string PendingGrbCoords  { get; private set; } = "—";
        public string PendingGrbTel     { get; private set; } = "—";
        public string LastCapturedName  { get; private set; } = "None yet";
        public string LastCapturedTime  { get; private set; } = "—";
        public string StatusUpdatedAt   { get; private set; } = "—";
    }
}
