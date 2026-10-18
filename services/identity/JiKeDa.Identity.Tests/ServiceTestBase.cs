using JiKeDa.Identity.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JiKeDa.Identity.Tests;

/// <summary>
/// Base class for Identity service unit tests. Each test runs against a fresh
/// SQLite in-memory database (seeded with the demo accounts) and does not require PostgreSQL.
/// </summary>
public abstract class ServiceTestBase
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<IdentityDbContext> _options = null!;

    [SetUp]
    public void SetUpDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateContext();
        context.Database.EnsureCreated(); // builds schema + HasData seed accounts
    }

    [TearDown]
    public void TearDownDatabase() => _connection.Dispose();

    protected IdentityDbContext CreateContext() => new(_options);
}
