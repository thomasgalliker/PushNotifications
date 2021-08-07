﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Newtonsoft.Json;
using PushNotifications.Internals;
using PushNotifications.Logging;

namespace PushNotifications.Google
{
    /// <summary>
    /// Sends push messages to Firebase Cloud Messaging using the FCM v1 HTTP API.
    /// </summary>
    [DebuggerDisplay("FcmClient: {FcmClient.ApiName}")]
    public class FcmClient : IFcmClient
    {
        public const string ApiName = "FCM HTTP v1 API";

        private readonly ILogger logger;
        private readonly HttpClient httpClient;
        private readonly FcmOptions options;
        private readonly ServiceAccountCredential credential;
        private readonly string projectId;

        /// <summary>
        /// Constructs a client instance with given <paramref name="options"/>
        /// for token-based authentication (using a .p8 certificate).
        /// </summary>
        private FcmClient(ILogger logger, HttpClient httpClient)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            this.httpClient.DefaultRequestHeaders.UserAgent.Clear();
            this.httpClient.DefaultRequestHeaders.UserAgent.Add(HttpClientUtils.GetProductInfo(this));
        }

        public FcmClient(FcmOptions options)
            : this(Logger.Current, new HttpClient(), options)
        {
        }

        public FcmClient(ILogger logger, HttpClient httpClient, FcmOptions options)
            : this(logger, httpClient)
        {
            if (logger is null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (httpClient is null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            this.options = options ?? throw new ArgumentNullException(nameof(options));

            string serviceAccountKeyFileContent;
            if (options.ServiceAccountKeyFilePath != null)
            {
                var fileInfo = new FileInfo(options.ServiceAccountKeyFilePath);
                if (fileInfo.Exists)
                {
                    serviceAccountKeyFileContent = File.ReadAllText(options.ServiceAccountKeyFilePath);
                }
                else
                {
                    throw new FileNotFoundException($"Service file (ServiceAccountKeyFilePath) could not be found at: {fileInfo.FullName}");
                }
            }
            else if (options.Credentials != null)
            {
                serviceAccountKeyFileContent = options.Credentials;
            }
            else
            {
                throw new ArgumentException("Either service file path or service file contents must be provided.", nameof(options));
            }

            this.credential = this.CreateServiceAccountCredential(new HttpClientFactory(), serviceAccountKeyFileContent);
            this.projectId = GetProjectId(serviceAccountKeyFileContent);
        }

        private static string GetProjectId(string serviceAccountKeyFileContent)
        {
            var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(serviceAccountKeyFileContent);
            if (dict.TryGetValue("project_id", out var value))
            {
                return value;
            }

            throw new Exception($"Could not read project_id from ServiceAccountKeyFilePath");
        }

        private ServiceAccountCredential CreateServiceAccountCredential(HttpClientFactory httpClientFactory, string credentials)
        {
            var serviceAccountCredential = GoogleCredential.FromJson(credentials)
                .CreateScoped("https://www.googleapis.com/auth/firebase.messaging")
                .UnderlyingCredential as ServiceAccountCredential;

            if (serviceAccountCredential == null)
            {
                throw new Exception($"Error creating ServiceAccountCredential");
            }

            var initializer = new ServiceAccountCredential.Initializer(serviceAccountCredential.Id, serviceAccountCredential.TokenServerUrl)
            {
                User = serviceAccountCredential.User,
                AccessMethod = serviceAccountCredential.AccessMethod,
                Clock = serviceAccountCredential.Clock,
                Key = serviceAccountCredential.Key,
                Scopes = serviceAccountCredential.Scopes,
                HttpClientFactory = httpClientFactory
            };

            return new ServiceAccountCredential(initializer);
        }

        private async Task<string> CreateAccessTokenAsync(CancellationToken cancellationToken)
        {
            // Execute the Request:
            var accessToken = await this.credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            if (accessToken == null)
            {
                throw new Exception("Failed to obtain Access Token for Request");
            }

            return accessToken;
        }

        public async Task<FcmResponse> SendAsync(FcmRequest fcmRequest, CancellationToken ct = default)
        {
            if (fcmRequest == null)
            {
                throw new ArgumentNullException(nameof(fcmRequest));
            }

            if (fcmRequest.Message == null)
            {
                throw new ArgumentNullException($"{nameof(fcmRequest)}.{nameof(fcmRequest.Message)}");
            }

            var url = $"https://fcm.googleapis.com/v1/projects/{this.projectId}/messages:send";
            var request = new HttpRequestMessage(HttpMethod.Post, url);

            var accessToken = await this.CreateAccessTokenAsync(ct).ConfigureAwait(false);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");

            var payload = JsonConvert.SerializeObject(fcmRequest, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            request.Content = new JsonContent(payload);

            var response = await this.httpClient.SendAsync(request, ct).ConfigureAwait(false);

            var responseContentJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            this.logger.Log(LogLevel.Debug, $"SendAsync returned json content:{Environment.NewLine}{responseContentJson}");

            var tokenDebuggerDisplay = $"Token={fcmRequest.Message.Token ?? "null"}";

            var fcmResponse = JsonConvert.DeserializeObject<FcmResponse>(responseContentJson);
            fcmResponse.Token = fcmRequest.Message.Token;  // Assign registration ID to each result in the list

            if (response.StatusCode == HttpStatusCode.OK) // TODO Use if (response.IsSuccessStatusCode)
            {
                this.logger.Log(LogLevel.Info, $"SendAsync to {tokenDebuggerDisplay} successfully completed");
                return fcmResponse;
            }
            else
            {
                this.logger.Log(LogLevel.Error, $"SendAsync to {tokenDebuggerDisplay} failed with StatusCode={(int)response.StatusCode} ({response.StatusCode})");
                return fcmResponse;
            }
        }
    }
}
