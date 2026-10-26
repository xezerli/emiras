using System.Text.Json;
using DentaCore.BuildingBlocks.Application;
using DentaCore.Sync.Application;
using DentaCore.Sync.Contracts;

namespace DentaCore.Sync.UnitTests;

public sealed class PushTests
{
    private sealed class FakeStore : ISyncClientChangeStore
    {
        public Dictionary<Guid, StoredChange> Rows { get; } = [];

        public Task<StoredChange?> FindAsync(Guid clientChangeId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.TryGetValue(clientChangeId, out var row) ? row : null);

        public Task SaveAsync(Guid clientChangeId, Guid deviceId, Guid userId, string entity, Guid entityId, string outcome, string responseJson, CancellationToken cancellationToken)
        {
            Rows.TryAdd(clientChangeId, new StoredChange(userId, outcome, responseJson));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHandler(string entity, Func<SyncChangeRequest, SyncApplyOutcome> apply) : ISyncEntityHandler
    {
        public string Entity { get; } = entity;

        public List<SyncChangeRequest> Calls { get; } = [];

        public Task<SyncApplyOutcome> ApplyAsync(SyncChangeRequest change, CancellationToken cancellationToken)
        {
            Calls.Add(change);
            return Task.FromResult(apply(change));
        }
    }

    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Device = Guid.NewGuid();

    private static PushChangesCommandHandler Handler(FakeStore store, Guid? user = null, params ISyncEntityHandler[] handlers) =>
        new(handlers, store, new CurrentUser(user ?? Me, [], [], null));

    private static PushChangeInput Change(string entity = "notes", string op = "I", Guid? clientChangeId = null, int? baseVersion = null) =>
        new(entity, Guid.NewGuid(), op, baseVersion, JsonDocument.Parse("{}").RootElement.Clone(), clientChangeId ?? Guid.NewGuid());

    [Fact]
    public async Task Each_outcome_goes_to_its_own_list_in_the_clients_order_and_is_recorded()
    {
        var store = new FakeStore();
        var handler = new FakeHandler("notes", c => c.Op switch
        {
            "I" => SyncApplyOutcome.Applied(1),
            "U" => SyncApplyOutcome.Conflict(7, JsonDocument.Parse("{\"x\":1}").RootElement.Clone()),
            _ => SyncApplyOutcome.Rejected("note.not_deletable", "no"),
        });
        var created = Change(op: "I");
        var updated = Change(op: "U", baseVersion: 1);
        var deleted = Change(op: "D");

        var result = (await Handler(store, null, handler).Handle(new PushChangesCommand(Device, [created, updated, deleted]), default)).Value;

        Assert.Equal(created.ClientChangeId, Assert.Single(result.Applied).ClientChangeId);
        Assert.Equal(1, result.Applied[0].RowVersion);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(updated.ClientChangeId, conflict.ClientChangeId);
        Assert.Equal(7, conflict.ServerVersion);
        Assert.Equal("needs_manual", conflict.Resolution);
        Assert.Equal("note.not_deletable", Assert.Single(result.Rejected).Code);
        Assert.Equal(["I", "U", "D"], handler.Calls.Select(c => c.Op).ToArray());
        Assert.Equal(3, store.Rows.Count);
    }

    [Fact]
    public async Task Replaying_a_recorded_change_returns_the_same_outcome_without_applying_it_again()
    {
        var store = new FakeStore();
        var handler = new FakeHandler("notes", _ => SyncApplyOutcome.Applied(3));
        var change = Change();
        var first = await Handler(store, null, handler).Handle(new PushChangesCommand(Device, [change]), default);

        var replay = await Handler(store, null, handler).Handle(new PushChangesCommand(Device, [change]), default);

        Assert.Single(handler.Calls);
        Assert.Equal(first.Value.Applied[0], replay.Value.Applied[0]);
    }

    [Fact]
    public async Task Replayed_conflicts_and_rejections_are_returned_as_recorded()
    {
        var store = new FakeStore();
        var calls = 0;
        var handler = new FakeHandler("notes", c =>
        {
            calls++;
            return c.Op == "U" ? SyncApplyOutcome.Conflict(2, JsonDocument.Parse("{}").RootElement.Clone()) : SyncApplyOutcome.Rejected("x.y", "no");
        });
        var conflicting = Change(op: "U", baseVersion: 1);
        var rejecting = Change(op: "D");
        await Handler(store, null, handler).Handle(new PushChangesCommand(Device, [conflicting, rejecting]), default);

        var replay = (await Handler(store, null, handler).Handle(new PushChangesCommand(Device, [conflicting, rejecting]), default)).Value;

        Assert.Equal(2, calls);
        Assert.Equal(2, replay.Conflicts[0].ServerVersion);
        Assert.Equal("x.y", replay.Rejected[0].Code);
    }

    [Fact]
    public async Task Client_change_id_of_another_user_is_refused_and_never_replayed()
    {
        var store = new FakeStore();
        var handler = new FakeHandler("notes", _ => SyncApplyOutcome.Applied(1));
        var change = Change();
        await Handler(store, Guid.NewGuid(), handler).Handle(new PushChangesCommand(Device, [change]), default);

        var result = (await Handler(store, Me, handler).Handle(new PushChangesCommand(Device, [change]), default)).Value;

        Assert.Equal("sync.client_change_id_taken", Assert.Single(result.Rejected).Code);
        Assert.Empty(result.Applied);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Unsupported_entity_is_rejected_but_not_recorded_so_it_can_be_retried_later()
    {
        var store = new FakeStore();
        var change = Change(entity: "invoices");

        var result = (await Handler(store, null, new FakeHandler("notes", _ => SyncApplyOutcome.Applied(1))).Handle(new PushChangesCommand(Device, [change]), default)).Value;

        Assert.Equal("sync.entity_not_writable", Assert.Single(result.Rejected).Code);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Unexpected_exception_propagates_and_nothing_is_recorded_for_that_change()
    {
        var store = new FakeStore();
        var ok = Change();
        var boom = Change();
        var handler = new FakeHandler("notes", c => c.EntityId == boom.EntityId ? throw new InvalidOperationException("db down") : SyncApplyOutcome.Applied(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(store, null, handler).Handle(new PushChangesCommand(Device, [ok, boom]), default));

        Assert.Contains(ok.ClientChangeId, store.Rows.Keys);     // əvvəlkilər artıq tətbiq olunub və yazılıb
        Assert.DoesNotContain(boom.ClientChangeId, store.Rows.Keys);   // uğursuz olan təkrar göndəriləcək
    }

    // ---------------- Validation ----------------
    [Fact]
    public void Validator_enforces_size_ops_ids_and_unique_client_change_ids()
    {
        var validator = new PushChangesCommandValidator();
        var good = Change();

        Assert.True(validator.Validate(new PushChangesCommand(Device, [good])).IsValid);
        Assert.False(validator.Validate(new PushChangesCommand(Device, [])).IsValid);
        Assert.False(validator.Validate(new PushChangesCommand(Device, Enumerable.Range(0, SyncLimits.MaxPushChanges + 1).Select(_ => Change()).ToList())).IsValid);
        Assert.False(validator.Validate(new PushChangesCommand(Guid.Empty, [good])).IsValid);
        Assert.False(validator.Validate(new PushChangesCommand(Device, [good, good])).IsValid);   // eyni clientChangeId iki dəfə
        Assert.False(validator.Validate(new PushChangesCommand(Device, [Change(op: "X")])).IsValid);
        Assert.False(validator.Validate(new PushChangesCommand(Device, [good with { EntityId = Guid.Empty }])).IsValid);
        Assert.False(validator.Validate(new PushChangesCommand(Device, [good with { Entity = " " }])).IsValid);
    }

    [Theory]
    [InlineData(-1, 10, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, 2001, false)]
    [InlineData(0, 2000, true)]
    [InlineData(12345, 1, true)]
    public void Pull_validator(long cursor, int limit, bool valid) =>
        Assert.Equal(valid, new PullChangesQueryValidator().Validate(new PullChangesQuery(cursor, limit)).IsValid);

    [Fact]
    public void Audit_descriptor_counts_outcomes_without_content()
    {
        var descriptor = new PushChangesCommand(Device, []).Describe(new PushResultDto([new AppliedDto(Guid.NewGuid(), Guid.NewGuid(), 1)], [], []));

        Assert.Equal("sync.push", descriptor.Action);
        Assert.Equal(Device, descriptor.EntityId);
        Assert.Equal("{\"applied\":1,\"conflicts\":0,\"rejected\":0}", descriptor.DetailsJson);
    }
}
