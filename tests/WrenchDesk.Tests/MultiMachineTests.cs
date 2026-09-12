using WrenchDesk.Data;

namespace WrenchDesk.Tests;

/// <summary>
/// A customer bringing two machines at once used to mean two tickets, two totals and two
/// invoices for one visit. These pin the behaviour that replaced that.
/// </summary>
public class MultiMachineTests
{
    [Fact]
    public void Existing_tickets_become_one_machine_tickets_keeping_their_words()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var equipmentId = h.Customers.InsertEquipment(new Equipment
        {
            CustomerId = customerId, Make = "Toro", Model = "22in"
        });

        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId });
        h.Tickets.AddMachine(new TicketEquipment
        {
            TicketId = ticketId,
            EquipmentId = equipmentId,
            Complaint = "Won't start",
            Diagnosis = "Carb rebuilt"
        });

        var machines = h.Tickets.Machines(ticketId);

        Assert.Single(machines);
        Assert.Equal("Won't start", machines[0].Complaint);
        Assert.Equal("Carb rebuilt", machines[0].Diagnosis);
        Assert.Contains("Toro", machines[0].EquipmentName);
    }

    [Fact]
    public void Two_machines_on_one_ticket_each_keep_their_own_complaint()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var mower = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Toro", Model = "22in" });
        var washer = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Honda", Model = "GX390" });

        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId });

        h.Tickets.AddMachine(new TicketEquipment
        {
            TicketId = ticketId, EquipmentId = mower,
            Complaint = "Won't start", Diagnosis = "Carb gummed up"
        });
        h.Tickets.AddMachine(new TicketEquipment
        {
            TicketId = ticketId, EquipmentId = washer,
            Complaint = "No pressure", Diagnosis = "Unloader valve"
        });

        var machines = h.Tickets.Machines(ticketId);

        Assert.Equal(2, machines.Count);
        Assert.Equal("Won't start", machines[0].Complaint);
        Assert.Equal("No pressure", machines[1].Complaint);

        // The whole point: one ticket, so one total and one invoice for the visit.
        Assert.Single(h.Tickets.ForCustomer(customerId));
    }

    [Fact]
    public void The_ticket_list_shows_both_machines_and_how_many()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var mower = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Toro", Model = "22in" });
        var washer = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Honda", Model = "GX390" });

        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId });
        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, EquipmentId = mower, Complaint = "Won't start" });
        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, EquipmentId = washer, Complaint = "No pressure" });

        var row = h.Tickets.ForCustomer(customerId).Single();

        Assert.Equal(2, row.MachineCount);
        Assert.Contains("Toro", row.EquipmentName);
        Assert.Contains("Honda", row.EquipmentName);
        Assert.Contains("Won't start", row.Complaint);
        Assert.Contains("No pressure", row.Complaint);
    }

    [Fact]
    public void One_total_covers_the_whole_visit()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId, TaxRateBp = 0 });

        var mower = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Toro" });
        var washer = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Honda" });

        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, EquipmentId = mower });
        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, EquipmentId = washer });

        h.AddLine(ticketId, "Part", 1, 40m, taxable: false, description: "Carb kit");
        h.AddLine(ticketId, "Part", 1, 112m, taxable: false, description: "Pump kit");

        // The shop asked for one bill rather than two, which is what the customer pays.
        Assert.Equal(15200, h.Tickets.ForCustomer(customerId).Single().TotalCents);
    }

    [Fact]
    public void A_line_can_say_which_machine_it_was_for()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId });

        var mower = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Toro" });

        var line = new TicketLine
        {
            TicketId = ticketId, Kind = "Part", Description = "Carb kit",
            UnitCents = 4000, EquipmentId = mower
        };
        h.Tickets.AddLine(line);

        Assert.Equal(mower, h.Tickets.Lines(ticketId).Single().EquipmentId);
    }

    [Fact]
    public void Taking_a_machine_off_leaves_its_charges_on_the_ticket()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId });

        var mower = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Toro" });
        var machineId = h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, EquipmentId = mower });

        h.Tickets.AddLine(new TicketLine
        {
            TicketId = ticketId, Kind = "Part", Description = "Carb kit",
            UnitCents = 4000, EquipmentId = mower
        });

        h.Tickets.RemoveMachine(machineId);

        // Removing a machine must never quietly reduce what the customer owes.
        var line = h.Tickets.Lines(ticketId).Single();
        Assert.Null(line.EquipmentId);
        Assert.Equal(4000, line.UnitCents);
        Assert.Equal(4000, h.Tickets.ForCustomer(customerId).Single().TotalCents);
    }

    [Fact]
    public void Duplicating_carries_every_machine_and_what_was_said_about_it()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId });

        var mower = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Toro" });
        var washer = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Honda" });

        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, EquipmentId = mower, Complaint = "Won't start" });
        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, EquipmentId = washer, Complaint = "No pressure" });

        var copyId = h.Tickets.Duplicate(ticketId);
        var copies = h.Tickets.Machines(copyId);

        Assert.Equal(2, copies.Count);
        Assert.Equal("Won't start", copies[0].Complaint);
        Assert.Equal("No pressure", copies[1].Complaint);
    }

    [Fact]
    public void Searching_finds_a_ticket_by_either_machines_complaint()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId });

        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, Complaint = "Won't start" });
        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, Complaint = "No pressure, pump noisy" });

        Assert.Single(h.Tickets.Search("pressure", null, openOnly: false));
        Assert.Single(h.Tickets.Search("start", null, openOnly: false));
        Assert.Empty(h.Tickets.Search("gearbox", null, openOnly: false));
    }

    [Fact]
    public void A_machine_still_lists_the_tickets_it_appeared_on()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var mower = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Toro" });

        var first = h.Tickets.Create(new Ticket { CustomerId = customerId });
        h.Tickets.AddMachine(new TicketEquipment { TicketId = first, EquipmentId = mower });

        var second = h.Tickets.Create(new Ticket { CustomerId = customerId });
        h.Tickets.AddMachine(new TicketEquipment { TicketId = second, EquipmentId = mower });

        Assert.Equal(2, h.Tickets.ForEquipment(mower).Count);
    }
}

