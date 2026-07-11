using System.ServiceProcess;

namespace StorageWatch.Updater;

internal class AgentRestartHelper
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private readonly Action<string>? _diagnosticLogger;

    public AgentRestartHelper(Action<string>? diagnosticLogger = null)
    {
        _diagnosticLogger = diagnosticLogger;
    }

    private void LogDiag(string message)
    {
        _diagnosticLogger?.Invoke($"[DIAG] {message}");
    }

    public bool TryStopAgentService(string serviceName)
    {
        LogDiag($"Stop requested. Component=agent, ServiceName={serviceName}");
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Agent service stop skipped.");
            LogDiag("Service stop skipped because current OS is not Windows.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            Console.WriteLine("Agent service stop skipped.");
            LogDiag("Service stop skipped because service name is empty.");
            return false;
        }

        try
        {
            Console.WriteLine("Agent service stop begins.");

            using var serviceController = new ServiceController(serviceName);
            var statusBefore = serviceController.Status;
            LogDiag($"Service stop: {serviceName}, StatusBefore={statusBefore}");

            if (serviceController.Status != ServiceControllerStatus.Stopped &&
                serviceController.Status != ServiceControllerStatus.StopPending)
            {
                serviceController.Stop();
                serviceController.WaitForStatus(ServiceControllerStatus.Stopped, DefaultTimeout);
                LogDiag($"Service stop: {serviceName}, Transition=Stopped");
            }

            Console.WriteLine("Agent service stop completed.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Agent service stop failed: {ex.Message}");
            LogDiag($"Service stop failed: {serviceName}, Error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool TryStartAgentService(string serviceName)
    {
        LogDiag($"Start requested. Component=agent, ServiceName={serviceName}");
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Agent service start skipped.");
            LogDiag("Service start skipped because current OS is not Windows.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            Console.WriteLine("Agent service start skipped.");
            LogDiag("Service start skipped because service name is empty.");
            return false;
        }

        try
        {
            Console.WriteLine("Agent service start begins.");

            using var serviceController = new ServiceController(serviceName);
            var statusBefore = serviceController.Status;
            LogDiag($"Service start: {serviceName}, StatusBefore={statusBefore}");

            serviceController.Start();
            serviceController.WaitForStatus(ServiceControllerStatus.Running, DefaultTimeout);
            var statusAfter = serviceController.Status;
            LogDiag($"Service start: {serviceName}, StatusAfter={statusAfter}");

            Console.WriteLine("Agent service start completed.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Agent service start failed: {ex.Message}");
            LogDiag($"Service start failed: {serviceName}, Error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool TryRestartAgentService(string serviceName)
    {
        LogDiag($"Restart requested. Component=agent, ServiceName={serviceName}");
        if (!TryStopAgentService(serviceName))
            return false;

        return TryStartAgentService(serviceName);
    }
}
