using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Tests {
    /// <summary>
    /// Fake camera mediator for local testing.
    /// SimulatedIsExposing = true  → camera appears busy.
    /// ExposureFinishesAfterSeconds → how long until IsExposing flips to false.
    ///   Set to 9999 to simulate a camera that never finishes (tests interrupt path).
    /// </summary>
    public class MockCameraMediator : ICameraMediator {

        public bool SimulatedConnected           { get; set; } = true;
        public bool SimulatedIsExposing          { get; set; } = false;
        public int  ExposureFinishesAfterSeconds { get; set; } = 0;

        private bool _timerStarted = false;

        // ── IDeviceMediator events ────────────────────────────────────────────
        public event Func<object, EventArgs, Task> Connected      { add { } remove { } }
        public event Func<object, EventArgs, Task> Disconnected   { add { } remove { } }
        public event Func<object, EventArgs, Task> DownloadTimeout { add { } remove { } }

        // ── Methods used by tests / production code ───────────────────────────
        public CameraInfo GetInfo() {
            if (SimulatedIsExposing && !_timerStarted && ExposureFinishesAfterSeconds > 0) {
                _timerStarted = true;
                Task.Delay(ExposureFinishesAfterSeconds * 1000)
                    .ContinueWith(_ => SimulatedIsExposing = false);
            }
            return new CameraInfo {
                Connected  = SimulatedConnected,
                IsExposing = SimulatedIsExposing
            };
        }

        public Task<bool> Connect()    => Task.FromResult(true);
        public Task       Disconnect() => Task.CompletedTask;

        public void RegisterConsumer(ICameraConsumer consumer) { }
        public void RemoveConsumer(ICameraConsumer consumer)   { }
        public void RegisterHandler(ICameraVM handler)         { }

        // ── Stub implementations (not used by tests) ──────────────────────────
        public bool   AtTargetTemp                                                        => false;
        public double TargetTemp                                                          => 0.0;
        public Task<IList<string>> Rescan()                                              => Task.FromResult<IList<string>>(new List<string>());
        public void   Broadcast(CameraInfo deviceInfo)                                   { }
        public string Action(string actionName, string actionParameters)                 => string.Empty;
        public string SendCommandString(string command, bool raw = true)                 => string.Empty;
        public bool   SendCommandBool(string command, bool raw = true)                   => false;
        public void   SendCommandBlind(string command, bool raw = true)                  { }
        public IDevice GetDevice()                                                        => null;
        public Task   Capture(CaptureSequence sequence, CancellationToken token, IProgress<ApplicationStatus> progress) => Task.CompletedTask;
        public IAsyncEnumerable<IExposureData> LiveView(CancellationToken token)         => throw new NotImplementedException();
        public IAsyncEnumerable<IExposureData> LiveView(CaptureSequence sequence, CancellationToken token) => throw new NotImplementedException();
        public Task<IExposureData> Download(CancellationToken token)                    => throw new NotImplementedException();
        public void   AbortExposure()                                                     { }
        public void   SetReadoutMode(short mode)                                          { }
        public void   SetReadoutModeForNormalImages(short mode)                          { }
        public void   SetBinning(short x, short y)                                       { }
        public void   SetDewHeater(bool onOff)                                           { }
        public Task<bool> CoolCamera(double temperature, TimeSpan duration, IProgress<ApplicationStatus> progress, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> WarmCamera(TimeSpan duration, IProgress<ApplicationStatus> progress, CancellationToken ct) => Task.FromResult(true);
        public void   RegisterCaptureBlock(ICameraConsumer cameraConsumer)               { }
        public void   ReleaseCaptureBlock(ICameraConsumer cameraConsumer)                { }
        public bool   IsFreeToCapture(ICameraConsumer cameraConsumer)                   => true;
        public void   RegisterCaptureBlock(object cameraConsumer)                        { }
        public void   ReleaseCaptureBlock(object cameraConsumer)                         { }
        public bool   IsFreeToCapture(object cameraConsumer)                            => true;
        public void   SetUSBLimit(int usbLimit)                                           { }
        public void   SetSubSambleRectangle(ObservableRectangle observableRectangle)     { }
    }
}
