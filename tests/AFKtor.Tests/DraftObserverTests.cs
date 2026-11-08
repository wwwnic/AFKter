using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class DraftObserverTests
{
    internal static DraftSnapshot Draft(long id = 101, int queue = 400, DraftRole? role = DraftRole.Middle,
        DraftActionKind kind = DraftActionKind.Ban, long remaining = 28000) => new(id, queue, 2,
        [new(2, role, 0, 103), new(3, DraftRole.Top, 0, 86)],
        [new(11, 2, kind, 0, true, false), new(12, 3, DraftActionKind.Ban, 86, true, false)], remaining);

    // Catches admitting a connection baseline, enabling mid-draft, or forgetting the next lifecycle.
    [Test]
    public void MidDraftAndRearmingWaitForNewGame()
    {
        var observer = new DraftObserver();
        observer.SetEnabled(true);
        var generation = observer.BeginSession();
        observer.Observe(generation, 1, Draft());
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.MidDraft));
        observer.Observe(generation, 2, Draft(102));
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Admitted));
        observer.SetEnabled(false);
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Disarmed));
        observer.SetEnabled(true);
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.MidDraft));
        observer.Observe(generation, 3, null);
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Ended));
        observer.Observe(generation, 4, Draft(103));
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Admitted));
    }

    // Catches admitting a draft that started after the last idle read but before enabling.
    [Test]
    public void EnablingRequiresFreshIdleEvidenceEvenWhenLastReadWasIdle()
    {
        var observer = new DraftObserver();
        var generation = observer.BeginSession();
        observer.Observe(generation, 1, null);
        observer.SetEnabled(true);
        observer.Observe(generation, 2, Draft());
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.MidDraft));
    }

    // Catches treating a first draft after an authoritative idle baseline as a reconnect baseline.
    [TestCase(DraftRole.Top)] [TestCase(DraftRole.Jungle)] [TestCase(DraftRole.Middle)]
    [TestCase(DraftRole.Bottom)] [TestCase(DraftRole.Support)]
    public void IdleBaselineAdmitsEveryAssignedRole(DraftRole role)
    {
        var observer = new DraftObserver();
        observer.SetEnabled(true);
        var generation = observer.BeginSession();
        observer.Observe(generation, 1, null);
        observer.Observe(generation, 2, Draft(role: role));
        Assert.Multiple(() =>
        {
            Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Admitted));
            Assert.That(observer.State.Role, Is.EqualTo(role));
            Assert.That(observer.State.ActionId, Is.EqualTo(11));
            Assert.That(observer.State.Action, Is.EqualTo(DraftActionKind.Ban));
            Assert.That(observer.State.TimeLeftMilliseconds, Is.EqualTo(28000));
        });
    }

    // Catches stale generation/sequence regression and duplicate timers restarting action identity.
    [Test]
    public void ReconnectInvalidatesOldInputsAndDoesNotReadmitOngoingDraft()
    {
        var observer = new DraftObserver();
        observer.SetEnabled(true);
        var old = observer.BeginSession();
        observer.Observe(old, 1, null);
        observer.Observe(old, 3, Draft(remaining: 9000));
        observer.Observe(old, 2, Draft(remaining: 30000));
        observer.Observe(old, 3, Draft(102));
        Assert.That(observer.State.TimeLeftMilliseconds, Is.EqualTo(9000));
        observer.Disconnect();
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Disconnected));
        var current = observer.BeginSession();
        observer.Observe(old, 99, Draft(999));
        observer.Observe(current, 1, Draft());
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.MidDraft));
        Assert.That(observer.State.GameId, Is.EqualTo(101));
    }

    [TestCase(420, DraftRole.Middle, DraftAdmission.UnsupportedQueue)]
    [TestCase(430, DraftRole.Middle, DraftAdmission.UnsupportedQueue)]
    [TestCase(400, null, DraftAdmission.UnknownRole)]
    public void UnsupportedInputsRemainVisibleWithoutAdmission(int queue, DraftRole? role, DraftAdmission expected)
    {
        var observer = new DraftObserver();
        observer.SetEnabled(true);
        var generation = observer.BeginSession();
        observer.Observe(generation, 1, null);
        observer.Observe(generation, 2, Draft(queue: queue, role: role));
        Assert.That(observer.State.Admission, Is.EqualTo(expected));
    }

    // Catches showing a completed/teammate action as an active local window.
    [Test]
    public void ProgressionClearsCompletedLocalActionAndEndsWithoutLosingEnabledState()
    {
        var observer = new DraftObserver();
        observer.SetEnabled(true);
        var generation = observer.BeginSession();
        observer.Observe(generation, 1, null);
        observer.Observe(generation, 2, Draft());
        observer.Observe(generation, 3, Draft(kind: DraftActionKind.Pick, remaining: 5000));
        Assert.That(observer.State.Action, Is.EqualTo(DraftActionKind.Pick));
        observer.Observe(generation, 4, Draft() with { Actions = [new(11, 2, DraftActionKind.Pick, 103, false, true)] });
        Assert.That(observer.State.ActionId, Is.Null);
        observer.Observe(generation, 5, null);
        observer.Observe(generation, 6, Draft(102));
        Assert.That(observer.State.Admission, Is.EqualTo(DraftAdmission.Admitted));
    }
}
