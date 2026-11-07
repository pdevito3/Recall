namespace Recall.UnitTests.Domain.Sessions.Cleanup;

using Recall.Server.Domain.ModelProviders.Clients;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Cleanup;
using Shouldly;

public class CleanupAttemptTests
{
    private const string Run = "run_1";

    private static ProviderFailure Failure(ProviderFailureKind kind) => new(kind, null, "words");

    [Test]
    [Arguments(CleanupRunState.NotRun)]
    [Arguments(CleanupRunState.Clean)]
    [Arguments(CleanupRunState.Failed)]
    public void a_job_of_a_run_that_ended_does_nothing(CleanupRunState state)
    {
        // Arrange & Act & Assert
        CleanupAttempt.Before(state, Run, Run, 1).ShouldBeOfType<CleanupStep.Skip>();
    }

    [Test]
    public void a_job_of_an_older_run_does_nothing()
    {
        // Arrange & Act & Assert
        CleanupAttempt.Before(CleanupRunState.Running, "run_2", Run, 1).ShouldBeOfType<CleanupStep.Skip>();
    }

    [Test]
    public void a_running_run_calls_the_provider_up_to_the_limit()
    {
        // Arrange & Act & Assert
        CleanupAttempt.Before(CleanupRunState.Running, Run, Run, 1).ShouldBeOfType<CleanupStep.CallProvider>();
        CleanupAttempt.Before(CleanupRunState.Running, Run, Run, CleanupAttempt.MaxProviderCalls).ShouldBeOfType<CleanupStep.CallProvider>();
    }

    [Test]
    public void a_claim_above_the_limit_ends_the_run_before_any_call()
    {
        // Arrange & Act & Assert
        CleanupAttempt.Before(CleanupRunState.Running, Run, Run, CleanupAttempt.MaxProviderCalls + 1)
            .ShouldBe(new CleanupStep.Fail(CleanupFailure.Interrupted));
    }

    [Test]
    public void the_limit_is_below_the_ten_claims_of_a_job_so_a_job_that_makes_the_server_exit_ends_its_run()
    {
        // Arrange & Act & Assert
        CleanupAttempt.MaxProviderCalls.ShouldBeLessThan(10);
    }

    [Test]
    public void a_refusal_never_tries_again()
    {
        // Arrange & Act & Assert
        CleanupAttempt.AfterFailure(Failure(ProviderFailureKind.Refused), 1).ShouldBe(new CleanupStep.Fail(CleanupFailure.ProviderRefused));
    }

    [Test]
    public void an_answer_that_the_app_cannot_use_never_tries_again()
    {
        // Arrange & Act & Assert
        CleanupAttempt.AfterFailure(Failure(ProviderFailureKind.BadOutput), 1).ShouldBe(new CleanupStep.Fail(CleanupFailure.BadModelOutput));
    }

    [Test]
    public void an_unavailable_provider_tries_again_until_the_limit()
    {
        // Arrange & Act & Assert
        CleanupAttempt.AfterFailure(Failure(ProviderFailureKind.Unavailable), CleanupAttempt.MaxProviderCalls - 1).ShouldBeOfType<CleanupStep.TryAgainLater>();
        CleanupAttempt.AfterFailure(Failure(ProviderFailureKind.Unavailable), CleanupAttempt.MaxProviderCalls).ShouldBe(new CleanupStep.Fail(CleanupFailure.ProviderUnavailable));
    }
}
