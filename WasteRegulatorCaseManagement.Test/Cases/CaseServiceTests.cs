using WasteRegulatorCaseManagement.AuditEvents;
using System.Security.Claims;
using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NSubstitute;
using NUlid;
using AwesomeAssertions.Execution;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Backed by ephemeral MongoDB. Persistence is the real
/// <see cref="CasePersistence"/>; assertions are made against the
/// document fetched back from Mongo, not against the in-memory instance
/// the test author handed to the engine.
/// </summary>
public class CaseServiceTests : IAsyncDisposable
{
	private const string CaseType = "test-type";
	private static readonly DateTime InitialNow = new(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);
	private static readonly DateTime TickedNow = InitialNow.AddMinutes(5);

	private readonly TestMongoDbClientFactory _clientFactory;
	private readonly string _databaseName;
	private readonly CasePersistence _persistence;
	private readonly FakeTimeProvider _time = new(TickedNow);

	public CaseServiceTests(MongoIntegrationFixture fixture)
	{
		_databaseName = MongoIntegrationFixture.NewDatabaseName("svc");
		_clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
		_persistence = new CasePersistence(_clientFactory, NullLoggerFactory.Instance, new CaseRegistry([]), new AuditEventService(_clientFactory, TimeProvider.System));
	}

	public async ValueTask DisposeAsync() =>
		await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

	private CaseService BuildService(CaseModuleConfig type) =>
		new(new CaseRegistry([type]), _persistence, NullLogger<CaseService>.Instance, _time);

	private static CaseIntake NewIntake() =>
		new(
			SourceSystemId: Ulid.NewUlid().ToString(),
			CompletionEvent: CaseTestCompletionEvent.DefaultSpec,
			Nation: "ENGLAND",
			OrgId: "test-org",
			ProducerType: null,
			DisplayData: new Dictionary<string, CaseDisplayField>()
		);

	private static CaseModuleConfig BuildType(CaseTransition[]? transitions = null)
	{
		var states = new[]
		{
			new CaseState("submitted", "Submitted"),
			new CaseState("approved", "Approved", IsTerminal: true),
			new CaseState("rejected", "Rejected", IsTerminal: true),
		};
		return TestCaseType.Create(
			CaseType,
			"Test type",
			initialState: states[0],
			states: states,
			transitions: transitions
		);
	}

	private async Task<Case> SeedAsync(
		string stateId = "submitted",
		Action<Case>? configure = null
	)
	{
		var caseRecord = new Case
		{
			SourceSystemId = Ulid.NewUlid().ToString(),
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = Ulid.NewUlid().ToString(),
			CaseType = CaseType,
			StateId = stateId,
			SubmittedAt = InitialNow,
			LastModifiedAt = InitialNow,
			SubmittedBy = "test-client",
		};
		configure?.Invoke(caseRecord);
		await _clientFactory.GetCollection<Case>("cases").InsertOneAsync(caseRecord, cancellationToken: TestContext.Current.CancellationToken);
		return caseRecord;
	}

	private async Task<Case> GetAsync(string id)
	{
		var fetched = await _persistence.GetByIdAsync(id, TestContext.Current.CancellationToken);
		return TestAsserts.NotNull(fetched);
	}

	private static ClaimsPrincipal User() => TestCaseworkerClaims.Principal();

	private static ClaimsPrincipal UserWithoutActorId() =>
		new(
			new ClaimsIdentity(
				[new Claim("client_id", "test-client"), new Claim("user:nation", "ENGLAND")],
				"test"
			)
		);

	private static ClaimsPrincipal UserWithRoles(string userId, params string[] roles)
	{
		var claims = new List<Claim>(TestCaseworkerClaims.StandardClaims);
		claims.RemoveAll(c => c.Type == "user:id");
		claims.Add(new Claim("user:id", userId));
		foreach (var role in roles)
		{
			claims.Add(new Claim(ClaimTypes.Role, role));
		}
		return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
	}

	// RA-410: CompleteTaskAsync and the whole task framework it drove
	// (CompleteTask_records_task_against_current_state_and_persists,
	// CompleteTask_is_idempotent_when_already_complete,
	// CompleteTask_treats_existing_completion_as_idempotent_after_bson_round_trip_with_different_casing,
	// CompleteTask_treats_existing_completion_as_idempotent_after_bson_round_trip_with_different_state_casing,
	// CompleteTask_fails_when_task_does_not_apply_to_current_state,
	// CompleteTask_returns_not_found_when_case_missing) are gone.

