using DiskSpaceService.Config;
using DiskSpaceService.Models;
using DiskSpaceService.Services.Logging;
using System.Collections.Generic;

namespace DiskSpaceService.Services.Alerting
{
    public static class AlertSenderFactory
    {
        public static List<IAlertSender> BuildSenders(
            DiskSpaceConfig config,
            RollingFileLogger logger)
        {
            var list = new List<IAlertSender>();

            if (config.GroupMe?.EnableGroupMe == true)
            {
                logger.Log("[ALERT FACTORY] Adding GroupMeAlertSender.");
                list.Add(new GroupMeAlertSender(config.GroupMe, logger));
            }

            if (config.Smtp?.EnableSmtp == true)
            {
                logger.Log("[ALERT FACTORY] Adding SmtpAlertSender.");
                list.Add(new SmtpAlertSender(config.Smtp, logger));
            }

            if (list.Count == 0)
            {
                logger.Log("[ALERT FACTORY] No alert senders enabled in config.");
            }

            return list;
        }
    }
}