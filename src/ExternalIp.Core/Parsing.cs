using System.Globalization;
using System.Text.Json;

namespace ExternalIp.Core;

public static class PublicIpv4
{
    // Strict dotted decimal. Special-purpose ranges are not Internet unicast addresses.
    public static bool IsValid(string? value)
    {
        if (value is null) return false;
        var parts = value.Split('.');
        if (parts.Length != 4) return false;
        Span<byte> bytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (parts[i].Length == 0 || (parts[i].Length > 1 && parts[i][0] == '0') ||
                !parts[i].All(c => c is >= '0' and <= '9') ||
                !byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])) return false;
        }
        var a = bytes[0]; var b = bytes[1]; var c = bytes[2]; var d = bytes[3];
        if (a is 0 or 10 or 127 || a >= 224) return false;
        if (a == 100 && b is >= 64 and <= 127) return false;
        if (a == 169 && b == 254 || a == 172 && b is >= 16 and <= 31) return false;
        if (a == 192 && (b == 168 || b == 0 && c == 2 || b == 88 && c == 99)) return false;
        if (a == 192 && b == 0 && c == 0 && d is not (9 or 10)) return false;
        if (a == 198 && (b is 18 or 19 || b == 51 && c == 100)) return false;
        return !(a == 203 && b == 0 && c == 113);
    }
}

public sealed record GeoLocation(string? Country, string? Region, string? City)
{
    public string Display => string.Join(", ", new[] { Country, Region, City }
        .Select(Clean).Where(s => s is not null).Distinct(StringComparer.OrdinalIgnoreCase));
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Equals("null", StringComparison.OrdinalIgnoreCase) || text.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("n/a", StringComparison.OrdinalIgnoreCase) || text == "-") return null;
        return text.Length > 200 ? text[..200] : text;
    }
}

public static class ResponseParser
{
    public static JsonElement? Field(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path == "$") return root;
        var pointer = path.StartsWith('/');
        var segments = pointer ? path[1..].Split('/') : path.TrimStart('$', '.').Split('.');
        var current = root;
        foreach (var raw in segments)
        {
            var segment = pointer ? raw.Replace("~1", "/").Replace("~0", "~") : raw;
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var property)) current = property;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None,
                CultureInfo.InvariantCulture, out var index) && index >= 0 && index < current.GetArrayLength()) current = current[index];
            else return null;
        }
        return current;
    }

    private static JsonDocument Document(string body) => JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });

    public static string Ip(string body, IpService service)
    {
        string? value;
        if (service.Format == IpResponseFormat.Text) value = body.Trim();
        else
        {
            using var document = Document(body);
            var field = Field(document.RootElement, service.IpPath);
            value = field?.ValueKind == JsonValueKind.String ? field.Value.GetString()?.Trim() : null;
        }
        if (!PublicIpv4.IsValid(value)) throw new FormatException("Ответ не содержит корректный публичный IPv4.");
        return value!;
    }

    public static GeoLocation Geo(string body, GeoService service, string expectedIp)
    {
        using var document = Document(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Ожидался JSON-объект геолокации.");
        if (!string.IsNullOrWhiteSpace(service.SuccessPath) && Field(root, service.SuccessPath)?.ValueKind != JsonValueKind.True)
            throw new FormatException("Геосервис сообщил об ошибке (поле успешности).");
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True ||
            root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new FormatException("Геосервис сообщил об ошибке.");
        if (!string.IsNullOrWhiteSpace(service.ResponseIpPath) && Field(root, service.ResponseIpPath)?.GetString() != expectedIp)
            throw new FormatException("IPv4 в геоответе не совпадает с запрошенным адресом.");
        string? Read(string path)
        {
            var field = Field(root, path);
            if (field is null || field.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (field.Value.ValueKind != JsonValueKind.String) throw new FormatException("Компонент расположения должен быть строкой.");
            return GeoLocation.Clean(field.Value.GetString());
        }
        var location = new GeoLocation(Read(service.CountryPath), Read(service.RegionPath), Read(service.CityPath));
        if (location.Display.Length == 0) throw new FormatException("Геосервис не вернул страну, регион или город.");
        return location;
    }
}
