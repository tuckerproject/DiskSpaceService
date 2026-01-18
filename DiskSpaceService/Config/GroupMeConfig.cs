namespace DiskSpaceService.Config
{
    /// <summary>
    /// Configuration settings for GroupMe alert delivery.
    /// </summary>
    public class GroupMeConfig
    {
        /// <summary>
        /// Enables or disables GroupMe notifications.
        /// </summary>
        public bool EnableGroupMe { get; set; }

        /// <summary>
        /// The GroupMe bot ID used to send messages.
        /// </summary>
        public string BotId { get; set; } = string.Empty;
    }
}