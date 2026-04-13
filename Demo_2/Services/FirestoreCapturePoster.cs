using Google.Apis.Auth.OAuth2;
using Newtonsoft.Json.Linq;
using NINA.Core.Utility;
using Sd.NINA.Demo2.Models;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Sd.NINA.Demo2.Properties;

namespace Sd.NINA.Demo2.Services {
    /// <summary>
    /// Posts a record to Firestore grb_captures collection after NINA finishes capturing
    /// images for a GRB alert. The Python image_analyzer.py watches this collection and
    /// runs image differencing analysis when status == "pending_analysis".
    /// </summary>
    public static class FirestoreCapturePoster {
        private static GoogleCredential _credential;
        private static string _projectId;
        private static readonly HttpClient _http = new HttpClient();

        public static void Initialize(string serviceAccountPath) {
            try {
                string rawJson = File.ReadAllText(serviceAccountPath);
                _projectId = JObject.Parse(rawJson)["project_id"]?.ToString();
                _credential = GoogleCredential.FromJson(rawJson)
                    .CreateScoped("https://www.googleapis.com/auth/datastore");
                Logger.Info("[GRB Poster] Initialized for project: " + _projectId);
            } catch (Exception ex) {
                Logger.Error("[GRB Poster] Failed to initialize: " + ex.Message);
            }
        }

        /// <param name="fitsDirectory">
        /// The actual folder where NINA saved the FITS files.
        /// Pass profileService.ActiveProfile.ImageFileSettings.FilePath from GRBCaptureInstruction
        /// so image_analyzer.py finds the right folder. Falls back to Documents\N.I.N.A if null.
        /// </param>
        public static async Task PostCaptureAsync(GRBEvent grb, int exposureCount, int exposureSeconds, string fitsDirectory = null) {
            if (_credential == null || string.IsNullOrEmpty(_projectId)) {
                Logger.Warning("[GRB Poster] Not initialized — skipping Firestore post.");
                return;
            }
            try {
                string accessToken = await _credential.UnderlyingCredential
                    .GetAccessTokenForRequestAsync();

                string url = "https://firestore.googleapis.com/v1/projects/" + _projectId
                           + "/databases/(default)/documents/grb_captures";

                // Use the real NINA save path if provided, otherwise fall back to default
                string fitsDir = !string.IsNullOrEmpty(fitsDirectory)
                    ? fitsDirectory
                    : System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "N.I.N.A",
                        DateTime.UtcNow.ToString("yyyy-MM-dd"));

                var body = new JObject {
                    ["fields"] = new JObject {
                        ["grb_name"]        = new JObject { ["stringValue"]  = grb.Name },
                        ["ra_deg"]          = new JObject { ["doubleValue"]  = grb.RA },
                        ["dec_deg"]         = new JObject { ["doubleValue"]  = grb.Dec },
                        ["error_arcmin"]    = new JObject { ["doubleValue"]  = grb.Error },
                        ["trigger_time"]    = new JObject { ["stringValue"]  = grb.TriggerTime.ToString("o") },
                        ["capture_time"]    = new JObject { ["stringValue"]  = DateTime.UtcNow.ToString("o") },
                        ["exposure_count"]  = new JObject { ["integerValue"] = exposureCount.ToString() },
                        ["exposure_seconds"]= new JObject { ["integerValue"] = exposureSeconds.ToString() },
                        ["fits_directory"]  = new JObject { ["stringValue"]  = fitsDir },
                        ["status"]          = new JObject { ["stringValue"]  = "pending_analysis" }
                    }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, url) {
                    Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    Logger.Info("[GRB Poster] Posted capture record for " + grb.Name);
                else
                    Logger.Warning("[GRB Poster] HTTP " + response.StatusCode + " posting for " + grb.Name);
            } catch (Exception ex) {
                Logger.Error("[GRB Poster] Failed: " + ex.Message);
            }
        }


