namespace WrenchDesk.Data;

/// <summary>Money is stored and passed around as whole cents to keep totals exact.</summary>
public static class Money
{
    private static readonly System.Globalization.CultureInfo Us =
        System.Globalization.CultureInfo.GetCultureInfo("en-US");

    public static string Fmt(long cents) => (cents / 100m).ToString("C", Us);

    /// <summary>Parses loose input like "45", "$45.00", "1,299.99". Returns false on junk.</summary>
    public static bool TryParse(string? text, out long cents)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = text.Replace("$", "").Replace(",", "").Trim();
        if (!decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) return false;
        cents = (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
        return true;
    }
}

public class Customer
{
    public long Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string BusinessName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string PhoneAlt { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address1 { get; set; } = "";
    public string Address2 { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Zip { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool IsArchived { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";

    /// <summary>Business name wins when present, since that is how commercial accounts get referred to.</summary>
    public string DisplayName
    {
        get
        {
            var person = $"{FirstName} {LastName}".Trim();
            if (!string.IsNullOrWhiteSpace(BusinessName))
                return string.IsNullOrWhiteSpace(person) ? BusinessName : $"{BusinessName} ({person})";
            return string.IsNullOrWhiteSpace(person) ? $"Customer #{Id}" : person;
        }
    }

    public string SortName =>
        !string.IsNullOrWhiteSpace(LastName) ? $"{LastName}, {FirstName}".Trim().TrimEnd(',')
        : !string.IsNullOrWhiteSpace(BusinessName) ? BusinessName
        : FirstName;

    public string OneLineAddress
    {
        get
        {
            var stateZip = string.Join(" ", new[] { State, Zip }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var parts = new[] { Address1, Address2, City, stateZip };
            return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    public bool HasAddress => !string.IsNullOrWhiteSpace(Address1) || !string.IsNullOrWhiteSpace(City);
}

public class Equipment
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public string Category { get; set; } = "Mower";
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string EngineMake { get; set; } = "";
    public string EngineModel { get; set; } = "";
    public string EngineSerial { get; set; } = "";
    public string Year { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool IsArchived { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";

    public static readonly string[] Categories =
    {
        "Mower", "Riding Mower", "Zero-Turn", "Pressure Washer", "Tiller",
        "Generator", "Chainsaw", "Trimmer/Weedeater", "Blower", "Edger",
        "Log Splitter", "ATV/UTV", "Other"
    };

    public string DisplayName
    {
        get
        {
            var core = string.Join(" ", new[] { Year, Make, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(core) ? Category : $"{core} ({Category})";
        }
    }
}

public class Ticket
{
    public long Id { get; set; }
    public string Number { get; set; } = "";
    public long CustomerId { get; set; }
    public long? EquipmentId { get; set; }
    public string Status { get; set; } = TicketStatus.Estimate;
    public string Complaint { get; set; } = "";
    public string Diagnosis { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>Tax rate in basis points: 725 == 7.25%. Snapshotted per ticket so old records keep their rate.</summary>
    public int TaxRateBp { get; set; }

    public string IntakeOn { get; set; } = "";
    public string? PromisedOn { get; set; }
    public string? CompletedOn { get; set; }
    public string? ClosedOn { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

/// <summary>
/// One machine on a ticket. A visit where somebody drops off a mower and a pressure washer is one
/// ticket with two of these — each keeping its own complaint and its own record of the work, so the
/// invoice still reads machine by machine.
/// </summary>
public class TicketEquipment
{
    public long Id { get; set; }
    public long TicketId { get; set; }
    public long? EquipmentId { get; set; }
    public string Complaint { get; set; } = "";
    public string Diagnosis { get; set; } = "";
    public int SortOrder { get; set; }

    /// <summary>
    /// Where the machine is standing - bay 3, back row, second shelf. Whatever the shop calls
    /// its own places; the point is being able to walk straight to it in July.
    /// </summary>
    public string Location { get; set; } = "";

    /// <summary>Filled in by the joined query for display; not stored on this row.</summary>
    public string EquipmentName { get; set; } = "";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(EquipmentName) ? "Machine not specified" : EquipmentName;
}

/// <summary>
/// One photo of a machine or of the work. The image itself is a file in the Photos folder beside
/// the database; this is only the record of what it belongs to.
/// </summary>
public class Photo
{
    public long Id { get; set; }
    public long TicketId { get; set; }

    /// <summary>Which machine it is of, on a ticket covering more than one.</summary>
    public long? EquipmentId { get; set; }

    /// <summary>Name of the file in the Photos folder. Generated, never anything a person typed.</summary>
    public string FileName { get; set; } = "";

    public string Caption { get; set; } = "";
    public string CreatedUtc { get; set; } = "";

    /// <summary>Where the browser fetches it from.</summary>
    public string Url => $"/photos/{FileName}";

    public DateTime? TakenOn =>
        DateTime.TryParse(CreatedUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt.ToLocalTime() : null;
}

/// <summary>A part on order, with enough of its ticket attached to be acted on from one list.</summary>
public class PartOnOrderRow
{
    public long LineId { get; set; }
    public long TicketId { get; set; }
    public string TicketNumber { get; set; } = "";
    public string TicketStatus { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string EquipmentName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Supplier { get; set; } = "";
    public string? OrderedOn { get; set; }
    public string? ExpectedOn { get; set; }
    public string Location { get; set; } = "";

    public int DaysWaiting =>
        DateOnly.TryParse(OrderedOn, System.Globalization.CultureInfo.InvariantCulture, out var from)
            ? Math.Max(0, DateOnly.FromDateTime(DateTime.Today).DayNumber - from.DayNumber)
            : 0;

    public bool IsOverdue =>
        DateOnly.TryParse(ExpectedOn, System.Globalization.CultureInfo.InvariantCulture, out var due)
        && due < DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Nothing was promised, so nobody can tell whether it is late. Worth chasing too.</summary>
    public bool NoDatePromised => string.IsNullOrWhiteSpace(ExpectedOn);
}

public static class TicketStatus
{
    public const string Estimate = "Estimate";
    public const string Approved = "Approved";
    public const string InProgress = "In Progress";
    public const string WaitingParts = "Waiting on Parts";
    public const string Ready = "Ready for Pickup";
    public const string Closed = "Closed";
    public const string Declined = "Declined";

    public static readonly string[] All = { Estimate, Approved, InProgress, WaitingParts, Ready, Closed, Declined };

    /// <summary>Statuses that still need shop attention — drives the dashboard board and the default ticket filter.</summary>
    public static readonly string[] Open = { Estimate, Approved, InProgress, WaitingParts, Ready };

    public static bool IsOpen(string status) => Open.Contains(status);

    public static string Badge(string status) => status switch
    {
        Estimate => "badge-estimate",
        Approved => "badge-approved",
        InProgress => "badge-progress",
        WaitingParts => "badge-waiting",
        Ready => "badge-ready",
        Closed => "badge-closed",
        Declined => "badge-declined",
        _ => "badge-closed"
    };
}

public class TicketLine
{
    public long Id { get; set; }
    public long TicketId { get; set; }
    public int SortOrder { get; set; }
    public string Kind { get; set; } = "Part";
    public string Description { get; set; } = "";

    /// <summary>Quantity times 1000, so 1.5 labor hours stores as 1500 with no floating point drift.</summary>
    public long QtyMilli { get; set; } = 1000;

    public long UnitCents { get; set; }
    public bool Taxable { get; set; } = true;

    /// <summary>Which machine this was for, on a ticket covering more than one. Null means the ticket as a whole.</summary>
    public long? EquipmentId { get; set; }

    /// <summary>Who the part was ordered from. Free text - shops order from whoever has it.</summary>
    public string Supplier { get; set; } = "";

    /// <summary>Set when the part was ordered. Until then this is just a line on an estimate.</summary>
    public string? OrderedOn { get; set; }

    /// <summary>When the supplier said it would come. Blank is allowed; plenty of them will not say.</summary>
    public string? ExpectedOn { get; set; }

    /// <summary>Set when it turned up. That is what takes the line off the parts board.</summary>
    public string? ArrivedOn { get; set; }

    /// <summary>Ordered and not here yet - the thing actually holding the machine up.</summary>
    public bool IsOnOrder =>
        !string.IsNullOrWhiteSpace(OrderedOn) && string.IsNullOrWhiteSpace(ArrivedOn);

    /// <summary>On order and past the day it was promised for. Worth a phone call to the supplier.</summary>
    public bool IsOverdue =>
        IsOnOrder
        && DateOnly.TryParse(ExpectedOn, System.Globalization.CultureInfo.InvariantCulture, out var due)
        && due < DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Days waited so far, for the parts board to sort and colour by.</summary>
    public int DaysWaiting =>
        IsOnOrder
        && DateOnly.TryParse(OrderedOn, System.Globalization.CultureInfo.InvariantCulture, out var from)
            ? Math.Max(0, DateOnly.FromDateTime(DateTime.Today).DayNumber - from.DayNumber)
            : 0;

    public static readonly string[] Kinds = { "Labor", "Part", "Fee", "Discount" };

    public decimal Qty
    {
        get => QtyMilli / 1000m;
        set => QtyMilli = (long)Math.Round(value * 1000m, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// How the line reads on a printed estimate or invoice.
    ///
    /// What the shop typed is what the customer sees. The kind is a filing decision the shop made
    /// for itself - a line typed "Welding" on a Fee should not print "Welding (Fee)", and a
    /// labour line left blank should say "Labor" once rather than "Labor (Labor)". The kind only
    /// stands in when nothing was written.
    /// </summary>
    public string PrintLabel =>
        string.IsNullOrWhiteSpace(Description) ? Kind : Description.Trim();

    /// <summary>Discounts subtract no matter how the quantity or price was typed in.</summary>
    public long TotalCents
    {
        get
        {
            var raw = (long)Math.Round(QtyMilli * UnitCents / 1000m, MidpointRounding.AwayFromZero);
            return Kind == "Discount" ? -Math.Abs(raw) : raw;
        }
    }
}

/// <summary>Priced rollup of a ticket's lines.</summary>
public class TicketTotals
{
    public long SubtotalCents { get; set; }
    public long TaxableBaseCents { get; set; }
    public long TaxCents { get; set; }
    public long TotalCents { get; set; }
    public long PaidCents { get; set; }
    public long BalanceCents => TotalCents - PaidCents;

    public static TicketTotals From(IEnumerable<TicketLine> lines, int taxRateBp, long paidCents)
    {
        var t = new TicketTotals { PaidCents = paidCents };
        foreach (var line in lines)
        {
            t.SubtotalCents += line.TotalCents;
            if (line.Taxable) t.TaxableBaseCents += line.TotalCents;
        }

        // Tax only the taxable base, so a taxable discount correctly reduces the tax too.
        t.TaxCents = (long)Math.Round(t.TaxableBaseCents * taxRateBp / 10000m, MidpointRounding.AwayFromZero);
        t.TotalCents = t.SubtotalCents + t.TaxCents;
        return t;
    }
}

public class Payment
{
    public long Id { get; set; }
    public long? CustomerId { get; set; }
    public long? TicketId { get; set; }
    public long AmountCents { get; set; }
    public string Method { get; set; } = "Cash";
    public string Reference { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>Local calendar date (yyyy-MM-dd), not UTC, so "money brought in today" matches the shop's day.</summary>
    public string PaidOn { get; set; } = "";

    public string CreatedUtc { get; set; } = "";

    public static readonly string[] Methods = { "Cash", "Check", "Card", "Transfer", "Other" };
}

public class Appointment
{
    public long Id { get; set; }
    public long? CustomerId { get; set; }
    public long? TicketId { get; set; }
    public string Kind { get; set; } = "Pickup";

    /// <summary>
    /// What the stop is called. Set when it came from a calendar entry someone typed themselves;
    /// blank for stops written up here, which build a heading from the customer and kind instead.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>A whole-day job with no set time. The date still matters; the clock does not.</summary>
    public bool IsAllDay { get; set; }

    /// <summary>Local wall-clock start, stored as yyyy-MM-dd HH:mm.</summary>
    public string ScheduledLocal { get; set; } = "";

    public int DurationMin { get; set; } = 60;
    public string Address { get; set; } = "";
    public string Status { get; set; } = "Scheduled";
    public string Notes { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";

    /// <summary>Google's id for the matching event, empty until this appointment has been synced.</summary>
    public string GoogleEventId { get; set; } = "";

    /// <summary>This row's UpdatedUtc as at the last successful sync — differs when the shop has edited since.</summary>
    public string GoogleSyncedUtc { get; set; } = "";

    /// <summary>Google's own "updated" stamp as at the last sync — differs when Google's copy has moved on.</summary>
    public string GoogleUpdated { get; set; } = "";

    public static readonly string[] Kinds = { "Pickup", "Delivery", "Drop-off", "On-site Service", "Other" };
    public static readonly string[] Statuses = { "Scheduled", "Done", "Canceled" };

    /// <summary>True when the shop has changed this appointment since it was last pushed to Google.</summary>
    public bool HasLocalChanges => GoogleSyncedUtc != UpdatedUtc;

    public DateTime? Start =>
        DateTime.TryParse(ScheduledLocal, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
}

/// <summary>
/// A part or job the shop reaches for again and again, offered as a one-click line on a ticket.
/// The list is the shop's own — seeded from what they told us, and theirs to change.
/// </summary>
public class QuickItem
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Part";

    /// <summary>Price to start the line at. Zero means "look up what it went out at last time".</summary>
    public long DefaultCents { get; set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Row shapes for list screens — joined so pages avoid N+1 lookups.</summary>
public class TicketRow
{
    public long Id { get; set; }
    public string Number { get; set; } = "";
    public string Status { get; set; } = "";
    public string Complaint { get; set; } = "";
    public string IntakeOn { get; set; } = "";
    public string? PromisedOn { get; set; }
    public long CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public long? EquipmentId { get; set; }
    public string EquipmentName { get; set; } = "";

    /// <summary>How many machines the ticket covers, so a list can say so without a second query.</summary>
    public int MachineCount { get; set; }

    public long TotalCents { get; set; }
    public long PaidCents { get; set; }
    public long BalanceCents => TotalCents - PaidCents;
}

public class AppointmentRow
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsAllDay { get; set; }
    public string ScheduledLocal { get; set; } = "";
    public int DurationMin { get; set; }
    public string Address { get; set; } = "";
    public string Status { get; set; } = "";
    public string Notes { get; set; } = "";
    public long? CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public long? TicketId { get; set; }
    public string TicketNumber { get; set; } = "";

    public DateTime? Start =>
        DateTime.TryParse(ScheduledLocal, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;

    /// <summary>
    /// What to put at the top of the stop. A calendar entry's own wording wins, because whoever
    /// typed it said what they meant; otherwise build one from the kind and who it is for.
    /// </summary>
    public string Heading
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title;

            var who = string.IsNullOrWhiteSpace(CustomerName) ? "(no customer)" : CustomerName;
            return $"{Kind} — {who}";
        }
    }

    /// <summary>When it happens, for the left-hand column of the schedule.</summary>
    public string WhenLabel =>
        IsAllDay ? "All day" : Start?.ToString("h:mm tt") ?? "—";
}

public class PaymentRow
{
    public long Id { get; set; }
    public long AmountCents { get; set; }
    public string Method { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>The day the money actually changed hands.</summary>
    public string PaidOn { get; set; } = "";

    /// <summary>
    /// The day it counts as takings: the job's completion date where the ticket has one, and the
    /// day it was paid otherwise. Usually the same as <see cref="PaidOn"/>; different when money
    /// was handed over before the job was finished.
    /// </summary>
    public string RevenueOn { get; set; } = "";

    /// <summary>True when the takings date and the payment date are not the same day.</summary>
    public bool CountedOnAnotherDay =>
        !string.IsNullOrWhiteSpace(RevenueOn) && RevenueOn != PaidOn;

    public long? CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public long? TicketId { get; set; }
    public string TicketNumber { get; set; } = "";
}

/// <summary>One bucket of takings — a day, or a week — used by the money screen and the dashboard.</summary>
public class MoneyBucket
{
    public string Label { get; set; } = "";
    public string StartOn { get; set; } = "";
    public string EndOn { get; set; } = "";
    public long TotalCents { get; set; }
    public int PaymentCount { get; set; }
}
