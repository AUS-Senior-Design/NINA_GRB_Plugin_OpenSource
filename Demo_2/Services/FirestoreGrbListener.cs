using Google.Apis.Auth.OAuth2;
using Newtonsoft.Json.Linq;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Profile.Interfaces;
using Sd.NINA.Demo2.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Sd.NINA.Demo2.Properties;


namespace Sd.NINA.Demo2.Services {
    /// <summary>
    /// Two independent polling loops:
    ///   Loop 1 — polls final_grb_alert every 30s, evaluates observability, writes to observable_list.
    ///   Loop 2 — polls observable_list every 30s, picks the GRB with the earliest EndTime,
    ///             queues it for capture if its window is now active, then removes it from observable_list.
    /// </summary>
    public class FirestoreGrbListener : IDisposable {

        // halla: two separate cancellation sources, one per loop
        // private CancellationTokenSource cts;
        private CancellationTokenSource _alertCts;      // Loop 1 — final_grb_alert
        private CancellationTokenSource _queueCts;      // Loop 2 — observable_list

        // halla: removed seenDocumentIds — we now re-evaluate every doc on every poll
        // because final_grb_alert docs get updated in place (same doc ID, new data),
        // so skipping seen IDs would cause us to miss updates to existing GRBs.
        // private readonly HashSet<string> seenDocumentIds = new HashSet<string>();

        // halla: track the last-seen data per GRB name so we only re-evaluate when the doc actually changed
        private readonly Dictionary<string, string> lastSeenDataByGrbName = new Dictionary<string, string>();

        private const int PollIntervalSeconds = 30;
        private string _projectId;
        private GoogleCredential _credential;

        // Set from Demo2.cs after the listener is created
        private GRBObservabilityService _observabilityService;

        //Insiyah: To insert profile lat and long--------
        private IProfileService _profileService;
        //------------------------------------------------

        public FirestoreGrbListener() { }

        /// <summary>
        /// Wire up the observability service so the listener can filter GRBs before queuing.
        /// Call this before Start() from Demo2.cs.
        /// </summary>
        public void SetObservabilityService(GRBObservabilityService service) {
            _observabilityService = service;
            Logger.Info("[GRB Listener] Observability service attached.");
        }

        // Insiyah : To get lat and long setting up service-----------
        public void SetProfileService(IProfileService profileService) {
            _profileService = profileService;
            Logger.Info("[GRB Listener] Profile service attached.");
        }
        //--------------------------------------------------------------

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

            // halla: start both loops independently
            // cts = new CancellationTokenSource();
            // _ = Task.Run(() => PollLoopAsync(cts.Token));
            _alertCts = new CancellationTokenSource();
            _queueCts = new CancellationTokenSource();
            _ = Task.Run(() => AlertPollLoopAsync(_alertCts.Token));
            _ = Task.Run(() => QueuePollLoopAsync(_queueCts.Token));

            Logger.Info("[GRB Listener] Firestore polling started (every " + PollIntervalSeconds + "s)");
        }

        // halla: stop both loops
        // public void Stop() { cts?.Cancel(); }
        public void Stop() {
            _alertCts?.Cancel();
            _queueCts?.Cancel();
        }

        // halla: dispose both
        // public void Dispose() { cts?.Cancel(); cts?.Dispose(); }
        public void Dispose() {
            _alertCts?.Cancel(); _alertCts?.Dispose();
            _queueCts?.Cancel(); _queueCts?.Dispose();
        }

        // ── Loop 1: final_grb_alert → observability → observable_list ────────────

        private async Task AlertPollLoopAsync(CancellationToken token) {
            using var httpClient = new HttpClient();
            int pollCount = 0;
            while (!token.IsCancellationRequested) {
                try {
                    pollCount++;
                    Logger.Info("[GRB Listener] Alert poll #" + pollCount);
                    await AlertPollOnceAsync(httpClient, token);
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    Logger.Error("[GRB Listener] Alert poll error: " + ex.Message);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), token); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task AlertPollOnceAsync(HttpClient httpClient, CancellationToken token) {
            string accessToken = await _credential.UnderlyingCredential
                .GetAccessTokenForRequestAsync(cancellationToken: token);

            // halla: poll final_grb_alert instead of grb_alerts.
            // final_grb_alert has exactly one document per GRB (merged/updated by Python),
            // so we never double-process the same GRB from multiple raw alerts.
            // string url = "https://firestore.googleapis.com/v1/projects/" + _projectId
            //            + "/databases/(default)/documents/grb_alerts?pageSize=40";
            string url = "https://firestore.googleapis.com/v1/projects/" + _projectId
                       + "/databases/(default)/documents/final_grb_alert?pageSize=100";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await httpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) {
                Logger.Warning("[GRB Listener] HTTP " + response.StatusCode);
                return;
            }
            string body = await response.Content.ReadAsStringAsync(token);
            ProcessAlertResponse(body);
        }

