namespace Api.Services.Abstractions
{
    public enum SqlErrorKind
    {
        None = 0,
        DisallowedTable,      // izinli olmayan tablo kullanımı
        DisallowedDml,        // INSERT/UPDATE/DELETE/DDL vb.
        MultipleStatements,   // birden fazla sorgu veya ; ile zincir
        IdVsStringCompare,    // int ID = 'metin' tipi hata
        SyntaxSuspicious,     // çok bariz söz dizimi şüphesi
        RuntimeDbError        // DB’den dönen çalıştırma hatasını temsil etmek için (runner dolduracak)
    }

    public sealed record SqlValidationResult(
        bool IsValid,
        SqlErrorKind Kind,
        string? Message
    );
}
