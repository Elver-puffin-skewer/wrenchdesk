# WrenchDesk

Shop management for **Walt's Small Engines** (651 Toney Rd, Toney, AL 35773 · (256) 852-0489) —
mowers, pressure washers, tillers, generators, chainsaws, and whatever else rolls through the door.

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

**Schedule** — pickups and deliveries with the customer's address attached, and **live two-way Google
Calendar sync**: write a stop up here and it appears on the shop calendar; move or cancel it from a
phone and it changes here. Every stop also has a **Directions** link that opens Google Maps for
whoever is driving. See [Google Calendar sync](#google-calendar-sync).

**Help** — a full guide lives inside the app under **Help**, written for the counter rather than for
a developer. It works offline and shows this shop's actual settings — where the records are, whether
backups are on, the exact URL to type into a phone — instead of generic instructions.

**Backups** — the entire system is one SQLite file. Press **Back up now** to write a copy straight to
a USB stick or external drive, or switch on a daily/weekly schedule that does it unattended.
Scheduled backups are **off until you turn them on**. See [Backups](#backups) below.

---

## Installing it on the shop PC

Go to the [**Releases**](https://github.com/Elver-puffin-skewer/wrenchdesk/releases) page, download
**`WrenchDesk.exe`**, and double-click it.

That is the whole install. One file, nothing else to download, nothing else to install — no .NET, no
runtime, no setup wizard. The first time you run it, it asks:

```
  WrenchDesk is not set up on this PC yet.

  Setting up will:
    - copy the program to your account so it stays put
    - put a WrenchDesk icon on your desktop
    - add it to the Start menu

  Set up WrenchDesk now? [Y/n]
```

Press Enter. It copies itself somewhere permanent, makes the shortcuts, and starts. From then on you
open it from the desktop icon and never see that question again.

It installs under your own user account, so there is **no administrator prompt**.

> **Windows will warn you** that the publisher is unknown, because the file is not code-signed
> (a certificate costs a few hundred dollars a year). Choose **More info** then **Run anyway**.

### Updating

Download the new `WrenchDesk.exe` and run it again. Your customers, tickets and payments live in
`Documents\WrenchDesk` and are never touched by an update.

### Removing it

```
WrenchDesk.exe --uninstall
```

Removes the shortcuts and the program. Your shop data is left alone.

### Other ways to run it

| Command | What it does |
| --- | --- |
| `WrenchDesk.exe` | Normal. Sets up on first run, then just opens. |
| `WrenchDesk.exe --portable` | Runs where it sits and never installs — for a USB stick. |
| `WrenchDesk.exe --install` | Sets up without being asked. |
| `WrenchDesk.exe --uninstall` | Removes shortcuts and the program. |
| `WrenchDesk.exe --console` | Attaches a console showing the startup details, for troubleshooting. |

### Pinning it to the taskbar

Windows does not let a program pin itself to the taskbar — Microsoft closed that off in Windows 10
and tightened it further in Windows 11, where even *Pin to Start* is refused to installers. So this
one step is manual, and takes a single right-click:

> Right-click the desktop icon → **Show more options** → **Pin to taskbar**

Or press Start, type *WrenchDesk*, right-click the result and choose **Pin to taskbar**. Dragging
the desktop icon onto the taskbar works too.

### What you see when it runs

Nothing, which is the point. WrenchDesk opens your browser and then sits **down by the clock**, in
the notification area alongside the volume and wifi icons. There is no console window to keep open
and nothing cluttering the taskbar.

Right-click the tray icon for:

| | |
| --- | --- |
| **Open WrenchDesk** | Opens the shop screen (double-clicking the icon does the same) |
| **Phone / tablet address** | Copies the LAN address to paste into a phone |
| **Open the records folder** | Where the database and backups live |
| **Stop WrenchDesk** | Properly shuts it down |

**Closing the browser does not stop it** — that only puts the page away. The program keeps running
so scheduled backups and calendar syncing still happen. Only *Stop WrenchDesk* shuts it down.

If the icon is not visible, Windows has tucked it away: click the small **^** arrow next to the
clock and drag WrenchDesk out.

To see what it is doing — the addresses it is serving on, and any errors — run
`WrenchDesk.exe --console`, which attaches a console window for that session.

### Using it from a phone or tablet

The second URL works from any phone, tablet or laptop on the same wifi — handy for writing up an
estimate at the bench, or checking a delivery address without walking back to the counter. The layout
adapts to a small screen.

The first time you run it, Windows Firewall may ask whether to allow WrenchDesk on the network. Say
yes to **Private networks** for the tablet URL to work. (Say no and it still works fine on the shop PC
itself.)

### Starting it automatically with Windows

Setup offers this on first run. To change your mind later, press `Win+R`, type `shell:startup`,
press Enter, and add or remove the WrenchDesk shortcut in the folder that opens.

---

## Where your data lives

Everything is in one file:

```
Documents\WrenchDesk\wrenchdesk.db
```

To move the shop to a new PC, or to keep an off-site copy, copy that file. That's the whole system —
there is no separate database server to install or configure.

The program is a single file with its defaults compiled in, so there is no config file unless you
want one. To change a setting, create `appsettings.json` next to `WrenchDesk.exe`
(`%LOCALAPPDATA%\WrenchDesk`):

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
| `ShowTrayIcon` | `false` runs with no tray icon at all. Only sensible for an unattended PC. |
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

## Branding

The colours come straight off the shop sign — navy `#16233d`, red `#c8202a`, cream `#f7f2dd` —
defined as CSS variables at the top of [`wwwroot/app.css`](wwwroot/app.css). Change them there and
the whole app follows.

### Using the real logo artwork

Out of the box the app draws its own badge in those colours, so it looks right with nothing to
install. To use the actual sign artwork instead, drop the image into your data folder as:

```
Documents\WrenchDesk\logo.png
```

It is picked up automatically — in the sidebar and at the top of every printed estimate and invoice
— with no restart needed. A transparent PNG around 600px wide works well. Remove the file and it
falls back to the drawn badge.

It sits with your data rather than beside the program on purpose: updating WrenchDesk never loses
it, and a backup of the data folder carries the branding too.

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

## Google Calendar sync

Changes flow **both ways**. Write up a pickup here and it lands on the shop calendar within a few
minutes. Drag it to a new time on your phone, or cancel it, and the schedule here follows.

### Use a calendar kept just for shop work

Everything on the synced calendar is treated as shop work — an event added on a phone becomes an
appointment here. Point it at a personal calendar and you would pull in dentist appointments and
birthdays. Settings has a **Create a new "Walt's Small Engines — Schedule" calendar** button that
makes a clean one in a single click; use that unless you already keep a shop calendar.

### The short way: a key file (recommended)

Give WrenchDesk its own Google address and share the shop calendar with it, like sharing with a
colleague. **No consent screen, no website or domain, no verification, and nothing that expires.**

1. [console.cloud.google.com](https://console.cloud.google.com), signed in with the account that
   owns the shop calendar. Create a project called **WrenchDesk**.
2. **APIs & Services → Library** → **Google Calendar API** → **Enable**.
3. **APIs & Services → Credentials → Create credentials → Service account**. Any name; skip the
   optional steps.
4. Click into it → **Keys** tab → **Add key → Create new key → JSON**. A file downloads.
5. Open that file in Notepad, copy everything, and paste it into **Settings → Use a key file**.
6. Settings shows an address ending `...iam.gserviceaccount.com`. In Google Calendar, hover the
   shop calendar → **⋮** → **Settings and sharing** → **Share with specific people** → add that
   address with **Make changes to events**.
7. Back in Settings, load the calendar list, pick the calendar, switch syncing on.

If the calendar list comes back empty, step 6 was missed or was done on a different calendar — a
service account only sees calendars explicitly shared with it, which is also what keeps it away
from anything personal.

### The long way: signing in with Google

Kept for anyone already using it. This is the route that asks for a consent screen, a website
domain, and publishing — none of which a repair shop has any use for.

#### One-time setup (about ten minutes, free)

Google requires every business to use its own credentials — they cannot be shipped inside the app,
which is also why this repo can be public.

1. Go to [console.cloud.google.com](https://console.cloud.google.com) and sign in with the account
   that owns the shop calendar.
2. Create a project — call it **WrenchDesk**.
3. **APIs & Services → Library**, search for **Google Calendar API**, press **Enable**.
4. **APIs & Services → OAuth consent screen**. Choose **External**, give it the app name
   *WrenchDesk*, and put your own email in the support and developer contact fields.

   You only need **App name**, **User support email** and **Developer contact email** here.
   Leave everything else blank — in particular:

   > **Authorised domains: try to leave it empty.** That box is normally only demanded because of
   > the three above it — *Application home page*, *Privacy policy link*, *Terms of service link*.
   > Clear all three and it usually stops asking.
   >
   > Putting the WrenchDesk address in there gives `Invalid URL: cannot contain a localhost
   > domain` — the field wants a bare domain like `waltssmallengines.com`, not an address, and not
   > `https://` or `www.`
   >
   > **If Google insists and the shop has no website**, do not invent a domain — one you do not own
   > is shown on the sign-in screen as though it were yours. Create the OAuth client as a
   > **Desktop app** in step 5 instead; that type needs no domain at all.
5. **APIs & Services → Credentials → Create credentials → OAuth client ID**.
   For **Application type** choose **Desktop app**.

   > **Why Desktop app.** It is the type Google intends for a program installed on a PC, which is
   > what this is. It needs no website, no authorised domain, and no redirect address typed in —
   > Google lets a program on your own machine receive the sign-in automatically. It also survives
   > a change of port, which a Web application client would not.

   If you choose **Web application** instead, you must also scroll to **Authorised redirect
   URIs** — *not* the domain box on the previous page — press **Add URI**, and paste exactly:

   ```
   http://localhost:5173/google/callback
   ```

   (If you changed `Port` in `appsettings.json`, use that number instead. Settings shows the exact
   URI to paste.)
6. Copy the **Client ID** and **Client secret** into **Settings → Google Calendar**, press
   **Save credentials**, then **Connect Google account** and approve the consent screen.
7. Pick or create the calendar, then tick **Keep this calendar and the shop schedule in step**.

### Error 403: access_denied

If connecting gives **"Error 403: access_denied"**, or *"can only be accessed by developer-approved
testers"*, the project is still in **Testing**. Fix it the same way as below — publish the app.

(As a stopgap you can add the shop's Google address under **Audience → Test users → + ADD USERS**,
which works immediately but must be redone every 7 days.)

### Important: set the consent screen to "In production"

While the OAuth consent screen is left in **Testing**, Google expires the connection **every 7
days** and syncing silently stops until someone reconnects. On the OAuth consent screen page, press
**Publish app** to move it to *In production*.

Because the app is not verified by Google, the consent screen shows an "unverified app" warning the
first time — press **Advanced → Go to WrenchDesk (unsafe)**. That warning is expected: it is your
own project, used only by your own account. Verification is only needed to offer an app to the
general public.

If the connection does lapse, Settings shows a red **Google has dropped the connection** notice and
syncing pauses until you press Connect again — it will not sit there failing quietly.

### How the two directions behave

| What happens | Result |
| --- | --- |
| Stop written up here | Appears on the Google calendar |
| Stop moved or renamed here | The Google event moves |
| Stop deleted here | The Google event is deleted |
| Event moved in Google | The appointment moves here |
| Event cancelled/deleted in Google | The appointment is removed here |
| New event added in Google | Becomes a stop here, keeping the entry's own wording as its heading |
| An entry naming a known customer | Linked to them automatically, matched on phone number or full name |
| An entry naming nobody on the books | Left unattached — there is a **+ link a customer** button on the stop |
| All-day entry on the calendar | Comes across as an all-day stop. Plenty of shop work is booked that way |
| Same stop edited in both places between syncs | The most recent edit wins |

Renaming an event's prefix in Google (say `Pickup — Dale` to `Delivery — Dale`) changes the kind
here too.

**If entries seem to be missing**, press **Re-scan the whole calendar** in Settings. A normal sync
only asks Google what has changed since last time, so anything the previous version passed over
would never be offered again on its own.

### Why it polls rather than updating instantly

Google can only push changes to a public HTTPS address, and a shop PC behind a home router does not
have one. So WrenchDesk asks Google what changed on an interval you choose in Settings — one minute
to an hour, five minutes by default. **Sync now** forces it immediately.

---

## Not built yet

Deliberately left out of the first version, roughly in the order they'd be worth adding:

- **Inventory** — parts on hand, reorder points, and pulling a part onto a ticket decrementing stock.
  The owner flagged this as wanted eventually; the ticket line structure already has room for it.
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
powershell build\publish.ps1    # produce build\output\ ready to hand to the shop
python build\make-icon.py       # regenerate the app icon (needs Pillow)
```

### How it's put together

| | |
| --- | --- |
| **UI** | Blazor Server (.NET 8) — server-rendered, so a tablet only needs a browser |
| **Data** | SQLite via Dapper, one local file, no server process |
| **Styling** | Hand-written CSS, no framework |
| **Calendar** | Google Calendar API v3, OAuth 2.0 loopback flow, tokens kept in the shop's own database |
| **Dependencies** | Dapper, Microsoft.Data.Sqlite, Google.Apis.Calendar.v3 |

```
Data/            models, migrations, and one repository per area
Services/        backups and scheduling
Services/Google/ calendar sync — contracts, mapping, engine, OAuth
Components/      Blazor pages and layout
tests/           xunit tests against a real migrated SQLite file
build/           publish script, installer, icon generator
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

**The calendar sync is testable without Google.** Everything the engine needs sits behind
`ICalendarApi`, and `FakeCalendarApi` in the test project models the behaviour that actually matters
— server-assigned ids, an `updated` stamp that moves on every write, deletions coming back as
cancellations, and sync tokens that expire. `CalendarSyncTests` uses it to pin both directions,
echo suppression (a pushed event must not read back as a change), conflict resolution, and recovery
from an expired token.

---

## Licence

MIT — see [LICENSE](LICENSE).
