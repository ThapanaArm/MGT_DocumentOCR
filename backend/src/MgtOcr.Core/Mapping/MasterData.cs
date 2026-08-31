namespace MgtOcr.Core.Mapping;

// Container for load_masters()'s 7 lists (app/main.py:94-104) — passed to RunMapping exactly like
// Python's `masters: dict` (each value already the plain-dict-per-row shape via DynamicRow.ToDictList).
public class MasterData
{
    public required List<Dictionary<string, object?>> Customers { get; init; }
    public required List<Dictionary<string, object?>> ShipTos { get; init; }
    public required List<Dictionary<string, object?>> Materials { get; init; }
    public required List<Dictionary<string, object?>> CustomerMaterials { get; init; }
    public required List<Dictionary<string, object?>> Vendors { get; init; }
    public required List<Dictionary<string, object?>> VendorMaterials { get; init; }
    public required List<Dictionary<string, object?>> Uoms { get; init; }
}
