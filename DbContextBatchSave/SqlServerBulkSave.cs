using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace DbContextBatchSave;

internal static class SqlServerBulkSave
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    private const int SqlServerJsonBatchRows = 10_000;

    public static bool TrySave(DbContext context, bool acceptAllChangesOnSuccess, out int affectedEntries)
    {
        if (!TryCreatePlans(context, out var plans, out affectedEntries))
            return false;

        if (plans.Count == 0)
            return true;

        var currentTransaction = context.Database.CurrentTransaction;
        using var ownedTransaction = currentTransaction is null ? context.Database.BeginTransaction() : null;
        var transaction = currentTransaction?.GetDbTransaction() ?? ownedTransaction!.GetDbTransaction();

        try
        {
            var connection = context.Database.GetDbConnection();
            foreach (var plan in plans)
                ExecutePlan(connection, transaction, context.Database.GetCommandTimeout(), plan);

            ownedTransaction?.Commit();
            AcceptChanges(plans, acceptAllChangesOnSuccess);
            return true;
        }
        catch
        {
            ownedTransaction?.Rollback();
            throw;
        }
    }

    private static bool TryCreatePlans(DbContext context, out List<TablePlan> plans, out int affectedEntries)
    {
        plans = [];
        affectedEntries = 0;

        if (context.Database.ProviderName != SqlServerProviderName
            || Transaction.Current is not null
            || context.Database.CreateExecutionStrategy().RetriesOnFailure
            || HasInterceptors(context))
        {
            return false;
        }

        var changeTracker = context.ChangeTracker;
        changeTracker.DetectChanges();
        var autoDetectChangesEnabled = changeTracker.AutoDetectChangesEnabled;
        changeTracker.AutoDetectChangesEnabled = false;

        try
        {
            var addedEntries = changeTracker.Entries()
                .Where(entry => entry.State == EntityState.Added)
                .ToList();
            affectedEntries = addedEntries.Count;
            if (addedEntries.Count == 0)
                return true;

            var entriesByType = addedEntries
                .GroupBy(entry => entry.Metadata)
                .ToDictionary(group => group.Key, group => group.ToList());
            var orderedTypes = new List<IEntityType>(entriesByType.Count);
            var visiting = new HashSet<IEntityType>();
            var visited = new HashSet<IEntityType>();

            bool Visit(IEntityType entityType)
            {
                if (visited.Contains(entityType))
                    return true;
                if (!visiting.Add(entityType))
                    return false;

                foreach (var foreignKey in entityType.GetForeignKeys())
                {
                    if (foreignKey.PrincipalEntityType == entityType)
                        return false;
                    if (entriesByType.ContainsKey(foreignKey.PrincipalEntityType)
                        && !Visit(foreignKey.PrincipalEntityType))
                    {
                        return false;
                    }
                }

                visiting.Remove(entityType);
                visited.Add(entityType);
                orderedTypes.Add(entityType);
                return true;
            }

            foreach (var entityType in entriesByType.Keys)
            {
                if (!Visit(entityType))
                    return false;
            }

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entityType in orderedTypes)
            {
                var tableName = entityType.GetTableName();
                if (string.IsNullOrEmpty(tableName))
                    return false;

                var schema = entityType.GetSchema();
                if (!tables.Add($"{schema}\0{tableName}"))
                    return false;

                var storeObject = StoreObjectIdentifier.Table(tableName, schema);
                var properties = entityType.GetProperties()
                    .Select(property => new ColumnPlan(property, property.GetColumnName(storeObject)))
                    .Where(column => column.ColumnName is not null)
                    .ToList();
                var primaryKey = entityType.FindPrimaryKey();
                if (primaryKey is null)
                    return false;

                var generatedProperties = properties
                    .Where(column => column.Property.ValueGenerated != ValueGenerated.Never)
                    .ToList();
                if (generatedProperties.Count > 1
                    || generatedProperties.Any(column => !primaryKey.Properties.Contains(column.Property))
                    || generatedProperties.Any(column => column.Property.ValueGenerated != ValueGenerated.OnAdd))
                {
                    return false;
                }

                var generatedKey = generatedProperties.SingleOrDefault()?.Property;
                if (generatedKey is not null
                    && (primaryKey.Properties.Count != 1 || !IsSupportedGeneratedKey(generatedKey.ClrType)))
                {
                    return false;
                }

                var insertColumns = properties
                    .Where(column => column.Property.ValueGenerated == ValueGenerated.Never)
                    .ToList();
                if (insertColumns.Count == 0
                    || insertColumns.Select(column => column.ColumnName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != insertColumns.Count
                    || properties.Any(column => !IsSupportedValue(column.Property.ClrType) || column.Property.GetValueConverter() is not null))
                {
                    return false;
                }

                plans.Add(new TablePlan(
                    entityType,
                    QuoteTable(tableName, schema),
                    insertColumns,
                    generatedKey,
                    entriesByType[entityType]));
            }

            return true;
        }
        catch
        {
            plans.Clear();
            affectedEntries = 0;
            return false;
        }
        finally
        {
            changeTracker.AutoDetectChangesEnabled = autoDetectChangesEnabled;
        }
    }

    private static bool HasInterceptors(DbContext context)
    {
        var options = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>();
        return options?.Interceptors?.Any() == true || options?.SingletonInterceptors?.Any() == true;
    }

    private static void ExecutePlan(
        DbConnection connection,
        DbTransaction transaction,
        int? commandTimeout,
        TablePlan plan)
    {
        var rowsPerBatch = GetRowsPerBatch();
        for (var start = 0; start < plan.Entries.Count; start += rowsPerBatch)
        {
            var rows = plan.Entries.Skip(start).Take(rowsPerBatch).ToList();
            using var command = CreateCommand(connection, transaction, commandTimeout, plan, rows);
            if (plan.GeneratedKey is null)
            {
                command.ExecuteNonQuery();
                continue;
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rowIndex = reader.GetInt32(0);
                var generatedValue = reader.GetValue(1);
                rows[rowIndex].Property(plan.GeneratedKey.Name).CurrentValue = ConvertGeneratedKey(
                    generatedValue,
                    plan.GeneratedKey.ClrType);
            }
        }
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction transaction,
        int? commandTimeout,
        TablePlan plan,
        IReadOnlyList<EntityEntry> rows)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (commandTimeout.HasValue)
            command.CommandTimeout = commandTimeout.Value;

        var columnList = string.Join(", ", plan.Columns.Select(column => QuoteIdentifier(column.ColumnName!)));
        var jsonSchema = string.Join(", ", plan.Columns.Select((column, index) =>
            $"[c{index}] {GetJsonSqlType(column.Property.ClrType)} '$.c{index}'"));
        var source = $"OPENJSON(@rows) WITH ([__row] int '$.r', {jsonSchema}) AS [source]";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@rows";
        parameter.DbType = DbType.String;
        parameter.Size = -1;
        parameter.Value = CreateJson(plan, rows);
        command.Parameters.Add(parameter);

        if (plan.GeneratedKey is null)
        {
            command.CommandText = $"INSERT INTO {plan.QuotedTable} ({columnList}) SELECT {string.Join(", ", plan.Columns.Select((_, index) => $"[source].[c{index}]"))} FROM {source};";
        }
        else
        {
            var keyColumn = QuoteIdentifier(plan.GeneratedKey.GetColumnName(
                StoreObjectIdentifier.Table(plan.EntityType.GetTableName()!, plan.EntityType.GetSchema()))!);
            command.CommandText = $"DECLARE @bulkOutput TABLE ([__row] int NOT NULL, [__key] sql_variant NOT NULL); "
                + $"MERGE {plan.QuotedTable} AS [target] USING {source} ON 1 = 0 "
                + $"WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({string.Join(", ", plan.Columns.Select((_, index) => $"[source].[c{index}]"))}) "
                + $"OUTPUT [source].[__row], INSERTED.{keyColumn} INTO @bulkOutput ([__row], [__key]); "
                + "SELECT [__row], [__key] FROM @bulkOutput ORDER BY [__row];";
        }

        return command;
    }

    private static string CreateJson(
        TablePlan plan,
        IReadOnlyList<EntityEntry> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
                writer.WriteStartObject();
                writer.WriteNumber("r", rowIndex);

            for (var columnIndex = 0; columnIndex < plan.Columns.Count; columnIndex++)
            {
                var property = plan.Columns[columnIndex].Property;
                    WriteJsonValue(
                        writer,
                        $"c{columnIndex}",
                        rows[rowIndex].Property(property.Name).CurrentValue,
                        property.ClrType);
            }

                writer.WriteEndObject();
        }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static int GetRowsPerBatch()
    {
        return SqlServerJsonBatchRows;
    }

    private static void AcceptChanges(IEnumerable<TablePlan> plans, bool acceptAllChangesOnSuccess)
    {
        if (!acceptAllChangesOnSuccess)
            return;

        foreach (var entry in plans.SelectMany(plan => plan.Entries))
            entry.State = EntityState.Unchanged;
    }

    private static bool IsSupportedGeneratedKey(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte)
            || type == typeof(short)
            || type == typeof(int)
            || type == typeof(long)
            || type == typeof(Guid);
    }

    private static bool IsSupportedValue(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string)
            || type == typeof(bool)
            || type == typeof(byte)
            || type == typeof(short)
            || type == typeof(int)
            || type == typeof(long)
            || type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal)
            || type == typeof(Guid)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan);
    }

    private static string GetJsonSqlType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return "nvarchar(max)";
        if (type == typeof(bool)) return "bit";
        if (type == typeof(byte)) return "tinyint";
        if (type == typeof(short)) return "smallint";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "bigint";
        if (type == typeof(float)) return "real";
        if (type == typeof(double)) return "float";
        if (type == typeof(decimal)) return "decimal(38, 18)";
        if (type == typeof(Guid)) return "uniqueidentifier";
        if (type == typeof(DateTime)) return "datetime2";
        if (type == typeof(DateTimeOffset)) return "datetimeoffset";
        if (type == typeof(TimeSpan)) return "time";
        throw new NotSupportedException($"The property type '{type.FullName}' is not supported by the SQL Server bulk insert path.");
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, string name, object? value, Type type)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) writer.WriteString(name, (string)value);
        else if (type == typeof(bool)) writer.WriteBoolean(name, (bool)value);
        else if (type == typeof(byte)) writer.WriteNumber(name, (byte)value);
        else if (type == typeof(short)) writer.WriteNumber(name, (short)value);
        else if (type == typeof(int)) writer.WriteNumber(name, (int)value);
        else if (type == typeof(long)) writer.WriteNumber(name, (long)value);
        else if (type == typeof(float)) writer.WriteNumber(name, (float)value);
        else if (type == typeof(double)) writer.WriteNumber(name, (double)value);
        else if (type == typeof(decimal)) writer.WriteNumber(name, (decimal)value);
        else if (type == typeof(Guid)) writer.WriteString(name, (Guid)value);
        else if (type == typeof(DateTime)) writer.WriteString(name, (DateTime)value);
        else if (type == typeof(DateTimeOffset)) writer.WriteString(name, (DateTimeOffset)value);
        else if (type == typeof(TimeSpan)) writer.WriteString(name, ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture));
        else throw new NotSupportedException($"The property type '{type.FullName}' is not supported by the SQL Server bulk insert path.");
    }

    private static object ConvertGeneratedKey(object value, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(Guid))
            return value is Guid guid ? guid : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    private static string QuoteTable(string tableName, string? schema)
    {
        return string.IsNullOrEmpty(schema)
            ? QuoteIdentifier(tableName)
            : QuoteIdentifier(schema) + "." + QuoteIdentifier(tableName);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    private sealed record ColumnPlan(IProperty Property, string? ColumnName);

    private sealed record TablePlan(
        IEntityType EntityType,
        string QuotedTable,
        IReadOnlyList<ColumnPlan> Columns,
        IProperty? GeneratedKey,
        List<EntityEntry> Entries);
}