public class CustomerMergeTests
{
    [Fact]
    public void Merging_moves_everything_onto_the_record_being_kept()
    {
        using var h = new TestDb();

        var keep = h.Customers.Insert(new Customer { FirstName = "Fitzgerald", LastName = "McQueen", Phone = "256-555-0100" });
        var dupe = h.Customers.Insert(new Customer { FirstName = "Fitzgerald", LastName = "McQueen" });

        var equipmentId = h.Customers.InsertEquipment(new Equipment { CustomerId = dupe, Make = "Simpson" });
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = dupe });
        h.Money.Insert(new Payment { CustomerId = dupe, TicketId = ticketId, AmountCents = 4000, PaidOn = "2026-09-08" });
        h.Schedule.Insert(new Appointment { CustomerId = dupe, Kind = "Pickup", ScheduledLocal = "2026-09-09 09:00" });

        Assert.Null(h.Customers.Merge(dupe, keep));

        Assert.Null(h.Customers.Get(dupe));
        Assert.Single(h.Tickets.ForCustomer(keep));
        Assert.Single(h.Customers.EquipmentFor(keep));
        Assert.Single(h.Money.ForCustomer(keep));
        Assert.Single(h.Schedule.ForCustomer(keep));
        Assert.Equal(keep, h.Customers.GetEquipment(equipmentId)!.CustomerId);
    }

    [Fact]
    public void Details_filled_in_on_the_duplicate_are_carried_across()
    {
        using var h = new TestDb();

        // The second record usually got made because someone had the phone number to hand.
        var keep = h.Customers.Insert(new Customer { FirstName = "Fitzgerald", LastName = "McQueen" });
        var dupe = h.Customers.Insert(new Customer
        {
            FirstName = "Fitzgerald", LastName = "McQueen",
            Phone = "256-555-0142", Address1 = "12 Mill Rd", City = "Toney"
        });

        h.Customers.Merge(dupe, keep);

        var survivor = h.Customers.Get(keep)!;
        Assert.Equal("256-555-0142", survivor.Phone);
        Assert.Equal("12 Mill Rd", survivor.Address1);
        Assert.Equal("Toney", survivor.City);
    }

    [Fact]
    public void What_the_record_being_kept_already_had_is_not_overwritten()
    {
        using var h = new TestDb();

        var keep = h.Customers.Insert(new Customer { FirstName = "Fitz", LastName = "McQueen", Phone = "256-555-0100" });
        var dupe = h.Customers.Insert(new Customer { FirstName = "Fitz", LastName = "McQueen", Phone = "256-555-9999" });

        h.Customers.Merge(dupe, keep);

        Assert.Equal("256-555-0100", h.Customers.Get(keep)!.Phone);
    }

    [Fact]
    public void A_duplicate_with_no_work_against_it_can_simply_be_deleted()
    {
        using var h = new TestDb();
        var dupe = h.Customers.Insert(new Customer { FirstName = "Typo", LastName = "Entry" });

        Assert.Null(h.Customers.TryDelete(dupe));
        Assert.Null(h.Customers.Get(dupe));
    }

    [Fact]
    public void Merging_a_record_into_itself_is_refused()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        Assert.NotNull(h.Customers.Merge(customerId, customerId));
        Assert.NotNull(h.Customers.Get(customerId));
    }

    [Fact]
    public void The_preview_says_what_would_move()
    {
        using var h = new TestDb();
        var dupe = h.Customers.Insert(new Customer { FirstName = "Fitz", LastName = "McQueen" });

        h.Customers.InsertEquipment(new Equipment { CustomerId = dupe, Make = "Generac" });
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = dupe });
        h.Money.Insert(new Payment { CustomerId = dupe, TicketId = ticketId, AmountCents = 11200, PaidOn = "2026-09-08" });

        var counts = h.Customers.MergePreview(dupe);

        Assert.Equal(1, counts.Tickets);
        Assert.Equal(1, counts.Equipment);
        Assert.Equal(1, counts.Payments);
        Assert.Equal(0, counts.Appointments);
    }
}