	[Fact]
	public async Task ApplyAction_succeeds_now_the_task_gate_is_removed()
	{
		// RA-410: this used to assert IncompleteTasks because "approve" was
		// gated on two outstanding tasks. The task framework (and the gate)
		// are gone, so the same seed now simply succeeds — regression cover
		// for the ungating.
		var type = BuildType(
			transitions: [new CaseTransition("approve", "Approve", "submitted", "approved")]
		);
		var caseRecord = await SeedAsync();

		var result = await BuildService(type)
			.ApplyActionAsync(
				caseRecord.Id,
				"approve",
				decision: null,
				User(),
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Should().BeEquivalentTo(new { StateId = "approved", IsTerminal = true, Version = 1 });
	}

	[Fact]
	public async Task ApplyAction_transitions_when_all_tasks_complete()
	{
		var type = BuildType(
			transitions: [new CaseTransition("approve", "Approve", "submitted", "approved")]
		);
		var caseRecord = await SeedAsync();

		var result = await BuildService(type)
			.ApplyActionAsync(
				caseRecord.Id,
				"approve",
				decision: null,
				User(),
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Should().BeEquivalentTo(new { StateId = "approved", LastModifiedAt = TickedNow, Version = 1 });
	}

	[Fact]
	public async Task ApplyAction_transitions_via_a_different_declared_action()
	{
		var type = BuildType(
			transitions: [new CaseTransition("withdraw", "Withdraw", "submitted", "rejected")]
		);
		var caseRecord = await SeedAsync();

		var result = await BuildService(type)
			.ApplyActionAsync(
				caseRecord.Id,
				"withdraw",
				decision: null,
				User(),
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.StateId.Should().Be("rejected");
	}

	[Fact]
	public async Task ApplyAction_fails_when_action_does_not_apply_to_current_state()
	{
		var type = BuildType(
			transitions: [new CaseTransition("approve", "Approve", "submitted", "approved")]
		);
		var caseRecord = await SeedAsync(stateId: "approved");

		var result = await BuildService(type)
			.ApplyActionAsync(
				caseRecord.Id,
				"approve",
				decision: null,
				User(),
				TestContext.Current.CancellationToken
			);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.TerminalState });
	}

	[Fact]
	public async Task ApplyAction_fails_when_action_unknown()
	{
		var type = BuildType(
			transitions: [new CaseTransition("approve", "Approve", "submitted", "approved")]
		);
		var caseRecord = await SeedAsync();

		var result = await BuildService(type)
			.ApplyActionAsync(caseRecord.Id, "delete", decision: null, User(), TestContext.Current.CancellationToken);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.UnknownAction });
	}

	// RA-351: an action id can be declared on more than one state (e.g.
	// `sla-extend` self-loops on both `assessment-in-progress` and `queried`).
	// ApplyActionAsync must resolve to the transition whose FromStateId matches
	// the case's CURRENT state, not the first one declared — otherwise the
	// generic action endpoint rejects the action from every state after the
	// first, even though Project() advertises it.
	[Fact]
	public async Task ApplyAction_resolves_a_multi_state_action_to_the_current_state()
	{
		var stateA = new CaseState("state-a", "State A");
		var stateB = new CaseState("state-b", "State B");
		var type = TestCaseType.Create(
			CaseType,
			"Test type",
			initialState: stateA,
			states: [stateA, stateB],
			transitions:
			[
                // Same action id declared twice; the current-state match is the
                // SECOND declaration, so a first-declaration-wins resolution
                // would wrongly reject it.
                new CaseTransition("ping", "Ping", "state-a", "state-a"),
				new CaseTransition("ping", "Ping", "state-b", "state-b"),
			]
		);
		var caseRecord = await SeedAsync(stateId: "state-b");

		var result = await BuildService(type)
			.ApplyActionAsync(caseRecord.Id, "ping", decision: null, User(), TestContext.Current.CancellationToken);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.StateId.Should().Be("state-b");
	}

	// RA-351: the fallback must not swallow a genuinely invalid request — an
	// action declared only on other states is still rejected with
	// InvalidTransition (not UnknownAction) when applied from a state it does
	// not cover.
	[Fact]
	public async Task ApplyAction_fails_InvalidTransition_when_multi_state_action_not_valid_here()
	{
		var stateA = new CaseState("state-a", "State A");
		var stateB = new CaseState("state-b", "State B");
		var stateC = new CaseState("state-c", "State C");
		var type = TestCaseType.Create(
			CaseType,
			"Test type",
			initialState: stateA,
			states: [stateA, stateB, stateC],
			transitions:
			[
				new CaseTransition("ping", "Ping", "state-a", "state-a"),
				new CaseTransition("ping", "Ping", "state-b", "state-b"),
			]
		);
		var caseRecord = await SeedAsync(stateId: "state-c");

		var result = await BuildService(type)
			.ApplyActionAsync(caseRecord.Id, "ping", decision: null, User(), TestContext.Current.CancellationToken);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.InvalidTransition });
	}

	[Fact]
	public async Task ApplyAction_returns_ConcurrencyConflict_when_persistence_throws()
	{
		var type = BuildType(
			transitions: [new CaseTransition("approve", "Approve", "submitted", "approved")]
		);
		var caseRecord = await SeedAsync();
		var racingService = BuildRacingService(type, caseRecord.Id);

		var result = await racingService.ApplyActionAsync(
			caseRecord.Id,
			"approve",
			decision: null,
			User(),
			TestContext.Current.CancellationToken
		);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.ConcurrencyConflict });
	}

	[Fact]
	public async Task ApplyAction_returns_conflict_when_another_change_lands_during_replace()
	{
		// ADR-0011 used to reload and merge a competing note after a
		// successful callback. Completion events conclude the case in one
		// transactional write instead, so a version race aborts the action.
		var type = new CaseModuleConfig
		{
			CaseType = CaseType,
			DisplayName = "Test type",
			TemplateVersion = "v1",
			InitialStateId = "submitted",
			States = [new CaseState("submitted", "Submitted"), new CaseState("approved", "Approved", IsTerminal: true)],
			Transitions = [new CaseTransition("approve", "Approve", "submitted", "approved")],
		};
		var caseRecord = await SeedAsync();
		var raced = false;
		var racingOnce = new RacingPersistence(
			_persistence,
			() =>
			{
				if (raced)
				{
					return;
				}
				raced = true;
				AddCompetingNote(caseRecord.Id);
			}
		);
		var service = new CaseService(
			new CaseRegistry([type]),
			racingOnce,
			NullLogger<CaseService>.Instance,
			_time
		);

		var result = await service.ApplyActionAsync(
			caseRecord.Id,
			"approve",
			decision: null,
			User(),
			TestContext.Current.CancellationToken
		);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.ConcurrencyConflict });
		var persisted = TestAsserts.NotNull(
			await _persistence.GetByIdAsync(caseRecord.Id, TestContext.Current.CancellationToken)
		);
		persisted.StateId.Should().Be("submitted");
		persisted.Timeline.Should().Contain(entry => entry.PerformedById == "competing-user");
		persisted.Timeline.Should().NotContain(entry => entry.Action == "state-changed");
	}

	[Fact]
	public async Task AddNote_records_user_id_verbatim_without_falling_back_to_client_id()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var result = await BuildService(type)
			.AddNoteAsync(
				caseRecord.Id,
				"An audit-worthy observation.",
				User(),
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue(result.Message);
		var fetched = await GetAsync(caseRecord.Id);
		var noteEntry = fetched.Timeline.Should().ContainSingle(a => a.Action == "note-added").Subject;
		noteEntry.PerformedById.Should().Be("test-user");
	}

	[Fact]
	public async Task Project_lists_all_actions_now_the_task_gate_is_removed()
	{
		// RA-410: this used to assert that only "withdraw" was available
		// because "approve" and "reject" were gated on an outstanding task.
		// The task framework (and the gate) are gone, so all three
		// caller-invocable transitions from the current state are now
		// listed — regression cover for the ungating.
		var type = BuildType(
			transitions:
			[
				new CaseTransition("approve", "Approve", "submitted", "approved"),
				new CaseTransition("reject", "Reject", "submitted", "rejected"),
				new CaseTransition("withdraw", "Withdraw", "submitted", "rejected"),
			]
		);
		// Project is a pure read-only function over an in-memory document
		// (no persistence call), so a hand-built instance exercises the
		// same code path.
		var caseRecord = new Case
		{
			SourceSystemId = Ulid.NewUlid().ToString(),
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = Ulid.NewUlid().ToString(),
			CaseType = CaseType,
			StateId = "submitted",
			SubmittedAt = InitialNow,
			LastModifiedAt = InitialNow,
			SubmittedBy = "test-client",
		};

		var projection = BuildService(type).Project(caseRecord);

		projection.AvailableActions.Select(a => a.ActionId).OrderBy(a => a).ToArray()
			.Should().Equal(["approve", "reject", "withdraw"]);
	}

	[Fact]
	public async Task Project_keeps_every_transition_when_all_are_caller_invocable()
	{
		// Regression guard for RA-364: the new filter must not cost a state
		// anything when nothing is declared non-invocable.
		var type = BuildType(
			transitions:
			[
				new CaseTransition("approve", "Approve", "submitted", "approved"),
				new CaseTransition("reject", "Reject", "submitted", "rejected"),
			]
		);
		var caseRecord = new Case
		{
			SourceSystemId = Ulid.NewUlid().ToString(),
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = Ulid.NewUlid().ToString(),
			CaseType = CaseType,
			StateId = "submitted",
			SubmittedAt = InitialNow,
			LastModifiedAt = InitialNow,
			SubmittedBy = "test-client",
		};

		var projection = BuildService(type).Project(caseRecord);

		projection.AvailableActions.Select(a => a.ActionId).OrderBy(a => a).ToArray()
			.Should().Equal(["approve", "reject"]);
		await Task.CompletedTask;
	}

	[Fact]
	public async Task Project_returns_no_actions_for_terminal_state()
	{
		var type = BuildType(
			transitions: [new CaseTransition("approve", "Approve", "submitted", "approved")]
		);
		var caseRecord = new Case
		{
			SourceSystemId = Ulid.NewUlid().ToString(),
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = Ulid.NewUlid().ToString(),
			CaseType = CaseType,
			StateId = "approved",
			SubmittedAt = InitialNow,
			LastModifiedAt = InitialNow,
			SubmittedBy = "test-client",
		};

		var projection = BuildService(type).Project(caseRecord);

		projection.AvailableActions.Should().BeEmpty();
		await Task.CompletedTask;
	}

	[Fact]
	public async Task ApplyAction_fails_when_type_is_unregistered_and_has_no_template_snapshot()
	{
		// Distinct from ApplyAction_fails_when_action_unknown (a registered
		// type rejecting an unknown action id): this covers LoadAsync's own
		// template-resolution guard, reached when the case's type has
		// been de-registered (or was never registered) AND it predates
		// template snapshots, so template resolution has
		// nothing to fall back on.
		var registeredType = BuildType();
		var orphanTypeId = "no-longer-registered-type";
		var caseRecord = new Case
		{
			SourceSystemId = Ulid.NewUlid().ToString(),
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = Ulid.NewUlid().ToString(),
			CaseType = orphanTypeId,
			StateId = "submitted",
			SubmittedAt = InitialNow,
			LastModifiedAt = InitialNow,
			SubmittedBy = "test-client",
			TemplateSnapshot = null,
		};
		await _clientFactory.GetCollection<Case>("cases").InsertOneAsync(caseRecord, cancellationToken: TestContext.Current.CancellationToken);

		var result = await BuildService(registeredType)
			.ApplyActionAsync(
				caseRecord.Id,
				"approve",
				decision: null,
				User(),
				TestContext.Current.CancellationToken
			);

		using var scope = new AssertionScope();
		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.UnknownAction });
		result.Message.Should().Contain(orphanTypeId);
	}

	[Fact]
	public async Task AssignAsync_returns_missing_actor_identity_when_user_id_claim_is_whitespace()
	{
		// Covers ResolveActorUserId's `string.IsNullOrWhiteSpace(id)` branch
		// for a claim that is *present* but blank, distinct from the
		// claim-absent case already covered above.
		var type = BuildType();
		var caseRecord = await SeedAsync();
		var user = new ClaimsPrincipal(
			new ClaimsIdentity(
				[new Claim("client_id", "test-client"), new Claim("user:nation", "ENGLAND"), new Claim("user:id", "   ")],
				"test"
			)
		);

		var result = await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice",
				user,
				TestContext.Current.CancellationToken
			);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.MissingActorIdentity });
	}

	// ---------------------- Assignment ----------------------

	[Fact]
	public async Task Assign_records_assignee_with_snapshot_and_audit_metadata()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var actor = UserWithRoles("actor-1", "assign");
		var result = await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice Example",
				actor,
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Should().BeEquivalentTo(
			new
			{
				AssignedToId = "alice-1",
				AssignedToName = "Alice Example",
				AssignedAt = TickedNow,
				AssignedBy = "actor-1",
				LastModifiedAt = TickedNow,
				Version = 1,
			}
		);
	}

	[Fact]
	public async Task Assign_re_assignment_replaces_previous_assignee()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync(configure: w =>
		{
			w.AssignedToId = "bob-1";
			w.AssignedToName = "Bob";
			w.AssignedAt = InitialNow;
			w.AssignedBy = "old-actor";
		});

		var actor = UserWithRoles("actor-1", "assign");
		var result = await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"carol-1",
				"Carol",
				actor,
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Should().BeEquivalentTo(new { AssignedToId = "carol-1", AssignedToName = "Carol", AssignedBy = "actor-1" });
	}

	[Fact]
	public async Task Assign_is_idempotent_when_assignee_unchanged()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync(configure: w =>
		{
			w.AssignedToId = "alice-1";
			w.AssignedToName = "Alice";
			w.AssignedAt = InitialNow;
			w.AssignedBy = "old-actor";
		});

		var actor = UserWithRoles("actor-1", "assign");
		var result = await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice",
				actor,
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);

		using var scope = new AssertionScope();
		result.IsIdempotentReplay.Should().BeTrue(
			"re-assigning to the same user must be flagged as a replay so "
				+ "the endpoint can set X-Idempotent-Replay: true"
		);
		fetched.Should().BeEquivalentTo(new { AssignedAt = InitialNow, AssignedBy = "old-actor", Version = 0 });
	}

	[Fact]
	public async Task AssignAsync_returns_missing_actor_identity_when_no_user_id_claim()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var result = await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice",
				UserWithoutActorId(),
				TestContext.Current.CancellationToken
			);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.MissingActorIdentity });
		var fetched = await GetAsync(caseRecord.Id);
		fetched.AssignedToId.Should().BeNull();
	}

	[Fact]
	public async Task UnassignAsync_returns_missing_actor_identity_when_no_user_id_claim()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync(configure: w =>
		{
			w.AssignedToId = "alice-1";
			w.AssignedToName = "Alice";
			w.AssignedAt = InitialNow;
			w.AssignedBy = "old-actor";
		});

		var result = await BuildService(type)
			.UnassignAsync(
				caseRecord.Id,
				UserWithoutActorId(),
				TestContext.Current.CancellationToken
			);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.MissingActorIdentity });
		var fetched = await GetAsync(caseRecord.Id);
		fetched.AssignedToId.Should().Be("alice-1");
	}

	// ---- RA-358: assignment is refused on a closed (terminal) case ----

	/// <summary>
	/// A type whose closed states cover every terminal id the service uses in
	/// anger. Built here rather than folded into <see cref="BuildType"/> so
	/// the existing assignment tests keep their original state machine.
	/// </summary>
	private static CaseModuleConfig BuildTypeWithTerminalStates()
	{
		var states = new[]
		{
			new CaseState("submitted", "Submitted"),
			new CaseState("withdrawn", "Withdrawn", IsTerminal: true),
			new CaseState("approved", "Approved", IsTerminal: true),
			new CaseState("rejected", "Rejected", IsTerminal: true),
		};
		return TestCaseType.Create(CaseType, "Test type", initialState: states[0], states: states);
	}

	[Theory]
	[InlineData("withdrawn")]
	[InlineData("approved")]
	[InlineData("rejected")]
	public async Task Assign_is_rejected_when_case_is_in_a_terminal_state(string stateId)
	{
		var type = BuildTypeWithTerminalStates();
		var caseRecord = await SeedAsync(stateId: stateId);

		var actor = UserWithRoles("actor-1", "assign");
		var result = await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice",
				actor,
				TestContext.Current.CancellationToken
			);

		var fetched = await GetAsync(caseRecord.Id);

		using var scope = new AssertionScope();
		result.Should().BeEquivalentTo(
			new { IsSuccess = false, FailureCode = CaseActionFailureCode.TerminalState, IsIdempotentReplay = false }
		);
		result.Message.Should().Contain(stateId);
		// RA-358: the message is rendered verbatim to the user, so it must
		// never carry the system-generated case id.
		result.Message.Should().NotContain(caseRecord.Id.ToString());

		fetched.Should().BeEquivalentTo(new { AssignedToId = (string?)null, Version = 0 });
		fetched.Timeline.Should().BeEmpty();
	}

	[Theory]
	[InlineData("withdrawn")]
	[InlineData("approved")]
	[InlineData("rejected")]
	public async Task Unassign_is_rejected_when_case_is_in_a_terminal_state(string stateId)
	{
		var type = BuildTypeWithTerminalStates();
		var caseRecord = await SeedAsync(
			stateId: stateId,
			configure: w =>
			{
				w.AssignedToId = "alice-1";
				w.AssignedToName = "Alice";
				w.AssignedAt = InitialNow;
				w.AssignedBy = "old-actor";
			}
		);

		var actor = UserWithRoles("actor-1", "assign");
		var result = await BuildService(type)
			.UnassignAsync(caseRecord.Id, actor, TestContext.Current.CancellationToken);

		var fetched = await GetAsync(caseRecord.Id);

		using var scope = new AssertionScope();
		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.TerminalState });
		result.Message.Should().Contain(stateId);
		result.Message.Should().NotContain(caseRecord.Id.ToString());

		fetched.Should().BeEquivalentTo(new { AssignedToId = "alice-1", Version = 0 });
		fetched.Timeline.Should().BeEmpty();
	}

	[Fact]
	public async Task Assign_terminal_check_runs_before_the_idempotent_replay_shortcut()
	{
		// Re-assigning a closed case to whoever already holds it must still be
		// refused rather than answered with a misleading 200 + replay header.
		var type = BuildTypeWithTerminalStates();
		var caseRecord = await SeedAsync(
			stateId: "withdrawn",
			configure: w =>
			{
				w.AssignedToId = "alice-1";
				w.AssignedToName = "Alice";
				w.AssignedAt = InitialNow;
				w.AssignedBy = "old-actor";
			}
		);

		var result = await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice",
				UserWithRoles("actor-1", "assign"),
				TestContext.Current.CancellationToken
			);

		result.Should().BeEquivalentTo(new { FailureCode = CaseActionFailureCode.TerminalState, IsIdempotentReplay = false });
	}

	[Fact]
	public async Task Unassign_terminal_check_runs_before_the_idempotent_replay_shortcut()
	{
		var type = BuildTypeWithTerminalStates();
		var caseRecord = await SeedAsync(stateId: "withdrawn");

		var result = await BuildService(type)
			.UnassignAsync(
				caseRecord.Id,
				UserWithRoles("actor-1", "assign"),
				TestContext.Current.CancellationToken
			);

		result.Should().BeEquivalentTo(new { FailureCode = CaseActionFailureCode.TerminalState, IsIdempotentReplay = false });
	}

	[Fact]
	public async Task Assign_terminality_is_judged_by_the_stored_template_snapshot()
	{
		// Template versioning: the item was submitted under a template that
		// calls "closed" terminal. The live registry no longer declares that
		// state at all, but the in-flight item must still be treated as closed.
		var snapshotType = TestCaseType.Create(
			CaseType,
			"Test type",
			initialState: new CaseState("submitted", "Submitted"),
			states:
			[
				new CaseState("submitted", "Submitted"),
				new CaseState("closed", "Closed", IsTerminal: true),
			]
		);
		var caseRecord = await SeedAsync(
			stateId: "closed",
			configure: w => w.TemplateSnapshot = CaseTemplateSnapshot.Capture(snapshotType)
		);

		var result = await BuildService(BuildType())
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice",
				UserWithRoles("actor-1", "assign"),
				TestContext.Current.CancellationToken
			);

		result.FailureCode.Should().Be(CaseActionFailureCode.TerminalState);
	}

	[Fact]
	public async Task Assign_succeeds_when_the_current_state_is_unknown_to_the_template()
	{
		// Not terminal because nothing says it is — assignment stays open
		// rather than failing closed on a state the template never declared.
		var caseRecord = await SeedAsync(stateId: "some-unmodelled-state");

		var result = await BuildService(BuildType())
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice",
				UserWithRoles("actor-1", "assign"),
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		(await GetAsync(caseRecord.Id)).AssignedToId.Should().Be("alice-1");
	}

	/// <summary>
	/// Unregistered type and no snapshot: terminality cannot be determined, so
	/// the guard fails closed rather than waving the mutation through. Without
	/// this, a legacy pre-snapshot item in a terminal state becomes freely
	/// assignable as soon as its type is de-registered or renamed — the same
	/// hole RA-358 closes, reached through a different door. Matches
	/// ApplyActionAsync, which refuses this condition before its own terminal
	/// check.
	/// </summary>
	private async Task<Case> SeedUnresolvableTemplateItemAsync()
	{
		var caseRecord = new Case
		{
			SourceSystemId = Ulid.NewUlid().ToString(),
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = Ulid.NewUlid().ToString(),
			CaseType = "not-registered",
			StateId = "submitted",
			SubmittedAt = InitialNow,
			LastModifiedAt = InitialNow,
			SubmittedBy = "test-client",
			AssignedToId = "bob-1",
			AssignedToName = "Bob",
			AssignedAt = InitialNow,
			AssignedBy = "old-actor",
		};
		await _clientFactory.GetCollection<Case>("cases").InsertOneAsync(caseRecord, cancellationToken: TestContext.Current.CancellationToken);
		return caseRecord;
	}

	[Fact]
	public async Task Assign_is_rejected_when_no_template_can_be_resolved_for_the_case()
	{
		var caseRecord = await SeedUnresolvableTemplateItemAsync();

		var result = await BuildService(BuildType())
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice",
				UserWithRoles("actor-1", "assign"),
				TestContext.Current.CancellationToken
			);

		var fetched = await GetAsync(caseRecord.Id);

		using var scope = new AssertionScope();
		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.UnknownAction });
		result.Message.Should().NotContain(caseRecord.Id.ToString());

		fetched.Should().BeEquivalentTo(new { AssignedToId = "bob-1", Version = 0 });
		fetched.Timeline.Should().BeEmpty();
	}

	[Fact]
	public async Task Unassign_is_rejected_when_no_template_can_be_resolved_for_the_case()
	{
		var caseRecord = await SeedUnresolvableTemplateItemAsync();

		var result = await BuildService(BuildType())
			.UnassignAsync(
				caseRecord.Id,
				UserWithRoles("actor-1", "assign"),
				TestContext.Current.CancellationToken
			);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.UnknownAction });

		var fetched = await GetAsync(caseRecord.Id);
		fetched.Should().BeEquivalentTo(new { AssignedToId = "bob-1", Version = 0 });
	}

	[Fact]
	public async Task Assign_blank_assignee_id_is_rejected()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var actor = UserWithRoles("actor-1", "assign");
		var result = await BuildService(type)
			.AssignAsync(caseRecord.Id, "   ", null, actor, TestContext.Current.CancellationToken);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.InvalidAssignment });
	}

	[Fact]
	public async Task Assign_returns_not_found_when_case_missing()
	{
		var type = BuildType();
		var actor = UserWithRoles("actor-1", "assign");
		var result = await BuildService(type)
			.AssignAsync(
				Ulid.NewUlid().ToString(),
				"alice-1",
				"Alice",
				actor,
				TestContext.Current.CancellationToken
			);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.CaseNotFound });
	}

	[Fact]
	public async Task Unassign_returns_not_found_when_case_missing()
	{
		var type = BuildType();
		var actor = UserWithRoles("actor-1", "assign");
		var result = await BuildService(type)
			.UnassignAsync(Ulid.NewUlid().ToString(), actor, TestContext.Current.CancellationToken);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.CaseNotFound });
	}

	[Fact]
	public async Task Assign_standard_user_can_self_assign_unassigned_item()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var actor = UserWithRoles("alice-1", "standard");
		var result = await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice",
				actor,
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.AssignedToId.Should().Be("alice-1");
	}

	[Fact]
	public async Task Assign_standard_user_can_assign_to_someone_else( /* RA-323 */
	)
	{
		// RA-323: every caseworker holds the same role, so a caller without
		// any special role can still assign/re-assign to anyone.
		var type = BuildType();
		var caseRecord = await SeedAsync(configure: w =>
		{
			w.AssignedToId = "bob-1";
			w.AssignedToName = "Bob";
		});

		var actor = UserWithRoles("alice-1", "standard");
		var result = await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"carol-1",
				"Carol",
				actor,
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.AssignedToId.Should().Be("carol-1");
	}

	[Fact]
	public async Task Unassign_clears_assignment_when_actor_has_assign_role()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync(configure: w =>
		{
			w.AssignedToId = "alice-1";
			w.AssignedToName = "Alice";
			w.AssignedAt = InitialNow;
			w.AssignedBy = "actor-1";
		});

		var actor = UserWithRoles("actor-2", "assign");
		var result = await BuildService(type)
			.UnassignAsync(caseRecord.Id, actor, TestContext.Current.CancellationToken);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Should().BeEquivalentTo(
			new
			{
				AssignedToId = (string?)null,
				AssignedToName = (string?)null,
				AssignedAt = (DateTime?)null,
				AssignedBy = (string?)null,
				LastModifiedAt = TickedNow,
				Version = 1,
			}
		);
	}

	[Fact]
	public async Task Unassign_is_idempotent_for_already_unassigned_item()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var actor = UserWithRoles("actor-1", "assign");
		var result = await BuildService(type)
			.UnassignAsync(caseRecord.Id, actor, TestContext.Current.CancellationToken);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);

		using var scope = new AssertionScope();
		result.IsIdempotentReplay.Should().BeTrue(
			"unassigning an already-unassigned item must be flagged as a replay so "
				+ "the endpoint can set X-Idempotent-Replay: true"
		);
		fetched.Version.Should().Be(0);
	}

	[Fact]
	public async Task Unassign_succeeds_for_standard_user( /* RA-323 */
	)
	{
		var type = BuildType();
		var caseRecord = await SeedAsync(configure: w =>
		{
			w.AssignedToId = "alice-1";
		});

		var actor = UserWithRoles("alice-1", "standard");
		var result = await BuildService(type)
			.UnassignAsync(caseRecord.Id, actor, TestContext.Current.CancellationToken);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.AssignedToId.Should().BeNull();
	}

	[Fact]
	public async Task AddNote_appends_note_with_author_snapshot_and_persists()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var actor = new ClaimsPrincipal(
			new ClaimsIdentity(
				[
					new Claim("client_id", "test-client"),
					new Claim("user:nation", "ENGLAND"), new Claim("user:id", "alice-1"),
					new Claim("user:name", "Alice Example"),
				],
				"test"
			)
		);

		var result = await BuildService(type)
			.AddNoteAsync(
				caseRecord.Id,
				"  Spoke to applicant; awaiting evidence.  ",
				actor,
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		var note = fetched.Timeline.Should().ContainSingle(e => e.Action == "note-added").Subject;

		using var scope = new AssertionScope();
		note.Should().BeEquivalentTo(
			new
			{
				Details = new Dictionary<string, string?> { ["text"] = "Spoke to applicant; awaiting evidence." },
				PerformedById = "alice-1",
				PerformedByName = "Alice Example",
				PerformedAt = TickedNow,
			}
		);
		fetched.Should().BeEquivalentTo(new { LastModifiedAt = TickedNow, Version = 1 });
	}

	[Fact]
	public async Task AddNote_returns_invalid_note_when_text_is_blank()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var result = await BuildService(type)
			.AddNoteAsync(caseRecord.Id, "   ", User(), TestContext.Current.CancellationToken);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.InvalidNote });
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Should().BeEquivalentTo(new { Timeline = Array.Empty<CaseTimelineEntry>(), Version = 0 });
	}

	[Fact]
	public async Task AddNote_returns_invalid_note_when_text_exceeds_limit()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var oversized = new string('x', CaseService.MaxNoteLength + 1);
		var result = await BuildService(type)
			.AddNoteAsync(caseRecord.Id, oversized, User(), TestContext.Current.CancellationToken);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.InvalidNote });
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().BeEmpty();
	}

	[Fact]
	public async Task AddNote_returns_not_found_when_case_missing()
	{
		var type = BuildType();

		var result = await BuildService(type)
			.AddNoteAsync(
				Ulid.NewUlid().ToString(),
				"anything",
				User(),
				TestContext.Current.CancellationToken
			);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.CaseNotFound });
	}

	[Fact]
	public async Task AddNote_allows_any_authenticated_user_without_assign_role()
	{
		// Notes are an audit narrative; any authenticated user (assessor or
		// otherwise) may add one. We assert this explicitly so a future change
		// doesn't accidentally tighten authorization.
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var standardUser = UserWithRoles("alice-1", "standard");
		var result = await BuildService(type)
			.AddNoteAsync(
				caseRecord.Id,
				"Note from a standard user.",
				standardUser,
				TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().ContainSingle(e => e.Action == "note-added");
	}

	private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
	}

	// ---------------------- Timeline (RA-97) ----------------------

	private static ClaimsPrincipal AuditUser(
		string userId = "alice-1",
		string userName = "Alice Example",
		string userEmail = "alice-1@regulator.example"
	) =>
		new(
			new ClaimsIdentity(
				[
					new Claim("client_id", TestCaseworkerClaims.ClientId),
					new Claim("user:nation", TestCaseworkerClaims.Nation),
					new Claim("user:id", userId),
					new Claim("user:name", userName),
					new Claim("user:email", userEmail),
				],
				"test"
			)
		);

	// RA-410: the CompleteTaskAsync audit coverage that used to live here
	// (Audit_CompleteTask_appends_entry_with_actor_and_task_details,
	// Audit_CompleteTask_idempotent_call_does_not_append_a_second_entry,
	// Audit_CompleteTask_failure_does_not_append_an_entry) is gone along with
	// the method itself. Audit_ApplyAction_* below covers the same
	// append-on-success / no-entry-on-idempotent-or-failure contract.

	[Fact]
	public async Task Audit_ApplyAction_records_from_and_to_state()
	{
		var type = BuildType(
			transitions: [new CaseTransition("withdraw", "Withdraw", "submitted", "rejected")]
		);
		var caseRecord = await SeedAsync();

		await BuildService(type)
			.ApplyActionAsync(
				caseRecord.Id,
				"withdraw",
				decision: null,
				AuditUser(),
				TestContext.Current.CancellationToken
			);

		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().BeEquivalentTo(
			[
				new
				{
					Action = "state-changed",
					ActionDisplayName = "Action applied",
					Details = new Dictionary<string, string?>
					{
						["actionId"] = "withdraw",
						["actionDisplayName"] = "Withdraw",
						["fromStateId"] = "submitted",
						["toStateId"] = "rejected",
					},
					PerformedById = "alice-1",
					PerformedAt = TickedNow,
					// The entry snapshots the state AS OF the event — the
					// resulting post-transition state, not the item's live state.
					StateId = "rejected",
				},
			]
		);
	}

	[Fact]
	public async Task Audit_ApplyAction_invalid_transition_does_not_append_an_entry()
	{
		// RA-410: this used to reach the failure via an outstanding task
		// (IncompleteTasks), which no longer gates anything. A genuinely
		// invalid transition — the action's FromStateId does not match the
		// item's current state — still fails and must still write no entry.
		var type = BuildType(
			transitions: [new CaseTransition("approve", "Approve", "submitted", "approved")]
		);
		var caseRecord = await SeedAsync(stateId: "rejected");

		var result = await BuildService(type)
			.ApplyActionAsync(
				caseRecord.Id,
				"approve",
				decision: null,
				AuditUser(),
				TestContext.Current.CancellationToken
			);

		result.Should().BeEquivalentTo(new { IsSuccess = false, FailureCode = CaseActionFailureCode.TerminalState });
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().BeEmpty();
	}

	[Fact]
	public async Task Audit_Assign_records_assignee_and_previous_assignee()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync(configure: w =>
		{
			w.AssignedToId = "bob-1";
			w.AssignedToName = "Bob Example";
		});

		var actor = UserWithRoles("alice-1", "assign");
		await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"carol-1",
				"Carol Example",
				actor,
				TestContext.Current.CancellationToken
			);

		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().BeEquivalentTo(
			[
				new
				{
					Action = "assigned",
					ActionDisplayName = "Assigned",
					Details = new Dictionary<string, string?>
					{
						["assigneeId"] = "carol-1",
						["assigneeName"] = "Carol Example",
						["previousAssigneeId"] = "bob-1",
						["previousAssigneeName"] = "Bob Example",
					},
					PerformedById = "alice-1",
				},
			]
		);
	}

	[Fact]
	public async Task Audit_Assign_idempotent_call_does_not_append_an_entry()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync(configure: w =>
		{
			w.AssignedToId = "alice-1";
			w.AssignedToName = "Alice";
		});

		var actor = UserWithRoles("actor-1", "assign");
		await BuildService(type)
			.AssignAsync(
				caseRecord.Id,
				"alice-1",
				"Alice",
				actor,
				TestContext.Current.CancellationToken
			);

		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().BeEmpty();
	}

	[Fact]
	public async Task Audit_Assign_validation_failure_does_not_append_an_entry()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var actor = UserWithRoles("alice-1", "standard");
		var result = await BuildService(type)
			.AssignAsync(caseRecord.Id, "   ", "Bob", actor, TestContext.Current.CancellationToken);

		result.IsSuccess.Should().BeFalse();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().BeEmpty();
	}

	[Fact]
	public async Task Audit_Unassign_records_previous_assignee()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync(configure: w =>
		{
			w.AssignedToId = "alice-1";
			w.AssignedToName = "Alice";
		});

		var actor = UserWithRoles("actor-1", "assign");
		await BuildService(type)
			.UnassignAsync(caseRecord.Id, actor, TestContext.Current.CancellationToken);

		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().BeEquivalentTo(
			[
				new
				{
					Action = "unassigned",
					ActionDisplayName = "Unassigned",
					Details = new Dictionary<string, string?>
					{
						["previousAssigneeId"] = "alice-1",
						["previousAssigneeName"] = "Alice",
					},
					PerformedById = "actor-1",
				},
			]
		);
	}

	[Fact]
	public async Task Audit_Unassign_already_unassigned_does_not_append_an_entry()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var actor = UserWithRoles("actor-1", "assign");
		await BuildService(type)
			.UnassignAsync(caseRecord.Id, actor, TestContext.Current.CancellationToken);

		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().BeEmpty();
	}

	[Fact]
	public async Task Audit_AddNote_records_note_id()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		await BuildService(type)
			.AddNoteAsync(
				caseRecord.Id,
				"  A note.  ",
				AuditUser(),
				TestContext.Current.CancellationToken
			);

		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().BeEquivalentTo(
			[
				new
				{
					Action = "note-added",
					ActionDisplayName = "Note added",
					// The entry holds the trimmed note body.
					Details = new Dictionary<string, string?> { ["text"] = "A note." },
					PerformedById = "alice-1",
					PerformedByName = "Alice Example",
					PerformedAt = TickedNow,
				},
			]
		);
	}

	[Fact]
	public async Task Audit_AddNote_validation_failure_does_not_append_an_entry()
	{
		var type = BuildType();
		var caseRecord = await SeedAsync();

		var result = await BuildService(type)
			.AddNoteAsync(caseRecord.Id, "   ", AuditUser(), TestContext.Current.CancellationToken);

		result.IsSuccess.Should().BeFalse();
		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().BeEmpty();
	}

	[Fact]
	public async Task Audit_log_is_chronological_across_a_sequence_of_actions()
	{
		// RA-410: the middle step used to be CompleteTaskAsync; the task
		// framework is gone, so AssignAsync stands in as the second of three
		// distinct engine mutations — the ordering behaviour under test does
		// not depend on which mutations they are.
		var type = BuildType(
			transitions: [new CaseTransition("approve", "Approve", "submitted", "approved")]
		);
		var caseRecord = await SeedAsync();

		var time = new MutableTimeProvider(TickedNow);
		var service = new CaseService(
			new CaseRegistry([type]),
			_persistence,
			NullLogger<CaseService>.Instance,
			time
		);

		await service.AddNoteAsync(
			caseRecord.Id,
			"first",
			AuditUser(),
			TestContext.Current.CancellationToken
		);
		time.Advance(TimeSpan.FromMinutes(1));
		await service.AssignAsync(
			caseRecord.Id,
			"alice-1",
			"Alice Example",
			AuditUser(),
			TestContext.Current.CancellationToken
		);
		time.Advance(TimeSpan.FromMinutes(1));
		await service.ApplyActionAsync(
			caseRecord.Id,
			"approve",
			decision: null,
			AuditUser(),
			TestContext.Current.CancellationToken
		);

		var fetched = await GetAsync(caseRecord.Id);
		fetched.Timeline.Should().HaveCount(3);

		using var scope = new AssertionScope();
		fetched.Timeline.Select(e => e.Action).Should().Equal("note-added", "assigned", "state-changed");
		// Strictly increasing timestamps — entries are appended in
		// chronological (insertion) order on disk.
		fetched.Timeline[0].PerformedAt.Should().BeBefore(fetched.Timeline[1].PerformedAt);
		fetched.Timeline[1].PerformedAt.Should().BeBefore(fetched.Timeline[2].PerformedAt);
		// Each entry snapshots the state AS OF its own event. The
		// note + assign happened while the item was still 'submitted'; only
		// the state-changed entry carries the post-transition 'approved'.
		// This is the regression the bug was about: earlier entries must NOT
		// inherit the live current state.
		fetched.StateId.Should().Be("approved");
		fetched.Timeline.Select(e => e.StateId).Should().Equal("submitted", "submitted", "approved");
	}

	private sealed class MutableTimeProvider(DateTime initial) : TimeProvider
	{
		private DateTime _now = initial;

		public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);

		public void Advance(TimeSpan by) => _now = _now.Add(by);
	}

	// ---------------------- SubmitAsync (RA-97 birth event) ----------------------

	[Fact]
	public async Task Submit_persists_case_with_initial_state_and_template_snapshot()
	{
		var type = BuildType();
		var intake = NewIntake() with
		{
			SourceSystemId = "csoc-2024-eng-00123",
			ProducerType = "COMPLIANCE_SCHEME",
			DisplayData = new Dictionary<string, CaseDisplayField>
			{
				["organisationName"] = new("Acme Packaging Ltd", "string"),
			},
		};

		var result = await BuildService(type)
			.SubmitAsync(
				type,
				intake,
				"test-client",
				AuditUser(),
				cancellationToken: TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(TestAsserts.NotNull(result.Case).Id);

		using var scope = new AssertionScope();
		fetched.Should().BeEquivalentTo(
			new
			{
				CaseType,
				StateId = "submitted",
				IsTerminal = false,
				SubmittedBy = "test-client",
				SubmittedAt = TickedNow,
				LastModifiedAt = TickedNow,
				TemplateVersion = "v1",
				TemplateSnapshot = new { TemplateVersion = "v1" },
				SourceSystemId = "csoc-2024-eng-00123",
				ProducerType = "COMPLIANCE_SCHEME",
			}
		);
		fetched.DisplayData.Should().Contain("organisationName", new CaseDisplayField("Acme Packaging Ltd", "string"));
	}

	[Fact]
	public async Task Submit_appends_single_case_submitted_audit_entry_in_same_create_call()
	{
		var type = BuildType();

		var result = await BuildService(type)
			.SubmitAsync(
				type,
				NewIntake(),
				"test-client",
				AuditUser(),
				cancellationToken: TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(TestAsserts.NotNull(result.Case).Id);

		using var scope = new AssertionScope();
		fetched.SubmittedAt.Should().Be(TickedNow);
		fetched.Should().BeEquivalentTo(
			new
			{
				// The audit entry must have been part of the original CreateAsync
				// write (not a follow-up replace), so Version is still 0.
				Version = 0,
				Timeline = new[]
				{
					new
					{
						Action = "case-created",
						ActionDisplayName = "Case submitted",
						PerformedById = "service:test-client",
						PerformedByName = "test-client",
						PerformedAt = TickedNow,
						Details = new Dictionary<string, string?>
						{
							["caseType"] = CaseType,
							["stateId"] = "submitted",
							["templateVersion"] = "v1",
							["source"] = null,
							["clientId"] = "test-client",
						},
						// The birth entry snapshots the initial state.
						StateId = "submitted",
					},
				},
			}
		);
	}

	[Fact]
	public async Task Submit_without_a_user_id_claim_attributes_the_case_to_the_calling_service()
	{
		var type = BuildType();

		var result = await BuildService(type)
			.SubmitAsync(
				type,
				NewIntake(),
				"test-client",
				UserWithoutActorId(),
				cancellationToken: TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue(result.Message);
		var fetched = await GetAsync(TestAsserts.NotNull(result.Case).Id);
		var entry = fetched.Timeline.Should().ContainSingle().Subject;
		entry.PerformedById.Should().Be("service:test-client");
	}


	[Fact]
	public async Task Submit_records_submission_metadata_on_birth_audit_entry()
	{
		// RA-126: source / clientId / userId are appended to the birth
		// entry's Details alongside the existing caseType / stateId /
		// templateVersion keys. PerformedAt must be the server-side receipt
		// time from the injected TimeProvider, not a client-supplied value.
		var type = BuildType();
		var metadata = new Dictionary<string, string?>
		{
			["source"] = "operator-fe",
		};

		var result = await BuildService(type)
			.SubmitAsync(
				type,
				NewIntake(),
				"test-client",
				AuditUser(),
				submissionMetadata: metadata,
				cancellationToken: TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(TestAsserts.NotNull(result.Case).Id);
		fetched.Timeline.Should().BeEquivalentTo(
			[
				new
				{
					Action = "case-created",
					PerformedAt = TickedNow,
					Details = new Dictionary<string, string?>
					{
						["caseType"] = CaseType,
						["stateId"] = "submitted",
						["templateVersion"] = "v1",
						["source"] = "operator-fe",
						["clientId"] = "test-client",
					},
				},
			]
		);
	}

	[Fact]
	public async Task Submit_records_null_metadata_keys_when_no_metadata_supplied()
	{
		// RA-126: when the caller passes no metadata the keys still appear
		// on Details. clientId / userId resolve from claims; source is null.
		var type = BuildType();

		var result = await BuildService(type)
			.SubmitAsync(
				type,
				NewIntake(),
				"test-client",
				AuditUser(),
				submissionMetadata: null,
				cancellationToken: TestContext.Current.CancellationToken
			);

		result.IsSuccess.Should().BeTrue();
		var fetched = await GetAsync(TestAsserts.NotNull(result.Case).Id);
		var entry = fetched.Timeline.Should().ContainSingle().Subject;
		entry.Details.Should().Contain(
			new Dictionary<string, string?>
			{
				["source"] = null,
				["clientId"] = "test-client",
			}
		);
	}

	/// <summary>
	/// Produces a service whose persistence wraps the real one so that a
	/// competing writer bumps the on-disk version between the engine's
	/// load and replace, triggering the optimistic-concurrency exception
	/// for real (no mocked throws).
	/// </summary>
	private CaseService BuildRacingService(CaseModuleConfig type, string id)
	{
		var racing = new RacingPersistence(_persistence, () => AddCompetingNote(id));
		return new CaseService(
			new CaseRegistry([type]),
			racing,
			NullLogger<CaseService>.Instance,
			_time
		);
	}

	private void AddCompetingNote(string id)
	{
		var raceLoaded = TestAsserts.NotNull(_persistence.GetByIdAsync(id).GetAwaiter().GetResult());
		raceLoaded.LastModifiedAt = raceLoaded.LastModifiedAt.AddMinutes(1);
		raceLoaded.Timeline.Add(new CaseTimelineEntry
		{
			Action = "note-added",
			ActionDisplayName = "Note added",
			PerformedAt = raceLoaded.LastModifiedAt,
			PerformedById = "competing-user",
		});
		_persistence.ReplaceAsync(raceLoaded).GetAwaiter().GetResult();
	}

	private sealed class RacingPersistence(ICasePersistence inner, Action onBeforeReplace)
		: ICasePersistence
	{
		public Task CreateAsync(Case caseRecord, CancellationToken cancellationToken = default) =>
			inner.CreateAsync(caseRecord, cancellationToken);

		public Task<Case?> GetByIdAsync(
			string id,
			CancellationToken cancellationToken = default
		) => inner.GetByIdAsync(id, cancellationToken);

		public Task<Case?> FindBySourceSystemIdAsync(
			string sourceSystemId,
			CancellationToken cancellationToken = default
		) => inner.FindBySourceSystemIdAsync(sourceSystemId, cancellationToken);

		public Task<CasePage> QueryAsync(
			CaseQuery query,
			CancellationToken cancellationToken = default
		) => inner.QueryAsync(query, cancellationToken);

		public Task ReplaceAsync(
			Case caseRecord,
			CaseCompletionEnvelopeContent? completionContent = null,
			CancellationToken cancellationToken = default
		)
		{
			onBeforeReplace();
			return inner.ReplaceAsync(caseRecord, completionContent, cancellationToken);
		}
	}
}
