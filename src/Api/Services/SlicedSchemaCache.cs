// src/Api/Services/SlicedSchemaCache.cs
using Api.DTOs;
using Api.Services.Abstractions;

namespace Api.Services;

// SLICEDSCHEMA CACHE SU AN DA SADECE ON MEMORY 
public class SlicedSchemaCache : ISlicedSchemaCache
{
    private SliceResultDto? _cached;
    public bool TryGet(out SliceResultDto? sliced)
    {
        sliced = _cached;
        return _cached != null;
    }
    public void Set(SliceResultDto sliced) => _cached = sliced;
    public void Clear() => _cached = null;
}