public class SecondBackupDestinationTests
{
    [Fact]
    public void A_scheduled_run_writes_both_copies()
    {
        using var h = new TestDb();
        var second = Path.Combine(Path.GetTempPath(), "wrenchdesk-tests", Guid.NewGuid().ToString("N"));

        h.Settings.Set(SettingsStore.BackupAutoEnabled, "true");
        h.Settings.Set(SettingsStore.BackupTimeOfDay, "06:00");
        h.Settings.Set(SettingsStore.BackupDestination2, second);

        var result = h.Backups.RunScheduledIfDue(new DateTime(2026, 9, 11, 18, 0, 0));

        Assert.NotNull(result);
        Assert.True(result!.Success, result.Error);

        // One drive at the bench and one elsewhere, both written the same night.
        Assert.Single(h.Backups.ListBackups());
        Assert.Single(h.Backups.ListBackups(second));

        Directory.Delete(second, recursive: true);
    }

    [Fact]
    public void An_unplugged_second_drive_does_not_cost_the_first_copy()
    {
        using var h = new TestDb();

        h.Settings.Set(SettingsStore.BackupAutoEnabled, "true");
        h.Settings.Set(SettingsStore.BackupTimeOfDay, "06:00");
        h.Settings.Set(SettingsStore.BackupDestination2, "Z:\\definitely-not-a-real-drive");

        var result = h.Backups.RunScheduledIfDue(new DateTime(2026, 9, 11, 18, 0, 0));

        Assert.NotNull(result);
        Assert.True(result!.Success, result.Error);
        Assert.Single(h.Backups.ListBackups());

        // The run still counts as done, and the failure is reported rather than swallowed.
        Assert.NotNull(h.Settings.GetTimestamp(SettingsStore.BackupLastRun));
        Assert.Contains("second copy failed", h.Settings.Get(SettingsStore.BackupLastError));
    }

    [Fact]
    public void With_no_second_place_set_nothing_changes()
    {
        using var h = new TestDb();

        h.Settings.Set(SettingsStore.BackupAutoEnabled, "true");
        h.Settings.Set(SettingsStore.BackupTimeOfDay, "06:00");

        var result = h.Backups.RunScheduledIfDue(new DateTime(2026, 9, 11, 18, 0, 0));

        Assert.True(result!.Success, result.Error);
        Assert.False(h.Backups.HasSecondDestination);
        Assert.Equal("", h.Settings.Get(SettingsStore.BackupLastError));
    }
}
