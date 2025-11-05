#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Api.DTOs
{
    /// <summary>
    /// Departman/prefix tabanlı slicing için politika.
    /// Ör: DeptId= "insan_kaynaklari", Name="İK", IncludePrefixes=["hr"], ExcludePrefixes=[]
    /// </summary>
    public sealed record DepartmentPolicy(
        string DeptId,
        string Name,
        string[] IncludePrefixes,
        string[]? ExcludePrefixes = null
    );

    /// <summary>
    /// StrictDepartmentSlicer çalışma seçenekleri.
    /// RefTargetsPrefixes: candidateRef ararken dikkate alınacak sözlük/parametre önekleri (örn: cd, df, bs).
    /// </summary>
    public sealed record DepartmentSliceOptions(
        IReadOnlyList<DepartmentPolicy> Policies,
        int MaxCandidateRefsPerPack = 20,
        string[]? RefTargetsPrefixes = null // null => varsayılan {"cd","df","bs"}
    )
    {
        public string[] EffectiveRefTargetsPrefixes =>
            RefTargetsPrefixes is { Length: > 0 } ? RefTargetsPrefixes : new[] { "cd", "df", "bs" };
    }

    /// <summary>
    /// Çekirdek tablo(lar)dan dışa doğru sık kullanılan sözlük/parametre hedeflerini
    /// metadata olarak tutmak için (LLM prompt gating veya UI toggle amaçlı).
    /// </summary>
    public sealed record CandidateRefDto(
        string TargetTable,   // Örn: "cdCountry"
        string Reason,        // Örn: "FK CountryCode from hrEmployee"
        int Score             // Basit skor: kaç farklı tablo/kolon referanslıyor vb.
    );

    /// <summary>
    /// Departman içinde tanıtılacak minimal "ref_" view/shadow spesifikasyonu.
    /// Prompt tarafında token-dostu şema ilanı için opsiyonel.
    /// </summary>
    public sealed record RefViewSpecDto(
        string Name,          // Örn: "hr_ref_Country"
        string SourceTable,   // Örn: "cdCountry"
        string[] Columns      // Örn: new[] { "Code", "Name" }
    );

    /// <summary>
    /// Departman pack DTO'su: Mevcut PackDto ile alan adlarını koruyoruz (compat) + opsiyonel yeni alanlar ekliyoruz.
    /// Eğer projende zaten PackDto varsa ve onu kullanmak istiyorsan, bu sınıfı değil onu genişletmeyi tercih edebilirsin.
    /// </summary>
    public sealed class DepartmentPackDto
    {
        // ---- PackDto ile uyumlu alanlar (isimler korunarak) ----
        [JsonPropertyName("categoryId")] public string CategoryId { get; init; } = null!;
        [JsonPropertyName("name")] public string Name { get; init; } = null!;
        [JsonPropertyName("tablesCore")] public List<string> TablesCore { get; init; } = new();
        [JsonPropertyName("tablesSatellite")] public List<string> TablesSatellite { get; init; } = new(); // Strict modda boş
        [JsonPropertyName("fkEdges")] public List<object> FkEdges { get; init; } = new();               // Strict modda boş (tip sade)
        [JsonPropertyName("bridgeRefs")] public List<object> BridgeRefs { get; init; } = new();            // Strict modda boş

        // ---- Yeni opsiyonel alanlar (prompt gating / UI için) ----
        [JsonPropertyName("candidateRefs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<CandidateRefDto>? CandidateRefs { get; init; }

        [JsonPropertyName("allowedRefViews")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RefViewSpecDto>? AllowedRefViews { get; init; }
    }

    /// <summary>
    /// Strict department slicing sonucu. Mevcut SliceResultDto varsa ona paralel isimlendirme.
    /// </summary>
    public sealed class DepartmentSliceResultDto
    {
        [JsonPropertyName("schemaName")] public string SchemaName { get; init; } = null!;
        [JsonPropertyName("packs")] public List<DepartmentPackDto> Packs { get; init; } = new();

        public DepartmentSliceResultDto() { }
        public DepartmentSliceResultDto(string schemaName, List<DepartmentPackDto> packs)
        {
            SchemaName = schemaName;
            Packs = packs;
        }
    }
}
