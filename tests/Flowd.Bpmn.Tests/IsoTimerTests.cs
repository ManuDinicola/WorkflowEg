using Flowd.Bpmn.Model;

namespace Flowd.Bpmn.Tests;

/// <summary>FR-TMR-001: the ISO 8601 subset of timer events.</summary>
public sealed class IsoTimerTests
{
    [Theory]
    [InlineData("PT15M", 15)]
    [InlineData("PT1H", 60)]
    [InlineData("P1D", 1440)]
    [InlineData("PT0S", 0)]
    public void FR_TMR_001_durations_parse(string text, int minutes)
    {
        IsoTimer.TryParseDuration(text, out var duration).ShouldBeTrue();
        duration.ShouldBe(TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("15 minutes")]
    [InlineData("-PT5M")]
    public void FR_TMR_001_bad_durations_are_rejected(string text) => IsoTimer.TryParseDuration(text, out _).ShouldBeFalse();

    [Theory]
    [InlineData("R3/PT10M", 3, 10)]
    [InlineData("R/PT1H", null, 60)]
    public void FR_TMR_001_cycles_parse(string text, int? repetitions, int minutes)
    {
        IsoTimer.TryParseCycle(text, out var cycle).ShouldBeTrue();
        cycle.Repetitions.ShouldBe(repetitions);
        cycle.Interval.ShouldBe(TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData("R0/PT10M")]
    [InlineData("PT10M")]
    [InlineData("R3/")]
    [InlineData("R3/PT0S")]
    public void FR_TMR_001_bad_cycles_are_rejected(string text) => IsoTimer.TryParseCycle(text, out _).ShouldBeFalse();

    [Fact]
    public void FR_TMR_001_dates_and_due_times_are_computed_from_the_injected_clock()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        IsoTimer.TryComputeDue(TimerKind.Date, "2026-12-01T10:00:00Z", now, out var date, out _).ShouldBeTrue();
        IsoTimer.TryComputeDue(TimerKind.Duration, "PT5M", now, out var duration, out _).ShouldBeTrue();
        IsoTimer.TryComputeDue(TimerKind.Cycle, "R2/PT1H", now, out var first, out var cycle).ShouldBeTrue();

        date.ShouldBe(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        duration.ShouldBe(now.AddMinutes(5));
        first.ShouldBe(now.AddHours(1));
        cycle!.Repetitions.ShouldBe(2);
        IsoTimer.TryComputeDue(TimerKind.Duration, "nonsense", now, out _, out _).ShouldBeFalse();
        IsoTimer.IsValid(TimerKind.Date, "nonsense").ShouldBeFalse();
    }
}
