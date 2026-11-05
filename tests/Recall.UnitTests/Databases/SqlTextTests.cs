namespace Recall.UnitTests.Databases;

using Recall.Server.Databases;
using Shouldly;

public class SqlTextTests
{
    private static readonly HashSet<string> Tables = ["users", "audit_entries"];

    [Test]
    [Arguments("UPDATE users AS u SET first_name = @p WHERE u.id = @id", "users")]
    [Arguments("update USERS set first_name = 'x'", "users")]
    [Arguments("DELETE FROM public.users WHERE id = $1", "users")]
    [Arguments("DELETE FROM \"users\" WHERE id = $1", "users")]
    [Arguments("INSERT INTO \"public\".\"audit_entries\" (kind) VALUES ('x')", "audit_entries")]
    [Arguments("WITH changed AS (UPDATE users SET first_name = 'x' RETURNING tenant_id) SELECT * FROM tenants", "users")]
    [Arguments("-- users.update\nUPDATE audit_entries SET kind = kind", "audit_entries")]
    public void names_the_first_table_of_the_set_that_a_word_names(string sql, string table)
    {
        // Act & Assert
        SqlText.FirstNamed(sql, Tables).ShouldBe(table);
    }

    [Test]
    [Arguments("DELETE FROM used_dpop_proofs WHERE expires <= @now")]
    [Arguments("UPDATE users_archive SET first_name = 'x'")]
    [Arguments("-- users\nSELECT 1")]
    [Arguments("/* users /* no nesting in SQLite */ SELECT 1")]
    [Arguments("SELECT 'users', 'it''s users'")]
    public void finds_no_table_in_a_comment_a_value_or_another_name(string sql)
    {
        // Act & Assert
        SqlText.FirstNamed(sql, Tables).ShouldBeNull();
    }

    [Test]
    [Arguments("/* a comment /* that does not nest */ UPDATE users SET first_name = 'x'")]
    [Arguments("UPDATE `users` SET first_name = 'x'")]
    [Arguments("UPDATE [users] SET first_name = 'x'")]
    [Arguments("UPDATE [USERS] SET first_name = 'x'")]
    [Arguments("SELECT $1 FROM users WHERE id = $2")]
    public void finds_a_table_after_a_comment_in_each_form_of_a_quoted_name_and_next_to_a_parameter(string sql)
    {
        // Act & Assert
        SqlText.FirstNamed(sql, Tables).ShouldBe("users");
    }

    [Test]
    [Arguments("INSERT INTO t VALUES (1)", true)]
    [Arguments("WITH c AS (DELETE FROM t RETURNING id) SELECT id FROM c", true)]
    [Arguments("replace into t values (1)", true)]
    [Arguments("SELECT id FROM t", false)]
    [Arguments("SELECT 'UPDATE' FROM t -- DELETE", false)]
    [Arguments("SELECT \"update\" FROM t", false)]
    [Arguments("SELECT updated_on FROM t", false)]
    public void a_write_has_a_bare_write_keyword(string sql, bool isWrite)
    {
        // Act & Assert
        SqlText.IsWrite(sql).ShouldBe(isWrite);
    }
}
