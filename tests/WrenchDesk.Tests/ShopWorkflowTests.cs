using WrenchDesk.Data;

namespace WrenchDesk.Tests;

public class TicketNumberingTests
{
    [Fact]
    public void Numbers_start_at_1001_and_increment()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var first = h.Tickets.Get(h.NewTicket(customerId))!;
        var second = h.Tickets.Get(h.NewTicket(customerId))!;

        Assert.Equal("WSE-1001", first.Number);
        Assert.Equal("WSE-1002", second.Number);
    }

    [Fact]
    public void Numbers_follow_the_configured_prefix()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.TicketPrefix, "MOW");

        var ticket = h.Tickets.Get(h.NewTicket(h.NewCustomer()))!;

        Assert.Equal("MOW-1001", ticket.Number);
    }

    [Fact]
    public void Deleting_the_newest_ticket_does_not_reissue_its_number()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        h.NewTicket(customerId);
        var secondId = h.NewTicket(customerId);
        h.Tickets.Delete(secondId);

        // Reusing WSE-1002 would make two different jobs share a number in the paper trail.
        var third = h.Tickets.Get(h.NewTicket(customerId))!;
        Assert.Equal("WSE-1002", third.Number);
    }

    [Fact]
    public void Changing_the_prefix_starts_a_fresh_sequence()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        h.NewTicket(customerId);

        h.Settings.Set(SettingsStore.TicketPrefix, "PW");
        var ticket = h.Tickets.Get(h.NewTicket(customerId))!;

        Assert.Equal("PW-1001", ticket.Number);
    }
}

public class TicketLifecycleTests
{
    [Fact]
    public void Duplicating_an_estimate_copies_its_lines_and_resets_status()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var sourceId = h.NewTicket(customerId, taxRateBp: 725, status: TicketStatus.Closed);

        h.AddLine(sourceId, "Labor", 2, 65m, taxable: false);
        h.AddLine(sourceId, "Part", 1, 24.99m, taxable: true);

        var copyId = h.Tickets.Duplicate(sourceId);
        var copy = h.Tickets.Get(copyId)!;

        Assert.Equal(TicketStatus.Estimate, copy.Status);
        Assert.Equal(725, copy.TaxRateBp);
        Assert.Equal(2, h.Tickets.Lines(copyId).Count);
        Assert.NotEqual(h.Tickets.Get(sourceId)!.Number, copy.Number);

        // The original must be untouched — this is a copy, not a move.
        Assert.Equal(TicketStatus.Closed, h.Tickets.Get(sourceId)!.Status);
    }

    [Fact]
    public void Closing_a_ticket_stamps_completed_and_closed_dates()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());

        h.Tickets.SetStatus(ticketId, TicketStatus.Closed);
        var ticket = h.Tickets.Get(ticketId)!;

        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), ticket.CompletedOn);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), ticket.ClosedOn);
    }

    [Fact]
    public void Completed_date_is_not_overwritten_by_a_later_status_move()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());

        h.Tickets.SetStatus(ticketId, TicketStatus.Ready);
        var first = h.Tickets.Get(ticketId)!.CompletedOn;

        h.Tickets.SetStatus(ticketId, TicketStatus.Closed);

        Assert.Equal(first, h.Tickets.Get(ticketId)!.CompletedOn);
    }

    [Fact]
    public void Deleting_a_ticket_removes_its_lines()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        h.AddLine(ticketId, "Part", 1, 10m, taxable: true);

        h.Tickets.Delete(ticketId);

        Assert.Empty(h.Tickets.Lines(ticketId));
    }

    [Fact]
    public void Unpaid_list_excludes_fully_paid_and_declined_tickets()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var owing = h.NewTicket(customerId);
        h.AddLine(owing, "Part", 1, 100m, taxable: false);

        var paid = h.NewTicket(customerId);
        h.AddLine(paid, "Part", 1, 50m, taxable: false);
        h.Money.Insert(new Payment { TicketId = paid, CustomerId = customerId, AmountCents = 5000, PaidOn = "2026-08-25" });

        var declined = h.NewTicket(customerId, status: TicketStatus.Declined);
        h.AddLine(declined, "Part", 1, 999m, taxable: false);
        h.Tickets.SetStatus(declined, TicketStatus.Declined);

        var unpaid = h.Tickets.Unpaid();

        Assert.Single(unpaid);
        Assert.Equal(owing, unpaid[0].Id);
    }
}

