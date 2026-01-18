using System.Net.Http;
using System.Text;
using System.Text.Json;
using DiskSpaceService.Config;
using DiskSpaceService.Models;
using DiskSpaceService.Services.Logging;

namespace DiskSpaceService.Services.Alerting
{
    /// <summary>
    /// Sends alert messages using the GroupMe Bot API.
    /// </summary>
    public class GroupMeAlertSender : IAlertSender
    {
        private readonly GroupMeConfig _config;
        private readonly HttpClient _httpClient = new();
        private readonly RollingFileLogger _logger;

        public GroupMeAlertSender(GroupMeConfig config, RollingFileLogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task SendAlertAsync(string message)
        {
            if (!_config.EnableGroupMe)
            {
                _logger.Log("[GROUPME] Skipping send: GroupMe is disabled in config.");
                return;
            }

            var payload = new
            {
                bot_id = _config.BotId,
                text = message
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                _logger.Log("[GROUPME] Sending alert via GroupMe Bot API.");
                var response = await _httpClient.PostAsync("https://api.groupme.com/v3/bots/post", content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Log($"[GROUPME ERROR] Non-success status code: {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[GROUPME ERROR] Exception while sending alert: {ex}");
            }
        }
    }
}