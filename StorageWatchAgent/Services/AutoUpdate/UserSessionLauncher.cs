using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using StorageWatch.Services.Logging;

namespace StorageWatch.Services.AutoUpdate;

public interface IUserSessionLauncher
{
    bool TryRestartUI(string uiExecutablePath, out int? sessionId);
}

public sealed class UserSessionLauncher : IUserSessionLauncher
{
    private readonly RollingFileLogger _logger;

    public UserSessionLauncher(RollingFileLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool TryRestartUI(string uiExecutablePath, out int? sessionId)
    {
        sessionId = null;

        if (!OperatingSystem.IsWindows())
        {
            _logger.Log("[UI-RESTART] Unsupported platform for user-session launch; restart skipped.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(uiExecutablePath) || !File.Exists(uiExecutablePath))
        {
            _logger.Log($"[UI-RESTART] UI executable path invalid or missing; restart skipped. Path={uiExecutablePath}");
            return false;
        }

        var activeSessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (activeSessionId == NativeMethods.InvalidSessionId)
        {
            _logger.Log("[UI-RESTART] No interactive session detected; UI restart skipped.");
            return false;
        }

        sessionId = unchecked((int)activeSessionId);

        IntPtr userToken = IntPtr.Zero;
        IntPtr duplicatedToken = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;

        try
        {
            if (!NativeMethods.WTSQueryUserToken(activeSessionId, out userToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to query user token for active session.");
            }

            var tokenAttributes = new NativeMethods.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>()
            };

            if (!NativeMethods.DuplicateTokenEx(
                    userToken,
                    NativeMethods.TOKEN_ALL_ACCESS,
                    ref tokenAttributes,
                    NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                    NativeMethods.TOKEN_TYPE.TokenPrimary,
                    out duplicatedToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to duplicate user token.");
            }

            if (!NativeMethods.CreateEnvironmentBlock(out environmentBlock, duplicatedToken, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create environment block for user session.");
            }

            var startupInfo = new NativeMethods.STARTUPINFO
            {
                cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                lpDesktop = @"winsta0\default"
            };

            var processInfo = new NativeMethods.PROCESS_INFORMATION();
            var commandLine = Quote(uiExecutablePath);

            var created = NativeMethods.CreateProcessAsUser(
                duplicatedToken,
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                environmentBlock,
                Path.GetDirectoryName(uiExecutablePath),
                ref startupInfo,
                out processInfo);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create UI process in user session.");
            }

            if (processInfo.hProcess != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processInfo.hProcess);
            }

            if (processInfo.hThread != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processInfo.hThread);
            }

            _logger.Log($"[UI-RESTART] UI launched in user session. SessionId={sessionId}, Path={uiExecutablePath}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log($"[UI-RESTART] User-session launch failed. SessionId={sessionId}, Error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyEnvironmentBlock(environmentBlock);
            }

            if (duplicatedToken != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(duplicatedToken);
            }

            if (userToken != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(userToken);
            }
        }
    }

    private static string Quote(string value)
    {
        return $"\"{value}\"";
    }

    private static class NativeMethods
    {
        public const uint InvalidSessionId = 0xFFFFFFFF;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint TOKEN_ALL_ACCESS = 0x000F01FF;

        [DllImport("kernel32.dll")]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            ref SECURITY_ATTRIBUTES lpTokenAttributes,
            SECURITY_IMPERSONATION_LEVEL ImpersonationLevel,
            TOKEN_TYPE TokenType,
            out IntPtr phNewToken);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        public enum SECURITY_IMPERSONATION_LEVEL
        {
            SecurityAnonymous,
            SecurityIdentification,
            SecurityImpersonation,
            SecurityDelegation
        }

        public enum TOKEN_TYPE
        {
            TokenPrimary = 1,
            TokenImpersonation
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }
    }
}
