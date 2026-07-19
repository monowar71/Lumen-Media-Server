namespace FreePlex.Domain.Media;

public class Genre
{
    private Genre() { }

    public Genre(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Genre name is required", nameof(name));

        Id = Guid.CreateVersion7();
        Name = name;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
}
