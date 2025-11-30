using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Windows;

namespace SongRequestDesktopV2Rewrite
{
    class FetchingService
    {
        private readonly HttpClient _httpClient;
        public static string _baseUrl = "http://127.0.0.1:5000/fetch?method=";
        private readonly string _secretKey;

        public FetchingService(string secretKey)
        {
            _httpClient = new HttpClient();
            _secretKey = secretKey;
            _baseUrl = (ConfigService.Instance.Current?.Address ?? "http://127.0.0.1:5000") + "/fetch?method=";
        }

        public async Task<string> SendRequest(string action, string ytid2 = null, string SecondArg = null)
        {
            try
            {

                var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + action);
                request.Headers.Add("Authorization", _secretKey);
                var content = new MultipartFormDataContent();
                if (action == "add-bad-word" || action == "delete-bad-word")
                {
                    if (ytid2 != null)
                    {
                        content.Add(new StringContent(ytid2), "word");
                    }
                }
                else if (action == "set-sendin-allowed")
                {
                    if (ytid2 != null)
                    {
                        content.Add(new StringContent(ytid2), "sendin-allowed");
                    }
                }
                else if (action == "set-now-playing")
                {
                    if (ytid2 != null)
                    {
                        string artist = SecondArg.Replace(" - Topic", "");
                        content.Add(new StringContent(ytid2), "now-playing-title");
                        content.Add(new StringContent(artist), "now-playing-artist");
                    }
                }
                else
                {
                    if (ytid2 != null)
                    {
                        content.Add(new StringContent(ytid2), "ytid");
                    }
                }
                request.Content = content;
                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                string responseContent = await response.Content.ReadAsStringAsync();
                return responseContent;
            }
            catch (Exception ex)
            {
                throw new Exception($"Request failed: {ex.Message}");
            }
        }




        private JObject CreateRequestBody(string ytid2)
        {
            var body = new JObject
            {
                ["secretKey"] = _secretKey
            };

            if (!string.IsNullOrEmpty(ytid2))
            {
                body.Add("ytid", ytid2);
            }

            MessageBox.Show(body.ToString());

            return body;
        }

        private JArray ParseDatabaseResponse(string responseContent)
        {
            JArray parsedResponse = JArray.Parse(responseContent);
            return parsedResponse;
        }
    }
}
