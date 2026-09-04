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

    /// <summary>
    /// An id with no name beside it, which is what a lookup column resolves. Nullable, and null on
    /// every seventh row, so the entry a filter offers for the rows carrying no id has rows to find.
    /// </summary>
    public int? TeamId { get; set; }

    /// <summary>
    /// Several ids per row, for the other cardinality. A primitive collection, which Entity Framework
    /// maps without a conversion and can translate <c>Any</c> over - so the collection column's
    /// expression filter runs against SQLite here rather than in memory.
    /// </summary>
    public List<int> TagIds { get; set; } = [];

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
        TeamId = i % 7 == 0 ? null : Lookups.Teams[i % (Lookups.Teams.Count - 1)].Id,
        TagIds = Lookups.Tags.Where((_, t) => (i / (t + 1)) % 3 == 0).Select(tag => tag.Id).ToList(),
    }).ToList();
}

/// <summary>The far side of a lookup: a row holding a name and the id other rows carry.</summary>
public class Team
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>The same for the collection column, so both have somewhere real to resolve against.</summary>
public class Tag
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>The names the rows' ids stand for, held once.</summary>
public static class Lookups
{
    /// <summary>
    /// The last one is used by no row - which is why it is named for it. The check-box list offers it
    /// anyway, which is the visible difference between a list drawn from the lookup and one scanned
    /// from the data, and the generator below cycles the others deliberately to keep it that way.
    /// </summary>
    public static readonly List<Team> Teams =
    [
        new() { Id = 1, Name = "Platform" },
        new() { Id = 2, Name = "Growth" },
        new() { Id = 3, Name = "Billing" },
        new() { Id = 4, Name = "Research" },
        new() { Id = 5, Name = "Archive (nobody)" },
    ];

    public static readonly List<Tag> Tags =
    [
        new() { Id = 10, Name = "Remote" },
        new() { Id = 20, Name = "Contract" },
        new() { Id = 30, Name = "On call" },
    ];
}

public class PlaygroundContext(DbContextOptions<PlaygroundContext> options) : DbContext(options)
{
    public DbSet<Row> Rows => Set<Row>();

    /// <summary>The lookup's own table, so a Query lookup is a real query rather than a list.</summary>
    public DbSet<Team> Teams => Set<Team>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>().Property(t => t.Id).ValueGeneratedNever();

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
            context.Teams.AddRange(Lookups.Teams.Select(t => new Team { Id = t.Id, Name = t.Name }));
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
    /// <remarks>
    /// A new queryable on every read, deliberately: that is what ordinary application code produces -
    /// a DbSet put through AsNoTracking, or a Where written in markup - and a playground that handed
    /// the grid one stable instance would be testing a shape nobody writes. It is what caught the
    /// render loop the grid used to spin over an asynchronous source under virtualization; leaving it
    /// this way is what would catch that again.
    /// </remarks>
    public IQueryable<Row> Rows => context!.Rows.AsNoTracking();

    /// <summary>
    /// The lookup's table, for the provenance that composes a projection into the provider's own
    /// query. Read the same way the rows are, so a Query lookup here is fetched through the executor
    /// exactly as one in an application would be.
    /// </summary>
    public IQueryable<Team> Teams => context!.Teams.AsNoTracking();

    public void Dispose()
    {
        context?.Dispose();
        connection?.Dispose();
        context = null;
        connection = null;
        rows = -1;
    }
}
