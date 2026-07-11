using System.ServiceProcess;

namespace StorageWatch.Updater;

internal class ServerRestartHelper
{
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);
    private const int MaxStartAttempts = 5;
    private readonly Action<string>? _diagnosticLogger;

    public ServerRestartHelper()
    {
        _diagnosticLogger = null;
    }

    public ServerRestartHelper(Action<string>? diagnosticLogger)
    {
        _diagnosticLogger = diagnosticLogger;
    }

    private void LogDiag(string message)
    {
        _diagnosticLogger?.Invoke($"[DIAG] {message}");
    }

    public bool TryRestartServer(string serviceName)
    {
        LogDiag($"Restart requested. Component=server, ServiceName={serviceName}");

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Server restart skipped.");
            LogDiag("Server restart skipped because current OS is not Windows.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            Console.WriteLine("Server restart skipped.");
            LogDiag("Server restart skipped because service name is empty.");
            return false;
        }

        try
        {
            Console.WriteLine("Server SCM start begins.");

            using var serviceController = new ServiceController(serviceName);
            var statusBefore = serviceController.Status;
            LogDiag($"Server service status before start: ServiceName={serviceName}, Status={statusBefore}");

            for (var attempt = 1; attempt <= MaxStartAttempts; attempt++)
            {
                try
                {
                    serviceController.Refresh();
                    var currentStatus = serviceController.Status;
                    LogDiag($"SCM start attempt {attempt}/{MaxStartAttempts}. ServiceName={serviceName}, CurrentStatus={currentStatus}");

                    if (currentStatus == ServiceControllerStatus.Running)
                    {
                        Console.WriteLine("Server service already running.");
                        LogDiag($"Server service already running. ServiceName={serviceName}");
                        return true;
                    }

                    if (currentStatus == ServiceControllerStatus.StopPending)
                    {
                        serviceController.WaitForStatus(ServiceControllerStatus.Stopped, StartRetryDelay);
                        serviceController.Refresh();
                    }

                    if (serviceController.Status == ServiceControllerStatus.Stopped)
                    {
                        serviceController.Start();
                    }

                    serviceController.WaitForStatus(ServiceControllerStatus.Running, StartRetryDelay);
                    serviceController.Refresh();
                    if (serviceController.Status == ServiceControllerStatus.Running)
                    {
                        Console.WriteLine("Server service start completed.");
                        LogDiag($"SCM start succeeded. ServiceName={serviceName}, FinalStatus={serviceController.Status}");
                        return true;
                    }
                }
                catch (Exception ex) when (attempt < MaxStartAttempts)
                {
                    LogDiag($"SCM start attempt {attempt} failed. ServiceName={serviceName}, Error={ex.GetType().Name}: {ex.Message}");
                }

                Thread.Sleep(StartRetryDelay);
            }

            serviceController.Refresh();
            var finalStatus = serviceController.Status;
            Console.WriteLine("Server service start failed.");
            LogDiag($"SCM start failed after retries. ServiceName={serviceName}, FinalStatus={finalStatus}, Timeout={StartTimeout}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server restart failed: {ex.Message}");
            LogDiag($"SCM server start threw exception: ServiceName={serviceName}, Error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
