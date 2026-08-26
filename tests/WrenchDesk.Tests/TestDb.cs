using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WrenchDesk.Data;
using WrenchDesk.Services;

namespace WrenchDesk.Tests;

/// <summary>
/// Spins up a real migrated SQLite database in a throwaway folder. These are not mocks —
/// the point is to prove the actual SQL behaves, especially the pricing views.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly string _dir;

    public Db Db { get; }
    public SettingsStore Settings { get; }
    public CustomerRepo Customers { get; }
    public TicketRepo Tickets { get; }
    public MoneyRepo Money { get; }
    public ScheduleRepo Schedule { get; }
    public BackupService Backups { get; }

    public TestDb()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wrenchdesk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WrenchDesk:DataDirectory"] = _dir })
            .Build();

        Db = new Db(config);
        Db.Migrate();

        Settings = new SettingsStore(Db);
        Customers = new CustomerRepo(Db);
        Tickets = new TicketRepo(Db, Settings);
        Money = new MoneyRepo(Db, Settings);
        Schedule = new ScheduleRepo(Db);
        Backups = new BackupService(Db, Settings, NullLogger<BackupService>.Instance);
    }

    public long NewCustomer(string first = "Dale", string last = "Fenner") =>
        Customers.Insert(new Customer { FirstName = first, LastName = last, Phone = "555-0100" });

    public long NewTicket(long customerId, int taxRateBp = 0, string status = TicketStatus.Estimate) =>
        Tickets.Create(new Ticket { CustomerId = customerId, TaxRateBp = taxRateBp, Status = status });

    public void AddLine(long ticketId, string kind, decimal qty, decimal each, bool taxable)
    {
        var line = new TicketLine
        {
            TicketId = ticketId,
            Kind = kind,
            Description = kind,
            UnitCents = (long)Math.Round(each * 100m, MidpointRounding.AwayFromZero),
            Taxable = taxable
        };
        line.Qty = qty;
        Tickets.AddLine(line);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp dir, fine to leave */ }
    }
}
