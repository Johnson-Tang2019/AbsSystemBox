// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Core;
namespace SysBox.UnitTests;

public sealed class CleanupPersistenceFeaturesTests
{
    private sealed class NoMemory : IMemoryMonitor
    {
        public Task<Result<MemorySnapshot>> ReadAsync(CancellationToken token) => throw new InvalidOperationException("This persistence test must never sample or clean.");
    }
    private sealed class NoTrim : IWorkingSetTrimmer
    {
        public IReadOnlyList<CleanupAttempt> TrimAllowed(CleanupPolicy policy, CancellationToken token) => throw new InvalidOperationException("Never execute in persistence tests.");
    }
    [Fact]
    public void ConfigurationRoundTripsWhileAnAgentOnlyHostRemainsDisarmed()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new CleanupSupervisor(root, new NoMemory(), new NoTrim(), () => true);
            Assert.False(service.Read().Policy.Enabled);
            service.Configure(new(Enabled: true, Periodic: true, AllowedProcesses: ["Fixture.EXE"]));
            var recovered = new CleanupSupervisor(root, new NoMemory(), new NoTrim(), () => false);
            Assert.True(recovered.Read().Policy.Periodic);
            Assert.Equal("fixture.exe", Assert.Single(recovered.Read().Policy.AllowedProcesses!));
            Assert.Contains("不会执行", recovered.Read().Status);
            recovered.Configure(recovered.Read().Policy with
            {
                Enabled = false
            });
            Assert.False(new CleanupSupervisor(root, new NoMemory(), new NoTrim(), () => true).Read().Policy.Enabled);
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("{")]
    [InlineData("{\"Policy\":{},\"Audit\":null,\"Runs\":[]}")]
    public void CorruptStoreIsPreservedAndCannotArmCleanup(string text)
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "cleanup.json");
            File.WriteAllText(path, text);
            var service = new CleanupSupervisor(root, new NoMemory(), new NoTrim(), () => true);
            Assert.False(service.Read().Policy.Enabled);
            Assert.Contains("损坏", service.Read().Status);
            Assert.Equal(text, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