        private void ProcessAlertResponse(string responseJson) {
            var documents = JObject.Parse(responseJson)["documents"] as JArray;
            Logger.Info("[GRB Listener] Documents in response: " + (documents?.Count.ToString() ?? "none"));
            if (documents == null) return;

            foreach (var doc in documents) {
                string docId = doc["name"]?.ToString();
                if (string.IsNullOrEmpty(docId)) continue;

                // halla: instead of skipping seen doc IDs, we fingerprint the document's
                // data and skip only if the content hasn't changed since last poll.
                // This lets us re-evaluate when Python updates a GRB's record in place.
                // if (seenDocumentIds.Contains(docId)) continue;
                // seenDocumentIds.Add(docId);
                string docFingerprint = doc["fields"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
                string grbNameForCheck = doc["fields"]?["GRB_name"]?["stringValue"]?.ToString() ?? docId;
                if (lastSeenDataByGrbName.TryGetValue(grbNameForCheck, out string lastFingerprint)
                    && lastFingerprint == docFingerprint) {
                    continue;
                }
                lastSeenDataByGrbName[grbNameForCheck] = docFingerprint;

                // ── Map document → GRBEvent ──────────────────────────────────────
                GRBEvent grb;
                try {
                    grb = MapDocumentToGRBEvent(doc);
                } catch (Exception ex) {
                    Logger.Error("[GRB Listener] Error mapping document: " + ex.Message);
                    continue;
                }

                Logger.Info($"[GRB Listener] New or updated alert: {grb.Name} " +
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
                        Logger.Warning($"[GRB Listener] {grb.Name} is NOT observable — skipping.");
                        Notification.ShowWarning($"GRB {grb.Name} is not observable.");
                        continue;
                    }

                    string windowStr = (obsResult.StartTime.HasValue && obsResult.EndTime.HasValue)
                        ? $" | Window: {obsResult.StartTime.Value:HH:mm}–{obsResult.EndTime.Value:HH:mm} UTC"
                        : "";
                    Logger.Info($"[GRB Listener] {grb.Name} is OBSERVABLE.{windowStr}");
                    Notification.ShowInformation($"GRB {grb.Name} is observable!{windowStr}");

                    // Insiyah: If observable we are storing the grb event into the collection: observable_list
                    // also stored: observable window, site_lat and site_long, MPC
                    _ = FirestoreCapturePoster.PostObservableAlertAsync(
                            grb,
                            obsResult,
                            _profileService.ActiveProfile.AstrometrySettings.Latitude,
                            _profileService.ActiveProfile.AstrometrySettings.Longitude,
                            Settings.Default.ObservatoryCode
                        );
                    //--------------------------------------------------------------

                } else {
                    Logger.Warning("[GRB Listener] No observability service set — queuing without check.");
                    Notification.ShowInformation("GRB Alert: " + grb.Name + " queued for capture.");
                }

                // halla: queuing is now handled by Loop 2 (QueuePollLoopAsync) which reads
                // observable_list, picks the earliest deadline, and queues when the window is active.
                // GRBPendingState.PendingGrb = grb;
                // Logger.Info($"[GRB Listener] {grb.Name} stored in GRBPendingState.");
            }
        }

        // ── Loop 2: observable_list → pick earliest deadline → queue if window is now ──

