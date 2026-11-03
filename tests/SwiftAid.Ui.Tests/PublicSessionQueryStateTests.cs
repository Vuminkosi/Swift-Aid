using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using SwiftAid.Contracts.Sessions;
using SwiftAid.UI.Components.Surfaces.Public;
using SwiftAid.UI.Components.Sessions;

namespace SwiftAid.Ui.Tests;

public sealed class PublicSessionQueryStateTests
{
    [Theory]
    [InlineData("/notifications", true)]
    [InlineData(null, false)]
    public async Task SubscriptionNavigationIsShownOnlyWhenHostProvidesIt(string? href, bool expected)
    {
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddMudServices();
        registrations.AddSingleton<IJSRuntime>(new UnavailableClipboard());
        registrations.AddSingleton<NavigationManager>(new TestNavigationManager());
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var markup = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<PublicSessionsSurface>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(PublicSessionsSurface.SubscriptionsHref)] = href }));
            return component.ToHtmlString();
        });

        Assert.Equal(expected, markup.Contains("My subscriptions", StringComparison.Ordinal));
        if (expected) Assert.Contains("/notifications", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionDetailsDisambiguateRepeatedLocalTime()
    {
        var session = Session(Guid.NewGuid()) with
        {
            StartUtc = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero),
            TimeZoneId = "America/New_York"
        };
        var format = typeof(PublicSessionsSurface).GetMethod("FormatDetailsWhen", BindingFlags.Static | BindingFlags.NonPublic)!;
        var first = (string)format.Invoke(null, [session])!;
        var second = (string)format.Invoke(null, [session with { StartUtc = session.StartUtc.AddHours(1) }])!;

        Assert.Contains("UTC-04:00", first, StringComparison.Ordinal);
        Assert.Contains("UTC-05:00", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReminderReturnSelectsTheRequestedSessionAndReloadsItsState()
    {
        var session = Session(Guid.NewGuid());
        Guid? loadedSession = null;
        var component = new PublicSessionsSurface();
        typeof(PublicSessionsSurface).GetProperty(nameof(component.Sessions))!.SetValue(component, new[] { session });
        typeof(PublicSessionsSurface).GetProperty(nameof(component.InitialSessionId))!.SetValue(component, session.SessionId);
        typeof(PublicSessionsSurface).GetProperty(nameof(component.InitialRemindersView))!.SetValue(component, true);
        typeof(PublicSessionsSurface).GetProperty(nameof(component.LoadSubscriptionStateAsync))!.SetValue(component,
            (Func<Guid, Task<PublicSessionsSurface.SessionSubscriptionPanelState?>>)(id =>
            {
                loadedSession = id;
                return Task.FromResult<PublicSessionsSurface.SessionSubscriptionPanelState?>(null);
            }));

        await InvokeAsync(component, "OnParametersSetAsync");

        Assert.Equal(session.SessionId, Field<Guid>(component, "_selectedSessionId"));
        Assert.Equal(1, Field<int>(component, "_activeTabIndex"));
        Assert.Equal(session.SessionId, loadedSession);
    }

    [Fact]
    public void AnonymousCalendarRouteUsesTheGuestEntry()
    {
        var app = new SwiftAid.Gateway.Components.App();
        var context = new DefaultHttpContext();
        context.Request.Path = "/sessions/calendar";
        typeof(SwiftAid.Gateway.Components.App).GetProperty("HttpContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(app, new HttpContextAccessor { HttpContext = context });

        Assert.True(Property<bool>(app, "IsGuestEntry"));
    }

    [Fact]
    public async Task CalendarEntryRendersTheCalendarAndQueriesTheSelectedDay()
    {
        var requests = new List<PublicSessionQuery>();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddMudServices();
        registrations.AddSingleton<IJSRuntime>(new UnavailableClipboard());
        registrations.AddSingleton<NavigationManager>(new TestNavigationManager());
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var markup = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<PublicSessionsSurface>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(PublicSessionsSurface.InitialCalendarView)] = true,
                    [nameof(PublicSessionsSurface.QuerySessionsAsync)] = (Func<PublicSessionQuery, Task<PublicSessionPage?>>)(query =>
                    {
                        requests.Add(query);
                        return Task.FromResult<PublicSessionPage?>(new PublicSessionPage([], null));
                    })
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Community calendar", markup, StringComparison.Ordinal);
        Assert.Contains(requests, query => query.LocalDateFrom is not null && query.LocalDateFrom == query.LocalDateTo);
        Assert.Contains(requests, query => query.LocalDateFrom is not null && query.LocalDateTo > query.LocalDateFrom);
    }

    [Fact]
    public async Task DetailRendersThePublicTitleSummaryAndStatus()
    {
        var session = Session(Guid.NewGuid()) with { Title = "Community water session", Summary = "Public session summary" };
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddMudServices();
        registrations.AddSingleton<IJSRuntime>(new UnavailableClipboard());
        registrations.AddSingleton<NavigationManager>(new TestNavigationManager());
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var markup = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<PublicSessionsSurface>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(PublicSessionsSurface.Sessions)] = new[] { session },
                    [nameof(PublicSessionsSurface.InitialSessionId)] = session.SessionId
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Community water session", markup, StringComparison.Ordinal);
        Assert.Contains("Public session summary", markup, StringComparison.Ordinal);
        Assert.Contains("Scheduled", markup, StringComparison.Ordinal);
        Assert.Contains("About this session", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MonthOverviewUsesFiltersAndContinuesFromTheServerCursor()
    {
        var first = Session(Guid.NewGuid()) with { StartUtc = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero) };
        var second = Session(Guid.NewGuid()) with { StartUtc = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero) };
        var position = new PublicSessionPosition(first.StartUtc, first.SessionId);
        var requests = new List<PublicSessionQuery>();
        var component = new PublicSessionsSurface();
        typeof(PublicSessionsSurface).GetField("_calendarMonth", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, new DateTime(2026, 11, 1));
        typeof(PublicSessionsSurface).GetField("_searchText", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "water");
        typeof(PublicSessionsSurface).GetField("_languageFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "en");
        typeof(PublicSessionsSurface).GetField("_countryFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "SZ");
        typeof(PublicSessionsSurface).GetField("_regionFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "hhohho");
        SetQuery(component, query =>
        {
            requests.Add(query);
            return Task.FromResult<PublicSessionPage?>(query.AfterSessionId is null
                ? new PublicSessionPage([first], position)
                : new PublicSessionPage([second], null));
        });

        await InvokeAsync(component, "RefreshCalendarMonthAsync");
        await InvokeAsync(component, "LoadMoreCalendarMonthAsync");

        Assert.Equal(new DateOnly(2026, 11, 1), requests[0].LocalDateFrom);
        Assert.Equal(new DateOnly(2026, 11, 30), requests[0].LocalDateTo);
        Assert.Equal("water", requests[0].Search);
        Assert.Equal("en", requests[0].Language);
        Assert.Equal("SZ", requests[0].CountryCode);
        Assert.Equal("hhohho", requests[0].RegionId);
        Assert.Equal(position.SessionId, requests[1].AfterSessionId);
        Assert.Equal(new[] { new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 19) }, Field<HashSet<DateOnly>>(component, "_monthDates").Order().ToArray());
        Assert.Null(Field<PublicSessionPosition?>(component, "_monthNext"));
    }

    [Fact]
    public async Task OlderMonthOverviewCannotReplaceTheCurrentMonth()
    {
        var older = new TaskCompletionSource<PublicSessionPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var component = new PublicSessionsSurface();
        var calls = 0;
        var current = Session(Guid.NewGuid()) with { StartUtc = new DateTimeOffset(2026, 12, 5, 12, 0, 0, TimeSpan.Zero) };
        SetQuery(component, _ => ++calls == 1 ? older.Task : Task.FromResult<PublicSessionPage?>(new PublicSessionPage([current], null)));

        var previousRequest = InvokeAsync(component, "RefreshCalendarMonthAsync");
        await InvokeAsync(component, "RefreshCalendarMonthAsync");
        older.SetResult(new PublicSessionPage([Session(Guid.NewGuid())], null));
        await previousRequest;

        Assert.Equal(new DateOnly(2026, 12, 5), Assert.Single(Field<HashSet<DateOnly>>(component, "_monthDates")));
    }

    [Fact]
    public async Task CopyInvitationFallsBackToSelectableTextWhenClipboardIsUnavailable()
    {
        var session = Session(Guid.NewGuid());
        var list = new PublicSessionList();
        typeof(PublicSessionList).GetProperty(nameof(list.LoadInvitationTextAsync))!.SetValue(list,
            (Func<Guid, Task<string?>>)(_ => Task.FromResult<string?>("Public invitation")));
        typeof(PublicSessionList).GetProperty("Js", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(list, new UnavailableClipboard());
        var copy = typeof(PublicSessionList).GetMethod("CopyInvitationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)copy.Invoke(list, [session])!;

        Assert.Equal(session.SessionId, typeof(PublicSessionList).GetField("_openInvitationId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(list));
        Assert.Equal("Public invitation", typeof(PublicSessionList).GetField("_openInvitationText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(list));
    }

    [Fact]
    public async Task EmptyBoundedSearchOffersContinuationBeforeReportingNoMatches()
    {
        var position = new PublicSessionPosition(DateTimeOffset.UtcNow, Guid.NewGuid());
        var component = new PublicSessionsSurface();
        SetQuery(component, query => Task.FromResult<PublicSessionPage?>(
            new PublicSessionPage([], query.AfterSessionId is null ? position : null)));

        await InvokeAsync(component, "RefreshQueryAsync");
        Assert.Equal("No matches in this batch", Property<string>(component, "EmptyResultsTitle"));
        Assert.Equal("Load more sessions to continue searching.", Property<string>(component, "EmptyResultsDescription"));

        await InvokeAsync(component, "LoadMoreAsync");
        Assert.Equal("No upcoming sessions found", Property<string>(component, "EmptyResultsTitle"));
    }

    [Fact]
    public void CalendarLinkUsesTheProvidedGatewayBasePath()
    {
        var list = new PublicSessionList();
        typeof(PublicSessionList).GetProperty(nameof(list.PublicBasePath))!.SetValue(list, "https://gateway.example/public/v1/sessions/");
        var method = typeof(PublicSessionList).GetMethod("CalendarHref", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var id = Guid.Parse("d0c00001-2222-4a44-9aa9-123456789abc");

        var href = (string)method.Invoke(list, [id])!;

        Assert.Equal($"https://gateway.example/public/v1/sessions/{id:D}/calendar", href);
    }

    [Fact]
    public void CompactTimeShowsTheEndDateForAnOvernightSession()
    {
        var session = Session(Guid.NewGuid()) with
        {
            StartUtc = new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.Zero),
            DurationMinutes = 120
        };
        var method = typeof(PublicSessionList).GetMethod("FormatWhenCompact", BindingFlags.Static | BindingFlags.NonPublic)!;

        var display = (string)method.Invoke(null, [session])!;

        Assert.Contains(new DateTime(2026, 11, 2).ToString("d MMM yyyy"), display);
        Assert.Contains("01:30", display);
        Assert.Contains("UTC", display);
    }

    [Fact]
    public async Task CalendarSelectionQueriesTheChosenLocalDayAndPreservesFilters()
    {
        PublicSessionQuery? observed = null;
        var component = new PublicSessionsSurface();
        SetQuery(component, query =>
        {
            observed = query;
            return Task.FromResult<PublicSessionPage?>(new PublicSessionPage([], null));
        });
        typeof(PublicSessionsSurface).GetField("_searchText", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "water");
        typeof(PublicSessionsSurface).GetField("_languageFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "en");
        var select = typeof(PublicSessionsSurface).GetMethod("SelectCalendarDayAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)select.Invoke(component, [new DateTime(2026, 11, 1)])!;

        Assert.Equal(new DateOnly(2026, 11, 1), observed!.LocalDateFrom);
        Assert.Equal(observed.LocalDateFrom, observed.LocalDateTo);
        Assert.Equal("water", observed.Search);
        Assert.Equal("en", observed.Language);
    }

    [Fact]
    public async Task CustomDatesAreSentAsLocalCalendarDates()
    {
        PublicSessionQuery? observed = null;
        var component = new PublicSessionsSurface();
        SetQuery(component, query =>
        {
            observed = query;
            return Task.FromResult<PublicSessionPage?>(new PublicSessionPage([], null));
        });
        var period = typeof(PublicSessionsSurface).GetField("_periodFilter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        period.SetValue(component, Enum.Parse(period.FieldType, "CustomDates"));
        typeof(PublicSessionsSurface).GetField("_dateFrom", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, new DateTime(2026, 11, 1));
        typeof(PublicSessionsSurface).GetField("_dateTo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, new DateTime(2026, 11, 2));

        await InvokeAsync(component, "RefreshQueryAsync");

        Assert.Equal(new DateOnly(2026, 11, 1), observed!.LocalDateFrom);
        Assert.Equal(new DateOnly(2026, 11, 2), observed.LocalDateTo);
    }

    [Fact]
    public async Task CategoryAndLanguageAreSentWithTheServerQuery()
    {
        PublicSessionQuery? observed = null;
        var component = new PublicSessionsSurface();
        SetQuery(component, query =>
        {
            observed = query;
            return Task.FromResult<PublicSessionPage?>(new PublicSessionPage([], null));
        });
        typeof(PublicSessionsSurface).GetField("_categoryFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "Nutrition");
        typeof(PublicSessionsSurface).GetField("_languageFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "en");
        typeof(PublicSessionsSurface).GetField("_countryFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "ZW");
        typeof(PublicSessionsSurface).GetField("_regionFilter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "harare");

        await InvokeAsync(component, "RefreshQueryAsync");

        Assert.Equal("Nutrition", observed!.Category);
        Assert.Equal("en", observed.Language);
        Assert.Equal("ZW", observed.CountryCode);
        Assert.Equal("harare", observed.RegionId);
        await InvokeAsync(component, "ClearFiltersAsync");
        Assert.Null(observed!.CountryCode);
        Assert.Null(observed.RegionId);
    }

    [Fact]
    public async Task LoadMore_UsesReturnedPositionAndKeepsPreviouslyLoadedSessions()
    {
        var first = Session(Guid.NewGuid());
        var second = Session(Guid.NewGuid());
        var position = new PublicSessionPosition(first.StartUtc, first.SessionId);
        var requests = new List<PublicSessionQuery>();
        var component = new PublicSessionsSurface();
        SetQuery(component, query =>
        {
            requests.Add(query);
            return Task.FromResult<PublicSessionPage?>(query.AfterSessionId is null
                ? new PublicSessionPage([first], position)
                : new PublicSessionPage([second], null));
        });

        await InvokeAsync(component, "RefreshQueryAsync");
        await InvokeAsync(component, "LoadMoreAsync");

        Assert.Equal(position.SessionId, requests[1].AfterSessionId);
        Assert.Equal(position.StartUtc, requests[1].AfterStartUtc);
        Assert.Equal(new[] { first.SessionId, second.SessionId }, Field<IReadOnlyList<PublicSessionView>>(component, "_querySessions").Select(item => item.SessionId));
        Assert.Null(Field<PublicSessionPosition?>(component, "_nextPage"));
    }

    [Fact]
    public async Task FailedSearchCanBeRetriedWithoutReportingAnEmptyCatalog()
    {
        var current = Session(Guid.NewGuid());
        var calls = 0;
        var component = new PublicSessionsSurface();
        SetQuery(component, _ => Task.FromResult<PublicSessionPage?>(++calls == 1
            ? null : new PublicSessionPage([current], null)));

        await InvokeAsync(component, "RefreshQueryAsync");
        Assert.NotNull(Field<string?>(component, "_queryError"));
        await InvokeAsync(component, "RetryAsync");

        Assert.Null(Field<string?>(component, "_queryError"));
        Assert.Equal(current.SessionId, Assert.Single(Field<IReadOnlyList<PublicSessionView>>(component, "_querySessions")).SessionId);
    }

    [Fact]
    public async Task SlowerPreviousSearchCannotReplaceLatestResults()
    {
        var firstRequest = new TaskCompletionSource<PublicSessionPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Session(Guid.NewGuid());
        var calls = 0;
        var component = new PublicSessionsSurface();
        SetQuery(component, _ => ++calls == 1
            ? firstRequest.Task
            : Task.FromResult<PublicSessionPage?>(new PublicSessionPage([current], null)));

        var old = InvokeAsync(component, "RefreshQueryAsync");
        await InvokeAsync(component, "RefreshQueryAsync");
        firstRequest.SetResult(new PublicSessionPage([Session(Guid.NewGuid())], null));
        await old;

        Assert.Equal(current.SessionId, Assert.Single(Field<IReadOnlyList<PublicSessionView>>(component, "_querySessions")).SessionId);
    }

    private static Task InvokeAsync(PublicSessionsSurface component, string method) =>
        (Task)typeof(PublicSessionsSurface).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null)!;

    private static void SetQuery(PublicSessionsSurface component, Func<PublicSessionQuery, Task<PublicSessionPage?>> query) =>
        typeof(PublicSessionsSurface).GetProperty(nameof(component.QuerySessionsAsync))!.SetValue(component, query);

    private static T Field<T>(PublicSessionsSurface component, string name) =>
        (T)typeof(PublicSessionsSurface).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;

    private static T Property<T>(PublicSessionsSurface component, string name) =>
        (T)typeof(PublicSessionsSurface).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;

    private static T Property<T>(object component, string name) =>
        (T)component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;

    private static PublicSessionView Session(Guid id) => new(
        id, 3, "Session", "Summary", "Community", [], "SZ", "Region", "Education", "Presenter", "en",
        DateTimeOffset.UtcNow.AddDays(2), "UTC", 60, null, [], SessionChannelType.None, null, "Scheduled", null, 0);

    private sealed class UnavailableClipboard : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromException<TValue>(new JSException("Clipboard unavailable"));

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromException<TValue>(new JSException("Clipboard unavailable"));
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }
}
