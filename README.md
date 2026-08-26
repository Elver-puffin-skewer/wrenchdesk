# WrenchDesk

Shop management for a small-engine repair business — mowers, pressure washers, tillers, generators,
chainsaws, and whatever else rolls through the door.

It replaces the notebook: customers, their equipment, estimates, repair history, and what money
actually came in today and this week. It runs entirely on your own PC. No subscription, no cloud
account, no per-user fee.

---

## What it does

**Customers** — name, business, two phone numbers, email, address, and free-text notes (gate code,
best time to call, dog in the yard). Search by any of it, including partial phone numbers.

**Equipment** — every machine a customer owns, with make, model, serial, and engine make/model/serial.
Each machine keeps its own repair count, so you can see the mower that's been in four times this year.

**Estimates and repairs** — one ticket per job. It starts as an *Estimate*, and when the customer says
go ahead you move it to *Approved* → *In Progress* → *Waiting on Parts* → *Ready for Pickup* → *Closed*.
It stays the same record the whole way through, so the quote and the finished repair are one story in
the customer's history rather than two disconnected pieces of paper.

Each ticket takes labor, parts, fees, and discounts as separate lines, with per-line tax control
(labor untaxed, parts taxed, or however your state works). Print it as an estimate with a signature
line, or as an invoice once the work is authorised.

**Money** — record payments as they come in, by cash, check, card or transfer. The dashboard shows
today's takings and this week's; the Money page breaks it down day by day and week by week, splits it
by payment method for reconciling the till, and shows what's still owed across all unpaid tickets.
Export any date range to CSV for a bookkeeper or a tax return.

**Schedule** — pickups and deliveries with the customer's address attached. Every stop has a one-click
**Add to Google Calendar** link that opens the event prefilled, and a **Directions** link that opens
Google Maps for whoever is driving. A whole run can be exported as an `.ics` file and imported into
Google Calendar in one go.

