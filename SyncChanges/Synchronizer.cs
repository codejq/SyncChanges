﻿using Humanizer;
using NLog;
using NPoco;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SyncChanges
{
    public class Synchronizer
    {
        public bool DryRun { get; set; } = false;

        static Logger Log = LogManager.GetCurrentClassLogger();
        Config Config { get; set; }
        bool Error { get; set; }

        public Synchronizer(Config config)
        {
            if (config == null) throw new ArgumentException("config is null", nameof(config));
            Config = config;
        }

        public bool Sync()
        {
            Error = false;

            foreach (var replicationSet in Config.ReplicationSets)
            {
                Log.Info($"Starting replication for replication set {replicationSet.Name}");

                var tables = GetTables(replicationSet.Source);
                if (replicationSet.Tables != null && replicationSet.Tables.Any())
                    tables = tables.Where(t => replicationSet.Tables.Contains(t.Name.Trim('[', ']'))).ToList();

                if (!tables.Any())
                {
                    Log.Warn("No tables to replicate (check if change tracking is enabled)");
                    continue;
                }

                Log.Info($"Replicating {"table".ToQuantity(tables.Count, ShowQuantityAs.None)} {string.Join(", ", tables.Select(t => t.Name))}");

                var destinationsByVersion = replicationSet.Destinations.GroupBy(d => GetCurrentVersion(d))
                    .Where(d => d.Key >= 0).ToList();

                foreach (var destinations in destinationsByVersion)
                    Replicate(replicationSet.Source, destinations, tables);
            }

            Log.Info($"Finished replication {(Error ? "with" : "without")} errors");

            return !Error;
        }

        class TableInfo
        {
            public string Name { get; set; }
            public IList<string> KeyColumns { get; set; }
            public IList<string> OtherColumns { get; set; }
        }

        private IList<TableInfo> GetTables(DatabaseInfo dbInfo)
        {
            try
            {
                using (var db = new Database(dbInfo.ConnectionString, DatabaseType.SqlServer2008))
                {
                    var sql = @"select ('[' + s.name + '].[' + t.name + ']') TableName, ('[' + COL_NAME(t.object_id, a.column_id) + ']') ColumnName,
coalesce(c.index_id, 0) IndexId
from sys.change_tracking_tables tr
join sys.tables t on t.object_id = tr.object_id
join sys.schemas s on s.schema_id = t.schema_id
join sys.columns a on a.object_id = t.object_id
left join sys.index_columns c on c.object_id = t.object_id and c.column_id = a.column_id
left join sys.indexes i on i.object_id = t.object_id and i.index_id = c.index_id";
                    var tables = db.Fetch<dynamic>(sql).GroupBy(t => t.TableName)
                        .Select(g => new TableInfo
                        {
                            Name = (string)g.Key,
                            KeyColumns = g.Where(c => (int)c.IndexId > 0).Select(c => (string)c.ColumnName).ToList(),
                            OtherColumns = g.Where(c => (int)c.IndexId == 0).Select(c => (string)c.ColumnName).ToList()
                        }).ToList();

                    return tables;
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Error getting tables to replicate from source database");
                throw;
            }
        }

        class Change
        {
            public TableInfo Table { get; set; }
            public long Version { get; set; }
            public char Operation { get; set; }
            public Dictionary<string, object> Keys { get; private set; } = new Dictionary<string, object>();
            public Dictionary<string, object> Others { get; private set; } = new Dictionary<string, object>();

            public object[] Values => Keys.Values.Concat(Others.Values).ToArray();

            public List<string> ColumnNames => Keys.Keys.Concat(Others.Keys).ToList();
        }

        class ChangeInfo
        {
            public long Version { get; set; }
            public List<Change> Changes { get; private set; } = new List<Change>();
        }

        private void Replicate(DatabaseInfo source, IGrouping<long, DatabaseInfo> destinations, IList<TableInfo> tables)
        {
            var changeInfo = RetrieveChanges(source, destinations, tables);
            if (changeInfo == null) return;

            // replicate changes to destinations
            foreach (var destination in destinations)
            {
                try
                {
                    Log.Info($"Replicating changes to destination {destination.Name}");

                    using (var db = new Database(destination.ConnectionString, DatabaseType.SqlServer2005))
                    using (var transaction = db.GetTransaction(System.Data.IsolationLevel.ReadUncommitted))
                    {
                        foreach (var change in changeInfo.Changes.OrderBy(c => c.Version).ThenBy(c => c.Table.Name))
                            PerformChange(db, change);

                        if (!DryRun)
                        {
                            db.Execute("update SyncInfo set Version = @0", changeInfo.Version);
                            transaction.Complete();
                        }

                        Log.Info($"Destination {destination.Name} now at version {changeInfo.Version}");
                    }
                }
                catch (Exception ex)
                {
                    Error = true;
                    Log.Error(ex, $"Error replicating changes to destination {destination.Name}");
                }
            }
        }

        private ChangeInfo RetrieveChanges(DatabaseInfo source, IGrouping<long, DatabaseInfo> destinations, IList<TableInfo> tables)
        {
            var destinationVersion = destinations.Key;
            var changeInfo = new ChangeInfo();

            using (var db = new Database(source.ConnectionString, DatabaseType.SqlServer2008))
            {
                var snapshotIsolationEnabled = db.ExecuteScalar<int>("select snapshot_isolation_state from sys.databases where name = DB_NAME()") == 1;
                if (snapshotIsolationEnabled)
                {
                    Log.Info($"Snapshot isolation is enabled in database {source.Name}");
                    db.BeginTransaction(System.Data.IsolationLevel.Snapshot);
                }
                else
                    Log.Info($"Snapshot isolation is not enabled in database {source.Name}, ignoring all changes above current version");

                changeInfo.Version = db.ExecuteScalar<long>("select CHANGE_TRACKING_CURRENT_VERSION()");
                Log.Info($"Current version of database {source.Name} is {changeInfo.Version}");

                foreach (var table in tables)
                {
                    var tableName = table.Name;
                    var minVersion = db.ExecuteScalar<long?>("select CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@0))", tableName);

                    Log.Info($"Minimum version of table {tableName} in database {source.Name} is {minVersion}");

                    if (minVersion > destinationVersion)
                    {
                        Log.Error($"Cannot replicate table {tableName} to {"destination".ToQuantity(destinations.Count(), ShowQuantityAs.None)} {string.Join(", ", destinations.Select(d => d.Name))} because minimum source version {minVersion} is greater than destination version {destinationVersion}");
                        Error = true;
                        return null;
                    }

                    var sql = $@"select c.SYS_CHANGE_OPERATION, c.SYS_CHANGE_VERSION,
{string.Join(", ", table.KeyColumns.Select(c => "c." + c))},
{string.Join(", ", table.OtherColumns.Select(c => "t." + c))}
from CHANGETABLE (CHANGES {tableName}, @0) c
left outer join {tableName} t on ";
                    sql += string.Join(" and ", table.KeyColumns.Select(k => $"c.{k} = t.{k}"));
                    sql += " order by c.SYS_CHANGE_VERSION";
                    db.OpenSharedConnection();
                    var cmd = db.CreateCommand(db.Connection, System.Data.CommandType.Text, sql, destinationVersion);

                    using (var reader = cmd.ExecuteReader())
                    {
                        var numChanges = 0;

                        while (reader.Read())
                        {
                            var col = 0;
                            var change = new Change { Operation = ((string)reader[col++])[0], Table = table };
                            var version = reader.GetInt64(col++);
                            change.Version = version;

                            if (!snapshotIsolationEnabled && version > changeInfo.Version)
                            {
                                Log.Warn($"Ignoring change version {version}");
                                continue;
                            }

                            for (int i = 0; i < table.KeyColumns.Count; i++, col++)
                                change.Keys[table.KeyColumns[i]] = reader.GetValue(col);
                            for (int i = 0; i < table.OtherColumns.Count; i++, col++)
                                change.Others[table.OtherColumns[i]] = reader.GetValue(col);

                            changeInfo.Changes.Add(change);
                            numChanges++;
                        }

                        Log.Info($"Table {tableName} has {numChanges}");
                    }
                }

                if (snapshotIsolationEnabled)
                    db.CompleteTransaction();
            }

            return changeInfo;
        }

        private void PerformChange(Database db, Change change)
        {
            var table = change.Table;
            var tableName = table.Name;
            var operation = change.Operation;

            switch (operation)
            {
                // Insert
                case 'I':
                    var insertColumnNames = change.ColumnNames;
                    var insertSql = $"set IDENTITY_INSERT {tableName} ON; " +
                        string.Format("insert into {0} ({1}) values ({2}); ", tableName,
                        string.Join(", ", insertColumnNames),
                        string.Join(", ", Parameters(insertColumnNames.Count))) +
                        $"set IDENTITY_INSERT {tableName} OFF";
                    var insertValues = change.Values;
                    if (DryRun)
                        Log.Info($"Executing insert: {insertSql} ({FormatArgs(insertValues)})");
                    else
                        db.Execute(insertSql, insertValues);
                    break;

                // Update
                case 'U':
                    var updateColumnNames = change.Others.Keys.ToList();
                    var updateSql = string.Format("update {0} set {1} where {2}", tableName,
                        string.Join(", ", updateColumnNames.Select((c, i) => $"{c} = @{i + change.Keys.Count}")),
                        PrimaryKeys(table, change));
                    var updateValues = change.Values;
                    if (DryRun)
                        Log.Info($"Executing update: {updateSql} ({FormatArgs(updateValues)})");
                    else
                        db.Execute(updateSql, updateValues);
                    break;

                // Delete
                case 'D':
                    var deleteSql = string.Format("delete from {0} where {1}", tableName, PrimaryKeys(table, change));
                    var deleteValues = change.Keys.Values.ToArray();
                    if (DryRun)
                        Log.Info($"Executing delete: {deleteSql} ({FormatArgs(deleteValues)})");
                    else
                        db.Execute(deleteSql, deleteValues);
                    break;
            }
        }

        private static string FormatArgs(object[] args) => string.Join(", ", args.Select((a, i) => $"@{i} = {a}"));

        private static string PrimaryKeys(TableInfo table, Change change) =>
            string.Join(" and ", change.Keys.Keys.Select((c, i) => $"{c} = @{i}"));

        private static IEnumerable<string> Parameters(int n) => Enumerable.Range(0, n).Select(c => "@" + c);

        private long GetCurrentVersion(DatabaseInfo dbInfo)
        {
            try
            {
                using (var db = new Database(dbInfo.ConnectionString, DatabaseType.SqlServer2005))
                {
                    var syncInfoTableExists = db.ExecuteScalar<string>("select top(1) name from sys.tables where name ='SyncInfo'") != null;
                    long currentVersion;

                    if (!syncInfoTableExists)
                    {
                        Log.Info($"SyncInfo table does not exist in database {dbInfo.Name}");
                        currentVersion = db.ExecuteScalar<long?>("select CHANGE_TRACKING_CURRENT_VERSION()") ?? -1;
                        if (currentVersion < 0)
                        {
                            Log.Info($"Change tracking not enabled in database {dbInfo.Name}, assuming version 0");
                            currentVersion = 0;
                        }
                        else
                            Log.Info($"Database {dbInfo.Name} is at version {currentVersion}");

                        if (!DryRun)
                        {
                            db.Execute("create table SyncInfo (Id int not null primary key default 1 check (Id = 1), Version bigint not null)");
                            db.Execute("insert into SyncInfo (Version) values (@0)", currentVersion);
                        }
                    }
                    else
                    {
                        currentVersion = db.ExecuteScalar<long>("select top(1) Version from SyncInfo");
                        Log.Info($"Database {dbInfo.Name} is at version {currentVersion}");
                    }

                    return currentVersion;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error getting current version of destination database {dbInfo.Name}. Skipping this destination.");
                Error = true;
                return -1;
            }
        }
    }
}
