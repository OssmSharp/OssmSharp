using System.Collections.Concurrent;
using CsCheck;
using OssmSharp.Web.Contracts;
using Xunit;
using static OssmSharp.Web.Contracts.ControlResponseCodes;
using ActionKind = OssmSharp.Web.Api.Tests.Control.Models.ControlSessionAction;
using Admission = OssmSharp.Web.Api.Tests.Control.Models.AdmissionState;
using ParallelActual = (OssmSharp.Web.Api.Tests.Control.ControlSessionTestCase Test, System.Collections.Concurrent.ConcurrentBag<(int Caller, bool Accepted, string Code)> Replies);
using ParallelModel = System.Collections.Generic.List<(int Caller, bool Accepted, string Code)>;
using Reference = OssmSharp.Web.Api.Tests.Control.Models.ControlSessionReference;
using SessionCase = OssmSharp.Web.Api.Tests.Control.ControlSessionTestCase;

namespace OssmSharp.Web.Api.Tests.Control;

public sealed class ControlSessionModelTests(ITestOutputHelper output) : ControlSessionTestCase
{
    private static readonly bool[] InvitationValidity = [false, true];
    private static readonly int[] Tabs = [1, 2];

    [Fact]
    public void AdmissionSpecificationIsClosedAndDetectsReplacementFaults()
    {
        var lastKnownState = AdmissionSpec().Exhaustive(out var violation, writeLine: output.WriteLine);
        Assert.Null(violation);
        Assert.True(lastKnownState.Closed);
        Assert.Equal(0, lastKnownState.DeadlockStates);
        Assert.Empty(lastKnownState.NeverTriggered);
        Assert.Empty(lastKnownState.NeverFired);
        AdmissionSpec()
            .Fault(
                "redeem drops existing admission",
                on: "Redeem",
                when: (before, _) => before.Admitted,
                perturb: (_, after) => after with { Admitted = false, Tab = 0 })
            .Fault(
                "join replaces attached tab",
                on: "Join",
                when: (before, _) => before.Tab != 0,
                perturb: (before, after) => after with { Tab = before.Tab == 1 ? 2 : 1, Accepted = true })
            .Faults(writeLine: output.WriteLine);
    }

