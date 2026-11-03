using System.Security.Claims;
using NSubstitute;
using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Eligibility;
using TapPasskeyOnboarding.Core.Graph;
using TapPasskeyOnboarding.Core.Models;
using TapPasskeyOnboarding.Core.Redaction;
using TapPasskeyOnboarding.Core.Security;
using TapPasskeyOnboarding.Core.Services;

namespace TapPasskeyOnboarding.UnitTests;

public sealed class SecurityAndEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 18, 0, 0, TimeSpan.Zero);
    private readonly IGraphDirectoryClient _graph = Substitute.For<IGraphDirectoryClient>();
    private readonly EligibilityOptions _options = new()
    {
        OnboardingGroupId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        CompletionGroupId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
        BreakGlassObjectIds = new(StringComparer.OrdinalIgnoreCase) { "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" },
        ServiceAccountObjectIds = new(StringComparer.OrdinalIgnoreCase) { "cccccccc-cccc-cccc-cccc-cccccccccccc" },
        ExclusionGroupIds = new(StringComparer.OrdinalIgnoreCase) { "dddddddd-dddd-dddd-dddd-dddddddddddd" }
    };

    [Fact]
    public void Password_claim_validation_is_fail_closed_and_oid_bound()
    {
        var time = Substitute.For<TimeProvider>();
        time.GetUtcNow().Returns(Now);
        var validator = new PasswordAuthenticationValidator(time, TimeSpan.FromMinutes(15));
        var valid = Principal(("oid", "11111111-1111-1111-1111-111111111111"), ("tid", "tenant"), ("amr", "pwd"), ("auth_time", Now.AddMinutes(-2).ToUnixTimeSeconds().ToString()));
        Assert.True(validator.Validate(valid, "tenant", out _));
        Assert.False(validator.Validate(Principal(("oid", "x"), ("tid", "tenant"), ("amr", "mfa"), ("auth_time", Now.ToUnixTimeSeconds().ToString())), "tenant", out _));
        Assert.False(validator.Validate(Principal(("oid", "x"), ("tid", "other"), ("amr", "pwd"), ("auth_time", Now.ToUnixTimeSeconds().ToString())), "tenant", out _));
        Assert.False(validator.Validate(Principal(("tid", "tenant"), ("amr", "pwd"), ("auth_time", Now.ToUnixTimeSeconds().ToString())), "tenant", out _));
        Assert.False(validator.Validate(Principal(("oid", "x"), ("tid", "tenant"), ("amr", "pwd"), ("auth_time", Now.AddHours(-1).ToUnixTimeSeconds().ToString())), "tenant", out _));
    }

    [Theory]
    [InlineData("Guest", false, "guest_excluded")]
    [InlineData("Member", true, "service_account_excluded")]
    public async Task Deterministic_user_exclusions_are_enforced(string userType, bool service, string expected)
    {
        const string oid = "11111111-1111-1111-1111-111111111111";
        _graph.GetUserAsync(oid, Arg.Any<CancellationToken>()).Returns(new DirectoryUser(oid, "User", userType, true, service));
        var result = await new EligibilityService(_graph, _options).EvaluateAsync(oid, default);
        Assert.Equal(expected, result.Code);
    }

    [Fact]
    public async Task Break_glass_privileged_exclusion_group_and_fixed_group_are_enforced()
    {
        _graph.GetUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new DirectoryUser(call.ArgAt<string>(0), "User", "Member", true, false));
        var service = new EligibilityService(_graph, _options);
        Assert.Equal("break_glass_excluded", (await service.EvaluateAsync("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", default)).Code);

        const string privileged = "11111111-1111-1111-1111-111111111111";
        _graph.IsPrivilegedRoleHolderAsync(privileged, default).Returns(true);
        Assert.Equal("privileged_role_excluded", (await service.EvaluateAsync(privileged, default)).Code);

        const string excluded = "22222222-2222-2222-2222-222222222222";
        _graph.IsMemberOfAnyGroupAsync(excluded, Arg.Any<IReadOnlyCollection<string>>(), default).Returns(true);
        Assert.Equal("exclusion_group", (await service.EvaluateAsync(excluded, default)).Code);

        const string notMember = "33333333-3333-3333-3333-333333333333";
        Assert.Equal("not_in_onboarding_group", (await service.EvaluateAsync(notMember, default)).Code);
        await _graph.Received().IsMemberOfGroupAsync(notMember, _options.OnboardingGroupId, default);
    }

    [Fact]
    public async Task Tap_lifetime_comes_from_trusted_configuration_and_never_user_input()
    {
        const string oid = "11111111-1111-1111-1111-111111111111";
        ConfigureEligible(oid);
        _graph.CreateSingleUseTapAsync(oid, TimeSpan.FromMinutes(10), default)
            .Returns(new TapIssuance("secret-value", Now.AddMinutes(10)));
        var audit = Substitute.For<IAuditSink>();
        var transitions = Substitute.For<ICompletionTransitionDispatcher>();
        var time = Substitute.For<TimeProvider>(); time.GetUtcNow().Returns(Now);
        var service = new OnboardingService(
            _graph,
            audit,
            new EligibilityService(_graph, _options),
            transitions,
            new TapIssuanceOptions(),
            time);
        var result = await service.IssueTapAsync(oid, "corr", default);
        Assert.Equal("secret-value", result.Secret);
        await _graph.Received(1).CreateSingleUseTapAsync(oid, TimeSpan.FromMinutes(10), default);
        await audit.Received(1).WriteAsync(Arg.Is<AuditEvent>(e => e.SubjectObjectId == oid && e.Decision == "issued"), default);
    }

    [Fact]
    public async Task Fido_detection_gates_idempotent_fixed_group_removal()
    {
        const string oid = "11111111-1111-1111-1111-111111111111";
        ConfigureEligible(oid);
        var audit = Substitute.For<IAuditSink>();
        var transitions = Substitute.For<ICompletionTransitionDispatcher>();
        var time = Substitute.For<TimeProvider>(); time.GetUtcNow().Returns(Now);
        var service = new OnboardingService(
            _graph,
            audit,
            new EligibilityService(_graph, _options),
            transitions,
            new TapIssuanceOptions(),
            time);
        Assert.False((await service.VerifyAndCompleteAsync(oid, "c1", default)).PasskeyConfirmed);
        await _graph.DidNotReceive().RemoveMemberFromGroupAsync(Arg.Any<string>(), Arg.Any<string>(), default);

        _graph.HasFido2MethodAsync(oid, default).Returns(true);
        var completed = await service.VerifyAndCompleteAsync(oid, "c2", default);
        Assert.True(completed.PasskeyConfirmed);
        Assert.Equal("transition_queued", completed.GroupTransitionStatus);
        await transitions.Received(1).DispatchAsync(oid, "c2", default);
        await _graph.DidNotReceive().AddMemberToGroupAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            default);
    }

    [Fact]
    public async Task Reconciliation_removes_only_confirmed_members_and_writes_secret_free_audit()
    {
        const string confirmed = "11111111-1111-1111-1111-111111111111";
        const string pending = "22222222-2222-2222-2222-222222222222";
        var graph = new MockSafeGraphDirectoryClient(TimeProvider.System);
        graph.AddEligibleUser(confirmed, hasFido: true);
        graph.AddEligibleUser(pending);
        var audit = Substitute.For<IAuditSink>();
        var transitions = Substitute.For<ICompletionTransitionDispatcher>();
        var service = new ReconciliationService(
            graph,
            audit,
            new EligibilityService(graph, _options),
            _options,
            transitions,
            TimeProvider.System);

        var removed = await service.RunAsync(2, "reconcile-correlation", default);

        Assert.Equal(1, removed);
        Assert.True(await graph.IsMemberOfGroupAsync(confirmed, _options.OnboardingGroupId, default));
        Assert.True(await graph.IsMemberOfGroupAsync(pending, _options.OnboardingGroupId, default));
        await transitions.Received(1).DispatchAsync(
            confirmed,
            "reconcile-correlation",
            Arg.Any<CancellationToken>());
        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e =>
                e.Operation == "reconcile" &&
                e.SubjectObjectId == confirmed &&
                e.Decision == "transition_queued" &&
                e.CorrelationId == "reconcile-correlation"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Redactor_removes_secret_bearing_fields()
    {
        var redacted = SecretRedactor.Redact("tap=123456 temporaryAccessPass:abcdef password=hunter2");
        Assert.DoesNotContain("123456", redacted);
        Assert.DoesNotContain("abcdef", redacted);
        Assert.DoesNotContain("hunter2", redacted);
    }

    private void ConfigureEligible(string oid)
    {
        _graph.GetUserAsync(oid, default).Returns(new DirectoryUser(oid, "User", "Member", true, false));
        _graph.IsMemberOfGroupAsync(oid, _options.OnboardingGroupId, default).Returns(true);
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(x => new Claim(x.Type, x.Value)), "test"));
}
