using DatabaseBackup;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class DatabaseBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"silksong-backup-tests-{Guid.NewGuid():N}");

    [Fact]
    public void MissingSource_SkipsBackup()
    {
        var result = DatabaseBackupService.Backup([Path.Combine(root, "missing.db"), Path.Combine(root, "backups")]);

        Assert.Equal(0, result);
        Assert.False(Directory.Exists(Path.Combine(root, "backups")));
    }

    [Fact]
    public void ExistingSource_CreatesReadableBackupWithCommittedData()
    {
        var source = Path.Combine(root, "source.db");
        var backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(root);
        using (var connection = new SqliteConnection($"Data Source={source}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Data (Value TEXT NOT NULL); INSERT INTO Data VALUES ('committed');";
            command.ExecuteNonQuery();
        }

        var result = DatabaseBackupService.Backup([source, backups]);

        Assert.Equal(0, result);
        var backup = Assert.Single(Directory.GetFiles(backups, "*.db"));
        using var backupConnection = new SqliteConnection($"Data Source={backup}");
        backupConnection.Open();
        using var query = backupConnection.CreateCommand();
        query.CommandText = "SELECT Value FROM Data";
        Assert.Equal("committed", query.ExecuteScalar());
    }

    [Fact]
    public void BackupFailure_IsSurfaced()
    {
        var source = Path.Combine(root, "source.db");
        Directory.CreateDirectory(root);
        using (var connection = new SqliteConnection($"Data Source={source}"))
        {
            connection.Open();
        }

        var invalidBackupPath = Path.Combine(root, "not-a-directory");
        File.WriteAllText(invalidBackupPath, "occupied");

        Assert.ThrowsAny<Exception>(() => DatabaseBackupService.Backup([source, invalidBackupPath]));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
