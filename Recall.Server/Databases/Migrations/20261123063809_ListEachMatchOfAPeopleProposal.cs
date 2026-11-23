using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Server.Databases.Migrations
{
    /// <inheritdoc />
    public partial class ListEachMatchOfAPeopleProposal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE sessions SET people_proposals = (
                    SELECT json_group_array(json_object(
                        'label', json_extract(proposal.value, '$.label'),
                        'matchedPersonIds', CASE
                            WHEN json_extract(proposal.value, '$.matchedPersonId') IS NULL THEN json('[]')
                            ELSE json_array(json_extract(proposal.value, '$.matchedPersonId'))
                        END))
                    FROM (SELECT value FROM json_each(sessions.people_proposals) ORDER BY key) AS proposal)
                WHERE json_array_length(people_proposals) > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE sessions SET people_proposals = (
                    SELECT json_group_array(json_object(
                        'label', json_extract(proposal.value, '$.label'),
                        'matchedPersonId', json_extract(proposal.value, '$.matchedPersonIds[0]')))
                    FROM (SELECT value FROM json_each(sessions.people_proposals) ORDER BY key) AS proposal)
                WHERE json_array_length(people_proposals) > 0;
                """);
        }
    }
}
