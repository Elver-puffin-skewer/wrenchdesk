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

    public static readonly string[] Kinds = { "Labor", "Part", "Fee", "Discount" };

    public decimal Qty
    {
        get => QtyMilli / 1000m;
        set => QtyMilli = (long)Math.Round(value * 1000m, MidpointRounding.AwayFromZero);
    }

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

    /// <summary>Local wall-clock start, stored as yyyy-MM-dd HH:mm.</summary>
    public string ScheduledLocal { get; set; } = "";

    public int DurationMin { get; set; } = 60;
    public string Address { get; set; } = "";
    public string Status { get; set; } = "Scheduled";
    public string Notes { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";

    public static readonly string[] Kinds = { "Pickup", "Delivery", "Drop-off", "On-site Service", "Other" };
    public static readonly string[] Statuses = { "Scheduled", "Done", "Canceled" };

    public DateTime? Start =>
        DateTime.TryParse(ScheduledLocal, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
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
    public long TotalCents { get; set; }
    public long PaidCents { get; set; }
    public long BalanceCents => TotalCents - PaidCents;
}

public class AppointmentRow
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
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
}

public class PaymentRow
{
    public long Id { get; set; }
    public long AmountCents { get; set; }
    public string Method { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Note { get; set; } = "";
    public string PaidOn { get; set; } = "";
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
