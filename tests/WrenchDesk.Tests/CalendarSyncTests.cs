using WrenchDesk.Data;
using WrenchDesk.Services.Google;

namespace WrenchDesk.Tests;

public class AppointmentEventMapperTests
{
    [Theory]
    [InlineData("Pickup — Dale Fenner (WD-1042)", "Pickup")]
    [InlineData("Delivery — Green Acres", "Delivery")]
    [InlineData("Drop-off — Someone", "Drop-off")]
    public void Kind_is_read_back_from_the_event_title(string summary, string expected)
    {
        Assert.Equal(expected, AppointmentEventMapper.ParseKind(summary));
    }

    [Theory]
    [InlineData("Dentist")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Lunch — with Rita")]
    public void An_unrecognised_title_yields_no_kind(string? summary)
    {
        Assert.Null(AppointmentEventMapper.ParseKind(summary));
    }

    [Fact]
    public void Newer_google_edit_wins_a_conflict()
    {
        var older = new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc).ToString("O");
        var newer = new DateTime(2026, 8, 25, 11, 0, 0, DateTimeKind.Utc).ToString("O");

        Assert.True(AppointmentEventMapper.GoogleWins(localUpdatedUtc: older, googleUpdated: newer));
        Assert.False(AppointmentEventMapper.GoogleWins(localUpdatedUtc: newer, googleUpdated: older));
    }

    [Fact]
    public void A_tie_goes_to_the_shop()
    {
        var same = new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc).ToString("O");

        // The person standing at the counter is looking at the local copy.
        Assert.False(AppointmentEventMapper.GoogleWins(same, same));
    }

    [Fact]
    public void Applying_an_event_moves_time_duration_and_address()
    {
        var appointment = new Appointment
        {
            Kind = "Pickup",
            ScheduledLocal = "2026-08-25 09:00",
            DurationMin = 60,
            Address = "12 Mill Creek Rd"
        };

        var ev = new CalendarEventData
        {
            Summary = "Delivery — Dale Fenner",
            Start = new DateTime(2026, 8, 26, 14, 30, 0),
            End = new DateTime(2026, 8, 26, 16, 0, 0),
            Location = "88 Industrial Way"
        };

        Assert.True(AppointmentEventMapper.ApplyToExisting(appointment, ev));
        Assert.Equal("2026-08-26 14:30", appointment.ScheduledLocal);
        Assert.Equal(90, appointment.DurationMin);
        Assert.Equal("88 Industrial Way", appointment.Address);
        Assert.Equal("Delivery", appointment.Kind);
    }

    [Fact]
    public void Applying_an_identical_event_reports_no_change()
    {
        var appointment = new Appointment
        {
            Kind = "Pickup",
            ScheduledLocal = "2026-08-25 09:00",
            DurationMin = 60,
            Address = "12 Mill Creek Rd"
        };

        var ev = new CalendarEventData
        {
            Summary = "Pickup — Dale Fenner",
            Start = new DateTime(2026, 8, 25, 9, 0, 0),
            End = new DateTime(2026, 8, 25, 10, 0, 0),
            Location = "12 Mill Creek Rd"
        };

        Assert.False(AppointmentEventMapper.ApplyToExisting(appointment, ev));
    }

    [Fact]
    public void A_foreign_event_becomes_an_appointment_with_no_customer()
    {
        var ev = new CalendarEventData
        {
            Id = "abc",
            Summary = "Pick up mower from Hank",
            Start = new DateTime(2026, 8, 27, 8, 0, 0),
            End = new DateTime(2026, 8, 27, 9, 0, 0),
            Location = "5 Oak St"
        };

        var appointment = AppointmentEventMapper.ToNewAppointment(ev);

        Assert.Null(appointment.CustomerId);
        Assert.Equal("Other", appointment.Kind);
        Assert.Equal("2026-08-27 08:00", appointment.ScheduledLocal);
        Assert.Equal("5 Oak St", appointment.Address);
        Assert.Equal("abc", appointment.GoogleEventId);

        // The title is where the meaning is, so it must not be dropped.
        Assert.Contains("Pick up mower from Hank", appointment.Notes);
    }
}

public class CalendarSyncTests
{
    private const string Cal = "shop-cal";

    [Fact]
    public async Task A_new_appointment_is_pushed_to_google()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();

        var customerId = h.NewCustomer("Dale", "Fenner");
        var apptId = h.NewAppointment(customerId, "Pickup", "2026-08-25 09:00");

        var report = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.True(report.Success, report.Error);
        Assert.Equal(1, report.Pushed);
        Assert.Single(api.Events);

