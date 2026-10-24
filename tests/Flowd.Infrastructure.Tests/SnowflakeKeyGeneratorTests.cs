using Flowd.Infrastructure.Keys;
using Microsoft.Extensions.Time.Testing;

namespace Flowd.Infrastructure.Tests;

/// <summary>FR-DOM-001: keys are 63-bit, time-ordered and unique.</summary>
public sealed class SnowflakeKeyGeneratorTests
{
    [Fact]
    public void FR_DOM_001_keys_are_positive_unique_and_increasing_within_one_millisecond()
    {
        var generator = new SnowflakeKeyGenerator(new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)), nodeId: 7);

        var keys = Enumerable.Range(0, 10_000).Select(_ => generator.NextKey()).ToList();

        keys.ShouldAllBe(k => k > 0);
        keys.Distinct().Count().ShouldBe(keys.Count);
        keys.ShouldBe(keys.Order());
    }

    [Fact]
    public void FR_DOM_001_keys_increase_with_time_and_never_go_back_when_the_clock_does()
    {
        var clock = new SettableTimeProvider(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero));
        var generator = new SnowflakeKeyGenerator(clock, nodeId: 0);

        var first = generator.NextKey();
        clock.Now = clock.Now.AddSeconds(1);
        var later = generator.NextKey();
        clock.Now = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var afterRollback = generator.NextKey();

        later.ShouldBeGreaterThan(first);
        afterRollback.ShouldBeGreaterThan(later);
    }

    [Fact]
    public void FR_DOM_001_different_nodes_never_collide_at_the_same_instant()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero));
        var a = new SnowflakeKeyGenerator(clock, nodeId: 1).NextKey();
        var b = new SnowflakeKeyGenerator(clock, nodeId: 2).NextKey();

        a.ShouldNotBe(b);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1024)]
    public void FR_DOM_001_node_id_must_fit_in_10_bits(int nodeId) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new SnowflakeKeyGenerator(TimeProvider.System, nodeId));
}

/// <summary>A clock tests can move backwards, which <see cref="FakeTimeProvider"/> refuses to do.</summary>
internal sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
