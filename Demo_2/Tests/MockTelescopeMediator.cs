using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Tests {
    /// <summary>
    /// Fake telescope mediator for local testing.
    /// Set SimulatedAtPark = true to test the unpark branch.
    /// </summary>
    public class MockTelescopeMediator : ITelescopeMediator {

        public bool SimulatedConnected { get; set; } = true;
        public bool SimulatedAtPark    { get; set; } = false;

        // ── IDeviceMediator events ────────────────────────────────────────────
        public event Func<object, EventArgs, Task> Connected    { add { } remove { } }
        public event Func<object, EventArgs, Task> Disconnected { add { } remove { } }

        // ── ITelescopeMediator events ─────────────────────────────────────────
        public event Func<object, BeforeMeridianFlipEventArgs, Task> BeforeMeridianFlip { add { } remove { } }
        public event Func<object, AfterMeridianFlipEventArgs, Task>  AfterMeridianFlip  { add { } remove { } }
        public event Func<object, EventArgs, Task>                   Parked              { add { } remove { } }
        public event Func<object, EventArgs, Task>                   Homed               { add { } remove { } }
        public event Func<object, EventArgs, Task>                   Unparked            { add { } remove { } }
        public event Func<object, MountSlewedEventArgs, Task>        Slewed              { add { } remove { } }

        // ── Methods used by tests / production code ───────────────────────────
        public TelescopeInfo GetInfo() => new TelescopeInfo {
            Connected = SimulatedConnected,
            AtPark    = SimulatedAtPark
        };

        public Task<bool> UnparkTelescope(IProgress<ApplicationStatus> progress, CancellationToken token) {
            SimulatedAtPark = false;
            return Task.FromResult(true);
        }

        public Task<bool> SlewToCoordinatesAsync(Coordinates coords, CancellationToken token)
            => Task.FromResult(true);

        public Task<bool> SlewToCoordinatesAsync(TopocentricCoordinates coords, CancellationToken token)
            => Task.FromResult(true);

        public Task<bool> SlewToTopocentricCoordinates(TopocentricCoordinates coords, CancellationToken token)
            => Task.FromResult(true);

        public Task<bool> Sync(Coordinates coordinates) => Task.FromResult(true);

        public Task SyncToCoordinatesAsync(Coordinates coords, CancellationToken token)
            => Task.CompletedTask;

        public Task<bool> Connect()    => Task.FromResult(true);
        public Task       Disconnect() => Task.CompletedTask;

        public void RegisterConsumer(ITelescopeConsumer consumer) { }
        public void RemoveConsumer(ITelescopeConsumer consumer)   { }
        public void RegisterHandler(ITelescopeVM handler)         { }

        // ── Stub implementations (not used by tests) ──────────────────────────
        public Task<IList<string>> Rescan()                                              => Task.FromResult<IList<string>>(new List<string>());
        public void   Broadcast(TelescopeInfo deviceInfo)                                { }
        public string Action(string actionName, string actionParameters)                 => string.Empty;
        public string SendCommandString(string command, bool raw = true)                 => string.Empty;
        public bool   SendCommandBool(string command, bool raw = true)                   => false;
        public void   SendCommandBlind(string command, bool raw = true)                  { }
        public IDevice GetDevice()                                                        => null;
        public void   MoveAxis(TelescopeAxes axis, double rate)                          { }
        public void   PulseGuide(GuideDirections direction, int duration)                { }
        public bool   SetTrackingEnabled(bool trackingEnabled)                           => false;
        public bool   SetTrackingMode(TrackingMode trackingMode)                         => false;
        public bool   SetCustomTrackingRate(SiderealShiftTrackingRate rate)              => false;
        public bool   SendToSnapPort(bool start)                                         => false;
        public Coordinates GetCurrentPosition()                                          => null;
        public Task<bool> ParkTelescope(IProgress<ApplicationStatus> progress, CancellationToken token) => Task.FromResult(true);
        public Task   WaitForSlew(CancellationToken token)                               => Task.CompletedTask;
        public Task<bool> FindHome(IProgress<ApplicationStatus> progress, CancellationToken token) => Task.FromResult(true);
        public void   StopSlew()                                                          { }
        public PierSide DestinationSideOfPier(Coordinates coordinates)                  => PierSide.pierUnknown;
        public Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) => Task.FromResult(true);
        public Task  RaiseBeforeMeridianFlip(BeforeMeridianFlipEventArgs e)             => Task.CompletedTask;
        public Task  RaiseAfterMeridianFlip(AfterMeridianFlipEventArgs e)               => Task.CompletedTask;
    }
}
