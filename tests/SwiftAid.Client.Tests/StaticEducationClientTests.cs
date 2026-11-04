using SwiftAid.Client.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class StaticEducationClientTests
{
    private readonly StaticEducationClient _client = new();

    [Fact]
    public async Task List_returns_demo_labelled_source_linked_catalogue()
    {
        var result = await _client.ListAsync(
            new EducationQuery(new PageRequest(1, 25)), CancellationToken.None);

        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.TotalCount >= 5);
        Assert.All(result.Value.Items, material =>
        {
            Assert.False(string.IsNullOrWhiteSpace(material.Title));
            Assert.False(string.IsNullOrWhiteSpace(material.Source));
            Assert.False(string.IsNullOrWhiteSpace(material.SourceVersion));
        });
        Assert.DoesNotContain(result.Value.Items, material =>
            material.ReviewState == EducationReviewState.ApprovedForPublic && material.IsApproved &&
            material.ReuseRights == EducationReuseRights.Unverified);
    }

    [Fact]
    public async Task List_filters_by_category_language_and_search()
    {
        var animal = await _client.ListAsync(
            new EducationQuery(new PageRequest(1, 25), Category: "Animal health"), CancellationToken.None);

        Assert.Equal(UiCapabilityResultCode.Success, animal.Code);
        Assert.All(animal.Value!.Items, material =>
            Assert.Equal("Animal health", material.Category));

        var search = await _client.ListAsync(
            new EducationQuery(new PageRequest(1, 25), Search: "cholera"), CancellationToken.None);

        Assert.Equal(UiCapabilityResultCode.Success, search.Code);
        Assert.NotEmpty(search.Value!.Items);
        Assert.All(search.Value.Items, material =>
            Assert.Contains("cholera", material.Title + material.Summary, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task List_reports_empty_page_for_offline_only_without_cached_pack()
    {
        var result = await _client.ListAsync(
            new EducationQuery(new PageRequest(1, 25), OfflineOnly: true), CancellationToken.None);

        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value.Items);
        Assert.Equal(0, result.Value.TotalCount);
    }

    [Fact]
    public async Task List_rejects_invalid_page()
    {
        var result = await _client.ListAsync(
            new EducationQuery(new PageRequest(0, 25)), CancellationToken.None);

        Assert.Equal(UiCapabilityResultCode.InvalidRequest, result.Code);
    }

    [Fact]
    public async Task List_pages_deterministically()
    {
        var first = await _client.ListAsync(
            new EducationQuery(new PageRequest(1, 2)), CancellationToken.None);
        var second = await _client.ListAsync(
            new EducationQuery(new PageRequest(2, 2)), CancellationToken.None);

        Assert.Equal(UiCapabilityResultCode.Success, first.Code);
        Assert.Equal(UiCapabilityResultCode.Success, second.Code);
        Assert.Equal(2, first.Value!.Items.Count);
        Assert.True(first.Value.HasMore);
        Assert.Empty(first.Value.Items.Select(material => material.Id)
            .Intersect(second.Value!.Items.Select(material => material.Id)));
    }
}
