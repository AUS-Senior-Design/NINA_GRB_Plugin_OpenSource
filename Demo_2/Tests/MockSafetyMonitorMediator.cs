using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Tests {
    /// <summary>
    /// Fake safety monitor for local testing.
    /// Toggle SimulatedIsSafe to test safe/unsafe branches.
    /// </summary>
    public class MockSafetyMonitorMediator : ISafetyMonitorMediator {

        public bool SimulatedIsSafe    { get; set; } = true;
        public bool SimulatedConnected { get; set; } = true;

        // ── IDeviceMediator events ────────────────────────────────────────────
        public event Func<object, EventArgs, Task>          Connected      { add { } remove { } }
        public event Func<object, EventArgs, Task>          Disconnected   { add { } remove { } }

        // ── ISafetyMonitorMediator events ─────────────────────────────────────
        public event EventHandler<IsSafeEventArgs>          IsSafeChanged  { add { } remove { } }

        // ── Methods used by tests / production code ───────────────────────────
        public SafetyMonitorInfo GetInfo() => new SafetyMonitorInfo {
            Connected = SimulatedConnected,
            IsSafe    = SimulatedIsSafe
        };

        public Task<bool> Connect()    => Task.FromResult(true);
        public Task       Disconnect() => Task.CompletedTask;

        public void RegisterConsumer(ISafetyMonitorConsumer consumer) { }
        public void RemoveConsumer(ISafetyMonitorConsumer consumer)   { }
        public void RegisterHandler(ISafetyMonitorVM handler)         { }

        // ── Stub implementations (not used by tests) ──────────────────────────
        public Task<IList<string>> Rescan()                                              => Task.FromResult<IList<string>>(new List<string>());
        public void   Broadcast(SafetyMonitorInfo deviceInfo)                            { }
        public string Action(string actionName, string actionParameters)                 => string.Empty;
        public string SendCommandString(string command, bool raw = true)                 => string.Empty;
        public bool   SendCommandBool(string command, bool raw = true)                   => false;
        public void   SendCommandBlind(string command, bool raw = true)                  { }
        public IDevice GetDevice()                                                        => null;
    }
}
