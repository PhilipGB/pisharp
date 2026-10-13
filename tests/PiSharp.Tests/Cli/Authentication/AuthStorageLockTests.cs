using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class AuthStorageLockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyCredentialLookupsDoNotCreateAnAgentDirectory(bool list)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-readonly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new AuthStorage(Path.Combine(root, "auth.json"));
            if (list) Assert.Empty(await storage.ListAsync());
            else Assert.Null(await storage.ReadAsync("fixture"));
            Assert.False(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WindowsUnauthorizedAccessUsesTheSameBoundedPermissionRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var attempts = 0;
            var delays = new List<int>();
            await using var lease = await AuthFileLock.AcquireAsync(Path.Combine(root, "lease"), default, windows: true,
                open: path => ++attempts == 1 ? throw new UnauthorizedAccessException("Fixture pending delete.") :
                    new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
                delay: (milliseconds, _) => { delays.Add(milliseconds); return Task.CompletedTask; }, random: () => 0);
            Assert.Equal(2, attempts);
            Assert.Equal(new[] { 10 }, delays);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ContentionDeadlineIsBoundedAndDoesNotConsumeTheHeldFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "lease");
            using var holder = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<IOException>(() => AuthFileLock.AcquireAsync(path, default, timeout: TimeSpan.FromMilliseconds(100)));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
            Assert.True(holder.CanWrite);
            holder.Dispose();
            await using var replacement = await AuthFileLock.AcquireAsync(path, default);
            Assert.True(replacement.CanWrite);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true, 1, true, 2)]
    [InlineData(true, 20, false, 6)]
    [InlineData(false, 1, false, 1)]
    public async Task WindowsPermissionRetriesAreTransientAndSeparatelyBounded(bool windows, int failures,
        bool success, int expectedAttempts)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var attempts = 0;
            var delays = new List<int>();
            var pending = AuthFileLock.AcquireAsync(Path.Combine(root, "lease"), default, windows: windows,
                open: path => ++attempts <= failures ? throw new NativeLockException(5) :
                    new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
                delay: (milliseconds, _) => { delays.Add(milliseconds); return Task.CompletedTask; }, random: () => 0);
            if (success) await using (await pending) { }
            else await Assert.ThrowsAsync<NativeLockException>(() => pending);
            Assert.Equal(expectedAttempts, attempts);
            Assert.Equal(expectedAttempts - 1, delays.Count);
            if (expectedAttempts == 6) Assert.Equal(new[] { 10, 20, 40, 80, 160 }, delays);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(11)]
    [InlineData(28)]
    [InlineData(35)]
    public async Task NonContentionIoFailuresAreNotRetried(int nativeCode)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var attempts = 0;
            await Assert.ThrowsAsync<NativeLockException>(() => AuthFileLock.AcquireAsync(Path.Combine(root, "lease"), default,
                windows: true, open: _ => { attempts++; throw new NativeLockException(nativeCode); }));
            Assert.Equal(1, attempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LockContentionDoesNotSpendTheWindowsPermissionRetryAllowance()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var attempts = 0;
            var delays = 0;
            var failures = new Queue<int>([32, 5, 32, 5, 32, 5, 32, 5, 32, 5]);
            await using var lease = await AuthFileLock.AcquireAsync(Path.Combine(root, "lease"), default, windows: true,
                open: path =>
                {
                    attempts++;
                    if (failures.TryDequeue(out var code)) throw new NativeLockException(code);
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }, delay: (_, _) => { delays++; return Task.CompletedTask; }, random: () => 0);
            Assert.Equal(11, attempts);
            Assert.Equal(10, delays);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancellationAfterAcquisitionClosesTheFileLeaseBeforeReturning()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var path = Path.Combine(root, "lease");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AuthFileLock.AcquireAsync(path, cancellation.Token,
                open: file =>
                {
                    var lease = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    cancellation.Cancel();
                    return lease;
                }));
            await using var replacement = await AuthFileLock.AcquireAsync(path, default);
            Assert.True(replacement.CanWrite);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ACallerCanceledDuringContentionNeverWritesAndReleasesTheProcessGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "auth.json");
            var storage = new AuthStorage(path);
            await storage.StoreApiKeyAsync("first", "fixture-first");
            using (var lease = new FileStream(path + ".pisharp.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                using var cancellation = new CancellationTokenSource(100);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.StoreApiKeyAsync("cancelled", "fixture", cancellation.Token));
            }
            await storage.StoreApiKeyAsync("next", "fixture-next");
            Assert.Null(await storage.ReadAsync("cancelled"));
            Assert.Equal("fixture-next", (await storage.ReadAsync("next"))!.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StorageWaitsForTheSharedFileLeaseBeforeUpdatingCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "auth.json");
            var storage = new AuthStorage(path);
            await storage.StoreApiKeyAsync("first", "fixture-first");
            using var lease = new FileStream(path + ".pisharp.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var writing = new AuthStorage(path).StoreApiKeyAsync("second", "fixture-second");
            try
            {
                await Task.Delay(150);
                Assert.False(writing.IsCompleted, "Credential writes ignored the shared file lease.");
            }
            finally { lease.Dispose(); }
            await writing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("fixture-first", (await storage.ReadAsync("first"))!.Key);
            Assert.Equal("fixture-second", (await storage.ReadAsync("second"))!.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task IndependentStorageInstancesPreserveAllConcurrentProviderWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "auth.json");
            await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
                new AuthStorage(path).StoreApiKeyAsync("provider" + index, "fixture-key")));
            Assert.Equal(20, (await new AuthStorage(path).ListAsync()).Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class NativeLockException : IOException
    {
        internal NativeLockException(int nativeCode) : base("Fixture native lock error.") =>
            HResult = unchecked((int)0x80070000) | nativeCode;
    }
}
