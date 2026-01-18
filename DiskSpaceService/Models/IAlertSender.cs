namespace DiskSpaceService.Models
{
    /// <summary>
    /// Defines a common interface for all alert delivery mechanisms.
    /// Implementations include GroupMe, SMTP, and any future alerting backends.
    /// </summary>
    public interface IAlertSender
    {
        /// <summary>
        /// Sends an alert message using the configured delivery method.
        /// </summary>
        Task SendAlertAsync(string message);
    }
}