namespace FreePlex.Domain.Libraries;

public class LibraryPath
{
    private LibraryPath() { }

    public LibraryPath(Guid libraryId, string path)
    {
        Id = Guid.CreateVersion7();
        LibraryId = libraryId;
        Path = path;
    }

    public Guid Id { get; private set; }
    public Guid LibraryId { get; private set; }
    public string Path { get; private set; } = null!;
}
