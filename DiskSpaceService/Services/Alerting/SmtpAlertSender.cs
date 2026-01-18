using System.Net;
using System.Net.Mail;
using DiskSpaceService.Config;
using DiskSpaceService.Models;
using DiskSpaceService.Services.Logging;

namespace DiskSpaceService.Services.Alerting
{
    /// <summary>
    /// Sends alert messages using SMTP email.
    /// </summary>
    public class SmtpAlertSender : IAlertSender
    {
        private readonly SmtpConfig _config;
        private readonly RollingFileLogger _logger;

        public SmtpAlertSender(SmtpConfig config, RollingFileLogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task SendAlertAsync(string message)
        {
            if (!_config.EnableSmtp)
            {
                _logger.Log("[SMTP] Skipping send: SMTP is disabled in config.");
                return;
            }

            try
            {
                using var client = new SmtpClient(_config.Host, _config.Port)
                {
                    EnableSsl = _config.UseSsl,
                    Credentials = new NetworkCredential(_config.Username, _config.Password)
                };

                var mail = new MailMessage(_config.FromAddress, _config.ToAddress)
                {
                    Subject = "Disk Space Alert",
                    Body = message
                };

                _logger.Log("[SMTP] Sending alert email.");
                await client.SendMailAsync(mail);
            }
            catch (Exception ex)
            {
                _logger.Log($"[SMTP ERROR] Exception while sending alert: {ex}");
            }
        }
    }
}