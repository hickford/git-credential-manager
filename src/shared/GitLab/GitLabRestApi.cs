using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GitCredentialManager;
using Newtonsoft.Json;

namespace GitLab
{
    public interface IGitLabRestApi : IDisposable
    {
        // https://docs.gitlab.com/ee/api/users.html#list-current-user-for-normal-users
        Task<GitLabUserInfo> GetUserInfoAsync(Uri targetUri, string accessToken);
    }

    public class GitLabRestApi : IGitLabRestApi
    {
        /// <summary>
        /// The maximum wait time for a network request before timing out
        /// </summary>
        private const int RequestTimeout = 15 * 1000; // 15 second limit

        private readonly ICommandContext _context;

        public GitLabRestApi(ICommandContext context)
        {
            EnsureArgument.NotNull(context, nameof(context));

            _context = context;
        }

        // assumes token has 'user' scope
        public async Task<GitLabUserInfo> GetUserInfoAsync(Uri targetUri, string accessToken)
        {
            Uri requestUri = GetApiRequestUri(targetUri, "user");

            _context.Trace.WriteLine($"HTTP: GET {requestUri}");
            using (var request = new HttpRequestMessage(HttpMethod.Get, requestUri))
            {
                request.AddBearerAuthenticationHeader(accessToken);

                using (HttpResponseMessage response = await HttpClient.SendAsync(request))
                {
                    _context.Trace.WriteLine($"HTTP: Response {(int) response.StatusCode} [{response.StatusCode}]");

                    response.EnsureSuccessStatusCode();

                    string json = await response.Content.ReadAsStringAsync();

                    return JsonConvert.DeserializeObject<GitLabUserInfo>(json);
                }
            }
        }

        private Uri GetApiRequestUri(Uri targetUri, string apiUrl)
        {
            var baseUrl = targetUri.GetLeftPart(UriPartial.Authority);
            return new Uri(baseUrl + $"/api/v4/{apiUrl}");
        }

        private HttpClient _httpClient;
        private HttpClient HttpClient
        {
            get
            {
                if (_httpClient is null)
                {
                    _httpClient = _context.HttpClientFactory.CreateClient();

                    // Set the common headers and timeout
                    _httpClient.Timeout = TimeSpan.FromMilliseconds(RequestTimeout);
                }

                return _httpClient;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    // https://docs.gitlab.com/ee/api/users.html#list-current-user-for-normal-users
    public class GitLabUserInfo
    {
        [JsonProperty("username")]
        public string UserName { get; set; }
    }
}
