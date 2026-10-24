using Flowd.Engine.Results;

namespace Flowd.Engine.Tests;

/// <summary>UNI-002: <see cref="Failure"/> is a closed set of expected failures.</summary>
public sealed class FailureTests
{
    private static string Describe(Failure failure) => failure switch
    {
        ValidationFailure v => $"validation:{v.Message}",
        NotFound n => $"not-found:{n.Message}",
        Conflict c => $"conflict:{c.Code}:{c.Message}",
        PermissionDenied p => $"denied:{p.Message}",
        RateLimited r => $"rate-limited:{r.Message}",
        Unavailable u => $"unavailable:{u.Message}",
    };

    [Fact]
    public void UNI_002_every_failure_case_can_be_constructed_and_matched()
    {
        Describe(new ValidationFailure("bad")).ShouldBe("validation:bad");
        Describe(new NotFound("missing")).ShouldBe("not-found:missing");
        Describe(new Conflict("JOB_NOT_ACTIVATED", "stale")).ShouldBe("conflict:JOB_NOT_ACTIVATED:stale");
        Describe(new PermissionDenied("no")).ShouldBe("denied:no");
        Describe(new RateLimited("slow down")).ShouldBe("rate-limited:slow down");
        Describe(new Unavailable("db")).ShouldBe("unavailable:db");
    }
}
