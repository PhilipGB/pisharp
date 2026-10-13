using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class AgentRunRetryPolicyTests
{
    [Theory]
    [InlineData("overloaded upstream", true)]
    [InlineData("rate limit 429", true)]
    [InlineData("503 service unavailable", true)]
    [InlineData("429 insufficient_quota", false)]
    [InlineData("503 billing limit reached", false)]
    [InlineData("maximum context length exceeded", false)]
    [InlineData("provider broke", false)]
    public void ClassifiesTransientProviderFailuresAndExcludesQuotaAndContextErrors(string message, bool expected)
    {
        var policy = AgentRunRetryPolicy.Default;

        Assert.Equal(expected, policy.CanRetry(new IOException(message), message, retries: 0, enabled: true));
    }

    [Fact]
    public void UsesExponentialDelayWithCapAndHonorsRetryBudget()
    {
        var policy = AgentRunRetryPolicy.Default;
        var unavailable = new HttpRequestException("503", null, System.Net.HttpStatusCode.ServiceUnavailable);

        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.DelayForAttempt(6));
        Assert.True(policy.CanRetry(unavailable, "503", retries: 2, enabled: true));
        Assert.False(policy.CanRetry(unavailable, "503", retries: 3, enabled: true));
        Assert.False(policy.CanRetry(unavailable, "503", retries: 0, enabled: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayForAttempt(0));
    }

    [Fact]
    public async Task SuccessfulModelResponseEndsRetryBeforeTheToolLoopAndResetsTheRetryCount()
    {
        var controller = new AgentRunRetryController(new AgentRunRetryPolicy(enabled: true, maxRetries: 2,
            baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero));
        var events = new List<AgentLifecycleEvent>();
        var attempt = 0;

        async IAsyncEnumerable<AgentResponseUpdate> Execute(bool continuation,
            Action<AgentLifecycleEvent> observeAttempt)
        {
            attempt++;
            if (attempt == 1)
                observeAttempt(new("prompt_accepted") { RunStartHead = "root" });
            else
                observeAttempt(new("agent_attempt_started") { RunStartHead = "root" });

            if (attempt == 1)
            {
                observeAttempt(new("model_request_failed", Error: "503 service unavailable"));
                await Task.Yield();
                throw new HttpRequestException("503 service unavailable", null,
                    System.Net.HttpStatusCode.ServiceUnavailable);
            }

            observeAttempt(new("model_request_completed"));
            if (attempt == 2)
            {
                observeAttempt(new("model_request_failed", Error: "503 service unavailable"));
                await Task.Yield();
                throw new HttpRequestException("503 service unavailable", null,
                    System.Net.HttpStatusCode.ServiceUnavailable);
            }

            yield break;
        }

        var outcome = await controller.RunTurnAsync(Execute, events.Add, _ => { },
            (_, _, _) => Task.CompletedTask, _ => Task.CompletedTask, () => { }, () => "head",
            CancellationToken.None);

        Assert.Equal(AgentTurnOutcome.Completed, outcome);
        Assert.Equal([1, 1], events.Where(item => item.Type == "auto_retry_start")
            .Select(item => item.RetryAttempt));
        Assert.Equal([1, 1], events.Where(item => item.Type == "auto_retry_end")
            .Select(item => item.RetryAttempt));
        Assert.All(events.Where(item => item.Type == "auto_retry_end"), item => Assert.True(item.RetrySuccess));
        var completedIndex = events.FindIndex(item => item.Type == "model_request_completed");
        var retryEndedIndex = events.FindIndex(item => item.Type == "auto_retry_end");
        Assert.True(completedIndex < retryEndedIndex);
    }
}