    [Fact]
    public async Task SynchronousAdmissionConformsWithRepliesAndSavedIdentity()
    {
        var owned = new List<SessionCase>();
        try
        {
            AdmissionSpec().Conform(
                create: () =>
                {
                    var test = CreateCase();
                    owned.Add(test);
                    test.PrepareAdmission();
                    return test;
                },
                apply: (test, step) =>
                {
                    ControlResponse response;
                    if (step.Action == "Redeem")
                    {
                        var redemption = test.Redeem(step.ArgIndex == 1);
                        response = new("redeem", redemption.Code, redemption.Accepted);
                    }
                    else
                    {
                        response = test.Component.JoinGuest($"tab-{step.ArgIndex + 1}", test.Identity, "join");
                    }
                    if (response.Accepted != step.After.Accepted || response.Code != step.After.Code)
                        return $"{step.Action}: expected {step.After.Code}/{step.After.Accepted}, got {response.Code}/{response.Accepted}";
                    var saved = test.Component.GetSavedGuestSession(test.Identity);
                    if ((saved is not null) != step.After.Admitted || (saved?.Connected == true) != (step.After.Tab != 0))
                        return $"{step.Action}: saved admission or attachment diverged";
                    if (test.IsAttached("tab-1") != (step.After.Tab == 1) || test.IsAttached("tab-2") != (step.After.Tab == 2))
                        return $"{step.Action}: the wrong tab is attached";
                    return null;
                },
                threads: 1,
                minSteps: 0,
                maxSteps: 16,
                writeLine: output.WriteLine);
        }
        finally
        {
            await Task.WhenAll(owned.Select(test => test.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task ParallelRedemptionHasOneAdmissionAndLinearizableReplies()
    {
        var owned = new ConcurrentBag<SessionCase>();
        try
        {
            Gen.Const(0).Select(_ =>
            {
                var test = CreateCase();
                owned.Add(test);
                test.PrepareAdmission();
                return ((Test: test, Replies: new ConcurrentBag<(int Caller, bool Accepted, string Code)>()), new ParallelModel());
            }).SampleParallel(
                Gen.Int[0, 2].Operation<ParallelActual, ParallelModel>(
                    caller => $"Redeem({caller})",
                    (actual, caller) =>
                    {
                        var result = actual.Test.Redeem(true);
                        actual.Replies.Add((caller, result.Accepted, result.Code));
                        if (result.Accepted)
                        {
                            Assert.False(string.IsNullOrWhiteSpace(result.SessionToken));
                            Assert.Equal(actual.Test.InvitationIdentity, actual.Test.Component.AuthenticateGuestSession(result.SessionToken));
                        }
                        else
                        {
                            Assert.Null(result.SessionToken);
                        }
                    },
                    (model, caller) =>
                    {
                        model.Add((caller, model.Count == 0, model.Count != 0 ? GuestAlreadyJoined : InvitationRedeemed));
                    }),
                equal: (actual, model) =>
                    actual.Replies.Order().SequenceEqual(model.Order())
                    && (actual.Test.Component.GetSavedGuestSession(actual.Test.Identity) is not null) == (model.Count != 0),
                maxSequentialOperations: 0,
                maxParallelOperations: 3,
                threads: 1,
                writeLine: output.WriteLine);
        }
        finally
        {
            await Task.WhenAll(owned.Select(test => test.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task IndependentAsyncSessionTransitionsComparePermissionRepliesRequestsAndObservations()
    {
        var owned = new ConcurrentBag<SessionCase>();
        try
        {
            var initial = Gen.Const(0).Select(async _ =>
            {
                var actual = CreateCase();
                owned.Add(actual);
                await actual.ReadViewAsync(string.Empty);
                return (actual, new Reference());
            });
            await initial.SampleModelBasedAsync(
                Gen.Int[0, 12].Operation<SessionCase, Reference>(
                    kind => ((ActionKind)kind).ToString(),
                    (actual, kind) => actual.ApplyAsync((ActionKind)kind),
                    (model, kind) =>
                    {
                        model.Apply((ActionKind)kind);
                        return Task.CompletedTask;
                    }),
                equal: (actual, model) => actual.LastView == model.View && actual.Requests.SequenceEqual(model.Requests),
                printActual: actual => $"{actual.LastView}; requests={string.Join(",", actual.Requests)}",
                printModel: model => $"{model.View}; requests={string.Join(",", model.Requests)}",
                classify: model => $"admitted={model.Admitted},permission={model.Allowed},attached={model.Tab},reply={model.Reply}",
                threads: 1,
                writeLine: output.WriteLine);
            private static Spec<Admission> AdmissionSpec() => Spec.From(new Admission())
                .Action("Redeem", InvitationValidity, (s, valid) => s.Admitted
                    ? s with { Accepted = false, Code = GuestAlreadyJoined }
                    : valid ? new Admission(true, 0, true, InvitationRedeemed)
                        : s with { Accepted = false, Code = InvalidInvitation })
                .Action("Join", Tabs, (s, tab) => !s.Admitted
                    ? s with { Accepted = false, Code = InvalidGuestSession }
                    : s.Tab != 0 ? s with { Accepted = false, Code = GuestAlreadyJoined }
                        : new Admission(true, tab, true, Joined))
                .Invariant("AttachedRequiresAdmission", "Only an admitted guest can attach.", s => s.Tab == 0 || s.Admitted)
                .Rule(
                    "AdmissionIsNotReplaced",
                    "Repeated redemption is rejected and retains admission.",
                    on: "Redeem",
                    when: (before, _) => before.Admitted,
                    then: (before, after) => after.Admitted && after.Tab == before.Tab && !after.Accepted && after.Code == GuestAlreadyJoined)
                .Rule(
                    "SecondTabIsRejected",
                    "An attached guest cannot be replaced by another tab.",
                    on: "Join",
                    when: (before, _) => before.Tab != 0,
                    then: (before, after) => after.Tab == before.Tab && !after.Accepted && after.Code == GuestAlreadyJoined)
                .Rule(
                    "JoinRequiresAdmission",
                    "A missing saved identity is rejected.",
                    on: "Join",
                    when: (before, _) => !before.Admitted,
                    then: (_, after) => after.Tab == 0 && !after.Accepted && after.Code == InvalidGuestSession)
                .Reachable("AdmissionSucceeded", "A valid invitation can be redeemed.", s => s.Admitted && s.Accepted && s.Code == InvitationRedeemed)
                .Reachable("TabOneAttached", "The first tab can attach.", s => s.Tab == 1)
                .Reachable("TabTwoAttached", "The second tab can attach first.", s => s.Tab == 2)
                .Reachable("InvalidInvitationRejected", "Invalid invitations remain in the attempted domain.", s => s.Code == InvalidInvitation);
        }
        finally
        {
            await Task.WhenAll(owned.Select(test => test.DisposeAsync().AsTask()));
        }
    }

}
