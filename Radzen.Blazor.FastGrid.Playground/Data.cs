using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Radzen.Blazor.FastGrid.Playground;

public class Row
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string Department { get; set; } = "";

    public int Age { get; set; }

    public DateTime Hired { get; set; }

    public decimal Salary { get; set; }

    public string Notes { get; set; } = "";

    static readonly string[] Departments = ["Engineering", "Sales", "Ops", "Finance", "Support"];

    public static List<Row> Make(int n) => Enumerable.Range(0, n).Select(i => new Row
    {
        Id = i,
        Name = "Person " + i,
        Department = Departments[i % Departments.Length],
        Age = 20 + (i % 45),
        Hired = new DateTime(2010, 1, 1).AddDays(i),
        Salary = 40000m + (i % 1000) * 37m,
        Notes = "Row " + i + " has a long enough note to truncate when the column is narrowed.",
    }).ToList();
}

public class PlaygroundContext(DbContextOptions<PlaygroundContext> options) : DbContext(options)
{
    public DbSet<Row> Rows => Set<Row>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The ids are the generator's, not the database's. Left to EF they are store-generated, and
        // then a row whose Id is 0 reads as "not set yet" - so seeding a set that starts at zero has
        // EF assign it a key of its own and collide with the row that already holds it.
        modelBuilder.Entity<Row>().Property(r => r.Id).ValueGeneratedNever();
    }
}

/// <summary>
/// A SQLite database held in memory, so the grid can be driven against a real Entity Framework
/// provider rather than a list. The two differ in more than speed: a queryable composes filters and
/// sorts into SQL and the grid awaits them, where a list is filtered and sorted in process.
/// </summary>
public sealed class EfSource : IDisposable
{
    SqliteConnection? connection;
    PlaygroundContext? context;
    int rows = -1;

    /// <summary>
    /// Builds the database if the row count has changed. Call it from an event rather than from a
    /// render: creating a database and seeding it is not something a render should be doing, and an
    /// exception thrown from one takes the circuit down rather than surfacing.
    /// </summary>
    public void Prepare(int count)
    {
        if (rows != count)
        {
            Dispose();

            connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            context = new PlaygroundContext(new DbContextOptionsBuilder<PlaygroundContext>()
                .UseSqlite(connection).Options);

            context.Database.EnsureCreated();
            context.Rows.AddRange(Row.Make(count));
            context.SaveChanges();

            rows = count;
        }
    }

    /// <summary>The rows, as a queryable the grid composes filters and sorts onto.</summary>
    /// <remarks>
    /// AsNoTracking: the grid only reads, and tracking every row would measure the change tracker
    /// rather than the grid.
    /// </remarks>
    public IQueryable<Row> Rows => context!.Rows.AsNoTracking();

    public void Dispose()
    {
        context?.Dispose();
        connection?.Dispose();
        context = null;
        connection = null;
        rows = -1;
    }
}
