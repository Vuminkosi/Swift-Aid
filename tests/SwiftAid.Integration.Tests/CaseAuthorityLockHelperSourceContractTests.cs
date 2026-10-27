namespace SwiftAid.Integration.Tests;

public sealed class CaseAuthorityLockHelperSourceContractTests
{
    [Fact]
    public void Checked_in_lock_helper_declares_expected_lock_contract()
    {
        var sql = File.ReadAllText(FindRepositoryFile("infra/postgres/case-authority-lock-helper.sql"))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        Assert.Contains("CREATE OR REPLACE FUNCTION swiftaid_internal.lock_case_authority(", sql, StringComparison.Ordinal);
        Assert.Contains("p_case_id uuid,\n    p_actor_id text,\n    p_actor_hints text[],\n    p_write boolean", sql, StringComparison.Ordinal);
        Assert.Contains("RETURNS smallint", sql, StringComparison.Ordinal);
        Assert.Contains("0=locked; 1=caller must roll back and rediscover; 2=required", sql, StringComparison.Ordinal);
        Assert.Contains("3=invalid argument. None is an access decision.", sql, StringComparison.Ordinal);
        Assert.Contains("SECURITY DEFINER\nSET search_path = pg_catalog", sql, StringComparison.Ordinal);

        var tableOrder = new[]
        {
            "FROM public.organizations AS organization",
            "FROM public.workspaces AS workspace",
            "FROM public.actor_access_states AS access_state",
            "FROM public.actors AS actor",
            "FROM public.organization_memberships AS membership",
            "FROM public.access_grant_groups AS grant_group",
            "FROM public.access_grant_group_regions AS grant_region",
            "FROM public.access_grant_group_operations AS grant_operation",
            "FROM public.case_shares AS case_share",
            "FROM public.managed_cases AS managed_case",
            "FROM public.managed_case_regions AS case_region"
        };

        var previousIndex = sql.IndexOf("BEGIN;", StringComparison.Ordinal);
        foreach (var tableReference in tableOrder)
        {
            var currentIndex = sql.IndexOf(tableReference, previousIndex + 1, StringComparison.Ordinal);
            Assert.True(currentIndex > previousIndex, $"Expected lock-helper table access in canonical order: {tableReference}");
            previousIndex = currentIndex;
        }
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository source file '{relativePath}'.");
    }
}