        private async Task QueuePollLoopAsync(CancellationToken token) {
            using var httpClient = new HttpClient();
            int pollCount = 0;
            while (!token.IsCancellationRequested) {
                try {
                    pollCount++;
                    Logger.Info("[GRB Queue] Queue poll #" + pollCount);
                    await QueuePollOnceAsync(httpClient, token);
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    Logger.Error("[GRB Queue] Queue poll error: " + ex.Message);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), token); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task QueuePollOnceAsync(HttpClient httpClient, CancellationToken token) {
            string accessToken = await _credential.UnderlyingCredential
                .GetAccessTokenForRequestAsync(cancellationToken: token);

            // halla: fetch all docs from observable_list
            string url = "https://firestore.googleapis.com/v1/projects/" + _projectId
                       + "/databases/(default)/documents/observable_list?pageSize=100";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await httpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) {
                Logger.Warning("[GRB Queue] HTTP " + response.StatusCode + " fetching observable_list");
                return;
            }

            string body = await response.Content.ReadAsStringAsync(token);
            var documents = JObject.Parse(body)["documents"] as JArray;
            if (documents == null || documents.Count == 0) {
                Logger.Info("[GRB Queue] observable_list is empty — nothing to queue.");
                return;
            }

            // halla: parse each doc into a (GRBEvent, startTime, endTime, docName) tuple,
            // then pick the one with the earliest EndTime (earliest deadline first)
            var candidates = new List<(GRBEvent grb, DateTime startTime, DateTime endTime, string docName)>();

            foreach (var doc in documents) {
                try {
                    string docName = doc["name"]?.ToString();
                    var fields = doc["fields"] as JObject;
                    if (fields == null || string.IsNullOrEmpty(docName)) continue;

                    string windowStartStr = fields["window_start"]?["stringValue"]?.ToString();
                    string windowEndStr = fields["window_end"]?["stringValue"]?.ToString();

                    if (!DateTime.TryParse(windowStartStr, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime windowStart)) continue;
                    if (!DateTime.TryParse(windowEndStr, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime windowEnd)) continue;

                    GRBEvent grb = MapDocumentToGRBEvent(doc);
                    candidates.Add((grb, windowStart, windowEnd, docName));
                } catch (Exception ex) {
                    Logger.Error("[GRB Queue] Failed to parse observable_list doc: " + ex.Message);
                }
            }

            if (candidates.Count == 0) return;

            // halla: earliest deadline first — pick the candidate whose window ends soonest
            var earliest = candidates.OrderBy(c => c.endTime).First();

            DateTime now = DateTime.UtcNow;

            if (now >= earliest.startTime && now < earliest.endTime) {
                // halla: window is active — queue the GRB for capture
                GRBPendingState.PendingGrb = earliest.grb;
                Logger.Info($"[GRB Queue] Window active — queued {earliest.grb.Name} " +
                            $"(window {earliest.startTime:HH:mm}–{earliest.endTime:HH:mm} UTC)");
                Notification.ShowInformation($"GRB {earliest.grb.Name} queued for capture.");

                // halla: remove from observable_list so it doesn't get queued again
                await DeleteObservableListDocAsync(httpClient, accessToken, earliest.docName);

            } else if (now < earliest.startTime) {
                // halla: window hasn't opened yet — just log and wait
                Logger.Info($"[GRB Queue] Next GRB is {earliest.grb.Name} — " +
                            $"window opens at {earliest.startTime:HH:mm} UTC, not yet.");
            } else {
                // halla: window has already expired — remove the stale doc and log it
                Logger.Warning($"[GRB Queue] Window for {earliest.grb.Name} has expired " +
                               $"(ended {earliest.endTime:HH:mm} UTC) — removing from observable_list.");
                await DeleteObservableListDocAsync(httpClient, accessToken, earliest.docName);
            }
        }

        // halla: deletes a document from observable_list by its full Firestore resource name
        private async Task DeleteObservableListDocAsync(HttpClient httpClient, string accessToken, string docName) {
            try {
                string deleteUrl = "https://firestore.googleapis.com/v1/" + docName;
                var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
                deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var deleteResponse = await httpClient.SendAsync(deleteRequest);
                if (deleteResponse.IsSuccessStatusCode)
                    Logger.Info("[GRB Queue] Removed from observable_list: " + docName);
                else
                    Logger.Warning("[GRB Queue] HTTP " + deleteResponse.StatusCode
                                 + " deleting from observable_list: " + docName);
            } catch (Exception ex) {
                Logger.Error("[GRB Queue] Failed to delete from observable_list: " + ex.Message);
            }
        }

        // ── Shared helpers ────────────────────────────────────────────────────────

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
                Name = GetString("GRB_name") ?? GetString("grb_name") ?? "Unknown",
                RA = GetDouble("ra_deg") ?? 0.0,
                Dec = GetDouble("dec_deg") ?? 0.0,
                // halla: observable_list stores error_arcmin, final_grb_alert stores error_deg —
                // try both field names so MapDocumentToGRBEvent works for docs from either collection
                // Error = (GetDouble("error_deg") ?? 0.0) * 60.0,
                Error = GetDouble("error_arcmin") ?? (GetDouble("error_deg") ?? 0.0) * 60.0,
                TriggerTime = ParseTriggerTime(fields),
                SpaceTelescope = GetString("telescope") ?? GetString("instrument") ?? "",
                Magnitude = GetDouble("magnitude"),
                Flux = GetDouble("flux"),
                SNR = GetDouble("snr"),
                CountRate = GetDouble("peak_count_per_sec"),
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
            // halla: observable_list stores trigger_time as an ISO 8601 string
            string tt2 = fields?["trigger_time"]?["stringValue"]?.ToString();
            if (!string.IsNullOrEmpty(tt2) && DateTime.TryParse(tt2, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime d3))
                return d3;
            return DateTime.UtcNow;
        }
    }
}