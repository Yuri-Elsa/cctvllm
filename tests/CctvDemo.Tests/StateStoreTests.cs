using Xunit;

public class StateDiffTests
{
    private static (string, string)[] Jobs(params (string, string)[] jobs) => jobs;

    [Fact]
    public void First_save_writes_everything_and_the_second_nothing()
    {
        var diff = new StateDiff();
        var first = diff.Compare(Jobs(("a", "1"), ("b", "2")), "s", null);
        Assert.Equal(2, first.Upserts.Count);
        Assert.NotNull(first.Settings);
        diff.Commit(first, "s");
        var second = diff.Compare(Jobs(("a", "1"), ("b", "2")), "s", null);
        Assert.Empty(second.Upserts); Assert.Empty(second.Deletes); Assert.Null(second.Settings); Assert.False(second.FolderChanged);
    }

    [Fact]
    public void Only_changed_and_removed_jobs_are_written()
    {
        var diff = new StateDiff();
        diff.Commit(diff.Compare(Jobs(("a", "1"), ("b", "2"), ("c", "3")), "s", null), "s");
        var change = diff.Compare(Jobs(("a", "1"), ("b", "2 changed"), ("d", "4")), "s", null);
        Assert.Equal(new[] { "b", "d" }, change.Upserts.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(new[] { "c" }, change.Deletes.ToArray());
    }

    [Fact]
    public void A_failed_write_is_repeated_because_it_was_never_committed()
    {
        var diff = new StateDiff();
        diff.Compare(Jobs(("a", "1")), "s", null);                         // the write failed: no Commit
        Assert.Single(diff.Compare(Jobs(("a", "1")), "s", null).Upserts);
    }

    [Fact]
    public void Folder_changes_are_noticed_including_clearing_it()
    {
        var diff = new StateDiff();
        diff.Commit(diff.Compare(Jobs(), "s", "/videos"), "s");
        Assert.False(diff.Compare(Jobs(), "s", "/videos").FolderChanged);
        Assert.True(diff.Compare(Jobs(), "s", null).FolderChanged);
    }
}
