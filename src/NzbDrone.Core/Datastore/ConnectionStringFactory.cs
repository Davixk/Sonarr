using System;
using System.Data.SQLite;
using Npgsql;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Datastore
{
    public interface IConnectionStringFactory
    {
        DatabaseConnectionInfo MainDbConnection { get; }
        DatabaseConnectionInfo LogDbConnection { get; }
        string GetDatabasePath(string connectionString);
    }

    public class ConnectionStringFactory : IConnectionStringFactory
    {
        private readonly IConfigFileProvider _configFileProvider;

        public ConnectionStringFactory(IAppFolderInfo appFolderInfo, IConfigFileProvider configFileProvider)
        {
            _configFileProvider = configFileProvider;

            MainDbConnection = _configFileProvider.PostgresHost.IsNotNullOrWhiteSpace() ? GetPostgresConnectionString(_configFileProvider.PostgresMainDb) :
                GetConnectionString(appFolderInfo.GetDatabase());

            LogDbConnection = _configFileProvider.PostgresHost.IsNotNullOrWhiteSpace() ? GetPostgresConnectionString(_configFileProvider.PostgresLogDb) :
                GetConnectionString(appFolderInfo.GetLogDatabase());
        }

        public DatabaseConnectionInfo MainDbConnection { get; private set; }
        public DatabaseConnectionInfo LogDbConnection { get; private set; }

        public string GetDatabasePath(string connectionString)
        {
            var connectionBuilder = new SQLiteConnectionStringBuilder(connectionString);

            return connectionBuilder.DataSource;
        }

        private static DatabaseConnectionInfo GetConnectionString(string dbPath)
        {
            var connectionBuilder = new SQLiteConnectionStringBuilder
            {
                DataSource = dbPath,
                CacheSize = (int)-20000,
                DateTimeKind = DateTimeKind.Utc,
                JournalMode = OsInfo.IsOsx ? SQLiteJournalModeEnum.Truncate : SQLiteJournalModeEnum.Wal,
                Pooling = true,
                Version = 3,
                BusyTimeout = GetBusyTimeout()
            };

            if (OsInfo.IsOsx)
            {
                connectionBuilder.Add("Full FSync", true);
            }

            return new DatabaseConnectionInfo(DatabaseType.SQLite, connectionBuilder.ConnectionString);
        }

        private DatabaseConnectionInfo GetPostgresConnectionString(string dbName)
        {
            var connectionBuilder = new NpgsqlConnectionStringBuilder
            {
                Database = dbName,
                Host = _configFileProvider.PostgresHost,
                Username = _configFileProvider.PostgresUser,
                Password = _configFileProvider.PostgresPassword,
                Port = _configFileProvider.PostgresPort,
                Enlist = false
            };

            return new DatabaseConnectionInfo(DatabaseType.PostgreSQL, connectionBuilder.ConnectionString);
        }

        // fork4: SQLITE_BUSY_TIMEOUT (ms). Default 1000 (matches upstream Radarr and Sonarr v5), which
        // raises Sonarr's old 100 ms floor; clamped >= 100 so it never drops below that floor. Tunable
        // higher for a large DB under heavy write load. internal so the value can be asserted directly and
        // echoed by the startup config log.
        internal static int GetBusyTimeout()
        {
            var raw = Environment.GetEnvironmentVariable("SQLITE_BUSY_TIMEOUT");

            if (int.TryParse(raw, out var ms))
            {
                return Math.Max(100, ms);
            }

            return 1000;
        }
    }
}
