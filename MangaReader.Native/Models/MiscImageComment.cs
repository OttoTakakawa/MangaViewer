namespace MangaReader.Native.Models;

public sealed class MiscImageComment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ImageId { get; init; } = "";
    public string Text { get; set; } = "";
    public double RelativeX { get; set; } = 0.5;
    public double RelativeY { get; set; } = 0.78;
}
