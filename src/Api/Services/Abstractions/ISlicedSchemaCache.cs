// src/Api/Services/Abstractions/ISlicedSchemaCache.cs
using Api.DTOs;

namespace Api.Services.Abstractions;

public interface ISlicedSchemaCache
{
    bool TryGet(out SliceResultDto? sliced);
    void Set(SliceResultDto sliced);
    void Clear();
}
