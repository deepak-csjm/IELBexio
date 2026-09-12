using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IelBexio.Infrastructure.Persistence;

/// <summary>
/// Renames tables, columns, keys and indexes to snake_case. PostgreSQL folds unquoted identifiers to
/// lower case, so PascalCase names would have to be quoted in every hand-written SQL statement
/// (the outbox dispatcher's <c>FOR UPDATE SKIP LOCKED</c> query, the index filter predicates, and
/// anything an operator types at psql). snake_case keeps all of that readable.
/// </summary>
public static class SnakeCaseNamingConvention
{
    public static void ApplySnakeCaseNames(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var tableName = entity.GetTableName();
            if (tableName is not null)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            var storeObject = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);

            foreach (var property in entity.GetProperties())
            {
                var current = storeObject is { } so ? property.GetColumnName(so) : property.GetColumnName();
                property.SetColumnName(ToSnakeCase(current ?? property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                var name = key.GetName();
                if (name is not null)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                var name = fk.GetConstraintName();
                if (name is not null)
                {
                    fk.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                var name = index.GetDatabaseName();
                if (name is not null)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previousIsLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (i > 0 && name[i - 1] != '_' && (previousIsLower || nextIsLower))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
