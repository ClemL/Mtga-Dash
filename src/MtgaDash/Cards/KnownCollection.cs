using System.Text.Json;

namespace MtgaDash.Cards;

/// <summary>One saved version of a deck: the evidence that its owner had these cards.</summary>
public sealed record SavedDeck(string DisplayName, string DeckId, string? DeckName, string CardsJson);

/// <summary>A card an account is known to own, with the fewest copies that can be true.</summary>
public sealed record KnownCard(int CardId, int Copies, int Decks, IReadOnlyList<string> DeckNames);

/// <summary>
/// The part of a collection the logs prove. MTGA no longer logs the collection itself, but a saved deck is
/// taken as proof of ownership: if a deck ever held four copies of a card, the account owns at least four.
/// So each card's count is the most copies in any single saved deck version (main deck, sideboard, command
/// zone and companions together), never a sum across decks, which would double-count shared copies. Cards
/// never saved in a deck are missing, so every number is a minimum. Pure: deck lists in, counts out.
/// </summary>
public static class KnownCollection
{
    private const int MaxDeckNames = 3;

    public static Dictionary<string, List<KnownCard>> ByAccount(IEnumerable<SavedDeck> decks)
    {
        var result = new Dictionary<string, List<KnownCard>>(StringComparer.Ordinal);
        foreach (var account in decks.GroupBy(d => d.DisplayName, StringComparer.Ordinal))
            result[account.Key] = For(account);
        return result;
    }

    public static List<KnownCard> For(IEnumerable<SavedDeck> decks)
    {
        var copies = new Dictionary<int, int>();
        var deckIds = new Dictionary<int, HashSet<string>>();
        var deckNames = new Dictionary<int, List<string>>();
        foreach (var deck in decks)
        {
            foreach (var (id, qty) in Totals(deck.CardsJson))
            {
                copies[id] = Math.Max(copies.GetValueOrDefault(id), qty);
                if (!deckIds.TryGetValue(id, out var ids)) deckIds[id] = ids = [];
                if (ids.Add(deck.DeckId) && deck.DeckName is { Length: > 0 } name)
                {
                    if (!deckNames.TryGetValue(id, out var names)) deckNames[id] = names = [];
                    if (names.Count < MaxDeckNames && !names.Contains(name)) names.Add(name);
                }
            }
        }
        return copies
            .Select(kv => new KnownCard(kv.Key, kv.Value, deckIds[kv.Key].Count, deckNames.GetValueOrDefault(kv.Key) ?? []))
            .OrderByDescending(k => k.Copies).ThenBy(k => k.CardId)
            .ToList();
    }

    /// <summary>Copies of each card in one deck version, all zones added up.</summary>
    internal static Dictionary<int, int> Totals(string cardsJson)
    {
        var totals = new Dictionary<int, int>();
        try
        {
            using var doc = JsonDocument.Parse(cardsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return totals;
            foreach (var zone in doc.RootElement.EnumerateObject())
            {
                if (zone.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var card in zone.Value.EnumerateArray())
                {
                    if (!card.TryGetProperty("CardId", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                    var qty = card.TryGetProperty("Quantity", out var q) && q.TryGetInt32(out var n) ? n : 1;
                    if (qty > 0) totals[id] = totals.GetValueOrDefault(id) + qty;
                }
            }
        }
        catch (JsonException)
        {
            // A corrupt stored list proves nothing; the other versions still count.
        }
        return totals;
    }
}
