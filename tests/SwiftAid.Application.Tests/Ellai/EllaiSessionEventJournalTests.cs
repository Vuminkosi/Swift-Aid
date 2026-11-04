using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T08")]
public sealed class EllaiSessionEventJournalTests
{
    [Fact]
    public void AppendAssignsOrderedSequences()
    {
        var journal = new EllaiSessionEventJournal();
        var now = DateTimeOffset.UtcNow;

        var first = journal.Append("s-1", "approval-required", "req-1", "hash-1 v1", now);
        var second = journal.Append("s-1", "approval-resolved", "req-1", "Approved", now);

        Assert.True(second.Sequence > first.Sequence);
        var page = journal.ReadSince("s-1", 0);
        Assert.Equal(2, page.Events.Count);
        Assert.Equal(second.Sequence, page.Cursor);
    }

    [Fact]
    public void CursorReplaysOnlyNewerEvents()
    {
        var journal = new EllaiSessionEventJournal();
        var now = DateTimeOffset.UtcNow;
        journal.Append("s-1", "approval-required", "req-1", "hash-1 v1", now);
        var second = journal.Append("s-1", "approval-edit", "req-2", "hash-2 v2", now);

        var page = journal.ReadSince("s-1", second.Sequence - 1);
        Assert.Single(page.Events);
        Assert.Equal("req-2", page.Events[0].Ref);

        var empty = journal.ReadSince("s-1", second.Sequence);
        Assert.Empty(empty.Events);
        Assert.Equal(second.Sequence, empty.Cursor);
    }

    [Fact]
    public void SessionsAreIsolatedAndUnknownReadsEmpty()
    {
        var journal = new EllaiSessionEventJournal();
        var now = DateTimeOffset.UtcNow;
        journal.Append("s-1", "approval-required", "req-1", "hash-1 v1", now);

        Assert.Empty(journal.ReadSince("s-2", 0).Events);
        Assert.Empty(journal.ReadSince("s-missing", 0).Events);
    }

    [Fact]
    public void JournalIsBoundedPerSession()
    {
        var journal = new EllaiSessionEventJournal();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < EllaiSessionEventJournal.MaxEventsPerSession + 10; i++)
        {
            journal.Append("s-1", "approval-required", $"req-{i}", "hash v1", now);
        }

        var total = 0;
        var cursor = 0L;
        while (true)
        {
            var page = journal.ReadSince("s-1", cursor);
            total += page.Events.Count;
            if (page.Events.Count == 0)
            {
                break;
            }

            cursor = page.Cursor;
        }

        Assert.Equal(EllaiSessionEventJournal.MaxEventsPerSession, total);
    }
}
