using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Tests {
    /// <summary>
    /// Fake dome mediator for local testing — no hardware needed.
    /// Set ShutterStatus manually to simulate any shutter state.
    /// </summary>
    public class MockDomeMediator : IDomeMediator {

        public ShutterState SimulatedShutterStatus { get; set; } = ShutterState.ShutterOpen;
        public bool SimulatedConnected { get; set; } = true;
        public int OpenDelaySeconds  { get; set; } = 2;
        public int CloseDelaySeconds { get; set; } = 2;

        // ── IDeviceMediator events ────────────────────────────────────────────
        public event Func<object, EventArgs, Task>        Connected    { add { } remove { } }
        public event Func<object, EventArgs, Task>        Disconnected { add { } remove { } }

        // ── IDomeMediator events ──────────────────────────────────────────────
        public event EventHandler<EventArgs>              Synced  { add { } remove { } }
        public event Func<object, EventArgs, Task>        Opened  { add { } remove { } }
        public event Func<object, EventArgs, Task>        Closed  { add { } remove { } }
        public event Func<object, EventArgs, Task>        Parked  { add { } remove { } }
        public event Func<object, EventArgs, Task>        Homed   { add { } remove { } }
        public event Func<object, DomeEventArgs, Task>    Slewed  { add { } remove { } }

        // ── Methods used by tests / production code ───────────────────────────
        public DomeInfo GetInfo() => new DomeInfo {
            Connected     = SimulatedConnected,
            ShutterStatus = SimulatedShutterStatus
        };

        public async Task<bool> OpenShutter(CancellationToken token) {
            SimulatedShutterStatus = ShutterState.ShutterOpening;
            await Task.Delay(OpenDelaySeconds * 1000, token);
            SimulatedShutterStatus = ShutterState.ShutterOpen;
            return true;
        }

        public async Task<bool> CloseShutter(CancellationToken token) {
            SimulatedShutterStatus = ShutterState.ShutterClosing;
            await Task.Delay(CloseDelaySeconds * 1000, token);
            SimulatedShutterStatus = ShutterState.ShutterClosed;
            return true;
        }

        public Task<bool> Connect()    => Task.FromResult(true);
        public Task       Disconnect() => Task.CompletedTask;

        public void RegisterConsumer(IDomeConsumer consumer) { }
        public void RemoveConsumer(IDomeConsumer consumer)   { }
        public void RegisterHandler(IDomeVM handler)         { }

        // ── Stub implementations (not used by tests) ──────────────────────────
        public bool IsFollowingScope                                                      => false;
        public Task<IList<string>> Rescan()                                              => Task.FromResult<IList<string>>(new List<string>());
        public void   Broadcast(DomeInfo deviceInfo)                                     { }
        public string Action(string actionName, string actionParameters)                 => string.Empty;
        public string SendCommandString(string command, bool raw = true)                 => string.Empty;
        public bool   SendCommandBool(string command, bool raw = true)                   => false;
        public void   SendCommandBlind(string command, bool raw = true)                  { }
        public IDevice GetDevice()                                                        => null;
        public Task   WaitForDomeSynchronization(CancellationToken cancellationToken)   => Task.CompletedTask;
        public Task<bool> SyncToScopeCoordinates(Coordinates coordinates, PierSide sideOfPier, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> EnableFollowing(CancellationToken cancellationToken)           => Task.FromResult(true);
        public Task<bool> DisableFollowing(CancellationToken cancellationToken)          => Task.FromResult(true);
        public Task<bool> Park(CancellationToken cancellationToken)                      => Task.FromResult(true);
        public Task<bool> FindHome(CancellationToken cancellationToken)                  => Task.FromResult(true);
        public Task<bool> SlewToAzimuth(double degrees, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
