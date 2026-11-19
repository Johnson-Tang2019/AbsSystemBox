// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.Native;

public sealed partial class WindowsPeripheralDiscovery
{
    private static readonly TimeSpan ConfigurationTimeout = TimeSpan.FromSeconds(8);

    public Task<Result<MousePerformanceReadout>> SetMouseDpiAsync(string deviceId, string expectedRevision,
        int stage, int dpi, CancellationToken cancellationToken) =>
        ConfigureAsync(deviceId, PeripheralKind.Mouse,
            (path, token) => WriteMouseDpi(path, expectedRevision, stage, dpi, token), cancellationToken);

    public Task<Result<KeyboardLightingReadout>> SetKeyboardLightingAsync(string deviceId, string expectedRevision,
        KeyboardLightingChange change, CancellationToken cancellationToken) =>
        ConfigureAsync(deviceId, PeripheralKind.Keyboard,
            (path, token) => WriteKeyboardLighting(path, expectedRevision, change, token), cancellationToken);

    private Task<Result<T>> ConfigureAsync<T>(string deviceId, PeripheralKind kind,
        Func<string, CancellationToken, Result<T>> write, CancellationToken cancellationToken) where T : notnull =>
        RunOperationAsync(token =>
        {
            if (string.IsNullOrWhiteSpace(deviceId) || !deviceId.EndsWith($":{kind}", StringComparison.Ordinal) ||
                !writableInterfaces.TryGetValue(deviceId, out var path))
                return OperationFailure<T>("该设备的可写设置尚未确认，请连接设备并刷新。");
            return write(path, token);
        }, ConfigurationTimeout, "外设写入未在限时内完成。请先刷新核对设备当前设置。", cancellationToken);

    private static Result<T> OperationFailure<T>(string message, bool retryable = true) where T : notnull =>
        Result<T>.Failure(new SysBoxError(ErrorCodes.Unavailable, message, retryable: retryable));

    internal static Dictionary<string, string> SelectWritableInterfaces(IReadOnlyList<PeripheralInterface> interfaces,
        IReadOnlyList<PeripheralDevice> devices)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in interfaces)
        {
            var kind = endpoint.MouseReadout?.Performance?.EditRevision is not null ? PeripheralKind.Mouse :
                endpoint.KeyboardLighting?.EditRevision is not null ? PeripheralKind.Keyboard : (PeripheralKind?)null;
            if (kind is { } candidate)
            {
                var container = string.IsNullOrWhiteSpace(endpoint.GroupId) ? endpoint.Id : endpoint.GroupId;
                var id = PeripheralIdentity.DeviceId(container, candidate);
                if (!paths.TryAdd(id, endpoint.Id))
                    paths[id] = "";
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if ((device.MouseReadout?.Performance?.EditRevision is not null || device.KeyboardLighting?.EditRevision is not null) &&
                paths.TryGetValue(device.Id, out var path) && path.Length != 0)
                result.Add(device.Id, path);
        }
        return result;
    }
}
