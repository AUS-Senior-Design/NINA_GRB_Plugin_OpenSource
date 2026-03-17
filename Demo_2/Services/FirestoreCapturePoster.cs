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

        public static async Task PostCaptureAsync(GRBEvent grb, int exposureCount, int exposureSeconds) {
            if (_credential == null || string.IsNullOrEmpty(_projectId)) {
                Logger.Warning("[GRB Poster] Not initialized — skipping Firestore post.");
                return;
            }
            try {
                string accessToken = await _credential.UnderlyingCredential
                    .GetAccessTokenForRequestAsync();

                string url = "https://firestore.googleapis.com/v1/projects/" + _projectId
                           + "/databases/(default)/documents/grb_captures";

                // NINA saves images to Documents\N.I.N.A\{date} — pass today's folder
                string fitsDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "N.I.N.A",
                    DateTime.UtcNow.ToString("yyyy-MM-dd"));

                var body = new JObject {
                    ["fields"] = new JObject {
                        ["grb_name"]         = new JObject { ["stringValue"]  = grb.Name },
                        ["ra_deg"]           = new JObject { ["doubleValue"]  = grb.RA },
                        ["dec_deg"]          = new JObject { ["doubleValue"]  = grb.Dec },
                        ["error_arcmin"]     = new JObject { ["doubleValue"]  = grb.Error },
                        ["capture_time"]     = new JObject { ["stringValue"]  = DateTime.UtcNow.ToString("o") },
                        ["exposure_count"]   = new JObject { ["integerValue"] = exposureCount.ToString() },
                        ["exposure_seconds"] = new JObject { ["integerValue"] = exposureSeconds.ToString() },
                        ["fits_directory"]   = new JObject { ["stringValue"]  = fitsDir },
                        ["status"]           = new JObject { ["stringValue"]  = "pending_analysis" }
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
    }
}
