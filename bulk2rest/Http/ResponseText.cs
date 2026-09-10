namespace bulk2rest.Http;

public static class ResponseText
{
    private const int ExcerptLimit = 500;

    /// Flattens a response body onto one line so it can sit in a status column.
    public static string Excerpt(string body)
    {
        var flat = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= ExcerptLimit ? flat : flat[..ExcerptLimit] + "…";
    }
}
