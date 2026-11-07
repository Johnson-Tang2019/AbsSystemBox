// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace SysBox.NtfsBroker;

public sealed record BrokerPeer(string Sid, int Session, string Image)
{
    public static void VerifyServiceServer(PipeStream pipe, string serviceName, string expectedImage, string expectedArguments)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
            throw new UnauthorizedAccessException();
        // Ordinary users cannot open a LocalSystem process. Bind the pipe PID to the SCM's
        // running own-process service, exact command, account and administrator-protected image.
        ServiceIdentity.Verify(pid, expectedImage, serviceName, expectedImage, expectedArguments);
        BrokerPolicy.VerifyProtected(expectedImage);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetNamedPipeClientComputerNameW(SafePipeHandle handle, StringBuilder name, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out uint value, int length, out uint required);
    public static BrokerPeer Read(PipeStream pipe, bool server, string? serviceName = null, string? expectedImage = null, string expectedArguments = "")
    {
        uint pid;
        if (!server)
        {
            var buffer = new StringBuilder(256);
            var remote = GetNamedPipeClientComputerNameW(pipe.SafePipeHandle, buffer, 256);
            if (remote || Marshal.GetLastWin32Error() != 229)
                throw new UnauthorizedAccessException();
        }
        if (!(server ? GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) : GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)))
            throw new UnauthorizedAccessException();
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid)
            throw new UnauthorizedAccessException();
        var actualImage = new StringBuilder(32768);
        uint imageLength = 32768;
        if (!QueryFullProcessImageNameW(process, 0, actualImage, ref imageLength) || imageLength == 0 || imageLength >= 32768 || !ProcessIdToSessionId(pid, out var processSession))
            throw new UnauthorizedAccessException();
        if (server)
        {
            if (serviceName is null)
                ServiceIdentity.Verify(pid, actualImage.ToString());
            else
                ServiceIdentity.Verify(pid, actualImage.ToString(), serviceName, expectedImage ?? throw new ArgumentNullException(nameof(expectedImage)), expectedArguments);
            return new("S-1-5-18", checked((int)processSession), Path.GetFullPath(actualImage.ToString()));
        }
        if (!OpenProcessToken(process, 8, out var token))
            throw new UnauthorizedAccessException();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (identity.User is null || !GetTokenInformation(token, 12, out var session, 4, out var required) || required != 4 || session != processSession)
                throw new UnauthorizedAccessException();
            var image = new StringBuilder(32768);
            uint size = 32768;
            if (!QueryFullProcessImageNameW(process, 0, image, ref size) || size == 0 || size >= 32768)
                throw new UnauthorizedAccessException();
            var peer = new BrokerPeer(identity.User.Value, checked((int)session), Path.GetFullPath(image.ToString()));
            if (!server)
            {
                string? impersonated = null;
                ((NamedPipeServerStream)pipe).RunAsClient(() => { using var caller = WindowsIdentity.GetCurrent(true); impersonated = caller?.User?.Value; });
                if (peer.Sid != impersonated)
                    throw new UnauthorizedAccessException();
            }
            return peer;
        }
    }
}