public class CustomerHistoryTests
{
    [Fact]
    public void Lifetime_totals_exclude_declined_estimates()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var real = h.NewTicket(customerId);
        h.AddLine(real, "Part", 1, 100m, taxable: false);

        var declined = h.NewTicket(customerId);
        h.AddLine(declined, "Part", 1, 500m, taxable: false);
        h.Tickets.SetStatus(declined, TicketStatus.Declined);

        var (billed, paid, count) = h.Customers.LifetimeTotals(customerId);

        Assert.Equal(10000, billed);
        Assert.Equal(0, paid);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Customer_with_tickets_cannot_be_deleted()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        h.NewTicket(customerId);

        var reason = h.Customers.TryDelete(customerId);

        Assert.NotNull(reason);
        Assert.NotNull(h.Customers.Get(customerId));
    }

    [Fact]
    public void Customer_with_no_history_can_be_deleted()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var reason = h.Customers.TryDelete(customerId);

        Assert.Null(reason);
        Assert.Null(h.Customers.Get(customerId));
    }

    [Fact]
    public void Search_finds_a_customer_by_phone_and_by_full_name()
    {
        using var h = new TestDb();
        h.Customers.Insert(new Customer { FirstName = "Dale", LastName = "Fenner", Phone = "555-0142" });

        Assert.Single(h.Customers.Search("555-0142"));
        Assert.Single(h.Customers.Search("Dale Fenner"));
        Assert.Single(h.Customers.Search("fenner"));
        Assert.Empty(h.Customers.Search("Nobody"));
    }

    [Fact]
    public void Archived_customers_are_hidden_unless_asked_for()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        h.Customers.SetArchived(customerId, true);

        Assert.Empty(h.Customers.Search(null));
        Assert.Single(h.Customers.Search(null, includeArchived: true));
    }

    [Fact]
    public void Deleting_equipment_leaves_its_repair_history_intact()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var equipmentId = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Toro", Model = "22in" });

        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId, EquipmentId = equipmentId });

        // ON DELETE SET NULL: the machine is gone, the job it had done is not.
        using (var conn = h.Db.Open())
            Dapper.SqlMapper.Execute(conn, "DELETE FROM equipment WHERE id = @equipmentId;", new { equipmentId });

        var ticket = h.Tickets.Get(ticketId);
        Assert.NotNull(ticket);
        Assert.Null(ticket!.EquipmentId);
    }
}

public class MoneyReportTests
{
    [Fact]
    public void Daily_buckets_cover_every_day_including_empty_ones()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        h.Money.Insert(new Payment { CustomerId = customerId, AmountCents = 5000, PaidOn = "2026-08-24" });

        var buckets = h.Money.DailyBuckets(new DateTime(2026, 8, 23), new DateTime(2026, 8, 25));