        var pushed = api.Events.Single();
        Assert.Contains("Pickup", pushed.Summary);
        Assert.Contains("Dale Fenner", pushed.Summary);
        Assert.Equal(new DateTime(2026, 8, 25, 9, 0, 0), pushed.Start);
        Assert.Equal(apptId, pushed.WrenchDeskAppointmentId);
    }

    [Fact]
    public async Task Syncing_twice_with_no_changes_does_nothing_the_second_time()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        h.NewAppointment(h.NewCustomer());

        await h.CalendarSync.SyncAsync(api, Cal);
        var second = await h.CalendarSync.SyncAsync(api, Cal);

        // The echo of our own push must not look like a change, or the two sides would
        // bounce edits off each other forever.
        Assert.Equal(0, second.TotalChanges);
        Assert.Single(api.Events);
        Assert.Equal(1, api.CreateCount);
    }

    [Fact]
    public async Task Editing_locally_updates_the_google_event()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        var apptId = h.NewAppointment(h.NewCustomer());

        await h.CalendarSync.SyncAsync(api, Cal);

        var appointment = h.Schedule.Get(apptId)!;
        appointment.ScheduledLocal = "2026-08-26 15:00";
        appointment.Address = "New address";
        h.Schedule.Update(appointment);

        var report = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal(1, report.Updated);
        var ev = api.Events.Single();
        Assert.Equal(new DateTime(2026, 8, 26, 15, 0, 0), ev.Start);
        Assert.Equal("New address", ev.Location);
    }

    [Fact]
    public async Task Deleting_locally_removes_the_google_event()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        var apptId = h.NewAppointment(h.NewCustomer());

        await h.CalendarSync.SyncAsync(api, Cal);
        Assert.Single(api.Events);

        h.Schedule.Delete(apptId);
        var report = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal(1, report.DeletedInGoogle);
        Assert.Empty(api.Events);
        Assert.Empty(h.Schedule.Tombstones());
    }

    [Fact]
    public async Task An_event_deleted_in_google_but_still_live_here_is_recreated()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        var apptId = h.NewAppointment(h.NewCustomer());

        await h.CalendarSync.SyncAsync(api, Cal);
        var originalId = api.Events.Single().Id;

        // Vanishes without a cancellation record — the shop record is the one with the customer on it.
        api.RemoveEventEntirely(originalId);

        var appointment = h.Schedule.Get(apptId)!;
        appointment.Notes = "Call ahead";
        h.Schedule.Update(appointment);

        var report = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal(1, report.Pushed);
        Assert.Single(api.Events);
        Assert.NotEqual(originalId, h.Schedule.Get(apptId)!.GoogleEventId);
    }

    [Fact]
    public async Task Moving_an_event_in_google_moves_the_appointment()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        var apptId = h.NewAppointment(h.NewCustomer(), "Pickup", "2026-08-25 09:00");

        await h.CalendarSync.SyncAsync(api, Cal);
        var eventId = api.Events.Single().Id;

        api.MoveEvent(eventId, new DateTime(2026, 8, 27, 13, 15, 0), minutes: 120);

        var report = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal(1, report.PulledUpdates);
        var appointment = h.Schedule.Get(apptId)!;
        Assert.Equal("2026-08-27 13:15", appointment.ScheduledLocal);
        Assert.Equal(120, appointment.DurationMin);
    }

    [Fact]
    public async Task A_pulled_change_is_not_pushed_straight_back()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        h.NewAppointment(h.NewCustomer());

        await h.CalendarSync.SyncAsync(api, Cal);
        api.MoveEvent(api.Events.Single().Id, new DateTime(2026, 8, 27, 13, 0, 0));
        await h.CalendarSync.SyncAsync(api, Cal);

        var updatesBefore = api.UpdateCount;
        var third = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal(0, third.TotalChanges);
        Assert.Equal(updatesBefore, api.UpdateCount);
    }

    [Fact]
    public async Task Cancelling_in_google_removes_the_appointment()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        var apptId = h.NewAppointment(h.NewCustomer());

        await h.CalendarSync.SyncAsync(api, Cal);
        api.CancelEvent(api.Events.Single().Id);

        var report = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal(1, report.DeletedLocally);
        Assert.Null(h.Schedule.Get(apptId));

        // No tombstone: Google already knows it is gone, so we must not try to delete it again.
        Assert.Empty(h.Schedule.Tombstones());
    }

    [Fact]
    public async Task An_event_created_in_google_becomes_an_appointment()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();

        api.SeedForeignEvent("Delivery — Hank Mabry", new DateTime(2026, 8, 28, 10, 0, 0),
            minutes: 90, location: "5 Oak St");

        var report = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal(1, report.Imported);

        var imported = h.Schedule.InRange(new DateTime(2026, 8, 28), new DateTime(2026, 8, 28)).Single();
        Assert.Equal("Delivery", imported.Kind);
        Assert.Equal(90, imported.DurationMin);
        Assert.Equal("5 Oak St", imported.Address);
        Assert.Null(imported.CustomerId);
    }

    [Fact]
    public async Task An_imported_event_is_not_duplicated_on_later_syncs()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        api.SeedForeignEvent("Pickup — Someone", new DateTime(2026, 8, 28, 10, 0, 0));

        await h.CalendarSync.SyncAsync(api, Cal);
        var second = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal(0, second.Imported);
        Assert.Single(h.Schedule.InRange(new DateTime(2026, 8, 28), new DateTime(2026, 8, 28)));
    }

    [Fact]
    public async Task All_day_entries_are_left_alone()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();

        api.SeedForeignEvent("Shop closed - vacation", new DateTime(2026, 8, 28), allDay: true);

        var report = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal(1, report.SkippedAllDay);
        Assert.Equal(0, report.Imported);
        Assert.Empty(h.Schedule.InRange(new DateTime(2026, 8, 28), new DateTime(2026, 8, 28)));
    }

    [Fact]
    public async Task When_both_sides_change_the_newer_edit_wins()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        var apptId = h.NewAppointment(h.NewCustomer(), "Pickup", "2026-08-25 09:00");

        await h.CalendarSync.SyncAsync(api, Cal);
        var eventId = api.Events.Single().Id;

        // Google moves it, then the shop moves it — the shop edit is the later one.
        api.MoveEvent(eventId, new DateTime(2026, 8, 26, 8, 0, 0));

        var appointment = h.Schedule.Get(apptId)!;
        appointment.ScheduledLocal = "2026-08-29 16:00";
        h.Schedule.Update(appointment);

        await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal("2026-08-29 16:00", h.Schedule.Get(apptId)!.ScheduledLocal);
        Assert.Equal(new DateTime(2026, 8, 29, 16, 0, 0), api.Events.Single().Start);
    }

    [Fact]
    public async Task An_expired_sync_token_falls_back_to_a_full_read()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        h.NewAppointment(h.NewCustomer());

        await h.CalendarSync.SyncAsync(api, Cal);
        Assert.NotEmpty(h.Settings.Get(SettingsStore.GoogleSyncToken));

        api.ExpireNextSyncToken = true;
        api.SeedForeignEvent("Pickup — Later", new DateTime(2026, 8, 29, 11, 0, 0));

        var report = await h.CalendarSync.SyncAsync(api, Cal);

        // Recovers rather than failing, and still finds the new event.
        Assert.True(report.Success, report.Error);
        Assert.Equal(1, report.Imported);
    }

    [Fact]
    public async Task Lost_authorisation_is_reported_as_needing_a_reconnect()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi { ThrowAuthError = true };
        h.NewAppointment(h.NewCustomer());

        var report = await h.CalendarSync.SyncAsync(api, Cal);

        Assert.False(report.Success);
        Assert.True(report.NeedsReconnect);
        Assert.True(h.Settings.GetBool(SettingsStore.GoogleNeedsReconnect));
        Assert.NotEmpty(h.Settings.Get(SettingsStore.GoogleLastError));
    }

    [Fact]
    public async Task Switching_calendars_clears_every_link()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        var apptId = h.NewAppointment(h.NewCustomer());

        await h.CalendarSync.SyncAsync(api, Cal);
        Assert.NotEmpty(h.Schedule.Get(apptId)!.GoogleEventId);

        h.Schedule.ClearAllGoogleLinks();

        var appointment = h.Schedule.Get(apptId)!;
        Assert.Empty(appointment.GoogleEventId);
        Assert.Empty(appointment.GoogleUpdated);
        Assert.True(appointment.HasLocalChanges);
    }

    [Fact]
    public async Task A_renamed_event_changes_the_kind()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        var apptId = h.NewAppointment(h.NewCustomer(), "Pickup");

        await h.CalendarSync.SyncAsync(api, Cal);
        api.RenameEvent(api.Events.Single().Id, "Delivery — Dale Fenner");

        await h.CalendarSync.SyncAsync(api, Cal);

        Assert.Equal("Delivery", h.Schedule.Get(apptId)!.Kind);
    }

    [Fact]
    public async Task The_report_reads_plainly()
    {
        using var h = new TestDb();
        var api = new FakeCalendarApi();
        h.NewAppointment(h.NewCustomer());

        var first = await h.CalendarSync.SyncAsync(api, Cal);
        Assert.Equal("1 added to Google", first.Describe());

        var second = await h.CalendarSync.SyncAsync(api, Cal);
        Assert.Equal("Already up to date", second.Describe());
    }
}
