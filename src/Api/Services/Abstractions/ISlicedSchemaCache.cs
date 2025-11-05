// src/Api/Services/Abstractions/ISlicedSchemaCache.cs
using Api.DTOs;

namespace Api.Services.Abstractions;

public interface ISlicedSchemaCache
{
    bool TryGet(out SliceResultDto? sliced);
    void Set(SliceResultDto sliced);
    void SetDepartment(DepartmentSliceResultDto dept);
    bool TryGetDepartment(out DepartmentSliceResultDto? dept);

}