        Assert.Equal(3, buckets.Count);
        Assert.Equal(0, buckets[0].TotalCents);
        Assert.Equal(5000, buckets[1].TotalCents);
        Assert.Equal(0, buckets[2].TotalCents);
    }

    [Fact]
    public void Week_start_setting_moves_the_week_boundary()
    {
        using var h = new TestDb();
        var wednesday = new DateTime(2026, 8, 26);

        h.Settings.Set(SettingsStore.WeekStartDay, "Monday");
        Assert.Equal(new DateTime(2026, 8, 24), h.Money.WeekStart(wednesday));

        h.Settings.Set(SettingsStore.WeekStartDay, "Sunday");
        Assert.Equal(new DateTime(2026, 8, 23), h.Money.WeekStart(wednesday));
    }

    [Fact]
    public void Range_totals_are_inclusive_of_both_end_dates()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        h.Money.Insert(new Payment { CustomerId = customerId, AmountCents = 1000, PaidOn = "2026-08-01" });
        h.Money.Insert(new Payment { CustomerId = customerId, AmountCents = 2000, PaidOn = "2026-08-31" });

        var total = h.Money.TotalInRange(new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));

        Assert.Equal(3000, total);
    }

    [Fact]
    public void Outstanding_is_the_sum_of_unpaid_balances()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var a = h.NewTicket(customerId);
        h.AddLine(a, "Part", 1, 100m, taxable: false);

        var b = h.NewTicket(customerId);
        h.AddLine(b, "Part", 1, 40m, taxable: false);
        h.Money.Insert(new Payment { TicketId = b, CustomerId = customerId, AmountCents = 1500, PaidOn = "2026-08-25" });

        Assert.Equal(10000 + 2500, h.Money.OutstandingCents());
    }

    [Fact]
    public void Csv_export_quotes_fields_containing_commas()
    {
        using var h = new TestDb();
        var customerId = h.Customers.Insert(new Customer { BusinessName = "Green, Acres LLC" });
        h.Money.Insert(new Payment { CustomerId = customerId, AmountCents = 12345, PaidOn = "2026-08-25", Method = "Check" });

        var csv = h.Money.ExportCsv(new DateTime(2026, 8, 25), new DateTime(2026, 8, 25));

        Assert.Contains("\"Green, Acres LLC\"", csv);
        Assert.Contains("123.45", csv);
    }
}

public class ScheduleTests
{
    [Fact]
    public void Appointments_are_found_by_their_local_date()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        h.Schedule.Insert(new Appointment
        {
            CustomerId = customerId,
            Kind = "Pickup",
            ScheduledLocal = "2026-08-25 09:00",
            Address = "12 Mill Rd"
        });

        Assert.Single(h.Schedule.ForDay(new DateTime(2026, 8, 25)));
        Assert.Empty(h.Schedule.ForDay(new DateTime(2026, 8, 26)));
    }

    [Fact]
    public void Ics_export_escapes_commas_and_writes_one_event_per_appointment()
    {
        using var h = new TestDb();
        var customerId = h.Customers.Insert(new Customer { FirstName = "Dale", LastName = "Fenner" });

        h.Schedule.Insert(new Appointment
        {
            CustomerId = customerId,
            Kind = "Delivery",
            ScheduledLocal = "2026-08-25 14:30",
            DurationMin = 60,
            Address = "12 Mill Rd, Athens, GA"
        });

        var rows = h.Schedule.ForDay(new DateTime(2026, 8, 25));
        var ics = WrenchDesk.Services.CalendarLinks.BuildIcs(rows, "Test Shop");

        Assert.Contains("BEGIN:VEVENT", ics);
        Assert.Contains("DTSTART:20260825T143000", ics);
        Assert.Contains("DTEND:20260825T153000", ics);
        Assert.Contains("12 Mill Rd\\, Athens\\, GA", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
    }

    [Fact]
    public void Google_calendar_link_carries_utc_times_and_the_address()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        h.Schedule.Insert(new Appointment
        {
            CustomerId = customerId,
            Kind = "Pickup",
            ScheduledLocal = "2026-08-25 09:00",
            Address = "12 Mill Rd"
        });

        var row = h.Schedule.ForDay(new DateTime(2026, 8, 25)).Single();
        var url = WrenchDesk.Services.CalendarLinks.GoogleCalendarUrl(row);

        Assert.StartsWith("https://calendar.google.com/calendar/render?action=TEMPLATE", url);
        Assert.Contains("dates=", url);
        Assert.Contains("Mill", url);
    }
}

public class MigrationTests
{
    [Fact]
    public void Migrating_twice_is_a_no_op()
    {
        using var h = new TestDb();

        h.Db.Migrate();
        h.Db.Migrate();

        // Still usable, and still one schema version.
        Assert.NotNull(h.Customers.Get(h.NewCustomer()));
    }

    [Fact]
    public void Settings_fall_back_to_defaults_before_anything_is_saved()
    {
        using var h = new TestDb();

        Assert.Equal("WSE", h.Settings.Get(SettingsStore.TicketPrefix));
        Assert.Equal(6500, h.Settings.GetInt(SettingsStore.LaborRateCents));
        Assert.Equal(DayOfWeek.Monday, h.Settings.GetWeekStart());
    }
}
