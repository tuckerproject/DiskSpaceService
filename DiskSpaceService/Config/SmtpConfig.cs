namespace DiskSpaceService.Config
{
    /// <summary>
    /// Configuration settings for SMTP email alert delivery.
    /// </summary>
    public class SmtpConfig
    {
        /// <summary>
        /// Enables or disables SMTP notifications.
        /// </summary>
        public bool EnableSmtp { get; set; }

        /// <summary>
        /// SMTP server hostname.
        /// </summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// SMTP server port number.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Whether to use SSL/TLS for SMTP.
        /// </summary>
        public bool UseSsl { get; set; }

        /// <summary>
        /// Username for SMTP authentication.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Password for SMTP authentication.
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// The email address that alerts will be sent from.
        /// </summary>
        public string FromAddress { get; set; } = string.Empty;

        /// <summary>
        /// The email address that alerts will be sent to.
        /// </summary>
        public string ToAddress { get; set; } = string.Empty;
    }
}