using WrenchDesk.Data;

namespace WrenchDesk.Tests;

/// <summary>
/// Photos live as files beside the database rather than inside it, so these check both halves
/// stay in step - and that the endpoint serving them cannot be talked into serving anything else.
/// </summary>
public class PhotoTests
{
    [Fact]
    public async Task A_photo_is_written_beside_the_database_and_recorded_against_the_ticket()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());

        var problem = await h.Photos.AddAsync(ticketId, null, "mower.jpg", Jpeg());
        Assert.Null(problem);

        var photo = h.Photos.ForTicket(ticketId).Single();
        Assert.Equal(ticketId, photo.TicketId);

        // The database stays small; the picture is a file next to it.
        var onDisk = Path.Combine(h.Db.PhotoDirectory, photo.FileName);
        Assert.True(File.Exists(onDisk));
        Assert.True(new FileInfo(onDisk).Length > 0);
    }

    [Fact]
    public async Task The_stored_name_owes_nothing_to_the_name_it_came_in_under()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());

        await h.Photos.AddAsync(ticketId, null, "../../evil name (1).jpg", Jpeg());

        var photo = h.Photos.ForTicket(ticketId).Single();
        Assert.StartsWith($"t{ticketId}-", photo.FileName);
        Assert.EndsWith(".jpg", photo.FileName);
        Assert.DoesNotContain("evil", photo.FileName);
        Assert.DoesNotContain("..", photo.FileName);
    }

    [Fact]
    public async Task Something_that_is_not_a_picture_is_refused_and_nothing_is_written()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());

        var problem = await h.Photos.AddAsync(ticketId, null, "invoice.pdf", Jpeg());

        Assert.NotNull(problem);
        Assert.Empty(h.Photos.ForTicket(ticketId));
        Assert.Empty(Directory.GetFiles(h.Db.PhotoDirectory));
    }

    [Fact]
    public async Task Removing_a_photo_takes_the_file_with_it()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        await h.Photos.AddAsync(ticketId, null, "before.png", Jpeg());

        var photo = h.Photos.ForTicket(ticketId).Single();
        var onDisk = Path.Combine(h.Db.PhotoDirectory, photo.FileName);

        h.Photos.Delete(photo.Id);

        Assert.Empty(h.Photos.ForTicket(ticketId));
        Assert.False(File.Exists(onDisk));
    }

    [Fact]
    public async Task Deleting_a_ticket_does_not_leave_its_photos_listed()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        await h.Photos.AddAsync(ticketId, null, "a.jpg", Jpeg());

        h.Tickets.Delete(ticketId);

        Assert.Empty(h.Photos.ForTicket(ticketId));
    }

    [Theory]
    [InlineData("../wrenchdesk.db")]
    [InlineData("..%2Fwrenchdesk.db")]
    [InlineData("sub/dir.jpg")]
    [InlineData("nothing-like-ours.jpg")]
    [InlineData("notapicture.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void A_name_that_is_not_ours_resolves_to_nothing(string? name)
    {
        using var h = new TestDb();

        // The endpoint hands whatever was in the URL straight to this, so it is the whole guard.
        Assert.Null(h.Photos.ResolveFile(name));
    }

    [Fact]
    public async Task A_name_that_is_ours_resolves_inside_the_photos_folder()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        await h.Photos.AddAsync(ticketId, null, "mower.jpg", Jpeg());

        var photo = h.Photos.ForTicket(ticketId).Single();
        var resolved = h.Photos.ResolveFile(photo.FileName);

        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(h.Db.PhotoDirectory), resolved!);
    }

    [Fact]
    public async Task A_backup_carries_the_photos_with_it()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        await h.Photos.AddAsync(ticketId, null, "one.jpg", Jpeg());
        await h.Photos.AddAsync(ticketId, null, "two.jpg", Jpeg());

        var destination = Path.Combine(Path.GetTempPath(), "wrenchdesk-tests", Guid.NewGuid().ToString("N"));

        var result = h.Backups.CreateBackup(destination);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.PhotosCopied);
        Assert.Null(result.PhotoWarning);

        // A copy of the database on its own would not be a copy of the shop any more.
        var copied = Directory.GetFiles(Path.Combine(destination, "Photos"));
        Assert.Equal(2, copied.Length);

        Directory.Delete(destination, recursive: true);
    }

    [Fact]
    public async Task Backing_up_twice_does_not_copy_the_same_photos_again()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        await h.Photos.AddAsync(ticketId, null, "one.jpg", Jpeg());

        var destination = Path.Combine(Path.GetTempPath(), "wrenchdesk-tests", Guid.NewGuid().ToString("N"));

        Assert.Equal(1, h.Backups.CreateBackup(destination).PhotosCopied);

        // A nightly backup to the same stick should copy what is new, not the whole shop again.
        Assert.Equal(0, h.Backups.CreateBackup(destination).PhotosCopied);

        await h.Photos.AddAsync(ticketId, null, "two.jpg", Jpeg());
        Assert.Equal(1, h.Backups.CreateBackup(destination).PhotosCopied);

        Directory.Delete(destination, recursive: true);
    }

    /// <summary>A few bytes standing in for a photo. Nothing here reads image content.</summary>
    private static MemoryStream Jpeg() =>
        new(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 });
}