        // Insiyah: Posts a new observable GRB alert to the observable_list Firestore collection,
        // including its coordinates, observability window, and site details.
        //
        // halla: changed from always POSTing (which creates a new doc every time) to:
        //   1. first query observable_list for an existing doc with matching grb_name
        //   2. if found → PATCH (update) that doc so we never duplicate a GRB in observable_list
        //   3. if not found → POST (create) a new doc as before
        public static async Task PostObservableAlertAsync(
            GRBEvent grb,
            GRBObservabilityResult obsResult,
            double siteLat,
            double siteLon,
            string mpcCode) {

            if (_credential == null || string.IsNullOrEmpty(_projectId)) {
                Logger.Warning("[GRB Poster] Not initialized — skipping observable_list post.");
                return;
            }
            try {
                string accessToken = await _credential.UnderlyingCredential
                    .GetAccessTokenForRequestAsync();

                string windowStart = obsResult.StartTime.HasValue
                    ? obsResult.StartTime.Value.ToString("o") : "";
                string windowEnd = obsResult.EndTime.HasValue
                    ? obsResult.EndTime.Value.ToString("o") : "";

                var fields = new JObject {
                    ["grb_name"] = new JObject { ["stringValue"] = grb.Name },
                    ["ra_deg"] = new JObject { ["doubleValue"] = grb.RA },
                    ["dec_deg"] = new JObject { ["doubleValue"] = grb.Dec },
                    ["error_arcmin"] = new JObject { ["doubleValue"] = grb.Error },
                    ["telescope"] = new JObject { ["stringValue"] = grb.SpaceTelescope ?? "" },
                    ["trigger_time"] = new JObject { ["stringValue"] = grb.TriggerTime.ToString("o") },
                    ["magnitude"] = new JObject { ["doubleValue"] = grb.Magnitude ?? 0.0 },
                    ["flux"] = new JObject { ["doubleValue"] = grb.Flux ?? 0.0 },
                    ["snr"] = new JObject { ["doubleValue"] = grb.SNR ?? 0.0 },
                    ["window_start"] = new JObject { ["stringValue"] = windowStart },
                    ["window_end"] = new JObject { ["stringValue"] = windowEnd },
                    ["site_lat"] = new JObject { ["doubleValue"] = siteLat },
                    ["site_long"] = new JObject { ["doubleValue"] = siteLon },
                    ["mpc_code"] = new JObject { ["stringValue"] = mpcCode ?? "" },
                    ["recorded_at"] = new JObject { ["stringValue"] = DateTime.UtcNow.ToString("o") },
                    ["status"] = new JObject { ["stringValue"] = "pending" }
                };

                var body = new JObject { ["fields"] = fields };

                // halla: look up whether this GRB already exists in observable_list
                string existingDocName = await FindObservableListDocNameAsync(accessToken, grb.Name);

                if (existingDocName != null) {
                    // halla: doc exists → PATCH to update it in place.
                    // We list every field we're updating in the updateMask so Firestore
                    // knows exactly which fields to overwrite (others are left untouched).
                    string updateMask = "updateMask.fieldPaths=grb_name"
                        + "&updateMask.fieldPaths=ra_deg"
                        + "&updateMask.fieldPaths=dec_deg"
                        + "&updateMask.fieldPaths=error_arcmin"
                        + "&updateMask.fieldPaths=telescope"
                        + "&updateMask.fieldPaths=trigger_time"
                        + "&updateMask.fieldPaths=magnitude"
                        + "&updateMask.fieldPaths=flux"
                        + "&updateMask.fieldPaths=snr"
                        + "&updateMask.fieldPaths=window_start"
                        + "&updateMask.fieldPaths=window_end"
                        + "&updateMask.fieldPaths=site_lat"
                        + "&updateMask.fieldPaths=site_long"
                        + "&updateMask.fieldPaths=mpc_code"
                        + "&updateMask.fieldPaths=recorded_at"
                        + "&updateMask.fieldPaths=status";

                    // existingDocName is already the full resource path, e.g.
                    // "projects/{id}/databases/(default)/documents/observable_list/{docId}"
                    string patchUrl = "https://firestore.googleapis.com/v1/" + existingDocName
                                    + "?" + updateMask;

                    var patchRequest = new HttpRequestMessage(new HttpMethod("PATCH"), patchUrl) {
                        Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
                    };
                    patchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                    var patchResponse = await _http.SendAsync(patchRequest);
                    if (patchResponse.IsSuccessStatusCode)
                        Logger.Info("[GRB Poster] Updated existing observable_list record for " + grb.Name);
                    else
                        Logger.Warning("[GRB Poster] HTTP " + patchResponse.StatusCode
                                     + " patching observable_list for " + grb.Name);

                } else {
                    // halla: doc does not exist → POST to create a new one (original behaviour)
                    // string url = "https://firestore.googleapis.com/v1/projects/" + _projectId
                    //             + "/databases/(default)/documents/observable_list";
                    string postUrl = "https://firestore.googleapis.com/v1/projects/" + _projectId
                                   + "/databases/(default)/documents/observable_list";

                    var postRequest = new HttpRequestMessage(HttpMethod.Post, postUrl) {
                        Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
                    };
                    postRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                    var postResponse = await _http.SendAsync(postRequest);
                    if (postResponse.IsSuccessStatusCode)
                        Logger.Info("[GRB Poster] Created new observable_list record for " + grb.Name);
                    else
                        Logger.Warning("[GRB Poster] HTTP " + postResponse.StatusCode
                                     + " posting to observable_list for " + grb.Name);
                }

            } catch (Exception ex) {
                Logger.Error("[GRB Poster] Failed to post to observable_list: " + ex.Message);
            }
        }

