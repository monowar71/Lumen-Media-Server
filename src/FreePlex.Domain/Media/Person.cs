namespace FreePlex.Domain.Media;

public class Person
{
    private Person() { }

    public Person(string name, string? tmdbId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Person name is required", nameof(name));

        Id = Guid.CreateVersion7();
        Name = name;
        TmdbId = tmdbId;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string? TmdbId { get; private set; }
    public string? ThumbPath { get; set; }
}
