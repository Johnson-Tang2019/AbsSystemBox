// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Transport;
namespace SysBox.NtfsBroker;

public sealed class BrokerClient
{
    public async Task<T> CallAsync<T>(string method, object arguments, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", BrokerServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(1000, timeout.Token);
            var peer = BrokerPeer.Read(pipe, true);
            if (peer.Sid != "S-1-5-18" || peer.Session != 0 || !string.Equals(peer.Image, BrokerPolicy.ServiceImage, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException();
            BrokerPolicy.VerifyProtected(peer.Image);
            await Protocol.WriteAsync(pipe, new RpcRequest(Protocol.Version, method, System.Text.Json.JsonSerializer.SerializeToElement(arguments, Protocol.Json)), timeout.Token);
            var response = await Protocol.ReadAsync<RpcResponse>(pipe, timeout.Token);
            if (response.Version != Protocol.Version)
                throw new IOException();
            if (response.Error is { } error)
                throw new SysBoxOperationException(error);
            return response.Value!.Value.Deserialize<T>(Protocol.Json) ?? throw new IOException();
        }
        catch (SysBoxOperationException) { throw; }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { throw new SysBoxOperationException(new("NTFS_SERVICE_UNAVAILABLE", "只读 NTFS 服务缺失、身份不可信或当前用户未授权。可使用普通目录模式。")); }
    }
    public INtfsMetadataReader Open(string volume) => new Remote(this, NtfsIdentity.Volume(volume));
    private sealed class Remote(BrokerClient client, string volume) : INtfsMetadataReader
    {
        private T Call<T>(string method, object value, CancellationToken token = default) => Task.Run(() => client.CallAsync<T>(method, value, token)).GetAwaiter().GetResult();
        public UsnJournal QueryJournal() => Call<UsnJournal>("journal", new { volume });
        public NtfsBatch Enumerate(ulong continuation, long highUsn, CancellationToken token) => Call<NtfsBatch>("enumerate", new { volume, continuation = continuation.ToString(System.Globalization.CultureInfo.InvariantCulture), highUsn }, token);
        public NtfsBatch ReadChanges(long startUsn, ulong journalId, CancellationToken token) => Call<NtfsBatch>("changes", new { volume, startUsn, journalId = journalId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, token);
        public NtfsObject[] ReadObjects(string[] ids, CancellationToken token) => Call<NtfsObject[]>("objects", new { volume, ids }, token);
        public void Dispose()
        {
        }
    }
}