        /// <summary>
        /// Posts a record to grb_captures with status="skipped" and the reason why
        /// the GRB could not be observed (e.g. safety unsafe, shutter timeout).
        /// This lets the Python image_analyzer and any dashboards see missed events.
        /// </summary>
        public static async Task PostSkippedAsync(GRBEvent grb, string reason) {
            if (_credential == null || string.IsNullOrEmpty(_projectId)) return;
            try {
                string accessToken = await _credential.UnderlyingCredential
                    .GetAccessTokenForRequestAsync();

                string url = "https://firestore.googleapis.com/v1/projects/" + _projectId
                           + "/databases/(default)/documents/grb_captures";

                var body = new JObject {
                    ["fields"] = new JObject {
                        ["grb_name"]       = new JObject { ["stringValue"]  = grb.Name },
                        ["ra_deg"]         = new JObject { ["doubleValue"]  = grb.RA },
                        ["dec_deg"]        = new JObject { ["doubleValue"]  = grb.Dec },
                        ["capture_time"]   = new JObject { ["stringValue"]  = DateTime.UtcNow.ToString("o") },
                        ["status"]         = new JObject { ["stringValue"]  = "skipped" },
                        ["skip_reason"]    = new JObject { ["stringValue"]  = reason }
                    }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, url) {
                    Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    Logger.Info("[GRB Poster] Posted skipped record for " + grb.Name + ": " + reason);
                else
                    Logger.Warning("[GRB Poster] HTTP " + response.StatusCode + " posting skip for " + grb.Name);
            } catch (Exception ex) {
                Logger.Error("[GRB Poster] Failed to post skip record: " + ex.Message);
            }
        }

        // halla: helper — queries observable_list for a doc whose grb_name matches.
        // Returns the full Firestore resource name (used as the PATCH URL path) if found,
        // or null if no matching doc exists.
        private static async Task<string> FindObservableListDocNameAsync(string accessToken, string grbName) {
            try {
                // Firestore REST structured query
                string queryUrl = "https://firestore.googleapis.com/v1/projects/" + _projectId
                                + "/databases/(default)/documents:runQuery";

                var queryBody = new JObject {
                    ["structuredQuery"] = new JObject {
                        ["from"] = new JArray {
                            new JObject { ["collectionId"] = "observable_list" }
                        },
                        ["where"] = new JObject {
                            ["fieldFilter"] = new JObject {
                                ["field"] = new JObject { ["fieldPath"] = "grb_name" },
                                ["op"] = "EQUAL",
                                ["value"] = new JObject { ["stringValue"] = grbName }
                            }
                        },
                        ["limit"] = 1
                    }
                };

                var queryRequest = new HttpRequestMessage(HttpMethod.Post, queryUrl) {
                    Content = new StringContent(queryBody.ToString(), Encoding.UTF8, "application/json")
                };
                queryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var queryResponse = await _http.SendAsync(queryRequest);
                if (!queryResponse.IsSuccessStatusCode) return null;

                string responseBody = await queryResponse.Content.ReadAsStringAsync();
                var results = JArray.Parse(responseBody);

                // runQuery returns an array; each element may or may not have a "document" key
                foreach (var result in results) {
                    string docName = result["document"]?["name"]?.ToString();
                    if (!string.IsNullOrEmpty(docName))
                        return docName;
                }

                return null; // no matching doc found
            } catch (Exception ex) {
                Logger.Error("[GRB Poster] Failed to query observable_list: " + ex.Message);
                return null;
            }
        }
    }
}