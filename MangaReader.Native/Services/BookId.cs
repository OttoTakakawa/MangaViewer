using System.Security.Cryptography;
using System.Text;

namespace MangaReader.Native.Services;

public static class BookId
{
    public static string FromFolderPath(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return System.Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
