﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Serilog;

namespace SNPN.Common
{
	public class NetworkService : INetworkService, IDisposable
	{
		private readonly AppConfiguration config;
		private readonly HttpClient httpClient;
		private readonly ILogger logger;

		private Dictionary<NotificationType, string> notificationTypeMapping = new Dictionary<NotificationType, string>
		  {
				{ NotificationType.Badge, "wns/badge" },
				{ NotificationType.Tile, "wns/tile" },
				{ NotificationType.Toast, "wns/toast" }
		  };

		public NetworkService(AppConfiguration configuration, ILogger logger, HttpMessageHandler httpHandler)
		{
			this.config = configuration;
			this.logger = logger;
			this.httpClient = new HttpClient(httpHandler);

			//Winchatty seems to crap itself if the Expect: 100-continue header is there.
			// Should be safe to leave this for every request we make.
			this.httpClient.DefaultRequestHeaders.ExpectContinue = false;
		}

		public async Task<int> WinChattyGetNewestEventId(CancellationToken ct)
		{
			using (var res = await this.httpClient.GetAsync($"{this.config.WinchattyApiBase}getNewestEventId", ct))
			{
				var json = JToken.Parse(await res.Content.ReadAsStringAsync());
				return json["eventId"].ToObject<int>();
			}
		}

		public async Task<JToken> WinChattyWaitForEvent(long latestEventId, CancellationToken ct)
		{
			using (var resEvent = await httpClient.GetAsync($"{this.config.WinchattyApiBase}waitForEvent?lastEventId={latestEventId}&includeParentAuthor=1", ct))
			{
				return JToken.Parse(await resEvent.Content.ReadAsStringAsync());
			}
		}

		public async Task<XDocument> GetTileContent()
		{
			using (var fileStream = await this.httpClient.GetStreamAsync("http://www.shacknews.com/rss?recent_articles=1"))
			{
				return XDocument.Load(fileStream);
			}
		}

		public async Task<bool> ReplyToNotification(string replyText, string parentId, string userName, string password)
		{
			if (string.IsNullOrWhiteSpace(parentId)) { throw new ArgumentNullException(nameof(parentId)); }
			if (string.IsNullOrWhiteSpace(userName)) { throw new ArgumentNullException(nameof(userName)); }
			if (string.IsNullOrWhiteSpace(password)) { throw new ArgumentNullException(nameof(password)); }

			var data = new Dictionary<string, string> {
				 		{ "text", replyText },
				 		{ "parentId", parentId },
				 		{ "username", userName },
				 		{ "password", password }
				 	};

			JToken parsedResponse;

			using (var formContent = new FormUrlEncodedContent(data))
			{
				using (var response = await this.httpClient.PostAsync($"{this.config.WinchattyApiBase}postComment", formContent))
				{
					parsedResponse = JToken.Parse(await response.Content.ReadAsStringAsync());
				}
			}


			var success = parsedResponse["result"]?.ToString().Equals("success");

			return success.HasValue && success.Value;
		}

		public async Task<ResponseResult> SendNotification(QueuedNotificationItem notification, string token)
		{
			var request = new HttpRequestMessage
			{
				RequestUri = new Uri(notification.Uri),
				Method = HttpMethod.Post
			};
			
			request.Headers.Add("Authorization", $"Bearer {token}");

			request.Headers.Add("X-WNS-Type", this.notificationTypeMapping[notification.Type]);

			if (notification.Group != NotificationGroups.None)
			{
				request.Headers.Add("X-WNS-Group", Uri.EscapeUriString(Enum.GetName(typeof(NotificationGroups), notification.Group)));
			}
			if (!string.IsNullOrWhiteSpace(notification.Tag))
			{
				request.Headers.Add("X-WNS-Tag", Uri.EscapeUriString(notification.Tag));
			}
			if (notification.Ttl > 0)
			{
				request.Headers.Add("X-WNS-TTL", notification.Ttl.ToString());
			}

			using (var stringContent = new StringContent(notification.Content.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"))
			{
				request.Content = stringContent;
				using (var response = await this.httpClient.SendAsync(request))
				{
					return this.ProcessResponse(response);
				}
			}
		}

		public async Task<string> GetNotificationToken()
		{
			var data = new FormUrlEncodedContent(new Dictionary<string, string> {
							{ "grant_type", "client_credentials" },
							{ "client_id", this.config.NotificationSid },
							{ "client_secret", this.config.ClientSecret },
							{ "scope", "notify.windows.com" }
						});
			using (var response = await this.httpClient.PostAsync("https://login.live.com/accesstoken.srf", data))
			{
				if (response.StatusCode == HttpStatusCode.OK)
				{
					var responseJson = JToken.Parse(await response.Content.ReadAsStringAsync());
					if (responseJson["access_token"] != null)
					{
						this.logger.Information("Got access token.");
						return responseJson["access_token"].Value<string>();
					}
				}
			}
			return null;
		}

		private ResponseResult ProcessResponse(HttpResponseMessage response)
		{
			//By default, we'll just let it die if we don't know specifically that we can try again.
			var result = ResponseResult.FailDoNotTryAgain;
			this.logger.Verbose("Notification Response Code: {responseStatusCode}", response.StatusCode);
			switch (response.StatusCode)
			{
				case HttpStatusCode.OK:
					result = ResponseResult.Success;
					break;
				case HttpStatusCode.NotFound:
				case HttpStatusCode.Gone:
				case HttpStatusCode.Forbidden:
					result |= ResponseResult.RemoveUser;					
					break;
				case HttpStatusCode.NotAcceptable:
					result = ResponseResult.FailTryAgain;
					break;
				case HttpStatusCode.Unauthorized:
					//Need to refresh the token, so invalidate it and we'll pick up a new one on retry.
					result = ResponseResult.FailTryAgain;
					result |= ResponseResult.InvalidateToken;
					break;
			}
			return result;
		}

		#region IDisposable Support
		private bool disposedValue; // To detect redundant calls

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					this.httpClient?.Dispose();
				}

				// TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
				// TODO: set large fields to null.

				disposedValue = true;
			}
		}

		// TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
		// ~NetworkService() {
		//   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
		//   Dispose(false);
		// }

		// This code added to correctly implement the disposable pattern.
		public void Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
			// TODO: uncomment the following line if the finalizer is overridden above.
			// GC.SuppressFinalize(this);
		}
		#endregion
	}
}