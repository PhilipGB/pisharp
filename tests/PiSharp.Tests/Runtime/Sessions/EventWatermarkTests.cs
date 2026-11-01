using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

/// <summary>
/// The drain wait behind accepted steering, follow-up and abort is only sound if the producer watermark
/// and the consumer count describe exactly the same events. These cases pin the two orderings that can
/// break it: a consumer finishing an event before the producer has counted it, and a counted write that
/// never reaches the stream.
/// </summary>
public sealed class EventWatermarkTests
{
    [Fact]
    public void AConsumerFinishingAnEventImmediatelyCannotOutrunTheWatermark()
    {
        var watermark = new EventWatermark();

        // The reservation is taken before the event becomes visible, so a consumer that finishes it
        // synchronously still leaves the counts agreeing.
        var reservation = watermark.Reserve();
        watermark.NoteEmitted();

        Assert.Equal(1, watermark.Produced);
        Assert.Equal(1, watermark.Emitted);
        Assert.True(watermark.WaitForDrainAsync().IsCompleted);
        reservation.Dispose();
    }

    [Fact]
    public void ACancelledWriteReleasesItsReservation()
    {
        var watermark = new EventWatermark();
        var delivered = watermark.Reserve();
        watermark.NoteEmitted();
        var cancelled = watermark.Reserve();

        // The second write never reached the stream, so its reservation must not keep a drain waiting.
        cancelled.Dispose();

        Assert.Equal(1, watermark.Produced);
        Assert.Equal(1, watermark.Emitted);
        Assert.True(watermark.WaitForDrainAsync().IsCompleted);
        delivered.Dispose();
    }

    [Fact]
    public async Task ARollbackReleasesAPendingDrainWaiter()
    {
        var watermark = new EventWatermark();
        var pending = watermark.Reserve();

        var drain = watermark.WaitForDrainAsync();
        Assert.False(drain.IsCompleted);

        pending.Dispose();
        // Bounded so a leaked reservation fails the test instead of hanging it.
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, watermark.Produced);
    }

    [Fact]
    public async Task TheWaiterCompletesWhenTheConsumerCatchesUp()
    {
        var watermark = new EventWatermark();
        var first = watermark.Reserve();
        var second = watermark.Reserve();

        var drain = watermark.WaitForDrainAsync();
        Assert.False(drain.IsCompleted);

        watermark.NoteEmitted();
        Assert.False(drain.IsCompleted);

        watermark.NoteEmitted();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void ACompletedStreamReleasesTheWaiterWithoutConsumingAnything()
    {
        var watermark = new EventWatermark();
        _ = watermark.Reserve();
        var drain = watermark.WaitForDrainAsync();

        watermark.NoteStreamCompleted();

        Assert.True(drain.IsCompleted);
        Assert.Equal(watermark.Produced, watermark.Emitted);
    }
}
