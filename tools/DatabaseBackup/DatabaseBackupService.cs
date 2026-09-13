using Microsoft.Data.Sqlite;

namespace DatabaseBackup;

public static class DatabaseBackupService
{
    public static int Backup(string[] arguments)
    {
        if (arguments.Length != 2)
        {
            Console.Error.WriteLine("Usage: DatabaseBackup <source-database> <backup-directory>");
            return 2;
        }

        var sourcePath = Path.GetFullPath(arguments[0]);
        var backupDirectory = Path.GetFullPath(arguments[1]);

        if (!File.Exists(sourcePath))
        {
            Console.WriteLine("Database does not exist; backup skipped.");
            return 0;
        }

        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"silksong-rando-logic-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.db");

        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly
        };
        var destinationBuilder = new SqliteConnectionStringBuilder { DataSource = backupPath };

        using var source = new SqliteConnection(sourceBuilder.ToString());
        using var destination = new SqliteConnection(destinationBuilder.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);

        Console.WriteLine($"Database backup created: {backupPath}");
        return 0;
    }
}
