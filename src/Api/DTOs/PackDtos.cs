using Api.DTOs;

namespace Api.DTOs;

public record BridgeRefDto(
    string ToCategory,                 // hedef pack id
    IReadOnlyList<string> ViaTables    // köprülenecek tablolar
);

public record PackDto(
    string CategoryId,                 // örn: "sales_orders"
    string Name,                       // örn: "Satış & Sipariş"
    IReadOnlyList<string> TablesCore,  // çekirdek tablolar
    IReadOnlyList<string> TablesSatellite,
    List<FkEdgeDto> FkEdges,
    IReadOnlyList<BridgeRefDto> BridgeRefs,           // kategoriler arası köprüler
    string Summary,                    // LLM’e verilecek kısa özet
    string Grain                       // veri taneciği (örn: "Order=header, OrderItems=line")
);

