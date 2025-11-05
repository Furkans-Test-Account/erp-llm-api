using System.Threading;
using System.Threading.Tasks;
using Api.DTOs;

namespace Api.Services.Abstractions
{
    public interface IStrictDepartmentSlicer
    {
        Task<DepartmentSliceResultDto> SliceAsync(
            SchemaDto schema,
            DepartmentSliceOptions options,
            CancellationToken ct = default
        );
    }
}
