#pragma warning disable BL0005 // Logic tests exercise component save-state transitions directly; production parameters are renderer-owned.
using System.Reflection;
using Microsoft.AspNetCore.Components;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;
using SwiftAid.UI.Components.Public.GuidedReportStepper;

namespace SwiftAid.Ui.Tests;

public sealed partial class GuidedReportDraftPersistenceTests
{
    [Fact]
    public void ReviewSummaryOmitsPlaceNamesMarkedUnknown()
    {
        var draft = CreateDraft();
        draft.RegionPresence = PresenceState.Known;
        draft.Region = "Synthetic region";
        draft.AreaPresence = PresenceState.Unknown;
        draft.Area = "Old area";
        draft.LocalityPresence = PresenceState.Unknown;
        draft.Locality = "Old locality";
        var component = CreateStepper(draft, _ => Task.CompletedTask);
        var property = typeof(GuidedReportStepper).GetProperty("LocationSummary", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Equal("Synthetic region", property.GetValue(component));
        draft.RegionPresence = PresenceState.Unknown;
        draft.CountryCode = "SZ";
        Assert.Equal("Unknown", property.GetValue(component));
    }

    [Fact]
    public void ReviewCountryMatchesTheAcceptedTrimmedCountryCode()
    {
        var draft = CreateDraft();
        draft.CountryCode = " SZ ";
        var component = CreateStepper(draft, _ => Task.CompletedTask);
        component.Countries = [new("SZ", "Eswatini")];
        component.CountryCatalogueVersion = "test-v1";
        component.CountryCatalogueIsCurrent = true;
        var property = typeof(GuidedReportStepper).GetProperty("CountrySummary", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Equal("Eswatini", property.GetValue(component));
    }

    [Fact]
    public async Task ConfiguredTextLimitsIdentifyInvalidFieldsInOrderAndPreserveEntries()
    {
        var draft = CreateDraft();
        draft.Narrative = new string('n', 101);
        draft.ObservationsText = new string('o', 251);
        var component = CreateStepper(draft, _ => Task.CompletedTask);
        component.TextLimits = new(100, 2, 250);

        await (Task)Invoke(component, "ContinueFromTypeAsync")!;
        await (Task)Invoke(component, "ContinueToReviewAsync")!;

        Assert.Equal(new[] { "report.original-statement-too-long", "report.observation-too-long" },
            component.GetState().ValidationIssues.Select(issue => issue.InputId));
        Assert.Equal(101, draft.Narrative.Length);
        Assert.Equal(251, draft.ObservationsText.Length);
        draft.Narrative = new string('n', 100);
        Assert.Equal("report.observation-too-long", Assert.Single(component.GetState().ValidationIssues).InputId);
        draft.ObservationsText = new string('o', 250);
        Assert.Empty(component.GetState().ValidationIssues);
        await (Task)Invoke(component, "ContinueToReviewAsync")!;
        Assert.True(component.GetState().Controls.Single(control => control.ControlId == "report.narrative").CompletionAccepted);
    }

    [Fact]
    public async Task ObservationValidationExplainsEachReasonInOrderWithoutExposingValues()
    {
        var draft = CreateDraft();
        draft.ObservationsText = string.Join("\n", new string('x', 251), "second", "third") + new string(' ', 20000);
        var component = CreateStepper(draft, _ => Task.CompletedTask);
        component.TextLimits = new(6000, 2, 250);
        await (Task)Invoke(component, "ContinueFromTypeAsync")!;
        await (Task)Invoke(component, "ContinueToReviewAsync")!;

        var issues = component.GetState().ValidationIssues;
        Assert.Equal(new[] { "report.too-many-observations", "report.observation-too-long", "report.observations-total-too-long" },
            issues.Select(issue => issue.InputId));
        Assert.All(issues, issue => Assert.Equal("ellai-report-other-observations", issue.Anchor));
        Assert.DoesNotContain("second", System.Text.Json.JsonSerializer.Serialize(issues));

        draft.ObservationsText = new string('x', 251) + "\nsecond" + new string(' ', 20000);
        Assert.Equal("report.observation-too-long", component.GetState().ValidationIssues.First().InputId);
        draft.ObservationsText = new string('x', 250) + "\nsecond" + new string(' ', 20000);
        Assert.Equal("report.observations-total-too-long", Assert.Single(component.GetState().ValidationIssues).InputId);
        draft.ObservationsText = new string('x', 250) + "\nsecond";
        Assert.Empty(component.GetState().ValidationIssues);
    }

    [Fact]
    public void StricterLimitsOnReviewBlockSubmissionWithoutDiscardingText()
    {
        var draft = CreateDraft();
        draft.ObservationsText = new string('o', 301);
        var component = CreateStepper(draft, _ => Task.CompletedTask);
        component.TextLimits = new(6000, 20, 500);
        SetField(component, "_step", 2);
        Assert.DoesNotContain(component.GetState().ValidationIssues, issue => issue.InputId == "report.text-limits-changed");

        component.TextLimits = new(6000, 20, 300);

        Assert.Contains(component.GetState().ValidationIssues, issue => issue.InputId == "report.text-limits-changed");
        Assert.False((bool)typeof(GuidedReportStepper).GetProperty("CanSubmit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!);
        Assert.Equal(301, draft.ObservationsText.Length);
    }

    [Fact]
    public void LegacySnapshotRestoresItsDateAndMigratesToCurrentVersion()
    {
        var draft = CreateDraft();
        draft.OccurrencePrecision = ReportedTimePrecision.DateOnly;
        draft.OccurrenceDate = new DateTime(2026, 10, 1);
        var current = draft.ToFormSnapshot();
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(current.Json)!.AsObject();
        Assert.True(legacyJson.Remove(nameof(PublicReportDraft.OccurrenceDateText)));

        var restored = PublicReportDraft.FromFormSnapshot(new GuestDraftFormSnapshot(1, legacyJson.ToJsonString()));

        Assert.Equal(new DateTime(2026, 10, 1), restored.OccurrenceDate);
        Assert.Equal("2026-10-01", restored.OccurrenceDateText);
        Assert.Equal(GuestDraftFormSnapshot.CurrentSchemaVersion, restored.ToFormSnapshot().SchemaVersion);
    }

    [Fact]
    public void InconsistentRawAndParsedDatesFailClosedWithoutDiscardingTheSnapshot()
    {
        var draft = CreateDraft();
        draft.OccurrenceDate = new DateTime(2026, 10, 1);
        draft.OccurrenceDateText = "2026-10-01";
        var snapshot = draft.ToFormSnapshot();
        var json = System.Text.Json.Nodes.JsonNode.Parse(snapshot.Json)!.AsObject();
        json[nameof(PublicReportDraft.OccurrenceDateText)] = "2026-1";
        var inconsistent = new GuestDraftFormSnapshot(2, json.ToJsonString());

        Assert.Throws<InvalidOperationException>(() => PublicReportDraft.FromFormSnapshot(inconsistent));
        Assert.Contains("2026-1", inconsistent.Json);
        draft.OccurrenceDateText = "2026-1";
        Assert.Throws<InvalidOperationException>(() => draft.ToFormSnapshot());
        Assert.Equal(new DateTime(2026, 10, 1), draft.OccurrenceDate);
    }

    [Fact]
    public async Task PartialDateAndTimeSurviveSnapshotAndNeverBecomeAValidDate()
    {
        var component = CreateStepper(CreateDraft(), _ => Task.CompletedTask);
        component.Draft.OccurrencePrecision = ReportedTimePrecision.Exact;
        component.Draft.OccurrenceDate = new DateTime(2026, 10, 1);
        var method = typeof(GuidedReportStepper).GetMethod("OccurrenceDateChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(component, ["2026-1"])!;
        component.Draft.OccurrenceTime = "08:";

        Assert.Null(component.Draft.OccurrenceDate);
        var restored = PublicReportDraft.FromFormSnapshot(component.Draft.ToFormSnapshot());
        Assert.Equal("2026-1", restored.OccurrenceDateText);
        Assert.Equal("08:", restored.OccurrenceTime);
        Assert.Null(restored.OccurrenceDate);
        Assert.Null(PublicReportDraft.ParseOccurrenceDate("2026-02-30"));
        Assert.Equal(new DateTime(2026, 10, 1), PublicReportDraft.ParseOccurrenceDate("2026-10-01"));
    }

    [Fact]
    public async Task ValidDraftSaveRetainsOriginalWhitespaceAndHiddenInput()
    {
        var draft = CreateDraft("  Synthetic original entry  ");
        draft.ObservationsText = "first observation\n\nsecond observation";
        draft.OccurrencePrecision = ReportedTimePrecision.Unknown;
        draft.OccurrenceOffset = "+02:";
        PublicReportSaveRequest? saved = null;
        var component = CreateStepper(draft, request =>
        {
            saved = request;
            request.ConfirmPersisted(Guid.NewGuid(), formSnapshotPersisted: request.FormSnapshot is not null);
            return Task.CompletedTask;
        });
        component.EditableDraftSnapshotsSupported = true;

        await InvokeSaveDraftAsync(component);

        Assert.Equal("Saved", GetSaveState(component));
        Assert.NotNull(saved?.FormSnapshot);
        var restored = PublicReportDraft.FromFormSnapshot(saved.FormSnapshot);
        Assert.Equal("  Synthetic original entry  ", restored.Narrative);
        Assert.Equal("first observation\n\nsecond observation", restored.ObservationsText);
        Assert.Equal("+02:", restored.OccurrenceOffset);
        Assert.Empty(component.GetState().ValidationIssues);
    }

    [Fact]
    public async Task LosslessValidDraftDoesNotRequireAnEditableSnapshot()
    {
        var draft = CreateDraft();
        PublicReportSaveRequest? saved = null;
        var component = CreateStepper(draft, request =>
        {
            saved = request;
            request.ConfirmPersisted(Guid.NewGuid());
            return Task.CompletedTask;
        });
        component.EditableDraftSnapshotsSupported = true;

        await InvokeSaveDraftAsync(component);

        Assert.Equal("Saved", GetSaveState(component));
        Assert.NotNull(saved);
        Assert.Null(saved.FormSnapshot);
    }

    [Fact]
    public void RestoredSnapshotKeepsEvidenceButRequiresCurrentReviewAndConfirmations()
    {
        var draft = CreateDraft("Synthetic original entry");
        draft.CountryCode = "SZ";
        draft.CountryConfirmed = true;
        draft.ReportReviewConfirmed = true;
        draft.ProcessingConfirmed = true;
        draft.OccurrenceOffset = "+02:";

        var restored = PublicReportDraft.FromFormSnapshot(draft.ToFormSnapshot());

        Assert.Equal("Synthetic original entry", restored.Narrative);
        Assert.Equal("SZ", restored.CountryCode);
        Assert.Equal("+02:", restored.OccurrenceOffset);
        Assert.False(restored.CountryConfirmed);
        Assert.False(restored.ReportReviewConfirmed);
        Assert.False(restored.ProcessingConfirmed);
    }

    [Fact]
    public async Task SupportedEditableDraftSavesAndRestoresIncompleteTimeWithoutQueuableCarrier()
    {
        var draft = CreateDraft("Synthetic private entry");
        draft.OccurrencePrecision = ReportedTimePrecision.Exact;
        draft.OccurrenceDate = null;
        draft.OccurrenceTime = "unfinished";
        draft.OccurrenceOffset = "+02:";
        PublicReportSaveRequest? saved = null;
        var component = CreateStepper(draft, request =>
        {
            saved = request;
            request.ConfirmPersisted(Guid.NewGuid(), formSnapshotPersisted: true);
            return Task.CompletedTask;
        });
        component.EditableDraftSnapshotsSupported = true;
        await InvokeSaveDraftAsync(component);

        Assert.Equal("Saved", GetSaveState(component));
        Assert.NotNull(saved?.FormSnapshot);
        Assert.Null(saved.Submission.Narrative);
        Assert.False(saved.Submission.Confirmations.ReportReviewConfirmed);
        var restored = PublicReportDraft.FromFormSnapshot(saved.FormSnapshot);
        Assert.Equal("Synthetic private entry", restored.Narrative);
        Assert.Equal("unfinished", restored.OccurrenceTime);
        Assert.Equal("+02:", restored.OccurrenceOffset);
        Assert.Null(restored.OccurrenceDate);
        Assert.Contains(component.GetState().ValidationIssues, issue => issue.InputId == "report.occurrence-time");
    }

    [Fact]
    public async Task SnapshotSaveRequiresExplicitSnapshotPersistenceConfirmation()
    {
        var draft = CreateDraft("Synthetic report");
        draft.OccurrencePrecision = ReportedTimePrecision.Exact;
        draft.OccurrenceTime = "unfinished";
        var component = CreateStepper(draft, request =>
        {
            request.ConfirmPersisted(Guid.NewGuid());
            return Task.CompletedTask;
        });
        component.EditableDraftSnapshotsSupported = true;
        await InvokeSaveDraftAsync(component);
        Assert.Equal("Failed", GetSaveState(component));
        Assert.False(component.GetState().Controls.Single(control => control.ControlId == "report.save").CompletionAccepted);
        Assert.Equal("unfinished", draft.OccurrenceTime);
    }

    [Fact]
    public void IncompleteOrUnknownSnapshotFieldsFailClosed()
    {
        var snapshot = new GuestDraftFormSnapshot(1, "{\"Narrative\":\"synthetic\"}");
        Assert.Throws<InvalidOperationException>(() => PublicReportDraft.FromFormSnapshot(snapshot));
    }

    [Fact]
    public async Task InvalidDraftSaveIdentifiesOnlyBlockingTimeFieldsAndKeepsIncompleteReport()
    {
        var draft = CreateDraft(string.Empty);
        draft.OccurrencePrecision = ReportedTimePrecision.Exact;
        draft.OccurrenceDate = null;
        draft.OccurrenceTime = "not a time";
        draft.OccurrenceOffset = "not an offset";
        var saves = 0;
        var component = CreateStepper(draft, request =>
        {
            saves++;
            request.ConfirmPersisted(Guid.NewGuid());
            return Task.CompletedTask;
        });

        await InvokeSaveDraftAsync(component);

        Assert.Equal(0, saves);
        Assert.Equal("Invalid", GetSaveState(component));
        Assert.True(component.GetState().HasValidationErrors);
        Assert.Equal(1, GetField<int>(component, "_step"));
        Assert.Equal(new[] { "report.occurrence-date", "report.occurrence-time", "report.utc-offset" },
            component.GetState().ValidationIssues.Select(issue => issue.InputId));
        Assert.Equal("not a time", draft.OccurrenceTime);
        draft.OccurrenceDate = new DateTime(2026, 11, 4);
        Assert.Equal("report.occurrence-time", component.GetState().ValidationIssues.First().InputId);
        draft.OccurrenceTime = "08:15";
        Assert.Equal("report.utc-offset", Assert.Single(component.GetState().ValidationIssues).InputId);
        draft.OccurrenceOffset = "+02:00";
        Assert.Empty(component.GetState().ValidationIssues);
        await InvokeSaveDraftAsync(component);
        Assert.Equal(1, saves);
        Assert.Equal("Saved", GetSaveState(component));
    }

    [Fact]
    public async Task UnrepresentableExactDateIsIdentifiedBeforeReviewOrSave()
    {
        var draft = CreateDraft();
        draft.OccurrencePrecision = ReportedTimePrecision.Exact;
        draft.OccurrenceDate = DateTime.MinValue;
        draft.OccurrenceTime = "00:00";
        draft.OccurrenceOffset = "+14:00";
        var component = CreateStepper(draft, _ => Task.CompletedTask);
        await InvokeSaveDraftAsync(component);
        Assert.Equal("report.invalid-exact-occurrence", Assert.Single(component.GetState().ValidationIssues).InputId);
        await (Task)Invoke(component, "ContinueToReviewAsync")!;
        Assert.Equal("report.invalid-exact-occurrence", Assert.Single(component.GetState().ValidationIssues).InputId);
        Assert.False(component.GetState().Controls.Single(control => control.ControlId == "report.narrative").CompletionAccepted);
    }

    [Fact]
    public async Task DraftSavePreservesCountryWhenCatalogueIsUnavailableWithoutAuthorizingSubmission()
    {
        var draft = CreateDraft();
        draft.CountryCode = "SZ";
        draft.CountryConfirmed = true;
        IntakeSubmission? saved = null;
        var component = CreateStepper(draft, request =>
        {
            saved = request.Submission;
            request.ConfirmPersisted(Guid.NewGuid());
            return Task.CompletedTask;
        });
        component.CountryCatalogueIsCurrent = false;
        component.Countries = [];
        await InvokeSaveDraftAsync(component);

        Assert.Equal("Saved", GetSaveState(component));
        Assert.NotNull(saved);
        Assert.Equal("SZ", saved.Location.CountryCode);
        Assert.False(saved.Confirmations.OccurrenceCountryConfirmed);
        Assert.Null(saved.Location.CatalogueVersion);
        Assert.Equal("SZ", draft.CountryCode);
        Assert.False((bool)typeof(GuidedReportStepper).GetProperty("CanSubmit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!);
    }

    [Fact]
    public async Task OversizedObservationTextBlocksReviewAndIdentifiesItsField()
    {
        var draft = CreateDraft();
        draft.ObservationsText = string.Join("\n", Enumerable.Repeat(new string('x', 1000), 20));
        var component = CreateStepper(draft, _ => Task.CompletedTask);

        await (Task)Invoke(component, "ContinueFromTypeAsync")!;
        await (Task)Invoke(component, "ContinueToReviewAsync")!;

        var issues = component.GetState().ValidationIssues;
        Assert.Equal(new[] { "report.observation-too-long", "report.observations-total-too-long" }, issues.Select(issue => issue.InputId));
        Assert.All(issues, issue => Assert.Equal("ellai-report-other-observations", issue.Anchor));
        Assert.False(component.GetState().Controls.Single(control => control.ControlId == "report.narrative").CompletionAccepted);
        draft.ObservationsText = new string(' ', 20000);
        Assert.Empty(component.GetState().ValidationIssues);
        await (Task)Invoke(component, "ContinueToReviewAsync")!;
        Assert.True(component.GetState().Controls.Single(control => control.ControlId == "report.narrative").CompletionAccepted);
    }

    [Fact]
    public async Task OversizedPlaceNamesBlockReviewAndAreIdentifiedInOrder()
    {
        var draft = CreateDraft();
        draft.RegionPresence = draft.AreaPresence = draft.LocalityPresence = PresenceState.Known;
        draft.Region = draft.Area = draft.Locality = new string('x', 201);
        var component = CreateStepper(draft, _ => Task.CompletedTask);

        await (Task)Invoke(component, "ContinueFromTypeAsync")!;
        await (Task)Invoke(component, "ContinueToReviewAsync")!;
        Assert.Equal(new[] { "report.region", "report.area", "report.locality" },
            component.GetState().ValidationIssues.Select(issue => issue.InputId));
        Assert.True(GetField<bool>(component, "_showManualPlaces"));
        Assert.Contains(draft.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(draft)),
            error => error.MemberNames.Contains(nameof(PublicReportDraft.Region)));

        draft.Region = new string('x', 200);
        Assert.Equal("report.area", component.GetState().ValidationIssues.First().InputId);
        draft.Area = "Synthetic area";
        Assert.Equal("report.locality", component.GetState().ValidationIssues.First().InputId);
        draft.Locality = "Synthetic locality";
        Assert.Empty(component.GetState().ValidationIssues);
        await (Task)Invoke(component, "ContinueToReviewAsync")!;
        Assert.True(component.GetState().Controls.Single(control => control.ControlId == "report.narrative").CompletionAccepted);
    }

    [Theory]
    [InlineData("+-1:00")]
    [InlineData("+ 2:00")]
    [InlineData("+02:-1")]
    [InlineData("+14:01")]
    public async Task InvalidExactOffsetBlocksReviewAndIdentifiesTheOffset(string offset)
    {
        var draft = CreateDraft();
        draft.OccurrencePrecision = ReportedTimePrecision.Exact;
        draft.OccurrenceDate = new DateTime(2026, 10, 31);
        draft.OccurrenceTime = "08:15";
        draft.OccurrenceOffset = offset;
        var component = CreateStepper(draft, _ => Task.CompletedTask);

        await (Task)Invoke(component, "ContinueFromTypeAsync")!;
        await (Task)Invoke(component, "ContinueToReviewAsync")!;

        Assert.Contains(component.GetState().ValidationIssues, issue => issue.InputId == "report.utc-offset");
        Assert.False(component.GetState().Controls.Single(control => control.ControlId == "report.narrative").CompletionAccepted);
        Assert.Contains(draft.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(draft)),
            error => error.MemberNames.Contains(nameof(PublicReportDraft.OccurrenceOffset)));
    }

    [Theory]
    [InlineData(nameof(PublicReportDraft.CountryConfirmed), true)]
    [InlineData(nameof(PublicReportDraft.ProcessingConfirmed), true)]
    [InlineData(nameof(PublicReportDraft.ReportReviewConfirmed), true)]
    [InlineData(nameof(PublicReportDraft.Narrative), false)]
    [InlineData(nameof(PublicReportDraft.CountryCode), false)]
    public void ConfirmationsPreserveReviewButReportEditsRequireAnotherReview(string field, bool remainsReviewed)
    {
        var component = CreateStepper(CreateDraft(), _ => Task.CompletedTask);
        component.Draft.ReportReviewConfirmed = true;
        typeof(GuidedReportStepper).GetMethod("HandleFieldChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(component, [null, new Microsoft.AspNetCore.Components.Forms.FieldChangedEventArgs(
                new Microsoft.AspNetCore.Components.Forms.FieldIdentifier(component.Draft, field))]);
        Assert.Equal(remainsReviewed, component.Draft.ReportReviewConfirmed);
    }

    [Theory]
    [InlineData("country", "report.country-catalogue-unavailable", "ellai-report-country-confirmed")]
    [InlineData("privacy", "report.privacy-notice-unavailable", "ellai-report-privacy-unavailable")]
    [InlineData("policy", "report.processing-policy-unavailable", "ellai-report-policy-unavailable")]
    public void UnavailableSubmissionRequirementsHaveSpecificGuidance(string unavailable, string inputId, string anchor)
    {
        var draft = CreateDraft();
        draft.CountryCode = "SZ";
        draft.CountryConfirmed = draft.ReportReviewConfirmed = draft.ProcessingConfirmed = true;
        var component = CreateStepper(draft, _ => Task.CompletedTask);
        component.Countries = [new("SZ", "Eswatini")];
        component.CountryCatalogueVersion = "test-v1";
        component.CountryCatalogueIsCurrent = unavailable != "country";
        component.PrivacyNoticeReference = "/privacy";
        component.PrivacyNoticeIsCurrent = unavailable != "privacy";
        component.ProcessingPolicyReference = "/policy";
        component.ProcessingPolicyVersion = unavailable == "policy" ? null : "test-v1";
        component.OnSubmit = EventCallback.Factory.Create<IntakeSubmission>(this, _ => { });
        SetField(component, "_step", 2);

        var issue = Assert.Single(component.GetState().ValidationIssues);
        Assert.Equal(inputId, issue.InputId);
        Assert.Equal(anchor, issue.Anchor);
        Assert.Contains("unavailable", issue.Message);
        Assert.Contains("cannot be sent", issue.Message);
        Assert.False((bool)typeof(GuidedReportStepper).GetProperty("CanSubmit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!);
        Assert.False(component.GetState().Controls.Single(control => control.ControlId == "report.submit").CompletionAccepted);
        Assert.NotNull(draft.Narrative);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(3, true, false)]
    [InlineData(7, true, true)]
    [InlineData(7, false, false)]
    public void ConfirmationProgressRequiresAllRealFormAcknowledgementsAndCurrentPolicy(int confirmations, bool currentPolicy, bool accepted)
    {
        var component = CreateStepper(CreateDraft(), _ => Task.CompletedTask);
        component.Countries = [new("SZ", "Eswatini")];
        component.CountryCatalogueVersion = "test-v1";
        component.CountryCatalogueIsCurrent = true;
        component.PrivacyNoticeReference = "/privacy";
        component.PrivacyNoticeIsCurrent = true;
        component.ProcessingPolicyReference = "/policy";
        component.ProcessingPolicyVersion = currentPolicy ? "test-v1" : null;
        var submitted = false;
        component.OnSubmit = EventCallback.Factory.Create<IntakeSubmission>(this, _ => submitted = true);
        component.Draft.CountryCode = "SZ";
        component.Draft.CountryConfirmed = (confirmations & 1) != 0;
        component.Draft.ReportReviewConfirmed = (confirmations & 2) != 0;
        component.Draft.ProcessingConfirmed = (confirmations & 4) != 0;
        SetField(component, "_step", 2);
        Assert.Equal(accepted, component.GetState().Controls.Single(control => control.ControlId == "report.confirmations").CompletionAccepted);
        Assert.False(component.GetState().Controls.Single(control => control.ControlId == "report.submit").CompletionAccepted);
        Assert.False(submitted);
    }

    [Fact]
    public async Task ANewDraftDoesNotInheritThePreviousDraftsValidationAttempt()
    {
        var component = CreateStepper(CreateDraft(string.Empty), _ => Task.CompletedTask);
        await (Task)Invoke(component, "ContinueFromTypeAsync")!;
        await (Task)Invoke(component, "ContinueToReviewAsync")!;
        Assert.Contains(component.GetState().ValidationIssues, issue => issue.InputId == "report.original-statement");

        component.Draft = CreateDraft(string.Empty);
        component.CurrentDraftId = Guid.NewGuid();
        InvokeOnParametersSet(component);
        await (Task)Invoke(component, "ContinueFromTypeAsync")!;
        Assert.Empty(component.GetState().ValidationIssues);
    }

    [Fact]
    public async Task SemanticCompletionComesFromAcceptedValidationAndNeverIncludesEnteredValues()
    {
        var component = CreateStepper(CreateDraft(string.Empty), _ => Task.CompletedTask);
        Assert.DoesNotContain(component.GetState().Controls, control => control.CompletionAccepted);
        await (Task)Invoke(component, "ContinueFromTypeAsync")!;
        Assert.True(component.GetState().Controls.Single(control => control.ControlId == "report.subject").CompletionAccepted);
        await (Task)Invoke(component, "ContinueToReviewAsync")!;
        Assert.False(component.GetState().Controls.Single(control => control.ControlId == "report.narrative").CompletionAccepted);
        component.Draft.Narrative = "Synthetic private observation for validation only.";
        await (Task)Invoke(component, "ContinueToReviewAsync")!;
        var state = component.GetState();
        Assert.True(state.Controls.Single(control => control.ControlId == "report.narrative").CompletionAccepted);
        Assert.True(state.Controls.Single(control => control.ControlId == "report.time-location").CompletionAccepted);
        Assert.False(state.Controls.Single(control => control.ControlId == "report.submit").CompletionAccepted);
        Assert.DoesNotContain("Synthetic private", System.Text.Json.JsonSerializer.Serialize(state));
        component.Draft = CreateDraft("A different draft.");
        component.CurrentDraftId = Guid.NewGuid();
        InvokeOnParametersSet(component);
        Assert.DoesNotContain(component.GetState().Controls, control => control.CompletionAccepted);
    }

    [Fact]
    public async Task UnavailableStorageDisablesSaveAndDoesNotReportSuccess()
    {
        var saveCalls = 0;
        var component = CreateStepper(
            CreateDraft(),
            request =>
            {
                saveCalls++;
                return Task.CompletedTask;
            },
            draftSavingAvailable: false);

        Assert.False(GetCanSaveDraft(component));

        await InvokeSaveDraftAsync(component);

        Assert.Equal(0, saveCalls);
        Assert.Equal("Unsaved", GetSaveState(component));
        Assert.Contains("report details", component.Draft.Narrative, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedStorageCallbackDoesNotReportSavedOrClearEntries()
    {
        var draft = CreateDraft();
        var component = CreateStepper(
            draft,
            _ => Task.FromException(new IOException("storage unavailable")));

        await InvokeSaveDraftAsync(component);

        Assert.Equal("Failed", GetSaveState(component));
        Assert.Same(draft, component.Draft);
        Assert.Equal("Observed concern; report details remain in the form.", component.Draft.Narrative);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulSavePreservesCurrentStepWhenDraftIsRehydrated(bool assignNewIdentity)
    {
        var draftId = assignNewIdentity ? (Guid?)null : Guid.NewGuid();
        var component = CreateStepper(
            CreateDraft(),
            _ => Task.CompletedTask,
            currentDraftId: draftId);
        SetField(component, "_step", 2);
        var savedId = draftId ?? Guid.NewGuid();
        IntakeSubmission? savedSubmission = null;
        component.OnSaveDraft = EventCallback.Factory.Create<PublicReportSaveRequest>(
            new object(),
            request =>
            {
                savedSubmission = request.Submission;
                request.ConfirmPersisted(savedId);
                return Task.CompletedTask;
            });

        await InvokeSaveDraftAsync(component);

        Assert.NotNull(savedSubmission);
        component.CurrentDraftId = savedId;
        component.Draft = PublicReportDraft.FromSubmission(savedSubmission);
        InvokeOnParametersSet(component);

        Assert.Equal(2, GetField<int>(component, "_step"));
        Assert.Equal("Saved", GetSaveState(component));
    }

    [Fact]
    public async Task LoadingADifferentDraftAfterSaveRefreshResetsTheWizardToItsFirstStep()
    {
        IntakeSubmission? savedSubmission = null;
        var component = CreateStepper(
            CreateDraft(),
            request =>
            {
                savedSubmission = request.Submission;
                request.ConfirmPersisted(request.PreviousDraftId!.Value);
                return Task.CompletedTask;
            },
            currentDraftId: Guid.NewGuid());
        SetField(component, "_step", 2);
        await InvokeSaveDraftAsync(component);
        Assert.NotNull(savedSubmission);
        var savedId = component.CurrentDraftId;
        component.CurrentDraftId = savedId;
        component.Draft = PublicReportDraft.FromSubmission(savedSubmission);
        InvokeOnParametersSet(component);
        Assert.Equal(2, GetField<int>(component, "_step"));

        component.CurrentDraftId = Guid.NewGuid();
        component.Draft = CreateDraft("A different report.");

        InvokeOnParametersSet(component);

        Assert.Equal(0, GetField<int>(component, "_step"));
    }

    private static GuidedReportStepper CreateStepper(
        PublicReportDraft draft,
        Func<PublicReportSaveRequest, Task> save,
        bool draftSavingAvailable = true,
        Guid? currentDraftId = null)
    {
        var component = new GuidedReportStepper
        {
            Draft = draft,
            DraftSavingAvailable = draftSavingAvailable,
            CurrentDraftId = currentDraftId,
            OnSaveDraft = EventCallback.Factory.Create<PublicReportSaveRequest>(new object(), save)
        };

        InvokeOnParametersSet(component);
        return component;
    }

    private static PublicReportDraft CreateDraft(string narrative = "Observed concern; report details remain in the form.") => new()
    {
        SubjectScope = IntakeSubjectScope.Human,
        Narrative = narrative,
        LocationUnknown = true
    };

    private static Task InvokeSaveDraftAsync(GuidedReportStepper component) =>
        (Task)Invoke(component, "SaveDraftAsync")!;

    private static void InvokeOnParametersSet(GuidedReportStepper component) =>
        _ = Invoke(component, "OnParametersSet");

    private static object? Invoke(GuidedReportStepper component, string name) =>
        component.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(component, null);

    private static bool GetCanSaveDraft(GuidedReportStepper component) =>
        (bool)component.GetType().GetProperty("CanSaveDraft", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component)!;

    private static string GetSaveState(GuidedReportStepper component) =>
        GetField<object>(component, "_saveState").ToString()!;

    private static T GetField<T>(GuidedReportStepper component, string name) =>
        (T)component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component)!;

    private static void SetField<T>(GuidedReportStepper component, string name, T value) =>
        component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, value);
}
#pragma warning restore BL0005
