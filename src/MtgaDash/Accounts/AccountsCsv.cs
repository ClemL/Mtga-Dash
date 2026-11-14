using Microsoft.VisualBasic.FileIO;

namespace MtgaDash.Accounts;

public sealed record AccountRow(string Email, string Status, string Notes, string MiscNotes, string NoteSource);

/// <summary>Reads the accounts CSV: <c>Status, Email, Notes, Misc Notes, Note Source</c>, RFC 4180 quoting.</summary>
public static class AccountsCsv
{
    public static List<AccountRow> Read(TextReader reader)
    {
        using var parser = new TextFieldParser(reader)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = true,
        };
        parser.SetDelimiters(",");

        var rows = new List<AccountRow>();
        if (parser.EndOfData) return rows;

        // Columns are found by header name, so a reordered export still imports.
        var header = (parser.ReadFields() ?? []).Select(h => h.Trim().TrimStart('\uFEFF')).ToArray();
        int Col(string name) => Array.FindIndex(header, h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        var iStatus = Col("Status");
        var iEmail = Col("Email");
        var iNotes = Col("Notes");
        var iMisc = Col("Misc Notes");
        var iSource = Col("Note Source");
        if (iEmail < 0) throw new InvalidDataException("The accounts CSV has no Email column.");

        while (!parser.EndOfData)
        {
            var f = parser.ReadFields();
            if (f is null) continue;
            string At(int i) => i >= 0 && i < f.Length ? f[i].Trim() : "";
            var email = At(iEmail);
            if (email.Length == 0 || !email.Contains('@')) continue;
            rows.Add(new AccountRow(email, At(iStatus), At(iNotes), At(iMisc), At(iSource)));
        }
        return rows;
    }

    public static List<AccountRow> Read(string path)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        return Read(reader);
    }
}
