// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Native;
using SysBox.Transport;
namespace SysBox.NtfsBroker;

public sealed class BrokerServer(BrokerPolicy policy, Func<string, INtfsMetadataReader> open)
{
    public const string PipeName = "AbsSystemBox.Ntfs.ReadOnly.v1";
    public static PipeSecurity Security(BrokerPolicy policy)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.SetOwner(system);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        foreach (var sid in policy.Grants.Select(g => g.Sid).Distinct())
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
    public async Task RunAsync(CancellationToken token)
    {
        policy.Validate();
        using var slots = new SemaphoreSlim(4);
        var pending = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await slots.WaitAsync(token);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, Security(policy), HandleInheritability.None);
                    await pipe.WaitForConnectionAsync(token);
                    var accepted = pipe;
                    pipe = null;
                    pending.RemoveAll(t => t.IsCompleted);
                    pending.Add(Task.Run(async () => { try { await HandleAsync(accepted, token); } finally { slots.Release(); } }, CancellationToken.None));
                }
                catch { pipe?.Dispose(); slots.Release(); throw; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(pending); }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var request = await Protocol.ReadAsync<RpcRequest>(pipe, timeout.Token);
                var peer = BrokerPeer.Read(pipe, false);
                var value = Dispatch(peer, request, timeout.Token);
                await Protocol.WriteAsync(pipe, new RpcResponse(Protocol.Version, JsonSerializer.SerializeToElement(value, Protocol.Json), null), timeout.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                var code = error is UnauthorizedAccessException ? "ACCESS_DENIED" : error is SysBoxOperationException op ? op.Error.Code : error is OperationCanceledException ? "CANCELLED" : "NTFS_UNAVAILABLE";
                try
                {
                    await Protocol.WriteAsync(pipe, new RpcResponse(Protocol.Version, null, new(code, "只读 NTFS 请求未完成；请检查授权、卷状态或重建索引。")), timeout.Token);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException) { }
            }
        }
    }
    public object Dispatch(BrokerPeer peer, RpcRequest request, CancellationToken token)
    {
        var allowed = policy.Allowed(peer.Sid, peer.Image);
        if (allowed.Length == 0)
            throw new UnauthorizedAccessException();
        if (request.Version != Protocol.Version || request.Arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException();
        if (request.Method == "capabilities")
            return allowed;
        if (request.Method is not ("journal" or "enumerate" or "changes" or "objects"))
            throw new SysBoxOperationException(new("METHOD_NOT_FOUND", "只读服务没有此接口。"));
        var volume = NtfsIdentity.Volume(request.Arguments.GetProperty("volume").GetString() ?? "");
        if (!allowed.Contains(volume, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException();
        token.ThrowIfCancellationRequested();
        using var reader = open(volume);
        return request.Method switch
        {
            "journal" => reader.QueryJournal(),
            "enumerate" => reader.Enumerate(ulong.Parse(request.Arguments.GetProperty("continuation").GetString()!, System.Globalization.CultureInfo.InvariantCulture), request.Arguments.GetProperty("highUsn").GetInt64(), token),
            "changes" => reader.ReadChanges(request.Arguments.GetProperty("startUsn").GetInt64(), ulong.Parse(request.Arguments.GetProperty("journalId").GetString()!, System.Globalization.CultureInfo.InvariantCulture), token),
            "objects" => ReadObjects(reader, request.Arguments, token),
            _ => throw new ArgumentException()
        };
    }
    private static NtfsObject[] ReadObjects(INtfsMetadataReader reader, JsonElement args, CancellationToken token)
    {
        var ids = args.GetProperty("ids").Deserialize<string[]>(Protocol.Json) ?? throw new ArgumentException();
        if (ids.Length is < 1 or > 16)
            throw new ArgumentException();
        return reader.ReadObjects(ids.Select(NtfsIdentity.FileId).ToArray(), token);
    }
}
