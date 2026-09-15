using System.Net;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class RetryPolicyTests
{
    [Fact]
    public async Task RetriesTransientFailuresWithBoundedAttempts()
    {
        var attempts = 0;
        var options = new RetryPolicyOptions(true, 2, TimeSpan.Zero, TimeSpan.Zero);

        var result = await RetryPolicy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new HttpRequestException("temporary", null, HttpStatusCode.ServiceUnavailable);
            }
            return Task.FromResult("ok");
        }, options);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task DoesNotRetryPermanentFailuresOrCancellation()
    {
        var attempts = 0;
        var options = new RetryPolicyOptions(true, 3, TimeSpan.Zero, TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RetryPolicy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            throw new InvalidOperationException("permanent");
        }, options));

        Assert.Equal(1, attempts);
        Assert.False(RetryPolicy.IsTransient(new HttpRequestException("temporary", null, HttpStatusCode.BadRequest)));
    }
}
