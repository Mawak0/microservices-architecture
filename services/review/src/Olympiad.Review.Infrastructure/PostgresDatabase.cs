using Olympiad.Persistence.Postgres;
using Npgsql;
namespace Olympiad.Review.Infrastructure;
public sealed class PostgresDatabase
{
    private static string Schema { get { using var stream = typeof(PostgresDatabase).Assembly.GetManifestResourceStream("Olympiad.Review.Infrastructure.PersistenceSchema.sql")!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); } }

    private readonly PostgresStore store;
    public PostgresDatabase(string connectionString) => store = new(connectionString);
    public NpgsqlConnection Open() => store.Open();
    internal PostgresStore Store => store;

    public void Initialize() => store.InitializeAsync(Schema, default).GetAwaiter().GetResult();

}
