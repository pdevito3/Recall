namespace Recall.Server.Databases;

using System.Linq.Expressions;
using Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Resources;

public static class JournalOwnerFilterExtension
{
    private const string OwnerPathAnnotation = "JournalOwnerPath";

    public static void FilterByJournalOwner(this ModelConfigurationBuilder configurationBuilder, AppDbContext context)
        => configurationBuilder.Conventions.Add(_ => new JournalOwnerFilterConvention(context));

    public static EntityTypeBuilder<TEntity> ApplyJournalOwner<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, string>> ownerIdPath)
        where TEntity : class
    {
        builder.Metadata.AddAnnotation(OwnerPathAnnotation, ownerIdPath);
        return builder;
    }

    private sealed class JournalOwnerFilterConvention(AppDbContext context) : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder,
            IConventionContext<IConventionModelBuilder> conventionContext)
        {
            foreach (var entityType in modelBuilder.Metadata.GetEntityTypes().Where(e => e.BaseType is null).ToList())
            {
                if (OwnerIdPathOf(entityType) is { } ownerIdPath)
                    entityType.SetQueryFilter(QueryFilterNames.JournalOwner, Expression.Lambda(
                        Expression.Equal(ownerIdPath.Body,
                            Expression.Property(Expression.Constant(context), nameof(AppDbContext.CurrentJournalOwnerId))),
                        ownerIdPath.Parameters));

                // A migration snapshot cannot hold the lambda of the path. The filter holds it now.
                entityType.RemoveAnnotation(OwnerPathAnnotation);
            }
        }
    }

    private static LambdaExpression? OwnerIdPathOf(IReadOnlyEntityType entityType)
    {
        if (entityType.FindAnnotation(OwnerPathAnnotation)?.Value is LambdaExpression path)
            return path;
        if (!entityType.ClrType.IsAssignableTo(typeof(IJournalOwned)))
            return null;

        var row = Expression.Parameter(entityType.ClrType, "e");
        return Expression.Lambda(Expression.Property(row, nameof(IJournalOwned.UserId)), row);
    }
}
