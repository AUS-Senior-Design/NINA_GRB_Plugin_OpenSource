using Google.Apis.Auth.OAuth2;
using Newtonsoft.Json.Linq;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using Sd.NINA.Demo2.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Sd.NINA.Demo2.Services {
    /// <summary>
    /// Polls Firestore for new GRB alerts every 30 seconds.
    /// Checks observability before queuing — shows a NINA notification if not observable.
    /// When a new observable GRB is found, stores it in GRBPendingState so the
    /// GRBAlertTrigger + GRBCaptureInstruction in the sequence can pick it up.
    /// </summary>
    public class FirestoreGrbListener : IDisposable {
        private CancellationTokenSource cts;
        private readonly HashSet<string> seenDocumentIds = new HashSet<string>();
        private const int PollIntervalSeconds = 30;
        private string _projectId;
        private GoogleCredential _credential;

        // Set from Demo2.cs after the listener is created
        private GRBObservabilityService _observabilityService;

        public FirestoreGrbListener() { }

        /// <summary>
        /// Wire up the observability service so the listener can filter GRBs before queuing.
        /// Call this before Start() from Demo2.cs.
        /// </summary>
        public void SetObservabilityService(GRBObservabilityService service) {
            _observabilityService = service;
            Logger.Info("[GRB Listener] Observability service attached.");
        }

        public void Start(string serviceAccountPath) {
            if (!File.Exists(serviceAccountPath)) {
                Logger.Warning("[GRB Listener] Service account not found: " + serviceAccountPath);
                return;
            }

            try {
                string rawJson = File.ReadAllText(serviceAccountPath);
                _projectId = JObject.Parse(rawJson)["project_id"]?.ToString();
                if (string.IsNullOrEmpty(_projectId)) {
                    Logger.Error("[GRB Listener] No project_id in service account JSON");
                    return;
                }
                _credential = GoogleCredential.FromJson(rawJson)
                    .CreateScoped("https://www.googleapis.com/auth/datastore");
                Logger.Info("[GRB Listener] Credential loaded for project: " + _projectId);
            } catch (Exception ex) {
                Logger.Error("[GRB Listener] Failed to load credential: " + ex.Message);
                return;
            }

            cts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoopAsync(cts.Token));
            Logger.Info("[GRB Listener] Firestore polling started (every " + PollIntervalSeconds + "s)");
        }

        public void Stop() { cts?.Cancel(); }
        public void Dispose() { cts?.Cancel(); cts?.Dispose(); }

        private async Task PollLoopAsync(CancellationToken token) {
            using var httpClient = new HttpClient();
            int pollCount = 0;
            while (!token.IsCancellationRequested) {
                try {
                    pollCount++;
                    Logger.Info("[GRB Listener] Poll #" + pollCount);
                    await PollOnceAsync(httpClient, token);
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    Logger.Error("[GRB Listener] Poll error: " + ex.Message);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), token); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task PollOnceAsync(HttpClient httpClient, CancellationToken token) {
            string accessToken = await _credential.UnderlyingCredential
                .GetAccessTokenForRequestAsync(cancellationToken: token);
            string url = "https://firestore.googleapis.com/v1/projects/" + _projectId
                       + "/databases/(default)/documents/grb_alerts?pageSize=20";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await httpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) {
                Logger.Warning("[GRB Listener] HTTP " + response.StatusCode);
                return;
            }
            string body = await response.Content.ReadAsStringAsync(token);
            ProcessResponse(body);
        }

        private void ProcessResponse(string responseJson) {
            var documents = JObject.Parse(responseJson)["documents"] as JArray;
            Logger.Info("[GRB Listener] Documents in response: " + (documents?.Count.ToString() ?? "none"));
            if (documents == null) return;

            foreach (var doc in documents) {
                string docId = doc["name"]?.ToString();
                if (string.IsNullOrEmpty(docId)) continue;
                if (seenDocumentIds.Contains(docId)) continue;
                seenDocumentIds.Add(docId);

                // ── Map document → GRBEvent ──────────────────────────────────────
                GRBEvent grb;
                try {
                    grb = MapDocumentToGRBEvent(doc);
                } catch (Exception ex) {
                    Logger.Error("[GRB Listener] Error mapping document: " + ex.Message);
                    continue;
                }

                Logger.Info($"[GRB Listener] New alert: {grb.Name} " +
                            $"RA={grb.RA:F4}°  Dec={grb.Dec:F4}°  " +
                            $"Error={grb.Error:F4} arcmin  " +
                            $"Telescope={grb.SpaceTelescope ?? "unknown"}");

                // ── Observability check ──────────────────────────────────────────
                if (_observabilityService != null) {
                    GRBObservabilityResult obsResult;
                    try {
                        obsResult = _observabilityService.Evaluate(grb);
                    } catch (Exception ex) {
                        Logger.Error($"[GRB Listener] Observability check failed for {grb.Name}: {ex.Message}");
                        continue;
                    }

                    if (!obsResult.IsObservable) {
                        // Not observable — notify and skip
                        Logger.Warning($"[GRB Listener] {grb.Name} is NOT observable — skipping.");
                        Notification.ShowWarning($"GRB {grb.Name} is not observable.");
                        continue;
                    }

                    // Observable — show window in the notification
                    string windowStr = (obsResult.StartTime.HasValue && obsResult.EndTime.HasValue)
                        ? $" | Window: {obsResult.StartTime.Value:HH:mm}–{obsResult.EndTime.Value:HH:mm} UTC"
                        : "";
                    Logger.Info($"[GRB Listener] {grb.Name} is OBSERVABLE.{windowStr}");
                    Notification.ShowInformation($"GRB {grb.Name} is observable!{windowStr}");

                } else {
                    // No observability service attached — queue all alerts (testing mode)
                    Logger.Warning("[GRB Listener] No observability service set — queuing without check.");
                    Notification.ShowInformation("GRB Alert: " + grb.Name + " queued for capture.");
                }

                // ── Queue for capture ────────────────────────────────────────────
                GRBPendingState.PendingGrb = grb;
                Logger.Info($"[GRB Listener] {grb.Name} stored in GRBPendingState.");
            }
        }

        private GRBEvent MapDocumentToGRBEvent(JToken doc) {
            var fields = doc["fields"] as JObject;
            string GetString(string key) => fields?[key]?["stringValue"]?.ToString();
            double? GetDouble(string key) {
                var v = fields?[key];
                if (v == null || v["nullValue"] != null) return null;
                if (v["doubleValue"] != null) return v["doubleValue"].Value<double>();
                if (v["integerValue"] != null) return v["integerValue"].Value<double>();
                return null;
            }
            return new GRBEvent {
                Name           = GetString("GRB_name") ?? "Unknown",
                RA             = GetDouble("ra_deg")   ?? 0.0,
                Dec            = GetDouble("dec_deg")  ?? 0.0,
                Error          = (GetDouble("error_deg") ?? 0.0) * 60.0,   // deg → arcmin
                TriggerTime    = ParseTriggerTime(fields),
                SpaceTelescope = GetString("telescope") ?? GetString("instrument") ?? "",
                Magnitude      = GetDouble("magnitude"),
                Flux           = GetDouble("flux"),
                SNR            = GetDouble("snr"),
                CountRate      = GetDouble("peak_count_per_sec"),
            };
        }

        private DateTime ParseTriggerTime(JObject fields) {
            string td = fields?["trigger_date"]?["stringValue"]?.ToString();
            string tt = fields?["trigger_time_utc"]?["stringValue"]?.ToString();
            if (td != null && tt != null && DateTime.TryParse(td + "T" + tt + "Z", out DateTime d1))
                return DateTime.SpecifyKind(d1, DateTimeKind.Utc);
            string ed = fields?["email_date"]?["stringValue"]?.ToString();
            string et = fields?["email_time"]?["stringValue"]?.ToString();
            if (ed != null && et != null && DateTime.TryParseExact(ed + " " + et, "yy/MM/dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime d2))
                return d2;
            return DateTime.UtcNow;
        }
    }
}