**Backups** — the entire system is one SQLite file. Press **Back up now** to write a copy straight to
a USB stick or external drive, or switch on a daily/weekly schedule that does it unattended.
Scheduled backups are **off until you turn them on**. See [Backups](#backups) below.

---

## Running it on the shop PC

Grab the latest build (or produce one with `build\publish.ps1` — see *Building* below), copy the
folder anywhere on the PC, and double-click **WrenchDesk.exe**.

A console window opens and prints something like:

```
  WrenchDesk is running.
  On this PC:      http://localhost:5173
  Phone / tablet:  http://192.168.1.20:5173
  Data file:       C:\Users\Shop\Documents\WrenchDesk\wrenchdesk.db
  Backups:         C:\Users\Shop\Documents\WrenchDesk\Backups

  Leave this window open while the shop is using it. Close it to stop.
```

Your browser opens automatically. **Leave the console window open** — closing it stops the program.

### Using it from a phone or tablet

The second URL works from any phone, tablet or laptop on the same wifi — handy for writing up an
estimate at the bench, or checking a delivery address without walking back to the counter. The layout
adapts to a small screen.

The first time you run it, Windows Firewall may ask whether to allow WrenchDesk on the network. Say
yes to **Private networks** for the tablet URL to work. (Say no and it still works fine on the shop PC
itself.)

### Starting it automatically with Windows

Press `Win+R`, type `shell:startup`, press Enter, and drop a shortcut to `WrenchDesk.exe` in the
folder that opens. It will start with the PC from then on.

---

## Where your data lives

Everything is in one file:

```
Documents\WrenchDesk\wrenchdesk.db
```

To move the shop to a new PC, or to keep an off-site copy, copy that file. That's the whole system —
there is no separate database server to install or configure.

To put the data somewhere else (a synced folder, a NAS, a second drive), edit `appsettings.json`
next to the exe:

```json
{
  "WrenchDesk": {
    "Port": 5173,
    "AllowLanAccess": true,
    "OpenBrowser": true,
    "DataDirectory": "D:\\ShopData"
  }
}
```

| Setting | What it does |
| --- | --- |
| `Port` | Which port to serve on. Change it if something else on the PC already uses 5173. |
| `AllowLanAccess` | `false` locks it to the shop PC only — no phone or tablet access. |
| `OpenBrowser` | `false` stops it opening a browser window on startup. |
| `DataDirectory` | Where the database and backups live. Blank means `Documents\WrenchDesk`. |

---

## Backups

Everything the shop has is in one file, so a backup is just a copy of it. WrenchDesk gives you two
ways to make one, both under **Settings**.

### Back up now (to a USB stick or external drive)

Plug the drive in, open **Settings → Back up now**, pick it from the list and press
**Back up to this drive**. The list shows every drive the shop PC can see, with free space, and puts
removable drives at the top. Pick *Somewhere else* to type a path — a network share or a second
internal disk.

Two things worth knowing:

- The drives listed are the ones plugged into **the shop PC**, not into the tablet you might be
  holding. The app writes the file server-side.
- On-demand backups are **never deleted automatically**. They sit there until you remove them.

### On a schedule

**Off by default.** Nothing is written on a schedule until you switch on *Run backups on a schedule*
under **Settings → Automatic backups**. Then choose:

| Setting | Notes |
| --- | --- |
| How often | Daily, or weekly on a day you pick |
| At what time | Pick a time the PC is normally on — after closing, before it gets switched off |
| Keep how many | Older ones are removed past this count (default 30) |
| Save them to | The data folder, or any drive — a USB stick left plugged in works well |

If the PC was switched off when a backup was due, it runs at the next opportunity rather than
skipping — a late backup beats no backup. Settings shows the last run, the next one due, and any
error from the last attempt (an unplugged USB drive, most likely). A failed run is retried on the
next check rather than being marked done.

**Retention only ever deletes files WrenchDesk itself wrote** — files named `wrenchdesk-*.db`.
Pointing it at a folder with your own documents in it cannot touch them. It also never deletes the
backup it just made.

### Restoring one

Every backup file is a complete, working database — there is nothing else to restore. Close
WrenchDesk, rename the backup to `wrenchdesk.db`, and put it where the old one was
(**Settings → Your data** shows the exact path). Start WrenchDesk again.

### Which destination to choose

A backup on the same drive as the live database protects you from a mistake — deleting the wrong
customer — but not from the drive itself failing. For a shop replacing paper, a cheap USB stick left
plugged in, with a daily schedule pointed at it, covers both. Better still, keep a second one off
site and swap them occasionally.

---

## First-time setup

Open **Settings** and fill in:

- **Shop name, address, phone** — these print at the top of every estimate and invoice.
- **Labor rate** — prefills every labor line you add, so the common case is one number away from done.
- **Sales tax %** — applied to new tickets. Set it to `0` if you don't charge tax. The rate is saved
  onto each ticket as it's created, so changing it later never rewrites old records.
- **Ticket prefix** — ticket numbers look like `WD-1042`. Use `MOW`, your initials, whatever you like.
- **Week starts on** — controls where the weekly money totals break.

---

## Day-to-day

**Machine comes in** → *+ New Ticket* → find the customer (or add them) → pick the machine → type
what they said is wrong → Create.

**Quoting it** → open the ticket, add labor and parts lines → *Print* hands them an estimate with a
signature line.

**They approve** → move the ticket to *Approved*, then *In Progress* as you work. Add parts to the
ticket as you use them.

**Done** → move to *Ready for Pickup*. Write what you actually did in *What we found / did* — that's
what prints on the invoice and what you'll want to read next time the machine comes back.

**They pay** → *+ Payment* on the ticket. It defaults to the full balance, so most of the time it's
two clicks. Move the ticket to *Closed*.

**Delivering it** → *Schedule* on the ticket, pick a day and time. The address comes from the
customer's record. On the Schedule page, hit *Add to Google Calendar* to put it on the shop calendar.

---

## Google Calendar

WrenchDesk doesn't ask for your Google password or need an API key. Instead:

- **Add to Google Calendar** on any stop opens Google with the event already filled in — title,
  time, address, customer phone, ticket number. Press Save.
- **Export .ics** on the Schedule page downloads the next 90 days as a calendar file. Import it at
  Google Calendar → Settings → Import & export.
- **Directions** opens Google Maps routed to the customer's address.

A proper two-way sync (where moving an event in Google moves it here) would need an OAuth sign-in
and a Google Cloud project. It's on the list below, not built yet.

---

## Not built yet

Deliberately left out of the first version, roughly in the order they'd be worth adding:

- **Inventory** — parts on hand, reorder points, and pulling a part onto a ticket decrementing stock.
  The owner flagged this as wanted eventually; the ticket line structure already has room for it.
- **Two-way Google Calendar sync** — needs OAuth. The one-click links above cover most of the need.
- **Photos on tickets** — before/after shots of a machine.
- **Text/email the customer** when a repair is ready.
- **Multiple users** with their own logins. Right now anyone who can reach the app can use it, which
  is the right trade-off for a single shop on its own wifi — see *Security* below.

---

## Security

This is built for one shop's own network. There are no user accounts and no password. Anyone who can
reach the app on your wifi can use it.

That's a deliberate trade-off for a two-person shop where the alternative is a notebook on the
counter, but it means:

- **Don't port-forward it to the internet.** It is not hardened for that.
- Keep the shop wifi on a password.
- Set `"AllowLanAccess": false` if you only ever want it on the shop PC.

If the shop ever grows to needing per-person logins and an audit trail of who changed what, that's a
real change rather than a setting — worth doing properly at that point.

---

## Building from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/Elver-puffin-skewer/wrenchdesk.git
cd wrenchdesk

dotnet run                      # development, on http://localhost:5173
dotnet test                     # run the test suite
powershell build\publish.ps1    # produce build\output\WrenchDesk.exe for the shop
```

### How it's put together

| | |
| --- | --- |
| **UI** | Blazor Server (.NET 8) — server-rendered, so a tablet only needs a browser |
| **Data** | SQLite via Dapper, one local file, no server process |
| **Styling** | Hand-written CSS, no framework |
| **Dependencies** | Two NuGet packages (Dapper, Microsoft.Data.Sqlite). That's the lot. |

```
Data/          models, migrations, and one repository per area
Services/      backups, Google Calendar and .ics generation
Components/    Blazor pages and layout
tests/         xunit tests against a real migrated SQLite file
build/         publish script
```

**Money is stored as whole cents** (`long`), never as a floating-point number. Quantities are stored
times 1000 so 1.5 labor hours is the integer `1500`. Both avoid the rounding drift that would
otherwise turn up as invoices that are a penny off.

**Ticket pricing is computed in two places** — in C# for the ticket screen, and in the `ticket_totals`
SQL view for list screens and reports. `PricingTests` pins those two together, including on awkward
half-cent tax cases, so the list and the invoice can never show different numbers.

**Schema changes** go on the end of the `Migrations` array in `Data/Db.cs` — never edit an entry that
has already shipped, because existing shop databases have run it. The file tracks its own version in
`PRAGMA user_version` and steps forward on startup.

---

## Licence

MIT — see [LICENSE](LICENSE).
